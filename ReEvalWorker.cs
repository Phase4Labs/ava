using System.Net;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace get_assessment_no_graph;

/// <summary>
/// Re-evaluates stop/T1/T2/runner for an open position based on current tape.
/// Uses the same dataset pipeline as ProduceCardWorker but with a position-aware prompt.
/// Results are written to trader_state reeval_* columns.
/// </summary>
public sealed class ReEvalWorker
{
    private readonly SupabaseRestClient _db;
    private readonly HttpClient         _openai;
    private readonly string             _model;

    // ── System prompt ─────────────────────────────────────────────
    // Self-contained — no external framework file required.
    private const string ReEvalSystemPrompt = """
        You are a position management assistant for an intraday high-volatility trading framework.

        Your job is to re-evaluate the stop, targets, and runner for a single open position
        using the supplied dataset and position context. You do not generate new trade ideas.

        Core rules:
        - Evaluate only the open position described in the question.
        - Anchor all levels to structural evidence in the dataset (VP levels, VWAP, bar structure).
        - Treat the explicit CURRENT MARKET SNAPSHOT in the question as authoritative for stop placement.
        - A long protective stop must remain below the current executable market.
        - A short protective stop must remain above the current executable market.
        - Never widen a stop — only tighten or trail.
        - If current levels are still valid and no structural reason to update exists, return them unchanged.
        - Never return a stop that the market has already crossed. If no safer valid update exists,
          return the current working stop unchanged; exit handling is performed separately.
        - stop_price and T1 must never be null for an open position.
        - Do not invent optional levels. Use null for T2 or runner when they cannot be justified.
        - Return ONLY valid JSON. No markdown, no explanations, no code fences.
        """;

    // ── Output schema ─────────────────────────────────────────────
    private const string ReEvalOutputSchema = """
        OUTPUT FORMAT (STRICT — return this JSON shape and nothing else):
        {
          "stop_price":       number,
          "stop_type":        "hard" | "profit_protection" | "soft_warning",
          "t1":               number,
          "t2":               number | null,
          "runner":           number | null,
          "runner_justified": true | false,
          "confidence":       0.00–1.00,
          "rationale":        "one sentence — cite the specific level or structure that drove the update"
        }

        Rules:
        - stop_type must be one of the three values above.
        - runner_justified must be explicitly true or false — never null.
        - If runner_justified is false, set runner to null.
        - confidence reflects how well the current dataset supports the updated levels.
        - rationale must be one sentence only. Be specific (e.g. "stop trailed to session_vp.poc at 14.82 after T1 hit").
        """;

    public ReEvalWorker(
        SupabaseRestClient db,
        string             openAiApiKey,
        string             model)
    {
        _db    = db;
        _model = model;

        _openai = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _openai.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", openAiApiKey);
    }

    // ── Public entry point ────────────────────────────────────────

    /// <summary>
    /// Run a re-evaluation for an open position.
    /// Returns true if re-eval produced updated levels and wrote them to DB.
    /// </summary>
    public async Task<bool> RunAsync(
        string         ticker,
        string         datasetJson,
        TraderStateRow position,
        DateTime       asofUtc,
        ReEvalLiveMarketSnapshot? liveMarket = null,
        CancellationToken ct = default)
    {
        ticker = ticker.ToUpperInvariant();

        var direction = (position.Position ?? "").Trim().ToLowerInvariant();
        if (direction != "long" && direction != "short")
        {
            Console.WriteLine($"[reeval] {ticker} — no open position, skipping");
            return false;
        }

        if (!TryBuildMarketContext(datasetJson, direction, liveMarket, out var market, out var marketError))
        {
            Console.WriteLine($"[reeval] {ticker} — market snapshot invalid: {marketError}");
            return false;
        }

        if (position.OpenedAtUtc.HasValue &&
            market!.DatasetAsOfUtc < FloorUtcMinute(position.OpenedAtUtc.Value))
        {
            Console.WriteLine($"[reeval] {ticker} — dataset predates current position " +
                              $"(dataset={market.DatasetAsOfUtc:o} opened={position.OpenedAtUtc:o})");
            return false;
        }

        var signalContext = await LoadSignalContextAsync(position.LastSignalId, ct);
        var question = BuildQuestion(position, market!, signalContext);

        Console.WriteLine($"[reeval] {ticker} — calling LLM " +
                          $"(entry=${position.EntryPrice:F2} {position.Position})");

        string outputText;
        try
        {
            var llmResult = await CallLlmAsync(ticker, asofUtc, question, datasetJson, ct);
            outputText = llmResult.OutputText;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[reeval] {ticker} — LLM call failed: {ex.Message}");
            return false;
        }

        if (!TryParseReEval(outputText, out var result) || result is null)
        {
            Console.WriteLine($"[reeval] {ticker} — parse failed: " +
                              $"{outputText[..Math.Min(200, outputText.Length)]}");
            return false;
        }

        var decision = ValidateLevels(result, position, market!);
        if (!decision.IsValid)
        {
            Console.WriteLine($"[reeval] {ticker} — sanity check FAILED, discarding " +
                              $"(stop={result.StopPrice} t1={result.T1} t2={result.T2} runner={result.Runner}) " +
                              $"reason={decision.Reason}");
            return false;
        }

        // Apply deterministic normalization (notably explicit runner removal).
        if (result.Runner != decision.NormalizedLevels.Runner)
        {
            Console.WriteLine($"[reeval] {ticker} — runner nulled (runner_justified=false, was {result.Runner})");
        }
        result.StopPrice = decision.NormalizedLevels.Stop;
        result.T1        = decision.NormalizedLevels.T1;
        result.T2        = decision.NormalizedLevels.T2;
        result.Runner    = decision.NormalizedLevels.Runner;

        Console.WriteLine($"[reeval] {ticker} — updated: " +
                          $"stop={result.StopPrice} ({result.StopType}) " +
                          $"t1={result.T1} t2={result.T2} runner={result.Runner} " +
                          $"conf={result.Confidence:F2} | {result.Rationale}");

        try
        {
            await _db.PatchIncludingNullsAsync(
                "trader_state",
                $"?ticker=eq.{Uri.EscapeDataString(ticker)}",
                new
                {
                    reeval_stop   = result.StopPrice,
                    reeval_t1     = result.T1,
                    reeval_t2     = result.T2,
                    reeval_runner = result.Runner,
                    reeval_at_utc = market!.ReferenceAtUtc,
                },
                ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[reeval] {ticker} — DB write failed: {ex.Message}");
            return false;
        }

        return true;
    }

    // ── Prompt builder ────────────────────────────────────────────

    private static string BuildQuestion(
        TraderStateRow pos,
        ReEvalMarketContext market,
        ReEvalSignalContext? signal)
    {
        var direction = pos.Position!.Trim().ToLowerInvariant();
        var elapsed   = pos.OpenedAtUtc.HasValue
            ? $"{Math.Max(0, (int)(market.ReferenceAtUtc - EnsureUtc(pos.OpenedAtUtc.Value)).TotalMinutes)} minutes"
            : "unknown duration";

        // Use most recent working levels as baseline
        var curStop       = pos.EffectiveStop;
        var curT1         = pos.EffectiveT1;
        var curT2         = pos.EffectiveT2;
        var curRunner     = pos.EffectiveRunner;
        var isFirstReeval = !pos.HasApplicableReeval;

        var sb = new StringBuilder();

        // ── Position summary ──────────────────────────────────────
        sb.AppendLine($"OPEN {direction.ToUpperInvariant()} POSITION — re-evaluate stop and targets.");
        sb.AppendLine();
        sb.AppendLine($"  Entry price:      ${pos.EntryPrice:F2}");
        if (signal?.EntryLow is not null || signal?.EntryHigh is not null)
            sb.AppendLine($"  Original window:  {FormatRange(signal.EntryLow, signal.EntryHigh)}");
        sb.AppendLine($"  Time in position: {elapsed}");
        sb.AppendLine($"  T1 hit:           {(pos.T1Hit ? "YES — partial profits taken" : "NO")}");
        sb.AppendLine($"  T2 hit:           {(pos.T2Hit ? "YES" : "NO")}");
        sb.AppendLine();

        sb.AppendLine("CURRENT MARKET SNAPSHOT (authoritative for stop safety):");
        sb.AppendLine($"  Executable reference for this {direction}: ${market.ReferencePrice:F4} ({market.ReferenceSource})");
        sb.AppendLine($"  Reference timestamp: {market.ReferenceAtUtc:o}");
        sb.AppendLine($"  Latest completed bar: O={market.LastBarOpen:F4} H={market.LastBarHigh:F4} " +
                      $"L={market.LastBarLow:F4} C={market.LastBarClose:F4} at {market.DatasetAsOfUtc:o}");
        if (market.Bid.HasValue || market.Ask.HasValue)
            sb.AppendLine($"  Live NBBO: bid={FormatOptional(market.Bid)} ask={FormatOptional(market.Ask)}");
        if (market.LastTrade.HasValue)
            sb.AppendLine($"  Live last trade: {FormatOptional(market.LastTrade)} at {market.LastTradeAtUtc:o}");
        sb.AppendLine();

        // ── Current levels ────────────────────────────────────────
        if (isFirstReeval)
            sb.AppendLine("Current levels (original signal):");
        else
            sb.AppendLine($"Current working levels (last re-eval at {pos.ReevalAtUtc:HH:mm} UTC):");

        if (!isFirstReeval)
            sb.AppendLine($"  Original signal — " +
                          $"stop: ${pos.StopPrice:F2}  " +
                          $"T1: ${pos.T1:F2}  " +
                          $"T2: ${pos.T2:F2}  " +
                          $"Runner: {(pos.Runner.HasValue ? $"${pos.Runner:F2}" : "none")}");

        sb.AppendLine($"  stop:   ${curStop:F2}");
        sb.AppendLine($"  T1:     {(curT1.HasValue   ? $"${curT1:F2}"     : "none")}");
        sb.AppendLine($"  T2:     {(curT2.HasValue   ? $"${curT2:F2}"     : "none")}");
        sb.AppendLine($"  Runner: {(curRunner.HasValue ? $"${curRunner:F2}" : "none")}");
        sb.AppendLine();

        // ── Stop rules ────────────────────────────────────────────
        sb.AppendLine("STOP RULES:");
        if (direction == "long")
        {
            sb.AppendLine($"  • stop_price MUST remain below the current executable reference (${market.ReferencePrice:F4}).");
            sb.AppendLine($"  • stop_price MUST be below entry (${pos.EntryPrice:F2}), unless T1 is already hit.");
            sb.AppendLine("  • If T1 is hit: profit-protection stop — may trail above entry.");
            sb.AppendLine("  • If T1 is not hit: hard stop at structural invalidation below entry.");
        }
        else
        {
            sb.AppendLine($"  • stop_price MUST remain above the current executable reference (${market.ReferencePrice:F4}).");
            sb.AppendLine($"  • stop_price MUST be above entry (${pos.EntryPrice:F2}), unless T1 is already hit.");
            sb.AppendLine("  • If T1 is hit: profit-protection stop — may trail below entry.");
            sb.AppendLine("  • If T1 is not hit: hard stop at structural invalidation above entry.");
        }
        sb.AppendLine($"  • Never widen the current working stop (${curStop:F4}). Only tighten or leave unchanged.");
        sb.AppendLine("  • A target price is never a valid protective stop unless price has already moved beyond it and the stop remains on the protective side of the current market.");
        sb.AppendLine();

        // ── Target rules ──────────────────────────────────────────
        sb.AppendLine("TARGET RULES:");
        sb.AppendLine("  • T1: nearest resistance (long) or support (short) with structural backing.");
        sb.AppendLine("  • T2: next major level — HOD retest, VWAP, measured move, or pre-market level.");
        sb.AppendLine("  • Use session_vp HVNs as T1/T2 ceilings. Use composite_vp.poc as runner magnet.");
        if (direction == "long")
        {
            sb.AppendLine($"  • T1 MUST be above entry (${pos.EntryPrice:F2}). T2 MUST be > T1.");
        }
        else
        {
            sb.AppendLine($"  • T1 MUST be below entry (${pos.EntryPrice:F2}). T2 MUST be < T1.");
        }
        sb.AppendLine("  • A target already recorded as hit is historical and MUST be returned unchanged.");
        sb.AppendLine();

        // ── Runner rules ──────────────────────────────────────────
        sb.AppendLine("RUNNER RULES (runner is NOT always warranted):");
        sb.AppendLine("  Runner is only justified when ALL of the following are true:");
        sb.AppendLine("    1. T1 has been hit (partial profit already taken)");
        sb.AppendLine("    2. Price structure is intact (higher lows for long / lower highs for short)");
        sb.AppendLine("    3. Stop is in profit-protection territory");
        sb.AppendLine("    4. Clear room exists to a major level beyond T2");
        sb.AppendLine("  If ANY condition is NOT met: runner=null and runner_justified=false.");
        if (direction == "long")
            sb.AppendLine("  If runner is set: it MUST be > T2.");
        else
            sb.AppendLine("  If runner is set: it MUST be < T2.");
        sb.AppendLine();

        // ── Setup-specific invalidation ───────────────────────────
        var entryType = pos.EntryType ?? "";

        if (direction == "short" && entryType == "overextension_fade")
        {
            sb.AppendLine("OVEREXTENSION FADE SHORT — check these invalidation conditions:");
            sb.AppendLine("  • Fresh high printed after entry → setup invalidated.");
            sb.AppendLine("    Stop = just above that new high (hard stop).");
            sb.AppendLine("  • VWAP reclaimed and held ≥3 consecutive bars → setup invalidated.");
            sb.AppendLine("    Tighten stop to just above VWAP. Note invalidation in rationale.");
            sb.AppendLine("  • Higher high with expanding volume → trend resuming, not reversing.");
            sb.AppendLine("    Lower confidence, tighten stop aggressively.");
            sb.AppendLine("  • No follow-through toward T1 after 4+ bars → move stop to breakeven.");
            sb.AppendLine("  • T1 hit → move stop to breakeven immediately.");
        }
        else if (direction == "short")
        {
            sb.AppendLine("SHORT POSITION CHECKS:");
            sb.AppendLine("  • VWAP reclaimed and held → thesis weakening, tighten stop.");
            sb.AppendLine("  • T1 hit → move stop to breakeven.");
        }
        else
        {
            sb.AppendLine("LONG POSITION CHECKS:");
            sb.AppendLine("  • Price lost VWAP and failed to reclaim → thesis weakening, tighten stop.");
            sb.AppendLine("  • T1 hit → move stop to breakeven.");
        }

        return sb.ToString();
    }

    // ── Level validation ──────────────────────────────────────────

    private static ReEvalSafetyPolicy.Decision ValidateLevels(
        ReEvalResult r,
        TraderStateRow pos,
        ReEvalMarketContext market)
    {
        var working = new ReEvalSafetyPolicy.Levels(
            pos.EffectiveStop,
            pos.EffectiveT1,
            pos.EffectiveT2,
            pos.EffectiveRunner);

        var input = new ReEvalSafetyPolicy.Input(
            pos.Position,
            pos.EntryPrice ?? 0m,
            market.ReferencePrice,
            pos.T1Hit,
            pos.T2Hit,
            working);

        var proposal = new ReEvalSafetyPolicy.Proposal(
            r.StopPrice,
            r.StopType,
            r.T1,
            r.T2,
            r.Runner,
            r.RunnerJustified == true);

        return ReEvalSafetyPolicy.Validate(input, proposal);
    }

    private async Task<ReEvalSignalContext?> LoadSignalContextAsync(Guid? signalId, CancellationToken ct)
    {
        if (!signalId.HasValue) return null;

        try
        {
            var rows = await _db.SelectAsync<ReEvalSignalContext>(
                "signal_events",
                $"?select=entry_low,entry_high&id=eq.{signalId.Value}&limit=1",
                ct);
            return rows.Count == 1 ? rows[0] : null;
        }
        catch (Exception ex)
        {
            // Entry-window context improves the re-evaluation but is not required
            // for the deterministic safety boundary.
            Console.WriteLine($"[reeval] signal context unavailable: {ex.Message}");
            return null;
        }
    }

    private static bool TryBuildMarketContext(
        string datasetJson,
        string direction,
        ReEvalLiveMarketSnapshot? live,
        out ReEvalMarketContext? context,
        out string error)
    {
        context = null;
        error = "";

        try
        {
            using var doc = JsonDocument.Parse(datasetJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("ts_asof_utc", out var asofEl) ||
                !asofEl.TryGetDateTime(out var datasetAsOf))
            {
                error = "dataset ts_asof_utc is missing";
                return false;
            }
            datasetAsOf = EnsureUtc(datasetAsOf);

            if (!root.TryGetProperty("intraday_bars", out var bars) ||
                bars.ValueKind != JsonValueKind.Array || bars.GetArrayLength() == 0)
            {
                error = "dataset has no intraday bars";
                return false;
            }

            var lastBar = bars[bars.GetArrayLength() - 1];
            if (!TryDecimal(lastBar, "o", out var open) ||
                !TryDecimal(lastBar, "h", out var high) ||
                !TryDecimal(lastBar, "l", out var low) ||
                !TryDecimal(lastBar, "c", out var close) || close <= 0m)
            {
                error = "latest completed bar is missing valid OHLC values";
                return false;
            }

            decimal? lastClose = null;
            if (root.TryGetProperty("reference_levels", out var refs) &&
                refs.TryGetProperty("last_close", out var closeEl) &&
                closeEl.ValueKind == JsonValueKind.Number)
                lastClose = closeEl.GetDecimal();
            lastClose ??= close;

            var bid = Positive(live?.Bid);
            var ask = Positive(live?.Ask);
            var lastTrade = Positive(live?.LastTrade);
            var quoteAt = live?.QuoteAtUtc is DateTime quoteTime
                ? EnsureUtc(quoteTime)
                : (DateTime?)null;
            var tradeAt = live?.LastTradeAtUtc is DateTime tradeTime
                ? EnsureUtc(tradeTime)
                : (DateTime?)null;

            var now = DateTime.UtcNow;
            var quoteFresh = quoteAt.HasValue && quoteAt.Value <= now.AddSeconds(5) &&
                             now - quoteAt.Value <= TimeSpan.FromSeconds(30);
            var tradeFresh = tradeAt.HasValue && tradeAt.Value <= now.AddSeconds(5) &&
                             now - tradeAt.Value <= TimeSpan.FromSeconds(60);

            decimal reference;
            DateTime referenceAt;
            string source;

            if (direction == "short" && quoteFresh && ask.HasValue)
            {
                reference = ask.Value;
                referenceAt = quoteAt!.Value;
                source = "live ask";
            }
            else if (direction == "long" && quoteFresh && bid.HasValue)
            {
                reference = bid.Value;
                referenceAt = quoteAt!.Value;
                source = "live bid";
            }
            else if (tradeFresh && lastTrade.HasValue)
            {
                reference = lastTrade.Value;
                referenceAt = tradeAt!.Value;
                source = "live last trade";
            }
            else
            {
                reference = lastClose.Value;
                referenceAt = datasetAsOf;
                source = "latest completed-minute close";
            }

            context = new ReEvalMarketContext(
                reference,
                referenceAt,
                source,
                bid,
                ask,
                quoteAt,
                lastTrade,
                tradeAt,
                datasetAsOf,
                open,
                high,
                low,
                close);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryDecimal(JsonElement parent, string name, out decimal value)
    {
        value = 0m;
        return parent.TryGetProperty(name, out var element) &&
               element.ValueKind == JsonValueKind.Number &&
               element.TryGetDecimal(out value);
    }

    private static decimal? Positive(decimal? value)
        => value.HasValue && value.Value > 0m ? value : null;

    private static DateTime EnsureUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc   => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _                  => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static DateTime FloorUtcMinute(DateTime value)
    {
        value = EnsureUtc(value);
        return new DateTime(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, DateTimeKind.Utc);
    }

    private static string FormatRange(decimal? low, decimal? high)
    {
        if (low.HasValue && high.HasValue) return $"${low:F2}–${high:F2}";
        return low.HasValue ? $"${low:F2}" : high.HasValue ? $"${high:F2}" : "unknown";
    }

    private static string FormatOptional(decimal? value)
        => value.HasValue ? $"${value.Value:F4}" : "unavailable";

    // ── LLM call ─────────────────────────────────────────────────

    private async Task<OpenAiCallResult> CallLlmAsync(
        string ticker,
        DateTime asOfUtc,
        string question,
        string datasetJson,
        CancellationToken ct)
    {
        const int maxAttempts = 3;
        Exception? last = null;

        OpenAiTelemetry.WriteDebugPayloadIfEnabled("position_reeval", ticker, asOfUtc, datasetJson);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var request = new
                {
                    model = _model,
                    store = false,
                    reasoning = new
                    {
                        effort = "medium"
                    },
                    text = new
                    {
                        format = new
                        {
                            type = "json_schema",
                            name = "position_reeval_v1",
                            strict = true,
                            schema = OpenAiJsonSchemas.ReEvalV1
                        }
                    },
                    input = new object[]
                    {
                        new {
                            role    = "system",
                            content = new object[]
                            {
                                new { type = "input_text", text = ReEvalSystemPrompt }
                            }
                        },
                        new {
                            role    = "user",
                            content = new object[]
                            {
                                new { type = "input_text", text = question },
                                new { type = "input_text", text = ReEvalOutputSchema },
                                new { type = "input_text", text = "DATASET_JSON:\n" + datasetJson }
                            }
                        }
                    }
                };

                var json = JsonSerializer.Serialize(request,
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
                    CallType = "position_reeval",
                    Ticker = ticker,
                    Model = _model,
                    ResponseId = responseId,
                    ServiceTier = serviceTier,
                    ReasoningEffort = "medium",
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

                return result;
            }
            catch (Exception ex) when (IsRetryable(ex))
            {
                last = ex;
                if (attempt >= maxAttempts) break;

                var delayMs = Math.Min(8000, 500 * (int)Math.Pow(2, attempt - 1));
                Console.WriteLine($"[reeval] LLM attempt {attempt} failed: {ex.Message} — retrying in {delayMs}ms");
                await Task.Delay(delayMs, ct);
            }
        }

        throw new Exception($"ReEval LLM failed after {maxAttempts} attempts", last);
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
        if (root.TryGetProperty("output_text", out var ot) && ot.ValueKind == JsonValueKind.String)
            return ot.GetString() ?? "";

        var sb = new StringBuilder();
        if (root.TryGetProperty("output", out var outArr) && outArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in outArr.EnumerateArray())
            {
                if (!item.TryGetProperty("content", out var content)) continue;
                foreach (var part in content.EnumerateArray())
                {
                    if (part.TryGetProperty("type",  out var t)   && t.GetString() == "output_text" &&
                        part.TryGetProperty("text",   out var txt) && txt.ValueKind == JsonValueKind.String)
                        sb.AppendLine(txt.GetString());
                }
            }
        }

        return sb.ToString().Trim();
    }

    // ── JSON parse ────────────────────────────────────────────────

    private static bool TryParseReEval(string raw, out ReEvalResult? result)
    {
        result = null;
        var text = raw?.Trim() ?? "";
        if (string.IsNullOrEmpty(text)) return false;

        // Strip markdown fences if present
        if (text.StartsWith("```"))
        {
            var start = text.IndexOf('{');
            var end   = text.LastIndexOf('}');
            if (start >= 0 && end > start)
                text = text[start..(end + 1)];
        }

        try
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            result = JsonSerializer.Deserialize<ReEvalResult>(text, opts);
            return result is not null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[reeval] JSON parse exception: {ex.Message}");
            return false;
        }
    }

    // ── Result model ──────────────────────────────────────────────

    private sealed record ReEvalMarketContext(
        decimal ReferencePrice,
        DateTime ReferenceAtUtc,
        string ReferenceSource,
        decimal? Bid,
        decimal? Ask,
        DateTime? QuoteAtUtc,
        decimal? LastTrade,
        DateTime? LastTradeAtUtc,
        DateTime DatasetAsOfUtc,
        decimal LastBarOpen,
        decimal LastBarHigh,
        decimal LastBarLow,
        decimal LastBarClose);

    private sealed class ReEvalSignalContext
    {
        [JsonPropertyName("entry_low")]  public decimal? EntryLow  { get; set; }
        [JsonPropertyName("entry_high")] public decimal? EntryHigh { get; set; }
    }

    private sealed class ReEvalResult
    {
        [JsonPropertyName("stop_price")]       public decimal? StopPrice       { get; set; }
        [JsonPropertyName("stop_type")]        public string?  StopType        { get; set; }
        [JsonPropertyName("t1")]               public decimal? T1              { get; set; }
        [JsonPropertyName("t2")]               public decimal? T2              { get; set; }
        [JsonPropertyName("runner")]           public decimal? Runner          { get; set; }
        [JsonPropertyName("runner_justified")] public bool?    RunnerJustified { get; set; }
        [JsonPropertyName("confidence")]       public decimal  Confidence      { get; set; }
        [JsonPropertyName("rationale")]        public string?  Rationale       { get; set; }
    }
}
