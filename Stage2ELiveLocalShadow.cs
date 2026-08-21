using System.Text.Json;
using get_assessment_no_graph.Llm;

namespace get_assessment_no_graph;

/// <summary>
/// Stage 2E.1 live local-model shadow capture.
///
/// Safety contract:
/// - disabled by default;
/// - never writes production DB tables;
/// - never calls TriggerEngine or emits a signal;
/// - never changes the authoritative GPT card, Stage 2B.4 decision, or Stage 2D ordering;
/// - at most one local inference is active process-wide; additional live evaluations are skipped;
/// - all local-model failures are contained inside the background shadow task.
///
/// The local model receives CompactMarketStateBuilder compact_v1 state and its stricter
/// executable-card schema. The resulting card is evaluated through the same Stage 2B.4
/// structural/quality layer and Stage 2D ordering, but is telemetry-only.
/// </summary>
public static class Stage2ELiveLocalShadow
{
    public const string Version = "stage2e1_live_local_shadow_v1";

    private const string LocalExecutableProposalGuidance = """
LOCAL AVA EXECUTABLE-PROPOSAL RULES:
- If verdict=TRADE, every scenario MUST provide numeric entry_low, entry_high, stop_price, and t1.
- entry_low <= entry_high.
- LONG geometry is STRICT: stop_price < entry_low <= entry_high < t1. Therefore t1 MUST be strictly greater than entry_high; equality is invalid.
- SHORT geometry is STRICT: stop_price > entry_high >= entry_low > t1. Therefore t1 MUST be strictly less than entry_low; equality is invalid.
- Before returning a TRADE scenario, verify its directional inequalities. If any inequality fails, remove that scenario rather than returning invalid geometry.
- t2 and runner are optional and may be null. If supplied, keep them strictly directionally beyond T1 and do not set them equal to another retained target.
- Scenario count is NOT a target. Prefer ONE well-supported executable scenario over multiple mediocre, speculative, or hedge-like alternatives.
- Return 2 or 3 scenarios only when each represents a genuinely distinct thesis independently supported by the current market state.
- Do not output both LONG and SHORT merely to cover both directions. Opposing scenarios require independent evidence for both.
- Near-duplicate scenarios with the same thesis and materially identical levels are not allowed.
- If no setup can satisfy complete executable geometry, return verdict=NO_TRADE and scenarios=[].
""";

    private const string LocalExecutableJsonInstruction = """
Return ONLY the JSON object required by the provided structured-output schema.
If verdict=NO_TRADE, scenarios must be [].
If verdict=TRADE, return 1-3 genuinely distinct scenarios, but prefer fewer scenarios when evidence is concentrated in one setup.
Every TRADE scenario must contain numeric entry_low, entry_high, stop_price, and t1 and must satisfy:
LONG: stop_price < entry_low <= entry_high < t1.
SHORT: stop_price > entry_high >= entry_low > t1.
T1 may NEVER equal the relevant entry boundary. t2 and runner may be null. No markdown or extra text.
""";

    private static readonly SemaphoreSlim TelemetryGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static int _inFlight;

    public static bool Enabled => ReadBool("AVA_LOCAL_SHADOW_ENABLED", false);

    public static void TryQueue(
        string ticker,
        DateTime asOfUtc,
        Guid jobId,
        string? openAiResponseId,
        string openAiModel,
        string executionQuestion,
        string fullDatasetJson,
        ExecutionCardJsonV1 gptCard,
        AvaScenarioDecisionResult? gptStage2B4,
        Stage2DQualitySelectionResult? gptStage2D)
    {
        if (!Enabled)
            return;

        var acquired = false;
        try
        {
            var gptSummary = SummarizeDecision(gptCard, gptStage2B4, gptStage2D);

            if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
            {
                SafeBackgroundTelemetry(new
                {
                    stage = Version,
                    event_type = "busy_skipped",
                    recorded_utc = DateTime.UtcNow,
                    ticker,
                    asof_utc = asOfUtc.ToUniversalTime(),
                    job_id = jobId,
                    openai_response_id = openAiResponseId,
                    openai_model = openAiModel,
                    local_model = Stage2ELiveLocalShadowConfig.Model,
                    reason = "A local shadow inference is already active. Stage 2E.1 does not queue stale work.",
                    gpt = gptSummary
                });

                Console.WriteLine(
                    $"{DateTime.UtcNow:o} STAGE2E1_LOCAL_SHADOW_BUSY_SKIP {ticker} " +
                    $"asof={asOfUtc:o} model={Stage2ELiveLocalShadowConfig.Model}");
                return;
            }

            acquired = true;

            // Deliberately detached from the production request cancellation token.
            // The Ollama HttpClient timeout bounds this task. No exception may escape.
            _ = Task.Run(async () =>
            {
                try
                {
                    await ExecuteAsync(
                        ticker,
                        asOfUtc,
                        jobId,
                        openAiResponseId,
                        openAiModel,
                        executionQuestion,
                        fullDatasetJson,
                        gptSummary);
                }
                catch (Exception ex)
                {
                    await TryWriteTelemetryAsync(new
                    {
                        stage = Version,
                        event_type = "shadow_error",
                        recorded_utc = DateTime.UtcNow,
                        ticker,
                        asof_utc = asOfUtc.ToUniversalTime(),
                        job_id = jobId,
                        openai_response_id = openAiResponseId,
                        openai_model = openAiModel,
                        local_model = Stage2ELiveLocalShadowConfig.Model,
                        error_type = ex.GetType().Name,
                        error = ex.Message,
                        gpt = gptSummary
                    });

                    Console.WriteLine(
                        $"{DateTime.UtcNow:o} STAGE2E1_LOCAL_SHADOW_ERROR {ticker} " +
                        $"asof={asOfUtc:o} err={ex.Message}");
                }
                finally
                {
                    Volatile.Write(ref _inFlight, 0);
                }
            });

            acquired = false; // background task owns release
            Console.WriteLine(
                $"{DateTime.UtcNow:o} STAGE2E1_LOCAL_SHADOW_QUEUED {ticker} " +
                $"asof={asOfUtc:o} model={Stage2ELiveLocalShadowConfig.Model}");
        }
        catch (Exception ex)
        {
            // Stage 2E.1 is observational. Even scheduling/summary bugs are fail-silent
            // with respect to production execution.
            Console.WriteLine(
                $"{DateTime.UtcNow:o} STAGE2E1_LOCAL_SHADOW_SCHEDULE_ERROR {ticker} " +
                $"asof={asOfUtc:o} err={ex.Message}");
        }
        finally
        {
            if (acquired)
                Volatile.Write(ref _inFlight, 0);
        }
    }

    private static async Task ExecuteAsync(
        string ticker,
        DateTime asOfUtc,
        Guid jobId,
        string? openAiResponseId,
        string openAiModel,
        string executionQuestion,
        string fullDatasetJson,
        Stage2EDecisionSummary gptSummary)
    {
        var compactDatasetJson = CompactMarketStateBuilder.Build(fullDatasetJson);
        var localSystemPrompt = executionQuestion + "\n\n" + LocalExecutableProposalGuidance;
        var localUserPrompt = LocalExecutableJsonInstruction + "\n\nDATASET_JSON:\n" + compactDatasetJson;
        var approxInputTokens = EstimateInputTokens(localSystemPrompt.Length + localUserPrompt.Length);
        var contextTokens = Stage2ELiveLocalShadowConfig.ContextTokens;

        if (approxInputTokens > (int)(contextTokens * 0.85))
        {
            await TryWriteTelemetryAsync(new
            {
                stage = Version,
                event_type = "context_skipped",
                recorded_utc = DateTime.UtcNow,
                ticker,
                asof_utc = asOfUtc.ToUniversalTime(),
                job_id = jobId,
                openai_response_id = openAiResponseId,
                openai_model = openAiModel,
                local_model = Stage2ELiveLocalShadowConfig.Model,
                full_dataset_chars = fullDatasetJson.Length,
                compact_dataset_chars = compactDatasetJson.Length,
                approximate_local_input_tokens = approxInputTokens,
                local_context_tokens = contextTokens,
                reason = "Estimated local input exceeds 85% of configured context.",
                gpt = gptSummary
            });

            Console.WriteLine(
                $"{DateTime.UtcNow:o} STAGE2E1_LOCAL_SHADOW_CONTEXT_SKIP {ticker} " +
                $"asof={asOfUtc:o} approx_tokens={approxInputTokens} context={contextTokens}");
            return;
        }

        using var localClient = new OllamaLlmClient(
            Stage2ELiveLocalShadowConfig.BaseUrl,
            TimeSpan.FromSeconds(Stage2ELiveLocalShadowConfig.TimeoutSeconds),
            contextTokens: contextTokens);

        var call = await localClient.CompleteStructuredAsync(
            Stage2ELiveLocalShadowConfig.Model,
            localSystemPrompt,
            localUserPrompt,
            OpenAiJsonSchemas.LocalExecutableCardV1,
            disableThinking: true,
            CancellationToken.None);

        var parseOk = ProduceCardWorker.TryParseExecutionCardJson(
            call.Content,
            out var localCard,
            out var parseError);

        AvaScenarioDecisionResult? localStage2B4 = null;
        Stage2DQualitySelectionResult? localStage2D = null;
        string? decisionError = null;

        if (parseOk && localCard is not null)
        {
            try
            {
                localStage2B4 = AvaScenarioDecisionLayer.Evaluate(localCard, compactDatasetJson);
                localStage2D = Stage2DQualitySelector.Select(localStage2B4);
            }
            catch (Exception ex)
            {
                decisionError = ex.Message;
            }
        }

        var localSummary = SummarizeDecision(localCard, localStage2B4, localStage2D);
        var comparison = Compare(gptSummary, localSummary);

        await TryWriteTelemetryAsync(new
        {
            stage = Version,
            event_type = "completed",
            recorded_utc = DateTime.UtcNow,
            ticker,
            asof_utc = asOfUtc.ToUniversalTime(),
            job_id = jobId,
            openai_response_id = openAiResponseId,
            openai_model = openAiModel,
            local_provider = call.Provider,
            local_model = call.Model,
            full_dataset_chars = fullDatasetJson.Length,
            compact_dataset_chars = compactDatasetJson.Length,
            compact_reduction_pct = fullDatasetJson.Length == 0
                ? 0m
                : Math.Round((1m - (decimal)compactDatasetJson.Length / fullDatasetJson.Length) * 100m, 1),
            approximate_local_input_tokens = approxInputTokens,
            local_context_tokens = contextTokens,
            local_elapsed_ms = (long)call.Elapsed.TotalMilliseconds,
            local_prompt_tokens = call.PromptEvalCount,
            local_output_tokens = call.EvalCount,
            local_parse_success = parseOk && localCard is not null,
            local_parse_error = parseError,
            local_decision_error = decisionError,
            gpt = gptSummary,
            local = localSummary,
            comparison,
            local_raw_json = call.Content
        });

        Console.WriteLine(
            $"{DateTime.UtcNow:o} STAGE2E1_LOCAL_SHADOW_COMPLETE {ticker} " +
            $"asof={asOfUtc:o} model={call.Model} elapsed_ms={(long)call.Elapsed.TotalMilliseconds} " +
            $"raw_match={Fmt(comparison.RawVerdictMatch)} structural_match={Fmt(comparison.StructuralVerdictMatch)} " +
            $"direction_match={Fmt(comparison.SelectedDirectionMatch)} setup_match={Fmt(comparison.SelectedSetupMatch)}");
    }

    private static Stage2EDecisionSummary SummarizeDecision(
        ExecutionCardJsonV1? rawCard,
        AvaScenarioDecisionResult? decision,
        Stage2DQualitySelectionResult? selection)
    {
        var selected = selection?.OrderedExecutableCard.Scenarios.FirstOrDefault()
                       ?? decision?.Structural.NormalizedCard.Scenarios.OrderBy(s => s.ScenarioRank).FirstOrDefault();

        string? selectedTier = null;
        if (selected is not null)
        {
            selectedTier = selection?.Scenarios
                .FirstOrDefault(s => s.ScenarioRank == selected.ScenarioRank)?.SelectionTier
                ?? decision?.Quality.Scenarios
                    .FirstOrDefault(s => s.ScenarioRank == selected.ScenarioRank)?.SelectionTier;
        }

        return new Stage2EDecisionSummary(
            RawVerdict: rawCard?.Verdict,
            RawScenarioCount: rawCard?.Scenarios?.Count ?? 0,
            StructuralVerdict: decision?.Structural.EffectiveVerdict,
            StructurallyValidScenarioCount: decision?.Structural.StructurallyValidScenarioCount,
            PreferredScenarioCount: decision?.Quality.PreferredScenarioCount,
            SecondaryScenarioCount: decision?.Quality.SecondaryScenarioCount,
            SelectedScenarioRank: selected?.ScenarioRank,
            SelectedDirection: selected?.Direction,
            SelectedSetup: selected?.EntryType,
            SelectedTier: selectedTier);
    }

    private static Stage2EComparison Compare(Stage2EDecisionSummary gpt, Stage2EDecisionSummary local)
        => new(
            RawVerdictMatch: CompareText(gpt.RawVerdict, local.RawVerdict),
            StructuralVerdictMatch: CompareText(gpt.StructuralVerdict, local.StructuralVerdict),
            SelectedDirectionMatch: CompareText(gpt.SelectedDirection, local.SelectedDirection),
            SelectedSetupMatch: CompareText(gpt.SelectedSetup, local.SelectedSetup),
            SelectedTierMatch: CompareText(gpt.SelectedTier, local.SelectedTier));

    private static bool? CompareText(string? a, string? b)
    {
        if (a is null || b is null) return null;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static string Fmt(bool? value) => value.HasValue ? value.Value.ToString() : "n/a";

    private static int EstimateInputTokens(int chars)
        => Math.Max(1, (int)Math.Ceiling(chars / 4.0));

    private static void SafeBackgroundTelemetry(object row)
    {
        _ = Task.Run(async () =>
        {
            try { await TryWriteTelemetryAsync(row); }
            catch { /* telemetry is best-effort */ }
        });
    }

    private static async Task TryWriteTelemetryAsync(object row)
    {
        if (!Stage2ELiveLocalShadowConfig.TelemetryEnabled)
            return;

        try
        {
            var dir = Path.GetFullPath(Stage2ELiveLocalShadowConfig.TelemetryDirectory);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"stage2e1_live_local_shadow_{DateTime.UtcNow:yyyyMMdd}.jsonl");
            var line = JsonSerializer.Serialize(row, JsonOptions) + Environment.NewLine;

            await TelemetryGate.WaitAsync();
            try
            {
                await File.AppendAllTextAsync(path, line, CancellationToken.None);
            }
            finally
            {
                TelemetryGate.Release();
            }
        }
        catch
        {
            // Stage 2E.1 telemetry can never affect the production signal path.
        }
    }

    private static bool ReadBool(string name, bool fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return raw.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => fallback
        };
    }
}

public static class Stage2ELiveLocalShadowConfig
{
    public static string BaseUrl =>
        Environment.GetEnvironmentVariable("AVA_LOCAL_LLM_BASE_URL")
        ?? Environment.GetEnvironmentVariable("LOCAL_LLM_BASE_URL")
        ?? "http://localhost:11434";

    public static string Model =>
        Environment.GetEnvironmentVariable("AVA_LOCAL_LLM_MODEL")
        ?? Environment.GetEnvironmentVariable("LOCAL_LLM_MODEL")
        ?? "gpt-oss:20b";

    public static int TimeoutSeconds =>
        ReadInt(
            Environment.GetEnvironmentVariable("AVA_LOCAL_LLM_TIMEOUT_SECONDS")
            ?? Environment.GetEnvironmentVariable("LOCAL_LLM_TIMEOUT_SECONDS"),
            fallback: 600,
            min: 30,
            max: 3600);

    public static int ContextTokens =>
        ReadInt(
            Environment.GetEnvironmentVariable("AVA_LOCAL_LLM_CONTEXT_TOKENS")
            ?? Environment.GetEnvironmentVariable("LOCAL_LLM_CONTEXT_TOKENS"),
            fallback: 32768,
            min: 4096,
            max: 131072);

    public static bool TelemetryEnabled
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("AVA_LOCAL_SHADOW_TELEMETRY");
            return string.IsNullOrWhiteSpace(raw) ||
                   !(string.Equals(raw, "0", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(raw, "off", StringComparison.OrdinalIgnoreCase));
        }
    }

    public static string TelemetryDirectory =>
        Environment.GetEnvironmentVariable("AVA_LOCAL_SHADOW_TELEMETRY_DIR")
        ?? "stage2e_local_shadow";

    private static int ReadInt(string? raw, int fallback, int min, int max)
    {
        if (!int.TryParse(raw, out var value))
            return fallback;
        return Math.Clamp(value, min, max);
    }
}

public sealed record Stage2EDecisionSummary(
    string? RawVerdict,
    int RawScenarioCount,
    string? StructuralVerdict,
    int? StructurallyValidScenarioCount,
    int? PreferredScenarioCount,
    int? SecondaryScenarioCount,
    int? SelectedScenarioRank,
    string? SelectedDirection,
    string? SelectedSetup,
    string? SelectedTier);

public sealed record Stage2EComparison(
    bool? RawVerdictMatch,
    bool? StructuralVerdictMatch,
    bool? SelectedDirectionMatch,
    bool? SelectedSetupMatch,
    bool? SelectedTierMatch);
