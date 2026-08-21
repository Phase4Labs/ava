using System.Globalization;
using System.Text;
using System.Text.Json;
using get_assessment_no_graph.Llm;

namespace get_assessment_no_graph;

/// <summary>
/// Historical dual-model shadow runner.
///
/// It fetches Massive historical bars up to a caller-selected ET analysis time,
/// builds the exact Stage-1 execution-card payload without look-ahead, then runs:
///   1) GPT-5.2 through the existing OpenAI card path (analysis-only, no DB signal writes)
///   2) a local Ollama model with the same execution prompt/schema
///
/// Results are written to a local JSONL comparison file. Neither model can emit
/// triggers/signals or modify execution-card production tables from this mode.
/// </summary>
public static class HistoricalShadowRunner
{
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
    private static readonly TimeZoneInfo EasternTz =
        TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");

    public static async Task<int> RunAsync(
        string[] args,
        string massiveApiKey,
        string supabaseUrl,
        string supabaseServiceKey,
        string openAiApiKey,
        string openAiModel,
        string executionQuestion,
        CancellationToken ct = default)
    {
        // Stage 2D quality-selection self-test rides through the already-safe
        // historical dispatcher so Program.cs does not need another fragile CLI hook.
        // Pure in-memory; exits before DB, Massive, OpenAI, Ollama, or live services.
        if (HasFlag(args, "--stage2d-quality-selftest"))
            return Stage2DQualitySelectionSelfTest.Run();

        // Stage 2C analogue-index build rides through the already-safe historical
        // command path so Program.cs does not need another offline dispatch hook.
        // This branch exits before DB, Massive, OpenAI, or Ollama initialization.
        if (HasFlag(args, "--analogue-build"))
            return await HistoricalAnalogueCli.RunAsync(args, ct);

        if (HasFlag(args, "--candidate-evidence-calibrate"))
        {
            if (HasFlag(args, "--candidate-evidence-calibrate-help"))
            {
                Stage2C6EvidenceCalibrationRunner.PrintHelp();
                return 0;
            }

            return await Stage2C6EvidenceCalibrationRunner.RunAsync(args, ct);
        }

        if (HasFlag(args, "--candidate-evidence-holdout"))
        {
            if (HasFlag(args, "--candidate-evidence-holdout-help"))
            {
                Stage2C7TemporalHoldoutRunner.PrintHelp();
                return 0;
            }

            return await Stage2C7TemporalHoldoutRunner.RunAsync(args, ct);
        }

        if (HasFlag(args, "--candidate-evidence-rank-sim"))
        {
            if (HasFlag(args, "--candidate-evidence-rank-sim-help"))
            {
                Stage2C8EvidenceRankingSimulationRunner.PrintHelp();
                return 0;
            }

            return await Stage2C8EvidenceRankingSimulationRunner.RunAsync(args, ct);
        }

        if (HasFlag(args, "--stage2c-benchmark"))
        {
            if (HasFlag(args, "--stage2c-benchmark-help"))
            {
                Stage2CBenchmarkRunner.PrintHelp();
                return 0;
            }

            return await Stage2CBenchmarkRunner.RunAsync(
                args,
                massiveApiKey,
                supabaseUrl,
                supabaseServiceKey,
                openAiApiKey,
                openAiModel,
                executionQuestion,
                ct);
        }

        if (HasFlag(args, "--historical-shadow-help") || HasFlag(args, "--help"))
        {
            PrintHelp();
            return 0;
        }

        HistoricalShadowOptions options;
        try
        {
            options = ParseOptions(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Historical shadow configuration error: {ex.Message}");
            Console.Error.WriteLine();
            PrintHelp();
            return 2;
        }

        var startEt = options.StartEt ?? GetDefaultStartEt();
        var endEt = options.EndEt ?? startEt.AddMinutes((options.Steps - 1) * options.StepMinutes);

        try
        {
            ValidateWindow(startEt, endEt, options.StepMinutes);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Historical shadow time-window error: {ex.Message}");
            return 2;
        }

        var startUtc = ToUtc(startEt);
        var endUtc = ToUtc(endEt);
        var firstCapUtc = LastClosedMinuteStartUtc(startUtc);
        var finalCapUtc = LastClosedMinuteStartUtc(endUtc);

        var outputPath = ResolveOutputPath(options, startEt);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        Console.WriteLine("AVA historical dual-model shadow replay");
        Console.WriteLine($"Ticker          : {options.Ticker}");
        Console.WriteLine($"Start ET        : {startEt:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"End ET          : {endEt:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"Step            : {options.StepMinutes} minute(s)");
        Console.WriteLine($"First bar cap   : {FromUtc(firstCapUtc):yyyy-MM-dd HH:mm:ss} ET");
        Console.WriteLine($"Final bar cap   : {FromUtc(finalCapUtc):yyyy-MM-dd HH:mm:ss} ET");
        Console.WriteLine($"Cloud           : {(options.RunCloud ? openAiModel : "disabled")}");
        Console.WriteLine($"Local           : {(options.RunLocal ? options.LocalModel : "disabled")}");
        Console.WriteLine($"Local context   : {options.LocalContextTokens:N0} tokens");
        Console.WriteLine($"Local repair    : {(options.LocalStructuralRepair ? "one validator-guided retry when zero scenarios are structurally valid" : "disabled")}");
        Console.WriteLine($"Stage 2A        : {(options.Stage2A ? "compact payload + semantic gate" : "disabled")}");
        Console.WriteLine($"Results         : {outputPath}");
        Console.WriteLine();
        Console.WriteLine("Safety: this mode writes historical minute bars/features only; it does NOT write execution cards, scenarios, triggers, or signals.");
        Console.WriteLine();

        using var massive = new PolygonClient(massiveApiKey);
        using var db = new SupabaseRestClient(supabaseUrl, supabaseServiceKey);
        var ingest = new PolygonIngestionService(massive, db);
        var payloadBuilder = new PayloadBuilder(db, massive);
        var cloudWorker = new ProduceCardWorker(db, openAiApiKey, openAiModel, executionQuestion, ingest);

        using OllamaLlmClient? localClient = options.RunLocal
            ? new OllamaLlmClient(
                options.LocalBaseUrl,
                TimeSpan.FromSeconds(options.LocalTimeoutSeconds),
                contextTokens: options.LocalContextTokens)
            : null;

        HistoricalAnalogueIndex? analogueIndex = null;
        if (!string.IsNullOrWhiteSpace(options.AnalogueIndexPath))
        {
            analogueIndex = HistoricalAnalogueIndex.Load(options.AnalogueIndexPath);
            Console.WriteLine($"Analogue index  : {Path.GetFullPath(options.AnalogueIndexPath)} ({analogueIndex.RecordCount:N0} records)");
            Console.WriteLine($"Analogue top N  : {options.AnalogueTopN}");
            Console.WriteLine("Analogue mode   : LEGACY prompt injection (research comparison only)");
        }

        HistoricalAnalogueIndex? candidateEvidenceIndex = null;
        if (!string.IsNullOrWhiteSpace(options.CandidateEvidenceIndexPath))
        {
            candidateEvidenceIndex = HistoricalAnalogueIndex.Load(options.CandidateEvidenceIndexPath);
            Console.WriteLine($"Candidate evidence index: {Path.GetFullPath(options.CandidateEvidenceIndexPath)} ({candidateEvidenceIndex.RecordCount:N0} records)");
            Console.WriteLine($"Candidate evidence top N: {options.CandidateEvidenceTopN}");
            Console.WriteLine("Candidate evidence mode : SIDECAR ONLY; never included in the LLM prompt and never changes the decision");
        }

        // Fetch/compute the complete historical window once. Features at each earlier
        // timestamp remain look-ahead safe because SessionFeatureCalculator is causal.
        Console.WriteLine($"Preloading Massive history through {FromUtc(finalCapUtc):HH:mm} ET...");
        var ingestedThrough = await ingest.IngestTodayAndEnsureFeaturesUpToAsync(
            options.Ticker,
            finalCapUtc,
            finalCapUtc,
            ct);

        if (!ingestedThrough.HasValue)
        {
            Console.Error.WriteLine("No regular-session minute bars were returned for this ticker/date. The date may be a market holiday, the ticker may be invalid, or the Massive plan may not expose that history.");
            return 3;
        }

        // PostgREST/System.Text.Json can return timestamptz values with different
        // DateTime Kind metadata depending on the serialized shape/runtime. ts_utc
        // is semantically UTC, so normalize it before comparing instants.
        var ingestedThroughUtc = NormalizeUtcTimestamp(ingestedThrough.Value);

        if (ingestedThroughUtc < firstCapUtc)
        {
            Console.Error.WriteLine($"Historical data ends at {FromUtc(ingestedThroughUtc):yyyy-MM-dd HH:mm:ss} ET, before requested start.");
            return 3;
        }

        Console.WriteLine($"History ready through {FromUtc(ingestedThroughUtc):HH:mm:ss} ET.");
        Console.WriteLine();

        var runId = Guid.NewGuid().ToString("n");
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false
        };

        var analysisEt = startEt;
        var sequence = 0;
        while (analysisEt <= endEt)
        {
            ct.ThrowIfCancellationRequested();
            sequence++;

            var analysisUtc = ToUtc(analysisEt);
            var capUtc = LastClosedMinuteStartUtc(analysisUtc);
            Console.WriteLine($"[{sequence}] {options.Ticker} analysis={analysisEt:yyyy-MM-dd HH:mm} ET cap={FromUtc(capUtc):HH:mm} ET");

            string datasetJson;
            try
            {
                datasetJson = await payloadBuilder.BuildDatasetJsonUpToAsync(
                    options.Ticker,
                    capUtc,
                    capUtc,
                    ct: ct,
                    historicalAsOf: true);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  DATASET FAIL: {ex.Message}");
                await AppendResultAsync(outputPath, new
                {
                    run_id = runId,
                    sequence,
                    ticker = options.Ticker,
                    analysis_time_et = analysisEt,
                    bar_cap_utc = capUtc,
                    status = "dataset_error",
                    error = ex.Message
                }, jsonOptions, ct);

                if (options.StopOnError) return 4;
                analysisEt = analysisEt.AddMinutes(options.StepMinutes);
                continue;
            }

            using var payloadDoc = JsonDocument.Parse(datasetJson);
            var payloadAsofUtc = payloadDoc.RootElement.GetProperty("ts_asof_utc").GetDateTime().ToUniversalTime();
            var barsElapsed = payloadDoc.RootElement.TryGetProperty("bars_elapsed", out var be) ? be.GetInt32() : 0;

            var compactDatasetJson = options.Stage2A
                ? CompactMarketStateBuilder.Build(datasetJson)
                : datasetJson;

            var fullApproxTokens = EstimateInputTokens(
                executionQuestion.Length + ProduceCardWorker.StrictJsonInstruction.Length + datasetJson.Length);
            var compactApproxTokens = EstimateInputTokens(
                executionQuestion.Length + ProduceCardWorker.StrictJsonInstruction.Length + compactDatasetJson.Length);
            var payloadReductionPct = datasetJson.Length > 0
                ? Math.Round((1m - (decimal)compactDatasetJson.Length / datasetJson.Length) * 100m, 1)
                : 0m;

            if (options.Stage2A)
            {
                Console.WriteLine($"  FULL payload   : bars={barsElapsed}, chars={datasetJson.Length:N0}, approx_input_tokens={fullApproxTokens:N0}");
                Console.WriteLine($"  COMPACT payload: chars={compactDatasetJson.Length:N0}, approx_input_tokens={compactApproxTokens:N0}, dataset_reduction={payloadReductionPct:0.0}%");
            }
            else
            {
                Console.WriteLine($"  Dataset: bars={barsElapsed}, chars={datasetJson.Length:N0}, approx_input_tokens={fullApproxTokens:N0}");
            }

            ExecutionCardAnalysisResult? cloud = null;
            ExecutionCardAnalysisResult? cloudCompact = null;
            ShadowModelResult? local = null;
            ShadowModelResult? localInitial = null;
            ShadowModelResult? localRepair = null;
            AvaScenarioDecisionResult? localInitialDecision = null;

            if (options.RunCloud)
            {
                try
                {
                    Console.WriteLine($"  GPT FULL: calling {openAiModel}...");
                    cloud = await cloudWorker.AnalyzeOnlyAsync(
                        options.Ticker,
                        payloadAsofUtc,
                        datasetJson,
                        callType: options.Stage2A ? "historical_shadow_cloud_full" : "historical_shadow_cloud",
                        ct: ct);
                    Console.WriteLine($"  GPT FULL: {(cloud.ParseSuccess ? "OK" : "INVALID")} {cloud.ElapsedMs / 1000.0:0.00}s verdict={cloud.Card?.Verdict ?? "?"} scenarios={cloud.Card?.Scenarios?.Count ?? 0}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  GPT FULL FAIL: {ex.Message}");
                    cloud = new ExecutionCardAnalysisResult(
                        "openai", openAiModel, null, "", null, false, ex.Message, 0, null, null);
                    if (options.StopOnError) return 5;
                }

                if (options.Stage2A)
                {
                    try
                    {
                        Console.WriteLine($"  GPT COMPACT: calling {openAiModel}...");
                        cloudCompact = await cloudWorker.AnalyzeOnlyAsync(
                            options.Ticker,
                            payloadAsofUtc,
                            compactDatasetJson,
                            callType: "historical_shadow_cloud_compact_v1",
                            ct: ct);
                        Console.WriteLine($"  GPT COMPACT: {(cloudCompact.ParseSuccess ? "OK" : "INVALID")} {cloudCompact.ElapsedMs / 1000.0:0.00}s verdict={cloudCompact.Card?.Verdict ?? "?"} scenarios={cloudCompact.Card?.Scenarios?.Count ?? 0}");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"  GPT COMPACT FAIL: {ex.Message}");
                        cloudCompact = new ExecutionCardAnalysisResult(
                            "openai", openAiModel, null, "", null, false, ex.Message, 0, null, null);
                        if (options.StopOnError) return 5;
                    }
                }
            }

            var localDatasetJson = options.Stage2A ? compactDatasetJson : datasetJson;
            HistoricalAnalogueContext? analogueContext = null;
            if (analogueIndex is not null)
            {
                if (!options.Stage2A)
                {
                    Console.WriteLine("  ANALOGUES SKIP: --analogue-index requires --stage2a compact payload mode.");
                }
                else
                {
                    analogueContext = analogueIndex.Query(compactDatasetJson, options.AnalogueTopN);
                    localDatasetJson = HistoricalAnalogueIndex.AttachContext(compactDatasetJson, analogueContext);
                    Console.WriteLine(
                        $"  ANALOGUES: returned={analogueContext.ReturnedAnalogues} " +
                        $"eligible_prior_sessions={analogueContext.EligiblePriorSessionRecords} " +
                        $"avg_distance={analogueContext.AverageDistance?.ToString("0.000") ?? "n/a"} " +
                        $"setup_groups={analogueContext.SetupOutcomes.Count}");
                }
            }
            var localSystemPrompt = executionQuestion + "\n\n" + LocalExecutableProposalGuidance + (analogueContext is null
                ? ""
                : "\n\nHISTORICAL ANALOGUE GUIDANCE:\nThe dataset contains historical_analogue_context built only from completed prior sessions. Treat it as empirical evidence, not ground truth. Use sample counts and realized-R outcomes to discipline setup selection and probabilities. Historical setups are evidence for selectivity, not a requirement to trade. Prefer no trade or one strong setup when the evidence does not independently support multiple scenarios. Do not copy historical price levels or force a trade.");
            var localApproxTokens = EstimateInputTokens(
                localSystemPrompt.Length + LocalExecutableJsonInstruction.Length + localDatasetJson.Length);

            if (options.RunLocal && localClient is not null)
            {
                if (localApproxTokens > (int)(options.LocalContextTokens * 0.85))
                {
                    var reason = $"Estimated input {localApproxTokens:N0} tokens exceeds 85% of configured local context {options.LocalContextTokens:N0}; local call skipped to avoid silent truncation.";
                    Console.WriteLine($"  LOCAL SKIP: {reason}");
                    local = ShadowModelResult.Skipped(options.LocalModel, reason);
                }
                else
                {
                    try
                    {
                        Console.WriteLine($"  LOCAL {(options.Stage2A ? "COMPACT" : "FULL")}: calling {options.LocalModel}...");
                        var localCall = await localClient.CompleteStructuredAsync(
                            options.LocalModel,
                            localSystemPrompt,
                            LocalExecutableJsonInstruction + "\n\nDATASET_JSON:\n" + localDatasetJson,
                            OpenAiJsonSchemas.LocalExecutableCardV1,
                            disableThinking: true,
                            ct);

                        var localParseOk = ProduceCardWorker.TryParseExecutionCardJson(
                            localCall.Content,
                            out var localCard,
                            out var localParseError);

                        local = new ShadowModelResult(
                            Provider: localCall.Provider,
                            Model: localCall.Model,
                            RawJson: localCall.Content,
                            Card: localCard,
                            ParseSuccess: localParseOk && localCard is not null,
                            ParseError: localParseError,
                            ElapsedMs: (long)localCall.Elapsed.TotalMilliseconds,
                            PromptTokens: localCall.PromptEvalCount,
                            OutputTokens: localCall.EvalCount,
                            Status: localParseOk ? "ok" : "invalid");
                        localInitial = local;

                        Console.WriteLine($"  LOCAL {(options.Stage2A ? "COMPACT" : "FULL")}: {(local.ParseSuccess ? "OK" : "INVALID")} {local.ElapsedMs / 1000.0:0.00}s verdict={local.Card?.Verdict ?? "?"} scenarios={local.Card?.Scenarios?.Count ?? 0}");

                        if (options.LocalStructuralRepair && local.ParseSuccess && local.Card is not null)
                        {
                            localInitialDecision = AvaScenarioDecisionLayer.Evaluate(local.Card, localDatasetJson);
                            var needsRepair =
                                string.Equals(local.Card.Verdict, "TRADE", StringComparison.OrdinalIgnoreCase) &&
                                localInitialDecision.Structural.RawScenarioCount > 0 &&
                                localInitialDecision.Structural.StructurallyValidScenarioCount == 0;

                            if (needsRepair)
                            {
                                var repairInstruction = BuildLocalStructuralRepairInstruction(
                                    local, localInitialDecision, localDatasetJson);
                                var repairApproxTokens = EstimateInputTokens(localSystemPrompt.Length + repairInstruction.Length);

                                if (repairApproxTokens > (int)(options.LocalContextTokens * 0.85))
                                {
                                    Console.WriteLine($"  LOCAL REPAIR SKIP: estimated input {repairApproxTokens:N0} tokens exceeds 85% of configured local context {options.LocalContextTokens:N0}.");
                                }
                                else
                                {
                                    Console.WriteLine("  LOCAL REPAIR: zero structurally valid TRADE scenarios; sending exact Stage 2B.4 errors for one repair attempt...");
                                    try
                                    {
                                        var repairCall = await localClient.CompleteStructuredAsync(
                                            options.LocalModel,
                                            localSystemPrompt + "\n\nVALIDATOR-GUIDED REPAIR MODE: Correct only the structural defects reported by Stage 2B.4. Do not game the validator by making arbitrary tiny price changes. Return at most one TRADE scenario; if a well-supported correction cannot be grounded in the dataset, return NO_TRADE.",
                                            repairInstruction,
                                            OpenAiJsonSchemas.LocalExecutableCardV1,
                                            disableThinking: true,
                                            ct);

                                        var repairParseOk = ProduceCardWorker.TryParseExecutionCardJson(
                                            repairCall.Content,
                                            out var repairCard,
                                            out var repairParseError);

                                        localRepair = new ShadowModelResult(
                                            Provider: repairCall.Provider,
                                            Model: repairCall.Model,
                                            RawJson: repairCall.Content,
                                            Card: repairCard,
                                            ParseSuccess: repairParseOk && repairCard is not null,
                                            ParseError: repairParseError,
                                            ElapsedMs: (long)repairCall.Elapsed.TotalMilliseconds,
                                            PromptTokens: repairCall.PromptEvalCount,
                                            OutputTokens: repairCall.EvalCount,
                                            Status: repairParseOk ? "repair_ok" : "repair_invalid");

                                        if (localRepair.ParseSuccess)
                                            local = localRepair;

                                        var repairedDecision = localRepair.ParseSuccess && localRepair.Card is not null
                                            ? AvaScenarioDecisionLayer.Evaluate(localRepair.Card, localDatasetJson)
                                            : null;

                                        Console.WriteLine(
                                            $"  LOCAL REPAIR: {(localRepair.ParseSuccess ? "OK" : "INVALID")} " +
                                            $"{localRepair.ElapsedMs / 1000.0:0.00}s verdict={localRepair.Card?.Verdict ?? "?"} " +
                                            $"scenarios={localRepair.Card?.Scenarios?.Count ?? 0} " +
                                            $"structural={repairedDecision?.Structural.EffectiveVerdict ?? "n/a"} " +
                                            $"valid={repairedDecision?.Structural.StructurallyValidScenarioCount ?? 0}/{repairedDecision?.Structural.RawScenarioCount ?? 0}");
                                    }
                                    catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                                    {
                                        Console.Error.WriteLine($"  LOCAL REPAIR TIMEOUT: repair call exceeded {options.LocalTimeoutSeconds} seconds; retaining fail-closed initial result.");
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.Error.WriteLine($"  LOCAL REPAIR FAIL: {ex.Message}; retaining fail-closed initial result.");
                                    }
                                }
                            }
                        }
                    }
                    catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                    {
                        var reason = $"Local call exceeded {options.LocalTimeoutSeconds} seconds.";
                        Console.Error.WriteLine($"  LOCAL TIMEOUT: {reason}");
                        local = ShadowModelResult.Error(options.LocalModel, reason);
                        if (options.StopOnError) return 6;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"  LOCAL FAIL: {ex.Message}");
                        local = ShadowModelResult.Error(options.LocalModel, ex.Message);
                        if (options.StopOnError) return 6;
                    }
                }
            }

            var cloudSemantic = cloud?.ParseSuccess == true
                ? ScenarioSemanticValidator.Validate(cloud.Card, datasetJson)
                : null;
            var cloudCompactSemantic = cloudCompact?.ParseSuccess == true
                ? ScenarioSemanticValidator.Validate(cloudCompact.Card, compactDatasetJson)
                : null;
            var localSemantic = local?.ParseSuccess == true
                ? ScenarioSemanticValidator.Validate(local.Card, localDatasetJson)
                : null;

            // Stage 2B.4 shared layer: same structural/quality logic for cloud and local cards.
            // Historical mode is analysis-only, so normalized cards are safe to surface here.
            var cloudDecision = cloud?.ParseSuccess == true
                ? AvaScenarioDecisionLayer.Evaluate(cloud.Card, datasetJson)
                : null;
            var cloudCompactDecision = cloudCompact?.ParseSuccess == true
                ? AvaScenarioDecisionLayer.Evaluate(cloudCompact.Card, compactDatasetJson)
                : null;
            var localDecision = local?.ParseSuccess == true
                ? AvaScenarioDecisionLayer.Evaluate(local.Card, localDatasetJson)
                : null;

            CandidateHistoricalEvidenceCard? candidateHistoricalEvidence = null;
            if (candidateEvidenceIndex is not null)
            {
                if (!options.Stage2A)
                {
                    Console.WriteLine("  CANDIDATE EVIDENCE SKIP: --candidate-evidence-index requires --stage2a compact payload mode.");
                }
                else if (localDecision is not null)
                {
                    var evidence = new List<CandidateScenarioHistoricalEvidence>();
                    foreach (var scenario in localDecision.Structural.NormalizedCard.Scenarios.OrderBy(x => x.ScenarioRank))
                    {
                        if (string.IsNullOrWhiteSpace(scenario.Direction) || string.IsNullOrWhiteSpace(scenario.EntryType))
                            continue;

                        evidence.Add(candidateEvidenceIndex.QueryCandidateEvidence(
                            compactDatasetJson,
                            scenario.ScenarioRank,
                            scenario.Direction,
                            scenario.EntryType,
                            options.CandidateEvidenceTopN));
                    }

                    candidateHistoricalEvidence = new CandidateHistoricalEvidenceCard(
                        EvidenceVersion: "stage2c6_candidate_evidence_v1",
                        Mode: "post_decision_sidecar",
                        DecisionEffect: "none_shadow_only",
                        Scenarios: evidence);

                    foreach (var e in evidence)
                    {
                        Console.WriteLine(
                            $"  CANDIDATE EVIDENCE rank {e.ScenarioRank}: {e.Direction.ToUpperInvariant()} {e.EntryType} " +
                            $"records={e.ReturnedAnalogueRecords}/{e.EligibleMatchingRecords} " +
                            $"avg_dist={e.AverageDistance?.ToString("0.000") ?? "n/a"} " +
                            $"trigger={FmtPct(e.TriggerRate)} resolved={e.ResolvedRSamples} " +
                            $"meanR={FmtDecimal(e.MeanRealizedR)} medianR={FmtDecimal(e.MedianRealizedR)} " +
                            $"exp/trigger={FmtDecimal(e.ExpectancyRPerTriggeredZeroUnresolved)} " +
                            $"T1/resolved={FmtPct(e.T1RateIfResolved)} pref={e.PreferredCount} sec={e.SecondaryCount}");
                    }

                    if (evidence.Count == 0)
                        Console.WriteLine("  CANDIDATE EVIDENCE: no structurally valid executable local scenario to evaluate.");
                }
            }

            if (cloudSemantic is not null)
                Console.WriteLine($"  SEMANTIC GPT FULL   : raw={cloudSemantic.RawVerdict} effective={cloudSemantic.EffectiveVerdict} accepted={cloudSemantic.AcceptedScenarioCount}/{cloudSemantic.RawScenarioCount}");
            if (cloudCompactSemantic is not null)
                Console.WriteLine($"  SEMANTIC GPT COMPACT: raw={cloudCompactSemantic.RawVerdict} effective={cloudCompactSemantic.EffectiveVerdict} accepted={cloudCompactSemantic.AcceptedScenarioCount}/{cloudCompactSemantic.RawScenarioCount}");
            if (localSemantic is not null)
            {
                Console.WriteLine($"  SEMANTIC LOCAL      : raw={localSemantic.RawVerdict} effective={localSemantic.EffectiveVerdict} accepted={localSemantic.AcceptedScenarioCount}/{localSemantic.RawScenarioCount}");
                foreach (var sr in localSemantic.Scenarios.Where(x => !x.Accepted))
                {
                    var errors = string.Join("; ", sr.Issues.Where(i => i.Severity == "error").Select(i => i.Message));
                    if (errors.Length > 0) Console.WriteLine($"    reject rank {sr.ScenarioRank}: {errors}");
                }
            }

            PrintStage2B4("GPT FULL", cloudDecision);
            PrintStage2B4("GPT COMPACT", cloudCompactDecision);
            PrintStage2B4("LOCAL", localDecision);

            var cloudVsLocal = Compare((options.Stage2A ? cloudCompact : cloud)?.Card, local?.Card);
            var fullVsCompactCloud = options.Stage2A ? Compare(cloud?.Card, cloudCompact?.Card) : null;

            bool? fullVsCompactEffectiveMatch = null;
            if (options.Stage2A && options.RunCloud)
            {
                if (cloudSemantic is not null && cloudCompactSemantic is not null)
                    fullVsCompactEffectiveMatch = string.Equals(cloudSemantic.EffectiveVerdict, cloudCompactSemantic.EffectiveVerdict, StringComparison.OrdinalIgnoreCase);

                Console.WriteLine($"  QUALITY FULL→COMPACT GPT: verdict_match={fullVsCompactCloud?.VerdictMatch?.ToString() ?? "n/a"}, top_direction_match={fullVsCompactCloud?.TopDirectionMatch?.ToString() ?? "n/a"}, effective_match={fullVsCompactEffectiveMatch?.ToString() ?? "n/a"}");
            }

            var effectiveCloudSemantic = options.Stage2A ? cloudCompactSemantic : cloudSemantic;
            bool? compactCloudVsLocalEffectiveMatch = effectiveCloudSemantic is not null && localSemantic is not null
                ? string.Equals(effectiveCloudSemantic.EffectiveVerdict, localSemantic.EffectiveVerdict, StringComparison.OrdinalIgnoreCase)
                : null;

            var effectiveCloudStage2B4 = options.Stage2A ? cloudCompactDecision : cloudDecision;
            bool? stage2b4CloudVsLocalMatch = effectiveCloudStage2B4 is not null && localDecision is not null
                ? string.Equals(effectiveCloudStage2B4.Structural.EffectiveVerdict, localDecision.Structural.EffectiveVerdict, StringComparison.OrdinalIgnoreCase)
                : null;

            if (options.RunCloud && options.RunLocal)
            {
                Console.WriteLine($"  COMPARE GPT COMPACT↔LOCAL: verdict_match={cloudVsLocal.VerdictMatch?.ToString() ?? "n/a"}, top_direction_match={cloudVsLocal.TopDirectionMatch?.ToString() ?? "n/a"}, top_entry_type_match={cloudVsLocal.TopEntryTypeMatch?.ToString() ?? "n/a"}");
                var effectiveCloud = effectiveCloudSemantic?.EffectiveVerdict;
                var effectiveLocal = localSemantic?.EffectiveVerdict;
                Console.WriteLine($"  COMPARE AFTER LEGACY GATE  : cloud={effectiveCloud ?? "n/a"}, local={effectiveLocal ?? "n/a"}, match={compactCloudVsLocalEffectiveMatch?.ToString() ?? "n/a"}");
                Console.WriteLine($"  COMPARE STAGE2B4 STRUCTURAL: cloud={effectiveCloudStage2B4?.Structural.EffectiveVerdict ?? "n/a"}, local={localDecision?.Structural.EffectiveVerdict ?? "n/a"}, match={stage2b4CloudVsLocalMatch?.ToString() ?? "n/a"}");
            }

            await AppendResultAsync(outputPath, new
            {
                run_id = runId,
                stage = candidateEvidenceIndex is not null
                    ? "stage2c6_candidate_evidence"
                    : options.Stage2A ? "stage2a_compact_semantic" : "stage3_historical_shadow",
                sequence,
                ticker = options.Ticker,
                analysis_time_et = analysisEt,
                analysis_time_utc = analysisUtc,
                bar_cap_utc = capUtc,
                payload_asof_utc = payloadAsofUtc,
                bars_elapsed = barsElapsed,
                payload = new
                {
                    full_chars = datasetJson.Length,
                    compact_chars = compactDatasetJson.Length,
                    dataset_reduction_pct = payloadReductionPct,
                    full_approx_input_tokens = fullApproxTokens,
                    compact_approx_input_tokens = compactApproxTokens,
                    local_context_tokens = options.LocalContextTokens
                },
                compact_dataset_json = options.Stage2A ? compactDatasetJson : null,
                historical_analogue_context = analogueContext,
                candidate_historical_evidence = candidateHistoricalEvidence,
                cloud_full = CloudResultForLog(cloud),
                cloud_compact = CloudResultForLog(cloudCompact),
                local = local,
                local_initial = localInitial,
                local_repair = localRepair,
                semantic = new
                {
                    cloud_full = cloudSemantic,
                    cloud_compact = cloudCompactSemantic,
                    local = localSemantic
                },
                stage2b4 = new
                {
                    cloud_full = cloudDecision,
                    cloud_compact = cloudCompactDecision,
                    local_initial = localInitialDecision,
                    local = localDecision
                },
                comparison = new
                {
                    full_vs_compact_cloud = fullVsCompactCloud,
                    compact_cloud_vs_local = cloudVsLocal,
                    full_vs_compact_effective_match = fullVsCompactEffectiveMatch,
                    effective_cloud_verdict = effectiveCloudSemantic?.EffectiveVerdict,
                    effective_local_verdict = localSemantic?.EffectiveVerdict,
                    compact_cloud_vs_local_effective_match = compactCloudVsLocalEffectiveMatch,
                    stage2b4_effective_cloud_verdict = effectiveCloudStage2B4?.Structural.EffectiveVerdict,
                    stage2b4_effective_local_verdict = localDecision?.Structural.EffectiveVerdict,
                    stage2b4_cloud_vs_local_match = stage2b4CloudVsLocalMatch
                }
            }, jsonOptions, ct);
            Console.WriteLine();

            if (options.DelayMs > 0)
                await Task.Delay(options.DelayMs, ct);

            analysisEt = analysisEt.AddMinutes(options.StepMinutes);
        }

        Console.WriteLine($"Historical shadow replay complete. Results: {outputPath}");
        return 0;
    }

    private static object? CloudResultForLog(ExecutionCardAnalysisResult? result)
    {
        if (result is null) return null;
        return new
        {
            provider = result.Provider,
            model = result.Model,
            response_id = result.ResponseId,
            parse_success = result.ParseSuccess,
            parse_error = result.ParseError,
            elapsed_ms = result.ElapsedMs,
            prompt_tokens = result.PromptTokens,
            cached_input_tokens = result.CachedInputTokens,
            output_tokens = result.OutputTokens,
            reasoning_tokens = result.ReasoningTokens,
            card = result.Card,
            raw_json = result.RawJson
        };
    }

    private static void PrintStage2B4(string label, AvaScenarioDecisionResult? decision)
    {
        if (decision is null) return;
        Console.WriteLine(
            $"  STAGE2B4 {label,-11}: structural={decision.Structural.EffectiveVerdict} " +
            $"valid={decision.Structural.StructurallyValidScenarioCount}/{decision.Structural.RawScenarioCount} " +
            $"preferred={decision.Quality.PreferredScenarioCount} secondary={decision.Quality.SecondaryScenarioCount}");

        foreach (var sr in decision.Structural.Scenarios.Where(s => !s.StructurallyValid))
        {
            var reasons = string.Join("; ", sr.HardIssues.Select(i => i.Message));
            Console.WriteLine($"    structural invalid rank {sr.ScenarioRank}: {reasons}");
        }
        foreach (var sr in decision.Structural.Scenarios.Where(s => s.RepairWarnings.Count > 0))
        {
            var repairs = string.Join("; ", sr.RepairWarnings.Select(i => i.Message));
            Console.WriteLine($"    normalized rank {sr.ScenarioRank}: {repairs}");
        }

        foreach (var qp in decision.Quality.Scenarios)
        {
            var structural = decision.Structural.Scenarios.FirstOrDefault(s => s.ScenarioRank == qp.ScenarioRank);
            var scenario = structural?.NormalizedScenario;
            var penalties = qp.SelectionPenalties.Count == 0
                ? "none"
                : string.Join(",", qp.SelectionPenalties.Select(x => x.Code));
            var observations = qp.Observations.Count == 0
                ? "none"
                : string.Join(",", qp.Observations.Select(x => x.Code));

            Console.WriteLine(
                $"    quality rank {qp.ScenarioRank}: tier={qp.SelectionTier} " +
                $"dir={scenario?.Direction ?? "n/a"} type={scenario?.EntryType ?? "n/a"} " +
                $"rr={qp.ConservativeT1RiskReward?.ToString("0.00") ?? "n/a"} " +
                $"rvol={qp.Rvol?.ToString("0.00") ?? "n/a"} " +
                $"penalties={penalties} observations={observations}");
        }
    }

    private static ShadowComparison Compare(ExecutionCardJsonV1? cloud, ExecutionCardJsonV1? local)
    {
        if (cloud is null || local is null)
            return new ShadowComparison(null, null, null, null, null, null, null, null, null, null);

        var cTop = cloud.Scenarios.OrderBy(s => s.ScenarioRank).FirstOrDefault();
        var lTop = local.Scenarios.OrderBy(s => s.ScenarioRank).FirstOrDefault();

        return new ShadowComparison(
            VerdictMatch: string.Equals(cloud.Verdict, local.Verdict, StringComparison.OrdinalIgnoreCase),
            CloudScenarioCount: cloud.Scenarios.Count,
            LocalScenarioCount: local.Scenarios.Count,
            TopDirectionMatch: CompareText(cTop?.Direction, lTop?.Direction),
            TopEntryTypeMatch: CompareText(cTop?.EntryType, lTop?.EntryType),
            TopGradeMatch: CompareText(cTop?.Grade, lTop?.Grade),
            TopScenarioProbDelta: Delta(cTop?.ScenarioProb, lTop?.ScenarioProb),
            TopSuccessProbDelta: Delta(cTop?.SuccessProb, lTop?.SuccessProb),
            TopEntryMidPctDelta: PctDelta(Mid(cTop?.EntryLow, cTop?.EntryHigh), Mid(lTop?.EntryLow, lTop?.EntryHigh)),
            TopT1PctDelta: PctDelta(cTop?.T1, lTop?.T1));
    }

    private static bool? CompareText(string? a, string? b)
    {
        if (a is null || b is null) return null;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static decimal? Delta(decimal? a, decimal? b)
        => a.HasValue && b.HasValue ? Math.Round(b.Value - a.Value, 4) : null;

    private static decimal? Mid(decimal? low, decimal? high)
    {
        if (low.HasValue && high.HasValue) return (low.Value + high.Value) / 2m;
        return low ?? high;
    }

    private static decimal? PctDelta(decimal? baseline, decimal? other)
    {
        if (!baseline.HasValue || !other.HasValue || baseline.Value == 0) return null;
        return Math.Round((other.Value - baseline.Value) / baseline.Value * 100m, 4);
    }

    private static async Task AppendResultAsync(
        string path,
        object row,
        JsonSerializerOptions options,
        CancellationToken ct)
    {
        var line = JsonSerializer.Serialize(row, options) + Environment.NewLine;
        await File.AppendAllTextAsync(path, line, Encoding.UTF8, ct);
    }

    private static string BuildLocalStructuralRepairInstruction(
        ShadowModelResult initial,
        AvaScenarioDecisionResult decision,
        string datasetJson)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Return ONLY a corrected JSON execution card.");
        sb.AppendLine("The previous local TRADE card had zero structurally valid scenarios.");
        sb.AppendLine("Repair only when the current dataset supports the correction. Do not invent a price merely to satisfy an inequality.");
        sb.AppendLine("On this repair pass return at most ONE strongest TRADE scenario. If no grounded executable correction exists, return NO_TRADE with scenarios=[].");
        sb.AppendLine();
        sb.AppendLine("STAGE2B4_HARD_ERRORS:");

        foreach (var issue in decision.Structural.CardIssues)
            sb.AppendLine($"- card: {issue.Code}: {issue.Message}");

        foreach (var scenario in decision.Structural.Scenarios.Where(x => !x.StructurallyValid))
        {
            foreach (var issue in scenario.HardIssues)
                sb.AppendLine($"- rank {scenario.ScenarioRank}: {issue.Code}: {issue.Message}");
        }

        sb.AppendLine();
        sb.AppendLine("PREVIOUS_JSON:");
        sb.AppendLine(initial.RawJson);
        sb.AppendLine();
        sb.AppendLine("DATASET_JSON:");
        sb.AppendLine(datasetJson);
        return sb.ToString();
    }

    private static int EstimateInputTokens(int chars)
        => Math.Max(1, (int)Math.Ceiling(chars / 4.0));

    private static string ResolveOutputPath(HistoricalShadowOptions options, DateTime startEt)
    {
        if (!string.IsNullOrWhiteSpace(options.OutputPath))
            return Path.GetFullPath(options.OutputPath);

        var dir = Path.GetFullPath("historical_shadow_results");
        var name = options.Stage2A
            ? $"{options.Ticker}_{startEt:yyyyMMdd_HHmm}_stage2a.jsonl"
            : $"{options.Ticker}_{startEt:yyyyMMdd_HHmm}_shadow.jsonl";
        return Path.Combine(dir, name);
    }

    private static HistoricalShadowOptions ParseOptions(string[] args)
    {
        var ticker = ReadOption(args, "--ticker")
                     ?? Environment.GetEnvironmentVariable("AVA_REPLAY_TICKER")
                     ?? "AAPL";
        ticker = ticker.Trim().ToUpperInvariant();
        if (ticker.Length == 0) throw new ArgumentException("Ticker cannot be empty.");

        var startEtRaw = ReadOption(args, "--start-et");
        var endEtRaw = ReadOption(args, "--end-et");
        DateTime? startEt = string.IsNullOrWhiteSpace(startEtRaw)
            ? null
            : ParseEt(startEtRaw, "--start-et");
        DateTime? endEt = string.IsNullOrWhiteSpace(endEtRaw)
            ? null
            : ParseEt(endEtRaw, "--end-et");

        var steps = ParseInt(ReadOption(args, "--steps"), 1, 1, 200, "--steps");
        var stepMinutes = ParseInt(ReadOption(args, "--step-minutes"), 5, 1, 60, "--step-minutes");
        var delayMs = ParseInt(ReadOption(args, "--delay-ms"), 0, 0, 60000, "--delay-ms");

        var localBaseUrl = ReadOption(args, "--local-url")
                           ?? Environment.GetEnvironmentVariable("LOCAL_LLM_BASE_URL")
                           ?? "http://localhost:11434";
        var localModel = ReadOption(args, "--local-model")
                         ?? Environment.GetEnvironmentVariable("LOCAL_LLM_MODEL")
                         ?? "qwen3:8b";
        var localTimeout = ParseInt(
            ReadOption(args, "--local-timeout-seconds") ?? Environment.GetEnvironmentVariable("LOCAL_LLM_TIMEOUT_SECONDS"),
            600, 10, 3600, "local timeout");
        var localContext = ParseInt(
            ReadOption(args, "--local-context-tokens") ?? Environment.GetEnvironmentVariable("LOCAL_LLM_CONTEXT_TOKENS"),
            32768, 4096, 131072, "local context");
        var analogueIndexPath = ReadOption(args, "--analogue-index") ?? Environment.GetEnvironmentVariable("AVA_ANALOGUE_INDEX");
        var analogueTop = ParseInt(ReadOption(args, "--analogue-top"), 24, 4, 100, "--analogue-top");
        var candidateEvidenceIndexPath = ReadOption(args, "--candidate-evidence-index") ?? Environment.GetEnvironmentVariable("AVA_CANDIDATE_EVIDENCE_INDEX");
        var candidateEvidenceTop = ParseInt(ReadOption(args, "--candidate-evidence-top"), 24, 4, 100, "--candidate-evidence-top");

        if (!string.IsNullOrWhiteSpace(analogueIndexPath) && !string.IsNullOrWhiteSpace(candidateEvidenceIndexPath))
            throw new ArgumentException("Use either --analogue-index (legacy prompt-injection research) or --candidate-evidence-index (Stage 2C.6 sidecar), not both in the same run.");

        return new HistoricalShadowOptions(
            ticker,
            startEt,
            endEt,
            steps,
            stepMinutes,
            delayMs,
            localBaseUrl,
            localModel,
            localTimeout,
            localContext,
            Stage2A: HasFlag(args, "--stage2a"),
            RunCloud: !HasFlag(args, "--no-cloud"),
            RunLocal: !HasFlag(args, "--no-local"),
            StopOnError: HasFlag(args, "--stop-on-error"),
            LocalStructuralRepair: !HasFlag(args, "--no-local-repair"),
            OutputPath: ReadOption(args, "--output"),
            AnalogueIndexPath: analogueIndexPath,
            AnalogueTopN: analogueTop,
            CandidateEvidenceIndexPath: candidateEvidenceIndexPath,
            CandidateEvidenceTopN: candidateEvidenceTop);
    }

    private static DateTime ParseEt(string raw, string optionName)
    {
        var formats = new[]
        {
            "yyyy-MM-dd HH:mm",
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-ddTHH:mm",
            "yyyy-MM-ddTHH:mm:ss"
        };

        if (!DateTime.TryParseExact(raw.Trim(), formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out var parsed) &&
            !DateTime.TryParse(raw.Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out parsed))
        {
            throw new ArgumentException($"{optionName} must look like 2026-08-07T10:30 or '2026-08-07 10:30'.");
        }

        return DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
    }

    private static void ValidateWindow(DateTime startEt, DateTime endEt, int stepMinutes)
    {
        if (endEt < startEt) throw new ArgumentException("End ET must be at or after start ET.");
        if (startEt.Date != endEt.Date) throw new ArgumentException("This first replay implementation must stay within one trading date.");
        if (startEt.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            throw new ArgumentException("Replay date is a weekend. Choose a historical trading day.");

        var earliest = startEt.Date.AddHours(9).AddMinutes(31); // 09:30 bar is closed at 09:31
        var latest = startEt.Date.AddHours(16);                  // 15:59 bar is closed at 16:00
        if (startEt < earliest || startEt > latest)
            throw new ArgumentException("Start ET must be between 09:31 and 16:00 so at least one regular-session minute is closed.");
        if (endEt > latest)
            throw new ArgumentException("End ET cannot be after 16:00 ET.");
        if (stepMinutes <= 0) throw new ArgumentException("Step minutes must be positive.");
    }

    private static DateTime GetDefaultStartEt()
    {
        var nowEt = FromUtc(DateTime.UtcNow);
        var date = nowEt.Date.AddDays(-1);
        while (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            date = date.AddDays(-1);
        return DateTime.SpecifyKind(date.AddHours(10), DateTimeKind.Unspecified);
    }

    private static DateTime ToUtc(DateTime et)
        => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(et, DateTimeKind.Unspecified), EasternTz);

    private static DateTime NormalizeUtcTimestamp(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            // Values read from ts_utc/timestamptz are UTC by contract. Treat an
            // Unspecified Kind as UTC instead of interpreting it in the machine's
            // local timezone.
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    private static DateTime FromUtc(DateTime utc)
    {
        utc = NormalizeUtcTimestamp(utc);
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(utc, EasternTz), DateTimeKind.Unspecified);
    }

    private static DateTime LastClosedMinuteStartUtc(DateTime analysisUtc)
    {
        analysisUtc = analysisUtc.ToUniversalTime();
        var floor = new DateTime(
            analysisUtc.Year, analysisUtc.Month, analysisUtc.Day,
            analysisUtc.Hour, analysisUtc.Minute, 0, DateTimeKind.Utc);
        return floor.AddMinutes(-1);
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return arg[(name.Length + 1)..].Trim('"');

            if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                return args[i + 1].Trim('"');
        }
        return null;
    }

    private static bool HasFlag(string[] args, string flag)
        => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    private static int ParseInt(string? raw, int fallback, int min, int max, string label)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new ArgumentException($"{label} must be an integer.");
        if (value < min || value > max)
            throw new ArgumentException($"{label} must be between {min} and {max}.");
        return value;
    }

    private static string FmtDecimal(decimal? value) => value.HasValue ? value.Value.ToString("0.000") : "n/a";

    private static string FmtPct(decimal? value) => value.HasValue ? $"{value.Value * 100m:0.0}%" : "n/a";

    private static void PrintHelp()
    {
        Console.WriteLine("AVA historical shadow replay");
        Console.WriteLine();
        Console.WriteLine("Stage 2C benchmark help:");
        Console.WriteLine("  dotnet run -- --historical-shadow --stage2c-benchmark --stage2c-benchmark-help");
        Console.WriteLine("Stage 2C.6 evidence calibration help:");
        Console.WriteLine("  dotnet run -- --historical-shadow --candidate-evidence-calibrate --candidate-evidence-calibrate-help");
        Console.WriteLine("Stage 2C.7 temporal holdout help:");
        Console.WriteLine("  dotnet run -- --historical-shadow --candidate-evidence-holdout --candidate-evidence-holdout-help");
        Console.WriteLine("Stage 2C.8 evidence-ranking simulation help:");
        Console.WriteLine("  dotnet run -- --historical-shadow --candidate-evidence-rank-sim --candidate-evidence-rank-sim-help");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run -- --historical-shadow --ticker=AAPL --start-et=2026-08-07T10:30");
        Console.WriteLine();
        Console.WriteLine("Advance from that historical instant:");
        Console.WriteLine("  dotnet run -- --historical-shadow --ticker=AAPL --start-et=2026-08-07T10:30 --steps=6 --step-minutes=5");
        Console.WriteLine();
        Console.WriteLine("Or specify an explicit end time:");
        Console.WriteLine("  dotnet run -- --historical-shadow --ticker=AAPL --start-et=2026-08-07T10:30 --end-et=2026-08-07T11:00 --step-minutes=5");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --ticker                 Symbol (default AAPL or AVA_REPLAY_TICKER)");
        Console.WriteLine("  --start-et               Analysis clock time in US Eastern. Default: 10:00 ET on prior weekday.");
        Console.WriteLine("  --end-et                 Optional final analysis clock time on the same day.");
        Console.WriteLine("  --steps                  Number of analysis instants if --end-et is absent (default 1).");
        Console.WriteLine("  --step-minutes           Historical clock advance (default 5).");
        Console.WriteLine("  --local-model            Ollama model (default LOCAL_LLM_MODEL or qwen3:8b).");
        Console.WriteLine("  --local-url              Ollama base URL (default http://localhost:11434).");
        Console.WriteLine("  --local-timeout-seconds  Per-call local timeout (default 600).");
        Console.WriteLine("  --local-context-tokens   Ollama num_ctx (default 32768).");
        Console.WriteLine("  --stage2a                Run GPT full + GPT compact + local compact, with semantic gates.");
        Console.WriteLine("  --analogue-index         LEGACY Stage 2C prompt-injection research mode; requires --stage2a.");
        Console.WriteLine("  --analogue-top           Number of diversified prior-session prompt analogues (default 24).");
        Console.WriteLine("  --candidate-evidence-index Stage 2C.6 post-decision historical evidence sidecar; requires --stage2a.");
        Console.WriteLine("  --candidate-evidence-top Candidate-conditioned prior records per executable scenario (default 24).");
        Console.WriteLine("  --no-cloud               Run local only (no OpenAI cost).");
        Console.WriteLine("  --no-local               Run GPT only.");
        Console.WriteLine("  --no-local-repair        Disable the single Stage 2B.4 validator-guided local repair attempt.");
        Console.WriteLine("  --stop-on-error          Stop immediately if either primary model call fails.");
        Console.WriteLine("  --output                 JSONL output path.");
        Console.WriteLine();
        Console.WriteLine("As-of semantics: --start-et=10:30 means the newest regular bar visible to AVA is the 10:29 ET bar.");
    }

    private sealed record HistoricalShadowOptions(
        string Ticker,
        DateTime? StartEt,
        DateTime? EndEt,
        int Steps,
        int StepMinutes,
        int DelayMs,
        string LocalBaseUrl,
        string LocalModel,
        int LocalTimeoutSeconds,
        int LocalContextTokens,
        bool Stage2A,
        bool RunCloud,
        bool RunLocal,
        bool StopOnError,
        bool LocalStructuralRepair,
        string? OutputPath,
        string? AnalogueIndexPath,
        int AnalogueTopN,
        string? CandidateEvidenceIndexPath,
        int CandidateEvidenceTopN);

    private sealed record ShadowModelResult(
        string Provider,
        string Model,
        string RawJson,
        ExecutionCardJsonV1? Card,
        bool ParseSuccess,
        string? ParseError,
        long ElapsedMs,
        int? PromptTokens,
        int? OutputTokens,
        string Status)
    {
        public static ShadowModelResult Skipped(string model, string reason)
            => new("ollama", model, "", null, false, reason, 0, null, null, "skipped");

        public static ShadowModelResult Error(string model, string reason)
            => new("ollama", model, "", null, false, reason, 0, null, null, "error");
    }

    private sealed record ShadowComparison(
        bool? VerdictMatch,
        int? CloudScenarioCount,
        int? LocalScenarioCount,
        bool? TopDirectionMatch,
        bool? TopEntryTypeMatch,
        bool? TopGradeMatch,
        decimal? TopScenarioProbDelta,
        decimal? TopSuccessProbDelta,
        decimal? TopEntryMidPctDelta,
        decimal? TopT1PctDelta);
}
