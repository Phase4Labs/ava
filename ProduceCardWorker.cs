using System.Text.Json.Serialization;
namespace get_assessment_no_graph;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Diagnostics;


public sealed class ProduceCardWorker
{
    private readonly SupabaseRestClient _db;
    private readonly HttpClient _openai;

    private readonly string _model;
    private readonly string _executionQuestion;

    private readonly CalibrationCollector _calibration;
    private readonly string _frameworkVersion;

    private readonly ScannerDisplay _display = new();
    public  ScannerDisplay Display => _display;

    // STRICT JSON schema instruction (use exactly your spec)
    internal const string StrictJsonInstruction = """
Output MUST be a single JSON object matching this schema (no extra text, no markdown, no code fences, no comments)

OUTPUT FORMAT (STRICT):
Return ONLY valid JSON with this shape:

{
  "schema_version": 1,
  "verdict": "TRADE" | "NO_TRADE",
  "scenarios": [
    {
      "scenario_rank": 1-3,
      "direction": "long" | "short",
      "entry_type": "reclaim_hold" | "break_hold" | "fade_pop" | "vwap_reclaim" | "overextension_fade",
      "scenario_prob": 0.00-1.00,
      "success_prob": 0.00-1.00,
      "entry_low": number|null,
      "entry_high": number|null,
      "stop_price": number|null,
      "t1": number|null,
      "t2": number|null,
      "runner": number|null,
      "grade": "A" | "B" | "C" | "D" | "F",
      "grade_rationale": "one sentence"|null
    }
  ]
}

If NO TRADE: verdict="NO_TRADE" and scenarios=[]
Return JSON only. No markdown. No extra keys.
""";

    private readonly PolygonIngestionService? _ingest;

    public ProduceCardWorker(
        SupabaseRestClient db,
        string openAiApiKey,
        string model,
        string executionQuestion,
        PolygonIngestionService? ingest = null)
    {
        _db     = db;
        _ingest = ingest;

        _model = model;
        _executionQuestion = executionQuestion;
        _frameworkVersion = ComputeFrameworkVersion();
        _calibration = new CalibrationCollector(_db, _model, _frameworkVersion);

        _openai = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        _openai.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", openAiApiKey);
    }

    /// <summary>
    /// Calls the existing GPT execution-card path without writing jobs, cards, scenarios,
    /// calibration rows, triggers, or signals. Intended for historical/shadow comparison only.
    /// </summary>
    public async Task<ExecutionCardAnalysisResult> AnalyzeOnlyAsync(
        string ticker,
        DateTime asOfUtc,
        string datasetJson,
        string callType = "historical_shadow_cloud",
        CancellationToken ct = default)
    {
        ticker = ticker.ToUpperInvariant();
        var llmResult = await CallOpenAiForCardAsync(ticker, asOfUtc, datasetJson, ct, callType);
        var ok = TryParseExecutionCardJson(llmResult.OutputText, out var card, out var error);

        return new ExecutionCardAnalysisResult(
            Provider: "openai",
            Model: _model,
            ResponseId: llmResult.ResponseId,
            RawJson: llmResult.OutputText,
            Card: card,
            ParseSuccess: ok && card is not null,
            ParseError: error,
            ElapsedMs: llmResult.LatencyMs,
            PromptTokens: llmResult.Usage.InputTokens,
            OutputTokens: llmResult.Usage.OutputTokens,
            CachedInputTokens: llmResult.Usage.CachedInputTokens,
            ReasoningTokens: llmResult.Usage.ReasoningTokens);
    }

    public async Task<CardQualitySummary> RunOnceAsync(
        string ticker,
        DateTime utcNow,
        string datasetJson,
        DateTime tsFromUtc,
        DateTime tsToUtc,
        CancellationToken ct = default)
    {
        ticker = ticker.ToUpperInvariant();

        // 1) Create job
        var jobId = Guid.NewGuid();
        var jobRow = new
        {
            id = jobId,
            ticker = ticker,
            ts_from_utc = tsFromUtc,
            ts_to_utc = tsToUtc,
            status = "running",
            framework_name = "hv_framework_v1",
            prompt_version = "v1"
        };
        await _db.UpsertAsync("analysis_jobs", new[] { jobRow }, "id", ct);

        try
        {
            // 2) Call OpenAI with retry/backoff
            AppLog.Llm($"Calling LLM for {ticker} asof={tsToUtc:HH:mm:ss}");
            var llmResult = await CallOpenAiForCardAsync(ticker, tsToUtc, datasetJson, ct);
            var responseId = llmResult.ResponseId;
            var outputText = llmResult.OutputText;
            AppLog.Llm($"Response received for {ticker}");

            // 3) Parse strict JSON (DB truth path)
            bool parseSuccess = TryParseExecutionCardJson(outputText, out ExecutionCardJsonV1? card, out var parseErr);
            if (!parseSuccess || card is null)
            {
                AppLog.Error($"CARD_JSON_INVALID {ticker} err={parseErr}");
                Console.WriteLine($"{DateTime.UtcNow:o} CARD_JSON_INVALID {ticker} asof={tsToUtc:o} err={parseErr}");

                // treat as NO_TRADE or mark validation_status = invalid
                card = new ExecutionCardJsonV1
                {
                    SchemaVersion = 1,
                    Verdict = "NO_TRADE",
                    Scenarios = new List<ExecutionScenarioJsonV1>()
                };
            }

            card.SchemaVersion = 1;
            card.Verdict = string.Equals(card.Verdict, "TRADE", StringComparison.OrdinalIgnoreCase) ? "TRADE" : "NO_TRADE";
            card.Scenarios ??= new List<ExecutionScenarioJsonV1>();
            if (card.Verdict == "NO_TRADE") card.Scenarios.Clear();

            // Calibration capture (best effort; do not fail the job if this fails)
            try
            {
                await _calibration.TryCollectAsync(
                    ticker: ticker,
                    asofTsUtc: tsToUtc,
                    datasetJson: datasetJson,
                    card: card,
                    openAiResponseId: responseId,
                    ct: ct);
            }
            catch (Exception calEx)
            {
                // Log and continue. Calibration must never break live evaluation.
                Console.WriteLine($"{DateTime.UtcNow:o} CALIBRATION_SAVE_FAILED {ticker} asof={tsToUtc:o} err={calEx.Message}");
            }

            // Stage 2B.4 promoted structural gate + Stage 2D validated quality ordering.
            // The raw model card remains the DB/audit record. Stage 2B.4 decides what is
            // structurally executable. Stage 2D may only reorder that normalized executable
            // copy as PREFERRED -> SECONDARY -> original scenario_rank. It cannot change any
            // scenario field or consult Stage 2C empirical evidence.
            AvaScenarioDecisionResult? stage2b4 = null;
            Stage2DQualitySelectionResult? stage2d = null;
            ExecutionCardJsonV1? executableCardOverride = null;
            var gateMode = Stage2B4GateConfig.Mode;
            try
            {
                stage2b4 = AvaScenarioDecisionLayer.Evaluate(card, datasetJson);

                // Establish the Stage 2B.4 structural result first. If Stage 2D later fails,
                // this authoritative structural gate remains in force rather than failing
                // all the way back to the raw card.
                if (gateMode == Stage2B4GateMode.Enforce)
                    executableCardOverride = stage2b4.Structural.NormalizedCard;

                Console.WriteLine(
                    $"{DateTime.UtcNow:o} STAGE2B4_{Stage2B4GateConfig.Label.ToUpperInvariant()} {ticker} " +
                    $"raw={stage2b4.Structural.RawVerdict} structural={stage2b4.Structural.EffectiveVerdict} " +
                    $"valid={stage2b4.Structural.StructurallyValidScenarioCount}/{stage2b4.Structural.RawScenarioCount} " +
                    $"preferred={stage2b4.Quality.PreferredScenarioCount} secondary={stage2b4.Quality.SecondaryScenarioCount}");

                await Stage2B4ShadowTelemetry.TryWriteAsync(
                    source: $"live_openai_card_{Stage2B4GateConfig.Label}",
                    provider: "openai",
                    model: _model,
                    ticker: ticker,
                    asOfUtc: tsToUtc,
                    decision: stage2b4,
                    ct: ct);

                try
                {
                    stage2d = Stage2DQualitySelector.Select(stage2b4);

                    if (gateMode == Stage2B4GateMode.Enforce && Stage2DQualitySelectionConfig.IsEnforced)
                        executableCardOverride = stage2d.OrderedExecutableCard;

                    Console.WriteLine(
                        $"{DateTime.UtcNow:o} STAGE2D_{Stage2DQualitySelectionConfig.Label.ToUpperInvariant()} {ticker} " +
                        $"structural_order=[{string.Join(",", stage2d.OriginalStructuralOrder)}] " +
                        $"quality_order=[{string.Join(",", stage2d.QualityExecutionOrder)}] " +
                        $"changed={stage2d.SelectionChanged} structural_gate={Stage2B4GateConfig.Label}");

                    await Stage2DQualitySelectionTelemetry.TryWriteAsync(
                        source: $"live_openai_card_{Stage2DQualitySelectionConfig.Label}",
                        provider: "openai",
                        model: _model,
                        ticker: ticker,
                        asOfUtc: tsToUtc,
                        selection: stage2d,
                        ct: ct);
                }
                catch (Exception stage2dEx)
                {
                    // Stage 2D fail-safe: retain the already-enforced Stage 2B.4 structural
                    // card. A quality-ordering bug may remove the quality preference, but it
                    // can never re-admit a structurally invalid raw scenario.
                    Console.WriteLine(
                        $"{DateTime.UtcNow:o} STAGE2D_SELECTION_FAILED_FALLBACK_STRUCTURAL {ticker} " +
                        $"quality_mode={Stage2DQualitySelectionConfig.Label} asof={tsToUtc:o} err={stage2dEx.Message}");
                }
            }
            catch (Exception stage2b4Ex)
            {
                // Preserve the Stage 2B.4 promotion behavior: a failure in the structural
                // decision layer is loud and operationally fail-open to the pre-2B.4 path.
                executableCardOverride = null;
                Console.WriteLine(
                    $"{DateTime.UtcNow:o} STAGE2B4_GATE_FAILED_FAIL_OPEN {ticker} " +
                    $"mode={Stage2B4GateConfig.Label} asof={tsToUtc:o} err={stage2b4Ex.Message}");
            }

            //var verdict = (card?.Verdict ?? "NO_TRADE").ToUpperInvariant();
            //if (verdict != "TRADE") verdict = "NO_TRADE";

            var cardJson = new
            {
                schema_version = 1,
                verdict = card?.Verdict,
                scenarios = card.Scenarios.Select(s => new
                {
                    scenario_rank = s.ScenarioRank,
                    direction = s.Direction,
                    entry_type = s.EntryType,
                    entry_low = s.EntryLow,
                    entry_high = s.EntryHigh,
                    stop_price = s.StopPrice,
                    t1 = s.T1,
                    t2 = s.T2,
                    runner = s.Runner,
                    scenario_prob = s.ScenarioProb,
                    success_prob = s.SuccessProb,
                    grade = s.Grade,
                    grade_rationale = s.GradeRationale
                }).ToList()
            };

            var verdict = card?.Verdict ?? "NO_TRADE";

            // 4) Upsert execution_cards (always)
            var cardRow = new
            {
                ticker = ticker,
                asof_ts_utc = tsToUtc,

                // REQUIRED by your current schema
                verdict = verdict.Equals("TRADE", StringComparison.OrdinalIgnoreCase) ? "TRADE" : "NO_TRADE",

                // If these columns exist in your execution_cards table, populate them too:
                schema_version = 1,
                raw_card_text = outputText,
                validation_status = parseSuccess ? "valid" : "invalid",

                // Legacy/compat columns (keep for backwards compatibility)
                model = _model,
                response_id = responseId,
                prompt_version = "v1",
                card_json = cardJson,
                card_text = outputText,
                job_id = jobId
            };

            await _db.UpsertAsync(
                "execution_cards",
                new[] { cardRow },
                "ticker,asof_ts_utc",
                ct);

            // 5) Upsert execution_card_scenarios ONLY if valid TRADE JSON with scenarios
            if (card is not null && card.Verdict == "TRADE" && card.Scenarios is not null && card.Scenarios.Count > 0)
            {
                var scenarioRows = card.Scenarios
                    .Where(s => s.ScenarioRank >= 1 && s.ScenarioRank <= 3)
                    .Select(s => new
                    {
                        ticker = ticker,
                        asof_ts_utc = tsToUtc,
                        rank = s.ScenarioRank,
                        direction = s.Direction,          // "long"|"short"
                        entry_type = s.EntryType,         // "reclaim_hold"|"break_hold"|"fade_pop"
                        setup = (string?)null,            // optional if you add to schema
                        entry_low = s.EntryLow,
                        entry_high = s.EntryHigh,
                        stop = s.StopPrice,               // IMPORTANT: DB column is stop
                        t1 = s.T1,
                        t2 = s.T2,
                        runner = s.Runner,
                        scenario_prob = s.ScenarioProb,
                        success_prob = s.SuccessProb,
                        grade = s.Grade,
                        grade_rationale = s.GradeRationale
                    })
                    .ToArray();

                await _db.UpsertAsync(
                    "execution_card_scenarios",
                    scenarioRows,
                    "ticker,asof_ts_utc,rank",
                    ct);
            }

            // 6) Trigger engine should run AFTER DB writes
            var triggerEngine = new TriggerEngine(_db, _ingest);
            await triggerEngine.EvaluateAndEmitAsync(
                ticker,
                tsToUtc,
                outputText,
                executableCardOverride,
                ct);

            await _db.PatchAsync("analysis_jobs", $"?id=eq.{jobId}", new
            {
                status = "done",
                error_message = (string?)null
            }, ct);

            // Stage 2E.1 live local shadow. Queue only after the authoritative GPT card has
            // completed DB persistence and TriggerEngine evaluation. This call is non-blocking,
            // disabled by default, telemetry-only, and skips immediately while Ollama is busy.
            Stage2ELiveLocalShadow.TryQueue(
                ticker: ticker,
                asOfUtc: tsToUtc,
                jobId: jobId,
                openAiResponseId: responseId,
                openAiModel: _model,
                executionQuestion: _executionQuestion,
                fullDatasetJson: datasetJson,
                gptCard: card,
                gptStage2B4: stage2b4,
                gptStage2D: stage2d);


            // 7) Triggering
            // IMPORTANT: If DB is source of truth, TriggerEngine should read execution_card_scenarios.
            // For now keep the call, but you should update TriggerEngine next (see “What’s left” below).
            //var triggerEngine = new TriggerEngine(_db);
            //await triggerEngine.EvaluateAndEmitAsync(ticker, tsToUtc, rawText, ct);

            // Optional: persist parse error for debugging (you can add a column later)
            _ = parseErr;

            // 8) Build and return quality summary for cadence feedback
            var scenarios = card?.Scenarios ?? new List<ExecutionScenarioJsonV1>();
            var anyQualified = scenarios.Any(s =>
                (s.ScenarioProb ?? 0m) >= 0.35m && (s.SuccessProb ?? 0m) >= 0.55m);

            return new CardQualitySummary(
                Verdict              : card?.Verdict ?? "NO_TRADE",
                ScenarioCount        : scenarios.Count,
                AvgScenarioProb      : scenarios.Count > 0
                    ? scenarios.Average(s => s.ScenarioProb ?? 0m) : 0m,
                AvgSuccessProb       : scenarios.Count > 0
                    ? scenarios.Average(s => s.SuccessProb ?? 0m) : 0m,
                AnyScenarioQualified : anyQualified
            );
        }
        catch (Exception ex)
        {
            await _db.PatchAsync("analysis_jobs", $"?id=eq.{jobId}", new
            {
                status = "error",
                error_message = ex.Message
            }, ct);

            throw;   // caller will not receive a summary on error — exception propagates
        }
    }

    private async Task<OpenAiCallResult> CallOpenAiForCardAsync(
        string ticker,
        DateTime asOfUtc,
        string datasetJson,
        CancellationToken ct,
        string callType = "execution_card")
    {
        const int maxAttempts = 3;
        Exception? last = null;

        OpenAiTelemetry.WriteDebugPayloadIfEnabled(callType, ticker, asOfUtc, datasetJson);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var request = new
                {
                    model = _model,
                    store = false,
                    text = new
                    {
                        format = new
                        {
                            type = "json_schema",
                            name = "execution_card_v1",
                            strict = true,
                            schema = OpenAiJsonSchemas.ExecutionCardV1
                        }
                    },
                    input = new object[]
                    {
                        new {
                            role    = "system",
                            content = new object[] { new { type = "input_text", text = _executionQuestion } }
                        },
                        new {
                            role    = "user",
                            content = new object[]
                            {
                                new { type = "input_text", text = StrictJsonInstruction },
                                new { type = "input_text", text = "DATASET_JSON:\n" + datasetJson }
                            }
                        }
                    }
                };

                var json = JsonSerializer.Serialize(
                    request,
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                var requestBytes = Encoding.UTF8.GetByteCount(json);
                var sw = Stopwatch.StartNew();

                using var resp = await _openai.PostAsync(
                    "https://api.openai.com/v1/responses",
                    new StringContent(json, Encoding.UTF8, "application/json"),
                    ct);

                var respText = await resp.Content.ReadAsStringAsync(ct);
                sw.Stop();

                if (!resp.IsSuccessStatusCode)
                {
                    if (IsTransient(resp.StatusCode))
                        throw new HttpRequestException($"OpenAI HTTP {(int)resp.StatusCode}: {respText}");

                    throw new Exception($"OpenAI HTTP {(int)resp.StatusCode}: {respText}");
                }

                using var doc = JsonDocument.Parse(respText);
                var responseId = doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                var outputText = ExtractOutputText(doc.RootElement);
                var usage = OpenAiTelemetry.ReadUsage(doc.RootElement);
                var serviceTier = OpenAiTelemetry.ReadServiceTier(doc.RootElement);

                var result = new OpenAiCallResult(
                    responseId,
                    outputText,
                    usage,
                    serviceTier,
                    sw.ElapsedMilliseconds,
                    attempt,
                    requestBytes);

                OpenAiTelemetry.Record(new LlmUsageLogRow
                {
                    TimestampUtc = DateTime.UtcNow,
                    AsOfUtc = asOfUtc.ToUniversalTime(),
                    CallType = callType,
                    Ticker = ticker,
                    Model = _model,
                    ResponseId = responseId,
                    ServiceTier = serviceTier,
                    ReasoningEffort = "default",
                    DatasetChars = datasetJson.Length,
                    RequestBytes = requestBytes,
                    ResponseChars = outputText.Length,
                    InputTokens = usage.InputTokens,
                    CachedInputTokens = usage.CachedInputTokens,
                    OutputTokens = usage.OutputTokens,
                    ReasoningTokens = usage.ReasoningTokens,
                    TotalTokens = usage.TotalTokens,
                    AttemptCount = attempt,
                    LatencyMs = sw.ElapsedMilliseconds
                });

                Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss} OpenAI response received responseId={responseId} len={outputText.Length}");
                AppLog.Llm($"Response OK responseId={responseId?.Substring(0, Math.Min(12, responseId?.Length ?? 0))} len={outputText.Length}");
                return result;
            }
            catch (Exception ex) when (IsRetryable(ex))
            {
                last = ex;
                if (attempt >= maxAttempts) break;

                var delayMs = Math.Min(8000, 500 * (int)Math.Pow(2, attempt - 1));
                AppLog.Llm($"Retrying {callType} {ticker} attempt={attempt + 1}/{maxAttempts} after={delayMs}ms");
                await Task.Delay(delayMs, ct);
            }
        }

        AppLog.Error($"LLM failed after {maxAttempts} attempts: {last?.Message}");
        throw new Exception($"OpenAI failed after {maxAttempts} attempts.", last);
    }

    private static bool IsTransient(HttpStatusCode code)
        => code == HttpStatusCode.TooManyRequests
           || code == HttpStatusCode.InternalServerError
           || code == HttpStatusCode.BadGateway
           || code == HttpStatusCode.ServiceUnavailable
           || code == HttpStatusCode.GatewayTimeout;

    private static bool IsRetryable(Exception ex)
        => ex is HttpRequestException
           || ex is TaskCanceledException
           || ex is TimeoutException;

    private static string ExtractOutputText(JsonElement root)
    {
        if (root.TryGetProperty("output_text", out var outputTextEl) && outputTextEl.ValueKind == JsonValueKind.String)
            return outputTextEl.GetString() ?? "";

        var sb = new StringBuilder();
        if (root.TryGetProperty("output", out var outArr) && outArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in outArr.EnumerateArray())
            {
                if (item.TryGetProperty("content", out var contentArr) && contentArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in contentArr.EnumerateArray())
                    {
                        if (part.TryGetProperty("type", out var tEl) && tEl.GetString() == "output_text" &&
                            part.TryGetProperty("text", out var txtEl) && txtEl.ValueKind == JsonValueKind.String)
                        {
                            sb.AppendLine(txtEl.GetString());
                        }
                    }
                }
            }
        }

        var s = sb.ToString().Trim();
        return string.IsNullOrEmpty(s) ? "" : s;
    }

    // -------------------------
    // JSON parsing + validation
    // -------------------------

    internal static bool TryParseExecutionCardJson(string raw, out ExecutionCardJsonV1? card, out string? error)
    {
        card = null;
        error = null;

        var text = raw?.Trim() ?? "";
        if (text.Length == 0)
        {
            error = "Empty model output.";
            return false;
        }

        // Fast path: direct JSON
        if (TryDeserialize(text, out card, out error))
            return Validate(card, out error);

        // Fallback: extract first JSON object/array from text (brace matching)
        var extracted = ExtractFirstJsonBlock(text);
        if (extracted is null)
        {
            error = error is null ? "No JSON detected in output." : error;
            return false;
        }

        if (!TryDeserialize(extracted, out card, out error))
            return false;

        return Validate(card, out error);
    }

    private static bool TryDeserialize(string json, out ExecutionCardJsonV1? card, out string? error)
    {
        card = null;
        error = null;

        try
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            card = JsonSerializer.Deserialize<ExecutionCardJsonV1>(json, opts);
            if (card is null)
            {
                error = "Deserialized null.";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = "JSON deserialize failed: " + ex.Message;
            return false;
        }
    }

    private static bool Validate(ExecutionCardJsonV1 card, out string? error)
    {
        error = null;

        if (card.SchemaVersion != 1)
        {
            error = $"schema_version must be 1 (got {card.SchemaVersion}).";
            return false;
        }

        if (!string.Equals(card.Verdict, "TRADE", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(card.Verdict, "NO_TRADE", StringComparison.OrdinalIgnoreCase))
        {
            error = $"verdict must be TRADE or NO_TRADE (got {card.Verdict}).";
            return false;
        }

        card.Verdict = card.Verdict.ToUpperInvariant();

        if (card.Scenarios is null)
            card.Scenarios = new List<ExecutionScenarioJsonV1>();

        if (card.Verdict == "NO_TRADE" && card.Scenarios.Count != 0)
        {
            error = "NO_TRADE must have scenarios=[].";
            return false;
        }

        // Minimal per-scenario validation
        foreach (var s in card.Scenarios)
        {
            if (s.ScenarioRank < 1 || s.ScenarioRank > 3)
            {
                error = $"scenario_rank must be 1..3 (got {s.ScenarioRank}).";
                return false;
            }

            if (s.Direction is null || (s.Direction != "long" && s.Direction != "short"))
            {
                error = $"direction must be long|short (got {s.Direction}).";
                return false;
            }

            if (s.EntryType is null || (s.EntryType != "reclaim_hold" && s.EntryType != "break_hold" && s.EntryType != "fade_pop" && s.EntryType != "vwap_reclaim" && s.EntryType != "overextension_fade"))
            {
                error = $"entry_type invalid (got {s.EntryType}).";
                return false;
            }

            if (s.ScenarioProb is not null && (s.ScenarioProb < 0m || s.ScenarioProb > 1m))
            {
                error = $"scenario_prob out of range (got {s.ScenarioProb}).";
                return false;
            }

            if (s.SuccessProb is not null && (s.SuccessProb < 0m || s.SuccessProb > 1m))
            {
                error = $"success_prob out of range (got {s.SuccessProb}).";
                return false;
            }

            // Grade is optional — if present must be A/B/C/D/F
            if (s.Grade is not null &&
                s.Grade != "A" && s.Grade != "B" && s.Grade != "C" &&
                s.Grade != "D" && s.Grade != "F")
            {
                error = $"grade must be A/B/C/D/F if present (got {s.Grade}).";
                return false;
            }
        }

        // Normalize fields
        foreach (var s in card.Scenarios)
        {
            s.Direction = s.Direction!.ToLowerInvariant();
            s.EntryType = s.EntryType!.ToLowerInvariant();
        }

        return true;
    }

    private static string? ExtractFirstJsonBlock(string text)
    {
        // Finds first '{' or '[' and then returns a balanced block.
        int startObj = text.IndexOf('{');
        int startArr = text.IndexOf('[');
        int start;

        if (startObj < 0 && startArr < 0) return null;
        if (startObj < 0) start = startArr;
        else if (startArr < 0) start = startObj;
        else start = Math.Min(startObj, startArr);

        char open = text[start];
        char close = open == '{' ? '}' : ']';

        int depth = 0;
        bool inString = false;
        char prev = '\0';

        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];

            if (c == '"' && prev != '\\')
                inString = !inString;

            if (!inString)
            {
                if (c == open) depth++;
                else if (c == close) depth--;

                if (depth == 0)
                    return text.Substring(start, i - start + 1);
            }

            prev = c;
        }

        return null;
    }

    private string ComputeFrameworkVersion()
    {
        // Anything that changes model behavior should be included.
        // Include StrictJsonInstruction because it changes output policy.
        var s = $"{_model}\n---\n{_executionQuestion}\n---\n{StrictJsonInstruction}";
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(s);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}