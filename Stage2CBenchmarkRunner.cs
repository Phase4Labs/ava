using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace get_assessment_no_graph;

/// <summary>
/// Stage 2C.5 deterministic benchmark harness.
///
/// Selects a small, reproducible stratified sample from the Stage 2B.4-enriched
/// historical corpus, then replays every state twice through the same local model:
///   1) compact state only
///   2) compact state + causal Stage 2C historical analogue context
///
/// GPT is not called. The stored GPT card is the teacher/reference only. Stage 2B.4
/// remains the executable structural/quality layer for both local runs.
///
/// The benchmark is resumable: completed state outputs are reused unless --rerun is set.
/// </summary>
public static class Stage2CBenchmarkRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private static readonly JsonSerializerOptions PrettyJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

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
        BenchmarkOptions options;
        try
        {
            options = ParseOptions(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Stage 2C benchmark configuration error: {ex.Message}");
            Console.Error.WriteLine();
            PrintHelp();
            return 2;
        }

        if (!File.Exists(options.CorpusPath))
        {
            Console.Error.WriteLine($"Corpus not found: {options.CorpusPath}");
            return 2;
        }
        if (!File.Exists(options.AnalogueIndexPath))
        {
            Console.Error.WriteLine($"Analogue index not found: {options.AnalogueIndexPath}");
            return 2;
        }

        Directory.CreateDirectory(options.OutputDir);
        var statesDir = Path.Combine(options.OutputDir, "states");
        Directory.CreateDirectory(statesDir);

        var candidates = await LoadCandidatesAsync(options.CorpusPath, ct);
        if (candidates.Count == 0)
        {
            Console.Error.WriteLine("No benchmark-eligible corpus states were found.");
            return 3;
        }

        var excludedKeys = await LoadExcludedKeysAsync(options.ExcludeSelectionPath, ct);
        if (excludedKeys.Count > 0)
            candidates = candidates.Where(c => !excludedKeys.Contains(CandidateKey(c))).ToList();

        var selected = SelectForProfile(candidates, options.SampleSize, options.Seed, options.SelectionProfile);
        if (selected.Count == 0)
        {
            Console.Error.WriteLine("The benchmark sampler returned no states.");
            return 3;
        }

        Console.WriteLine("AVA Stage 2C.5 local-memory benchmark");
        Console.WriteLine($"Corpus          : {Path.GetFullPath(options.CorpusPath)}");
        Console.WriteLine($"Analogue index  : {Path.GetFullPath(options.AnalogueIndexPath)}");
        Console.WriteLine($"Eligible states : {candidates.Count:N0}");
        Console.WriteLine($"Selected states : {selected.Count:N0}");
        Console.WriteLine($"Seed            : {options.Seed}");
        Console.WriteLine($"Selection profile: {options.SelectionProfile}");
        Console.WriteLine($"Excluded states : {excludedKeys.Count:N0}");
        Console.WriteLine($"Local model     : {options.LocalModel}");
        Console.WriteLine($"Analogue top N  : {options.AnalogueTopN}");
        Console.WriteLine($"Local repair    : disabled (benchmark isolates memory effect)");
        Console.WriteLine($"Resume          : {(options.Rerun ? "disabled; force rerun" : "enabled; reuse completed state outputs")}");
        Console.WriteLine($"Output          : {Path.GetFullPath(options.OutputDir)}");
        Console.WriteLine();
        Console.WriteLine("Each selected state runs twice: no-memory, then Stage 2C memory. GPT is not called.");
        Console.WriteLine();

        await WriteSelectionAsync(options.OutputDir, selected, options, ct);

        var rows = new List<BenchmarkRow>();
        using var polygon = new PolygonClient(massiveApiKey);

        for (var i = 0; i < selected.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var state = selected[i];
            var stateId = $"{i + 1:00}_{SafeName(state.Ticker)}_{state.AnalysisEt:yyyyMMdd_HHmmss}";
            Console.WriteLine($"[{i + 1}/{selected.Count}] {state.Ticker} {state.AnalysisEt:yyyy-MM-dd HH:mm:ss} ET teacher={state.TeacherRawVerdict}/{state.TeacherStructuralVerdict} stratum={state.StratumKey}");

            var noMemoryPath = Path.Combine(statesDir, stateId + "_no_memory.jsonl");
            var memoryPath = Path.Combine(statesDir, stateId + "_memory.jsonl");

            var noMemory = await RunOrReuseAsync(
                state,
                noMemoryPath,
                memory: false,
                options,
                massiveApiKey,
                supabaseUrl,
                supabaseServiceKey,
                openAiApiKey,
                openAiModel,
                executionQuestion,
                ct);

            var memory = await RunOrReuseAsync(
                state,
                memoryPath,
                memory: true,
                options,
                massiveApiKey,
                supabaseUrl,
                supabaseServiceKey,
                openAiApiKey,
                openAiModel,
                executionQuestion,
                ct);

            IReadOnlyList<MinuteBar> sessionBars = Array.Empty<MinuteBar>();
            if (noMemory.IsUsable || memory.IsUsable)
            {
                try
                {
                    var sessionOpenUtc = ToUtc(state.AnalysisEt.Date.AddHours(9).AddMinutes(30));
                    var lastRegularBarUtc = ToUtc(state.AnalysisEt.Date.AddHours(15).AddMinutes(59));
                    sessionBars = await polygon.GetMinuteBarsAsync(state.Ticker, sessionOpenUtc, lastRegularBarUtc, ct);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  OUTCOME LABEL WARNING: could not fetch session bars: {ex.Message}");
                }
            }

            var noMemoryOutcome = EvaluateLocalDecision(noMemory, sessionBars, state.AsofUtc);
            var memoryOutcome = EvaluateLocalDecision(memory, sessionBars, state.AsofUtc);

            var row = BenchmarkRow.Create(i + 1, state, noMemory, memory, noMemoryOutcome, memoryOutcome);
            rows.Add(row);

            PrintCompactComparison(row);
            await WriteProgressAsync(options.OutputDir, selected.Count, rows, options, ct);
            Console.WriteLine();
        }

        var finalSummary = BuildSummary(selected.Count, rows, options);
        await WriteFinalAsync(options.OutputDir, rows, finalSummary, ct);

        Console.WriteLine("Stage 2C.5 benchmark complete.");
        PrintSummary(finalSummary);
        Console.WriteLine($"CSV     : {Path.GetFullPath(Path.Combine(options.OutputDir, "stage2c_benchmark_results.csv"))}");
        Console.WriteLine($"JSON    : {Path.GetFullPath(Path.Combine(options.OutputDir, "stage2c_benchmark_results.json"))}");
        Console.WriteLine($"Summary : {Path.GetFullPath(Path.Combine(options.OutputDir, "stage2c_benchmark_summary.json"))}");
        return rows.Any(r => r.NoMemoryStatus != "ok" || r.MemoryStatus != "ok") ? 1 : 0;
    }

    private static async Task<ReplayObservation> RunOrReuseAsync(
        BenchmarkCandidate state,
        string outputPath,
        bool memory,
        BenchmarkOptions options,
        string massiveApiKey,
        string supabaseUrl,
        string supabaseServiceKey,
        string openAiApiKey,
        string openAiModel,
        string executionQuestion,
        CancellationToken ct)
    {
        if (!options.Rerun && File.Exists(outputPath))
        {
            var existing = TryReadReplay(outputPath);
            if (existing.IsUsable)
            {
                Console.WriteLine($"  {(memory ? "MEMORY" : "NO MEMORY"),-10}: reuse {Path.GetFileName(outputPath)}");
                return existing with { Reused = true };
            }
        }

        if (File.Exists(outputPath)) File.Delete(outputPath);

        var nestedArgs = new List<string>
        {
            "--historical-shadow",
            "--stage2a",
            "--no-cloud",
            "--no-local-repair",
            $"--ticker={state.Ticker}",
            $"--start-et={state.AnalysisEt:yyyy-MM-ddTHH:mm:ss}",
            $"--local-model={options.LocalModel}",
            $"--local-url={options.LocalBaseUrl}",
            $"--local-timeout-seconds={options.LocalTimeoutSeconds}",
            $"--local-context-tokens={options.LocalContextTokens}",
            $"--output={outputPath}"
        };

        if (memory)
        {
            nestedArgs.Add($"--analogue-index={options.AnalogueIndexPath}");
            nestedArgs.Add($"--analogue-top={options.AnalogueTopN}");
        }

        var rc = await HistoricalShadowRunner.RunAsync(
            nestedArgs.ToArray(),
            massiveApiKey,
            supabaseUrl,
            supabaseServiceKey,
            openAiApiKey,
            openAiModel,
            executionQuestion,
            ct);

        var result = TryReadReplay(outputPath);
        if (rc != 0 && result.Status == "missing")
            return result with { Status = "error", Error = $"Historical replay returned exit code {rc}." };
        return result;
    }

    private static ReplayObservation TryReadReplay(string path)
    {
        try
        {
            if (!File.Exists(path)) return ReplayObservation.Missing("result file does not exist");
            var line = File.ReadLines(path).LastOrDefault(x => !string.IsNullOrWhiteSpace(x));
            if (string.IsNullOrWhiteSpace(line)) return ReplayObservation.Missing("result file is empty");

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!TryObject(root, out var local, "local"))
                return ReplayObservation.Missing("local result is absent");

            var status = GetStringAny(local, "status") ?? "unknown";
            var parseSuccess = GetBoolAny(local, "parseSuccess", "parse_success") ?? false;
            var elapsedMs = GetLongAny(local, "elapsedMs", "elapsed_ms") ?? 0;
            var promptTokens = GetIntAny(local, "promptTokens", "prompt_tokens");
            var outputTokens = GetIntAny(local, "outputTokens", "output_tokens");
            var parseError = GetStringAny(local, "parseError", "parse_error");

            ExecutionCardJsonV1? rawCard = null;
            if (TryObject(local, out var localCardEl, "card"))
                rawCard = JsonSerializer.Deserialize<ExecutionCardJsonV1>(localCardEl.GetRawText(), JsonOptions);

            string? compactDatasetJson = GetStringAny(root, "compact_dataset_json", "compactDatasetJson");

            string structuralVerdict = "n/a";
            var validCount = 0;
            var preferredCount = 0;
            var secondaryCount = 0;
            ExecutionCardJsonV1? normalizedCard = null;
            var preferredRanks = new HashSet<int>();

            if (TryNestedObject(root, out var localStage, "stage2b4", "local"))
            {
                if (TryObject(localStage, out var structural, "structural"))
                {
                    structuralVerdict = GetStringAny(structural, "effective_verdict", "effectiveVerdict") ?? "n/a";
                    validCount = GetIntAny(structural, "structurally_valid_scenario_count", "structurallyValidScenarioCount") ?? 0;
                    if (TryObjectAny(structural, out var normalized, "normalized_card", "normalizedCard"))
                        normalizedCard = JsonSerializer.Deserialize<ExecutionCardJsonV1>(normalized.GetRawText(), JsonOptions);
                }
                if (TryObject(localStage, out var quality, "quality"))
                {
                    preferredCount = GetIntAny(quality, "preferred_scenario_count", "preferredScenarioCount") ?? 0;
                    secondaryCount = GetIntAny(quality, "secondary_scenario_count", "secondaryScenarioCount") ?? 0;
                    if (TryArray(quality, out var qualityScenarios, "scenarios"))
                    {
                        foreach (var q in qualityScenarios.EnumerateArray())
                        {
                            var tier = GetStringAny(q, "selection_tier", "selectionTier");
                            var rank = GetIntAny(q, "scenario_rank", "scenarioRank");
                            if (rank.HasValue && string.Equals(tier, "PREFERRED", StringComparison.OrdinalIgnoreCase))
                                preferredRanks.Add(rank.Value);
                        }
                    }
                }
            }

            HistoricalAnalogueSummary analogue = HistoricalAnalogueSummary.Empty;
            if (TryObjectAny(root, out var analogueEl, "historical_analogue_context", "historicalAnalogueContext"))
            {
                analogue = new HistoricalAnalogueSummary(
                    GetIntAny(analogueEl, "returnedAnalogues", "returned_analogues") ?? 0,
                    GetIntAny(analogueEl, "eligiblePriorSessionRecords", "eligible_prior_session_records") ?? 0,
                    GetDecimalAny(analogueEl, "averageDistance", "average_distance"),
                    TryArrayAny(analogueEl, out var setupArr, "setupOutcomes", "setup_outcomes") ? setupArr.GetArrayLength() : 0);
            }

            var selected = SelectExecutableScenario(normalizedCard, preferredRanks);
            var topDirection = selected?.Direction;
            var topEntryType = selected?.EntryType;
            var topTier = selected is null
                ? null
                : (preferredRanks.Contains(selected.ScenarioRank) ? "PREFERRED" : "SECONDARY");

            var normalizedStatus = string.Equals(status, "error", StringComparison.OrdinalIgnoreCase)
                ? "error"
                : parseSuccess ? "ok" : status;

            return new ReplayObservation(
                Status: normalizedStatus,
                Error: parseError,
                ParseSuccess: parseSuccess,
                RawVerdict: rawCard?.Verdict ?? "n/a",
                RawScenarioCount: rawCard?.Scenarios?.Count ?? 0,
                StructuralVerdict: structuralVerdict,
                StructurallyValidScenarioCount: validCount,
                PreferredScenarioCount: preferredCount,
                SecondaryScenarioCount: secondaryCount,
                TopDirection: topDirection,
                TopEntryType: topEntryType,
                TopSelectionTier: topTier,
                SelectedScenario: selected,
                CompactDatasetJson: compactDatasetJson,
                ElapsedMs: elapsedMs,
                PromptTokens: promptTokens,
                OutputTokens: outputTokens,
                Analogue: analogue,
                Reused: false);
        }
        catch (Exception ex)
        {
            return ReplayObservation.Missing(ex.Message) with { Status = "error" };
        }
    }

    private static LocalOutcomeSummary EvaluateLocalDecision(
        ReplayObservation replay,
        IReadOnlyList<MinuteBar> sessionBars,
        DateTime fallbackAsofUtc)
    {
        if (!replay.IsUsable || replay.SelectedScenario is null)
            return LocalOutcomeSummary.NoTrade;
        if (sessionBars.Count == 0 || string.IsNullOrWhiteSpace(replay.CompactDatasetJson))
            return new LocalOutcomeSummary(true, null, null, null, null, null, "OUTCOME_UNAVAILABLE");

        try
        {
            var outcome = CorpusOutcomeEvaluator.Evaluate(
                replay.SelectedScenario,
                replay.CompactDatasetJson,
                sessionBars,
                fallbackAsofUtc);
            var realizedR = ResolvedT1OrStopR(replay.SelectedScenario, outcome);
            return new LocalOutcomeSummary(
                HasTrade: true,
                Triggered: outcome.Triggered,
                PrimaryOutcome: outcome.PrimaryOutcome,
                T1BeforeStop: outcome.T1BeforeStop,
                ResolvedR: realizedR,
                MfeR: outcome.MfeR,
                OutcomeStatus: outcome.OutcomeMethod);
        }
        catch (Exception ex)
        {
            return new LocalOutcomeSummary(true, null, null, null, null, null, "OUTCOME_ERROR: " + ex.Message);
        }
    }

    private static decimal? ResolvedT1OrStopR(ExecutionScenarioJsonV1 scenario, ScenarioRealizedOutcome outcome)
    {
        if (!outcome.Triggered) return null;
        if (outcome.T1BeforeStop == true)
        {
            var isLong = string.Equals(scenario.Direction, "long", StringComparison.OrdinalIgnoreCase);
            var entry = isLong ? scenario.EntryHigh ?? scenario.EntryLow : scenario.EntryLow ?? scenario.EntryHigh;
            if (!entry.HasValue || !scenario.StopPrice.HasValue || !scenario.T1.HasValue) return null;
            var risk = isLong ? entry.Value - scenario.StopPrice.Value : scenario.StopPrice.Value - entry.Value;
            var reward = isLong ? scenario.T1.Value - entry.Value : entry.Value - scenario.T1.Value;
            if (risk <= 0 || reward <= 0) return null;
            return Math.Round(reward / risk, 3);
        }
        if (string.Equals(outcome.PrimaryOutcome, "STOP_BEFORE_T1", StringComparison.OrdinalIgnoreCase))
            return -1m;
        return null;
    }

    private static ExecutionScenarioJsonV1? SelectExecutableScenario(
        ExecutionCardJsonV1? normalizedCard,
        IReadOnlySet<int> preferredRanks)
    {
        if (normalizedCard?.Scenarios is null || normalizedCard.Scenarios.Count == 0) return null;
        return normalizedCard.Scenarios
                   .Where(s => preferredRanks.Contains(s.ScenarioRank))
                   .OrderBy(s => s.ScenarioRank)
                   .FirstOrDefault()
               ?? normalizedCard.Scenarios.OrderBy(s => s.ScenarioRank).FirstOrDefault();
    }

    private static async Task<List<BenchmarkCandidate>> LoadCandidatesAsync(string corpusPath, CancellationToken ct)
    {
        var result = new List<BenchmarkCandidate>();
        var lineNumber = 0;
        await foreach (var line in File.ReadLinesAsync(corpusPath, ct))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (TryBuildCandidate(doc.RootElement, lineNumber, out var candidate) && candidate is not null)
                    result.Add(candidate);
            }
            catch (JsonException)
            {
                // Skip malformed corpus rows. The source corpus build already reports its own errors.
            }
        }
        return result;
    }

    private static bool TryBuildCandidate(JsonElement root, int lineNumber, out BenchmarkCandidate? candidate)
    {
        candidate = null;
        if (!TryObject(root, out var source, "source")) return false;
        var ticker = GetStringAny(source, "ticker")?.Trim().ToUpperInvariant();
        var asof = GetDateTimeAny(source, "asof_utc", "asofUtc");
        if (string.IsNullOrWhiteSpace(ticker) || !asof.HasValue) return false;

        var analysisEt = FromUtc(asof.Value);
        var tod = analysisEt.TimeOfDay;
        if (analysisEt.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
        if (tod < new TimeSpan(9, 31, 0) || tod > new TimeSpan(16, 0, 59)) return false;

        if (!TryObject(root, out var teacher, "teacher")) return false;
        if (!TryObject(teacher, out var card, "card")) return false;
        var rawVerdict = GetStringAny(card, "verdict") ?? "unknown";

        string structuralVerdict = "unknown";
        var preferredRanks = new HashSet<int>();
        var structurallyValidRanks = new HashSet<int>();
        ExecutionCardJsonV1? normalizedCard = null;

        if (TryObjectAny(teacher, out var stage2b4, "stage2b4", "stage2B4"))
        {
            if (TryObject(stage2b4, out var structural, "structural"))
            {
                structuralVerdict = GetStringAny(structural, "effective_verdict", "effectiveVerdict") ?? "unknown";
                if (TryArray(structural, out var ss, "scenarios"))
                {
                    foreach (var s in ss.EnumerateArray())
                    {
                        var rank = GetIntAny(s, "scenario_rank", "scenarioRank");
                        var valid = GetBoolAny(s, "structurally_valid", "structurallyValid");
                        if (rank.HasValue && valid == true) structurallyValidRanks.Add(rank.Value);
                    }
                }
                if (TryObjectAny(structural, out var normalized, "normalized_card", "normalizedCard"))
                    normalizedCard = JsonSerializer.Deserialize<ExecutionCardJsonV1>(normalized.GetRawText(), JsonOptions);
            }
            if (TryObject(stage2b4, out var quality, "quality") && TryArray(quality, out var qs, "scenarios"))
            {
                foreach (var q in qs.EnumerateArray())
                {
                    var rank = GetIntAny(q, "scenario_rank", "scenarioRank");
                    var tier = GetStringAny(q, "selection_tier", "selectionTier");
                    if (rank.HasValue && string.Equals(tier, "PREFERRED", StringComparison.OrdinalIgnoreCase))
                        preferredRanks.Add(rank.Value);
                }
            }
        }

        var teacherScenario = SelectExecutableScenario(normalizedCard, preferredRanks);
        var teacherRank = teacherScenario?.ScenarioRank;
        var teacherTier = teacherScenario is null
            ? null
            : (preferredRanks.Contains(teacherScenario.ScenarioRank) ? "PREFERRED" : "SECONDARY");

        decimal? teacherResolvedR = null;
        bool? teacherTriggered = null;
        string? teacherOutcome = null;
        if (teacherRank.HasValue && TryArray(root, out var scenarios, "scenarios"))
        {
            foreach (var sr in scenarios.EnumerateArray())
            {
                var rank = GetIntAny(sr, "ScenarioRank", "scenarioRank", "scenario_rank");
                if (rank != teacherRank.Value) continue;
                teacherResolvedR = GetDecimalAny(sr, "ResolvedT1OrStopR", "resolvedT1OrStopR", "resolved_t1_or_stop_r");
                if (TryObjectAny(sr, out var outcome, "Outcome", "outcome"))
                {
                    teacherTriggered = GetBoolAny(outcome, "Triggered", "triggered");
                    teacherOutcome = GetStringAny(outcome, "PrimaryOutcome", "primaryOutcome", "primary_outcome");
                }
                break;
            }
        }

        var sessionBucket = SessionBucket(analysisEt);
        var outcomeBucket = string.Equals(structuralVerdict, "NO_TRADE", StringComparison.OrdinalIgnoreCase)
            ? "no_trade"
            : teacherResolvedR.HasValue && teacherResolvedR.Value > 0 ? "positive"
            : teacherResolvedR.HasValue && teacherResolvedR.Value < 0 ? "negative"
            : teacherTriggered == false ? "not_triggered"
            : "unresolved";
        var direction = teacherScenario?.Direction?.ToLowerInvariant() ?? "none";
        var entryType = teacherScenario?.EntryType?.ToLowerInvariant() ?? "none";
        var rawBucket = string.Equals(rawVerdict, "TRADE", StringComparison.OrdinalIgnoreCase) ? "trade" : "no_trade";
        var stratum = rawBucket == "trade"
            ? $"{rawBucket}|{sessionBucket}|{direction}|{entryType}|{outcomeBucket}"
            : $"{rawBucket}|{sessionBucket}";

        candidate = new BenchmarkCandidate(
            CorpusLine: lineNumber,
            Ticker: ticker,
            AsofUtc: EnsureUtc(asof.Value),
            AnalysisEt: analysisEt,
            TeacherRawVerdict: rawVerdict,
            TeacherStructuralVerdict: structuralVerdict,
            TeacherTopDirection: teacherScenario?.Direction,
            TeacherTopEntryType: teacherScenario?.EntryType,
            TeacherTopSelectionTier: teacherTier,
            TeacherTopResolvedR: teacherResolvedR,
            TeacherTopTriggered: teacherTriggered,
            TeacherTopOutcome: teacherOutcome,
            SessionBucket: sessionBucket,
            OutcomeBucket: outcomeBucket,
            StratumKey: stratum);
        return true;
    }

    private static List<BenchmarkCandidate> SelectForProfile(
        IReadOnlyList<BenchmarkCandidate> candidates,
        int requested,
        int seed,
        string profile)
    {
        if (string.Equals(profile, "resolved-opportunities", StringComparison.OrdinalIgnoreCase))
            return SelectResolvedOpportunities(candidates, requested, seed);
        return SelectStratified(candidates, requested, seed);
    }

    private static List<BenchmarkCandidate> SelectResolvedOpportunities(
        IReadOnlyList<BenchmarkCandidate> candidates,
        int requested,
        int seed)
    {
        var pool = candidates
            .Where(c => Eq(c.TeacherStructuralVerdict, "TRADE") && c.TeacherTopResolvedR.HasValue)
            .ToList();
        var n = Math.Min(requested, pool.Count);
        if (n <= 0) return [];

        var positive = pool.Where(c => c.TeacherTopResolvedR > 0m).ToList();
        var negative = pool.Where(c => c.TeacherTopResolvedR < 0m).ToList();
        var posTarget = Math.Min(positive.Count, (n + 1) / 2);
        var negTarget = Math.Min(negative.Count, n - posTarget);
        if (posTarget + negTarget < n)
        {
            var missing = n - posTarget - negTarget;
            var addPositive = Math.Min(missing, positive.Count - posTarget);
            posTarget += addPositive;
            missing -= addPositive;
            negTarget += Math.Min(missing, negative.Count - negTarget);
        }

        var selected = new List<BenchmarkCandidate>();
        selected.AddRange(TakeDiverse(positive, posTarget, seed + 41003));
        selected.AddRange(TakeDiverse(negative, negTarget, seed + 51007));

        if (selected.Count < n)
        {
            var keys = selected.Select(CandidateKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            selected.AddRange(pool
                .Where(c => !keys.Contains(CandidateKey(c)))
                .OrderBy(c => StableHash(seed + 61001, c))
                .Take(n - selected.Count));
        }

        return selected
            .OrderBy(c => StableHash(seed + 71003, c))
            .Take(n)
            .ToList();
    }

    private static async Task<HashSet<string>> LoadExcludedKeysAsync(string? selectionPath, CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(selectionPath)) return result;
        if (!File.Exists(selectionPath))
            throw new FileNotFoundException("Exclude-selection file not found.", selectionPath);

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(selectionPath, ct));
        if (!TryArray(doc.RootElement, out var selected, "selected")) return result;
        foreach (var item in selected.EnumerateArray())
        {
            var ticker = GetStringAny(item, "ticker", "Ticker")?.Trim().ToUpperInvariant();
            var asof = GetDateTimeAny(item, "asofUtc", "AsofUtc", "asof_utc");
            if (string.IsNullOrWhiteSpace(ticker) || !asof.HasValue) continue;
            result.Add($"{ticker}|{EnsureUtc(asof.Value):O}");
        }
        return result;
    }

    private static List<BenchmarkCandidate> SelectStratified(
        IReadOnlyList<BenchmarkCandidate> candidates,
        int requested,
        int seed)
    {
        var n = Math.Min(requested, candidates.Count);
        if (n <= 0) return [];

        var tradePool = candidates.Where(c => string.Equals(c.TeacherRawVerdict, "TRADE", StringComparison.OrdinalIgnoreCase)).ToList();
        var noTradePool = candidates.Where(c => !string.Equals(c.TeacherRawVerdict, "TRADE", StringComparison.OrdinalIgnoreCase)).ToList();

        var tradeTarget = Math.Min(tradePool.Count, n / 2);
        var noTradeTarget = Math.Min(noTradePool.Count, n - tradeTarget);
        if (tradeTarget + noTradeTarget < n)
        {
            var missing = n - tradeTarget - noTradeTarget;
            var tradeSpare = tradePool.Count - tradeTarget;
            var addTrade = Math.Min(missing, tradeSpare);
            tradeTarget += addTrade;
            missing -= addTrade;
            noTradeTarget += Math.Min(missing, noTradePool.Count - noTradeTarget);
        }

        var selected = new List<BenchmarkCandidate>();
        selected.AddRange(TakeDiverse(tradePool, tradeTarget, seed));
        selected.AddRange(TakeDiverse(noTradePool, noTradeTarget, seed + 100003));

        if (selected.Count < n)
        {
            var keys = selected.Select(CandidateKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var remaining = candidates
                .Where(c => !keys.Contains(CandidateKey(c)))
                .OrderBy(c => StableHash(seed + 200003, c))
                .Take(n - selected.Count);
            selected.AddRange(remaining);
        }

        return selected
            .OrderBy(c => StableHash(seed + 300007, c))
            .Take(n)
            .ToList();
    }

    private static List<BenchmarkCandidate> TakeDiverse(
        IReadOnlyList<BenchmarkCandidate> pool,
        int count,
        int seed)
    {
        if (count <= 0 || pool.Count == 0) return [];

        var groups = pool
            .GroupBy(c => c.StratumKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Queue<BenchmarkCandidate>(g.OrderBy(c => StableHash(seed, c))))
            .OrderBy(q => q.Count == 0 ? "" : StableHash(seed + 1, q.Peek()))
            .ToList();

        var selected = new List<BenchmarkCandidate>();
        var tickerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var tickerSoftCap = Math.Max(1, (int)Math.Ceiling(count / 4.0));

        // First pass: maximize stratum diversity while softly limiting ticker repetition.
        var progress = true;
        while (selected.Count < count && progress)
        {
            progress = false;
            foreach (var q in groups)
            {
                if (selected.Count >= count) break;
                while (q.Count > 0 && tickerCounts.GetValueOrDefault(q.Peek().Ticker) >= tickerSoftCap)
                {
                    // Rotate rather than discard so the candidate remains available in the fill pass.
                    var rotated = q.Dequeue();
                    q.Enqueue(rotated);
                    if (q.All(x => tickerCounts.GetValueOrDefault(x.Ticker) >= tickerSoftCap)) break;
                }
                if (q.Count == 0) continue;
                var next = q.Peek();
                if (tickerCounts.GetValueOrDefault(next.Ticker) >= tickerSoftCap) continue;
                q.Dequeue();
                selected.Add(next);
                tickerCounts[next.Ticker] = tickerCounts.GetValueOrDefault(next.Ticker) + 1;
                progress = true;
            }
        }

        // Fill without the ticker cap if necessary.
        var already = selected.Select(CandidateKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fill = pool
            .Where(c => !already.Contains(CandidateKey(c)))
            .OrderBy(c => StableHash(seed + 17, c))
            .Take(count - selected.Count);
        selected.AddRange(fill);
        return selected.Take(count).ToList();
    }

    private static string StableHash(int seed, BenchmarkCandidate c)
    {
        var value = $"{seed}|{c.Ticker}|{c.AsofUtc:O}|{c.TeacherRawVerdict}|{c.StratumKey}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string CandidateKey(BenchmarkCandidate c) => $"{c.Ticker}|{c.AsofUtc:O}";

    private static string SessionBucket(DateTime et)
    {
        var t = et.TimeOfDay;
        if (t < new TimeSpan(10, 30, 0)) return "opening";
        if (t < new TimeSpan(12, 0, 0)) return "morning";
        if (t < new TimeSpan(14, 0, 0)) return "midday";
        return "afternoon";
    }

    private static void PrintCompactComparison(BenchmarkRow row)
    {
        Console.WriteLine(
            $"  RESULT: teacher={row.TeacherStructuralVerdict} | " +
            $"no-memory={row.NoMemoryStructuralVerdict} valid={row.NoMemoryValidScenarios} pref={row.NoMemoryPreferredScenarios} {row.NoMemoryElapsedSeconds:0.0}s | " +
            $"memory={row.MemoryStructuralVerdict} valid={row.MemoryValidScenarios} pref={row.MemoryPreferredScenarios} {row.MemoryElapsedSeconds:0.0}s");
        Console.WriteLine(
            $"  OUTCOME: no-memory R={Fmt(row.NoMemoryResolvedR)} ({row.NoMemoryPrimaryOutcome ?? "n/a"}) | " +
            $"memory R={Fmt(row.MemoryResolvedR)} ({row.MemoryPrimaryOutcome ?? "n/a"}) | " +
            $"analogue_groups={row.MemorySetupGroups} avg_distance={Fmt(row.MemoryAverageDistance)}");
    }

    private static BenchmarkSummary BuildSummary(int selectedCount, IReadOnlyList<BenchmarkRow> rows, BenchmarkOptions options)
    {
        var completed = rows.Where(r => r.NoMemoryStatus == "ok" && r.MemoryStatus == "ok").ToList();
        return new BenchmarkSummary(
            Stage: "stage2c5_benchmark_v1",
            GeneratedUtc: DateTime.UtcNow,
            CorpusPath: Path.GetFullPath(options.CorpusPath),
            AnalogueIndexPath: Path.GetFullPath(options.AnalogueIndexPath),
            LocalModel: options.LocalModel,
            Seed: options.Seed,
            SelectionProfile: options.SelectionProfile,
            RequestedStates: options.SampleSize,
            SelectedStates: selectedCount,
            CompletedPairs: completed.Count,
            NoMemory: BuildArmSummary(completed, memory: false),
            Memory: BuildArmSummary(completed, memory: true),
            NoMemoryOpportunityDiagnostics: BuildOpportunityDiagnostics(completed, memory: false),
            MemoryOpportunityDiagnostics: BuildOpportunityDiagnostics(completed, memory: true));
    }

    private static ArmSummary BuildArmSummary(IReadOnlyList<BenchmarkRow> rows, bool memory)
    {
        var rawAgreement = rows.Count(r => Eq(memory ? r.MemoryRawVerdict : r.NoMemoryRawVerdict, r.TeacherRawVerdict));
        var structuralAgreement = rows.Count(r => Eq(memory ? r.MemoryStructuralVerdict : r.NoMemoryStructuralVerdict, r.TeacherStructuralVerdict));
        var tradeStates = rows.Count(r => Eq(memory ? r.MemoryStructuralVerdict : r.NoMemoryStructuralVerdict, "TRADE"));
        var validScenarios = rows.Sum(r => memory ? r.MemoryValidScenarios : r.NoMemoryValidScenarios);
        var preferred = rows.Sum(r => memory ? r.MemoryPreferredScenarios : r.NoMemoryPreferredScenarios);
        var secondary = rows.Sum(r => memory ? r.MemorySecondaryScenarios : r.NoMemorySecondaryScenarios);
        var elapsed = rows.Select(r => memory ? r.MemoryElapsedSeconds : r.NoMemoryElapsedSeconds).Where(x => x > 0).ToList();
        var resolvedR = rows.Select(r => memory ? r.MemoryResolvedR : r.NoMemoryResolvedR).Where(x => x.HasValue).Select(x => x!.Value).ToList();
        var triggered = rows.Count(r => (memory ? r.MemoryTriggered : r.NoMemoryTriggered) == true);
        var decisionR = rows.Select(r => (memory ? r.MemoryResolvedR : r.NoMemoryResolvedR) ?? 0m).ToList();

        return new ArmSummary(
            States: rows.Count,
            TeacherRawAgreementRate: Rate(rawAgreement, rows.Count),
            TeacherStructuralAgreementRate: Rate(structuralAgreement, rows.Count),
            StructuralTradeStateRate: Rate(tradeStates, rows.Count),
            StructurallyValidScenarios: validScenarios,
            PreferredScenarios: preferred,
            SecondaryScenarios: secondary,
            PreferredShareOfValid: Rate(preferred, validScenarios),
            MedianInferenceSeconds: Median(elapsed),
            MeanResolvedR: Mean(resolvedR),
            MeanDecisionRPerStateZeroNonResolved: Mean(decisionR),
            ExpectancyRPerTriggeredZeroUnresolved: triggered == 0 ? null : Math.Round(resolvedR.Sum() / triggered, 4),
            TriggeredSelections: triggered,
            ResolvedSelections: resolvedR.Count);
    }

    private static OpportunityDiagnostics BuildOpportunityDiagnostics(IReadOnlyList<BenchmarkRow> rows, bool memory)
    {
        var positives = rows.Where(r => r.TeacherTopResolvedR > 0m).ToList();
        var negatives = rows.Where(r => r.TeacherTopResolvedR < 0m).ToList();
        bool IsTrade(BenchmarkRow r) => Eq(memory ? r.MemoryStructuralVerdict : r.NoMemoryStructuralVerdict, "TRADE");
        string? Direction(BenchmarkRow r) => memory ? r.MemoryTopDirection : r.NoMemoryTopDirection;
        bool Triggered(BenchmarkRow r) => (memory ? r.MemoryTriggered : r.NoMemoryTriggered) == true;
        decimal? ResolvedR(BenchmarkRow r) => memory ? r.MemoryResolvedR : r.NoMemoryResolvedR;

        var tradeOnPositive = positives.Count(IsTrade);
        var triggeredOnPositive = positives.Count(r => IsTrade(r) && Triggered(r));
        var noTradeOnNegative = negatives.Count(r => !IsTrade(r));
        var directionMismatch = rows.Count(r => IsTrade(r) && Eq(r.TeacherStructuralVerdict, "TRADE") &&
            !string.IsNullOrWhiteSpace(r.TeacherTopDirection) && !string.IsNullOrWhiteSpace(Direction(r)) &&
            !Eq(r.TeacherTopDirection, Direction(r)));
        var resolved = rows.Select(ResolvedR).Where(x => x.HasValue).Select(x => x!.Value).ToList();

        return new OpportunityDiagnostics(
            TeacherPositiveStates: positives.Count,
            TeacherNegativeStates: negatives.Count,
            TradeOnTeacherPositiveStates: tradeOnPositive,
            TradeOnTeacherPositiveRate: Rate(tradeOnPositive, positives.Count),
            TriggeredOnTeacherPositiveStates: triggeredOnPositive,
            AvoidedTeacherNegativeStates: noTradeOnNegative,
            AvoidedTeacherNegativeRate: Rate(noTradeOnNegative, negatives.Count),
            DirectionMismatchWhenBothTrade: directionMismatch,
            ResolvedWins: resolved.Count(x => x > 0m),
            ResolvedLosses: resolved.Count(x => x < 0m),
            TotalResolvedR: resolved.Count == 0 ? null : Math.Round(resolved.Sum(), 4));
    }

    private static async Task WriteSelectionAsync(
        string outputDir,
        IReadOnlyList<BenchmarkCandidate> selected,
        BenchmarkOptions options,
        CancellationToken ct)
    {
        var payload = new
        {
            stage = "stage2c5_benchmark_v1",
            selection_profile = options.SelectionProfile,
            exclude_selection = options.ExcludeSelectionPath,
            seed = options.Seed,
            sample_size = selected.Count,
            local_model = options.LocalModel,
            selected = selected.Select((c, i) => new
            {
                state = i + 1,
                c.CorpusLine,
                c.Ticker,
                c.AsofUtc,
                c.AnalysisEt,
                c.TeacherRawVerdict,
                c.TeacherStructuralVerdict,
                c.TeacherTopDirection,
                c.TeacherTopEntryType,
                c.TeacherTopSelectionTier,
                c.TeacherTopResolvedR,
                c.TeacherTopOutcome,
                c.SessionBucket,
                c.OutcomeBucket,
                c.StratumKey
            })
        };
        await File.WriteAllTextAsync(
            Path.Combine(outputDir, "stage2c_benchmark_selection.json"),
            JsonSerializer.Serialize(payload, PrettyJsonOptions),
            ct);
    }

    private static async Task WriteProgressAsync(
        string outputDir,
        int selectedCount,
        IReadOnlyList<BenchmarkRow> rows,
        BenchmarkOptions options,
        CancellationToken ct)
    {
        var summary = BuildSummary(selectedCount, rows, options);
        await WriteFinalAsync(outputDir, rows, summary, ct);
    }

    private static async Task WriteFinalAsync(
        string outputDir,
        IReadOnlyList<BenchmarkRow> rows,
        BenchmarkSummary summary,
        CancellationToken ct)
    {
        await File.WriteAllTextAsync(
            Path.Combine(outputDir, "stage2c_benchmark_results.json"),
            JsonSerializer.Serialize(rows, PrettyJsonOptions),
            ct);
        await File.WriteAllTextAsync(
            Path.Combine(outputDir, "stage2c_benchmark_summary.json"),
            JsonSerializer.Serialize(summary, PrettyJsonOptions),
            ct);
        await WriteCsvAsync(Path.Combine(outputDir, "stage2c_benchmark_results.csv"), rows, ct);
    }

    private static async Task WriteCsvAsync(string path, IReadOnlyList<BenchmarkRow> rows, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("state,ticker,analysis_et,session_bucket,stratum,teacher_raw_verdict,teacher_structural_verdict,teacher_top_direction,teacher_top_entry_type,teacher_top_tier,teacher_top_resolved_r,teacher_top_outcome,no_memory_status,no_memory_raw_verdict,no_memory_structural_verdict,no_memory_valid,no_memory_preferred,no_memory_secondary,no_memory_top_direction,no_memory_top_entry_type,no_memory_top_tier,no_memory_elapsed_seconds,no_memory_triggered,no_memory_outcome,no_memory_resolved_r,memory_status,memory_raw_verdict,memory_structural_verdict,memory_valid,memory_preferred,memory_secondary,memory_top_direction,memory_top_entry_type,memory_top_tier,memory_elapsed_seconds,memory_triggered,memory_outcome,memory_resolved_r,memory_returned_analogues,memory_setup_groups,memory_avg_distance");
        foreach (var r in rows)
        {
            var values = new object?[]
            {
                r.State, r.Ticker, r.AnalysisEt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), r.SessionBucket, r.StratumKey,
                r.TeacherRawVerdict, r.TeacherStructuralVerdict, r.TeacherTopDirection, r.TeacherTopEntryType, r.TeacherTopSelectionTier, r.TeacherTopResolvedR, r.TeacherTopOutcome,
                r.NoMemoryStatus, r.NoMemoryRawVerdict, r.NoMemoryStructuralVerdict, r.NoMemoryValidScenarios, r.NoMemoryPreferredScenarios, r.NoMemorySecondaryScenarios, r.NoMemoryTopDirection, r.NoMemoryTopEntryType, r.NoMemoryTopSelectionTier, r.NoMemoryElapsedSeconds, r.NoMemoryTriggered, r.NoMemoryPrimaryOutcome, r.NoMemoryResolvedR,
                r.MemoryStatus, r.MemoryRawVerdict, r.MemoryStructuralVerdict, r.MemoryValidScenarios, r.MemoryPreferredScenarios, r.MemorySecondaryScenarios, r.MemoryTopDirection, r.MemoryTopEntryType, r.MemoryTopSelectionTier, r.MemoryElapsedSeconds, r.MemoryTriggered, r.MemoryPrimaryOutcome, r.MemoryResolvedR,
                r.MemoryReturnedAnalogues, r.MemorySetupGroups, r.MemoryAverageDistance
            };
            sb.AppendLine(string.Join(',', values.Select(Csv)));
        }
        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(false), ct);
    }

    private static void PrintSummary(BenchmarkSummary s)
    {
        Console.WriteLine($"Completed pairs : {s.CompletedPairs}/{s.SelectedStates}");
        Console.WriteLine($"Teacher structural agreement: no-memory={Pct(s.NoMemory.TeacherStructuralAgreementRate)} memory={Pct(s.Memory.TeacherStructuralAgreementRate)}");
        Console.WriteLine($"Preferred share of valid     : no-memory={Pct(s.NoMemory.PreferredShareOfValid)} memory={Pct(s.Memory.PreferredShareOfValid)}");
        Console.WriteLine($"Mean decision R/state        : no-memory={Fmt(s.NoMemory.MeanDecisionRPerStateZeroNonResolved)} memory={Fmt(s.Memory.MeanDecisionRPerStateZeroNonResolved)}");
        Console.WriteLine($"Expectancy R/triggered       : no-memory={Fmt(s.NoMemory.ExpectancyRPerTriggeredZeroUnresolved)} memory={Fmt(s.Memory.ExpectancyRPerTriggeredZeroUnresolved)}");
        Console.WriteLine($"Median inference seconds     : no-memory={Fmt(s.NoMemory.MedianInferenceSeconds)} memory={Fmt(s.Memory.MedianInferenceSeconds)}");
        Console.WriteLine($"Teacher-positive TRADE rate : no-memory={Pct(s.NoMemoryOpportunityDiagnostics.TradeOnTeacherPositiveRate)} memory={Pct(s.MemoryOpportunityDiagnostics.TradeOnTeacherPositiveRate)}");
        Console.WriteLine($"Teacher-negative avoidance  : no-memory={Pct(s.NoMemoryOpportunityDiagnostics.AvoidedTeacherNegativeRate)} memory={Pct(s.MemoryOpportunityDiagnostics.AvoidedTeacherNegativeRate)}");
        Console.WriteLine($"Direction mismatches         : no-memory={s.NoMemoryOpportunityDiagnostics.DirectionMismatchWhenBothTrade} memory={s.MemoryOpportunityDiagnostics.DirectionMismatchWhenBothTrade}");
        Console.WriteLine($"Total resolved R             : no-memory={Fmt(s.NoMemoryOpportunityDiagnostics.TotalResolvedR)} memory={Fmt(s.MemoryOpportunityDiagnostics.TotalResolvedR)}");
    }

    private static BenchmarkOptions ParseOptions(string[] args)
    {
        var corpus = ReadOption(args, "--corpus")
                     ?? throw new ArgumentException("--corpus=<Stage 2B.4 corpus JSONL> is required.");
        var analogue = ReadOption(args, "--analogue-index")
                       ?? throw new ArgumentException("--analogue-index=<Stage 2C index JSON> is required.");
        var model = ReadOption(args, "--local-model")
                    ?? Environment.GetEnvironmentVariable("LOCAL_LLM_MODEL")
                    ?? "gpt-oss:20b";
        var localUrl = ReadOption(args, "--local-url")
                       ?? Environment.GetEnvironmentVariable("LOCAL_LLM_BASE_URL")
                       ?? "http://localhost:11434";
        var outputDir = ReadOption(args, "--output-dir") ?? "stage2c_benchmark";
        var selectionProfile = (ReadOption(args, "--selection-profile") ?? "balanced").Trim().ToLowerInvariant();
        if (selectionProfile != "balanced" && selectionProfile != "resolved-opportunities")
            throw new ArgumentException("--selection-profile must be balanced or resolved-opportunities.");
        var excludeSelection = ReadOption(args, "--exclude-selection");

        return new BenchmarkOptions(
            CorpusPath: Path.GetFullPath(corpus),
            AnalogueIndexPath: Path.GetFullPath(analogue),
            LocalModel: model,
            LocalBaseUrl: localUrl,
            LocalTimeoutSeconds: ParseInt(ReadOption(args, "--local-timeout-seconds"), 600, 30, 3600, "--local-timeout-seconds"),
            LocalContextTokens: ParseInt(ReadOption(args, "--local-context-tokens"), 32768, 4096, 262144, "--local-context-tokens"),
            AnalogueTopN: ParseInt(ReadOption(args, "--analogue-top"), 24, 1, 200, "--analogue-top"),
            SampleSize: ParseInt(ReadOption(args, "--sample"), 10, 2, 100, "--sample"),
            Seed: ParseInt(ReadOption(args, "--seed"), 42, 0, int.MaxValue, "--seed"),
            SelectionProfile: selectionProfile,
            ExcludeSelectionPath: string.IsNullOrWhiteSpace(excludeSelection) ? null : Path.GetFullPath(excludeSelection),
            OutputDir: Path.GetFullPath(outputDir),
            Rerun: HasFlag(args, "--rerun"));
    }

    public static void PrintHelp()
    {
        Console.WriteLine("AVA Stage 2C.5 benchmark");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run -- --historical-shadow --stage2c-benchmark `");
        Console.WriteLine("    --corpus=.\\historical_corpus_stage2c_source\\ava_corpus_all_....jsonl `");
        Console.WriteLine("    --analogue-index=.\\historical_corpus_stage2c_source\\ava_analogue_index.json `");
        Console.WriteLine("    --local-model=gpt-oss:20b --sample=10 --seed=42 --output-dir=.\\stage2c_benchmark");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --sample                 Number of deterministic stratified states (default 10).");
        Console.WriteLine("  --seed                   Stable sampling seed (default 42).");
        Console.WriteLine("  --selection-profile      balanced (default) or resolved-opportunities.");
        Console.WriteLine("  --exclude-selection      Prior stage2c_benchmark_selection.json to exclude already-run states.");
        Console.WriteLine("  --local-model            Ollama model (default LOCAL_LLM_MODEL or gpt-oss:20b).");
        Console.WriteLine("  --analogue-top           Memory analogue count (default 24).");
        Console.WriteLine("  --local-url              Ollama URL (default http://localhost:11434).");
        Console.WriteLine("  --local-timeout-seconds  Per-call timeout (default 600).");
        Console.WriteLine("  --local-context-tokens   Ollama num_ctx (default 32768).");
        Console.WriteLine("  --output-dir             Benchmark output directory (default stage2c_benchmark).");
        Console.WriteLine("  --rerun                  Ignore completed per-state outputs and rerun all calls.");
        Console.WriteLine();
        Console.WriteLine("Benchmark repair is intentionally disabled so the only treatment difference is Stage 2C memory.");
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

    private static DateTime EnsureUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static DateTime FromUtc(DateTime value)
        => DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(EnsureUtc(value), EasternTz), DateTimeKind.Unspecified);

    private static DateTime ToUtc(DateTime et)
        => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(et, DateTimeKind.Unspecified), EasternTz);

    private static string SafeName(string value)
        => new(value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());

    private static decimal Rate(int numerator, int denominator)
        => denominator == 0 ? 0m : Math.Round((decimal)numerator / denominator, 4);

    private static decimal? Mean(IReadOnlyList<decimal> values)
        => values.Count == 0 ? null : Math.Round(values.Average(), 4);

    private static decimal? Median(IReadOnlyList<decimal> values)
    {
        if (values.Count == 0) return null;
        var ordered = values.OrderBy(x => x).ToList();
        var mid = ordered.Count / 2;
        var value = ordered.Count % 2 == 1 ? ordered[mid] : (ordered[mid - 1] + ordered[mid]) / 2m;
        return Math.Round(value, 2);
    }

    private static bool Eq(string? a, string? b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Fmt(decimal? value)
        => value.HasValue ? value.Value.ToString("0.000", CultureInfo.InvariantCulture) : "n/a";

    private static string Pct(decimal value) => (value * 100m).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private static string Csv(object? value)
    {
        if (value is null) return "";
        var text = value switch
        {
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            double d => d.ToString(CultureInfo.InvariantCulture),
            float f => f.ToString(CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
        };
        if (!text.Contains(',') && !text.Contains('"') && !text.Contains('\n') && !text.Contains('\r')) return text;
        return '"' + text.Replace("\"", "\"\"") + '"';
    }

    private static bool TryObject(JsonElement parent, out JsonElement value, string name)
    {
        value = default;
        return parent.ValueKind == JsonValueKind.Object &&
               parent.TryGetProperty(name, out value) &&
               value.ValueKind == JsonValueKind.Object;
    }

    private static bool TryObjectAny(JsonElement parent, out JsonElement value, params string[] names)
    {
        foreach (var name in names)
            if (TryObject(parent, out value, name)) return true;
        value = default;
        return false;
    }

    private static bool TryNestedObject(JsonElement parent, out JsonElement value, string first, string second)
    {
        value = default;
        return TryObject(parent, out var a, first) && TryObject(a, out value, second);
    }

    private static bool TryArray(JsonElement parent, out JsonElement value, string name)
    {
        value = default;
        return parent.ValueKind == JsonValueKind.Object &&
               parent.TryGetProperty(name, out value) &&
               value.ValueKind == JsonValueKind.Array;
    }

    private static bool TryArrayAny(JsonElement parent, out JsonElement value, params string[] names)
    {
        foreach (var name in names)
            if (TryArray(parent, out value, name)) return true;
        value = default;
        return false;
    }

    private static string? GetStringAny(JsonElement parent, params string[] names)
    {
        foreach (var name in names)
        {
            if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        }
        return null;
    }

    private static int? GetIntAny(JsonElement parent, params string[] names)
    {
        foreach (var name in names)
        {
            if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        }
        return null;
    }

    private static long? GetLongAny(JsonElement parent, params string[] names)
    {
        foreach (var name in names)
        {
            if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        }
        return null;
    }

    private static decimal? GetDecimalAny(JsonElement parent, params string[] names)
    {
        foreach (var name in names)
        {
            if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var n)) return n;
        }
        return null;
    }

    private static bool? GetBoolAny(JsonElement parent, params string[] names)
    {
        foreach (var name in names)
        {
            if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var v)) continue;
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
        }
        return null;
    }

    private static DateTime? GetDateTimeAny(JsonElement parent, params string[] names)
    {
        foreach (var name in names)
        {
            if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var v)) continue;
            if (v.ValueKind == JsonValueKind.String && DateTime.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
                return EnsureUtc(dt);
        }
        return null;
    }

    private sealed record BenchmarkOptions(
        string CorpusPath,
        string AnalogueIndexPath,
        string LocalModel,
        string LocalBaseUrl,
        int LocalTimeoutSeconds,
        int LocalContextTokens,
        int AnalogueTopN,
        int SampleSize,
        int Seed,
        string SelectionProfile,
        string? ExcludeSelectionPath,
        string OutputDir,
        bool Rerun);

    internal sealed record BenchmarkCandidate(
        int CorpusLine,
        string Ticker,
        DateTime AsofUtc,
        DateTime AnalysisEt,
        string TeacherRawVerdict,
        string TeacherStructuralVerdict,
        string? TeacherTopDirection,
        string? TeacherTopEntryType,
        string? TeacherTopSelectionTier,
        decimal? TeacherTopResolvedR,
        bool? TeacherTopTriggered,
        string? TeacherTopOutcome,
        string SessionBucket,
        string OutcomeBucket,
        string StratumKey);

    internal sealed record HistoricalAnalogueSummary(
        int ReturnedAnalogueCount,
        int EligiblePriorSessionRecords,
        decimal? AverageDistance,
        int SetupGroups)
    {
        public static readonly HistoricalAnalogueSummary Empty = new(0, 0, null, 0);
    }

    internal sealed record ReplayObservation(
        string Status,
        string? Error,
        bool ParseSuccess,
        string RawVerdict,
        int RawScenarioCount,
        string StructuralVerdict,
        int StructurallyValidScenarioCount,
        int PreferredScenarioCount,
        int SecondaryScenarioCount,
        string? TopDirection,
        string? TopEntryType,
        string? TopSelectionTier,
        ExecutionScenarioJsonV1? SelectedScenario,
        string? CompactDatasetJson,
        long ElapsedMs,
        int? PromptTokens,
        int? OutputTokens,
        HistoricalAnalogueSummary Analogue,
        bool Reused)
    {
        public bool IsUsable => Status == "ok" && ParseSuccess;
        public static ReplayObservation Missing(string error)
            => new("missing", error, false, "n/a", 0, "n/a", 0, 0, 0, null, null, null, null, null, 0, null, null, HistoricalAnalogueSummary.Empty, false);
    }

    internal sealed record LocalOutcomeSummary(
        bool HasTrade,
        bool? Triggered,
        string? PrimaryOutcome,
        bool? T1BeforeStop,
        decimal? ResolvedR,
        decimal? MfeR,
        string OutcomeStatus)
    {
        public static readonly LocalOutcomeSummary NoTrade = new(false, false, "NO_TRADE", null, null, null, "NO_TRADE");
    }

    public sealed record BenchmarkRow(
        int State,
        string Ticker,
        DateTime AnalysisEt,
        string SessionBucket,
        string StratumKey,
        string TeacherRawVerdict,
        string TeacherStructuralVerdict,
        string? TeacherTopDirection,
        string? TeacherTopEntryType,
        string? TeacherTopSelectionTier,
        decimal? TeacherTopResolvedR,
        string? TeacherTopOutcome,
        string NoMemoryStatus,
        string NoMemoryRawVerdict,
        string NoMemoryStructuralVerdict,
        int NoMemoryValidScenarios,
        int NoMemoryPreferredScenarios,
        int NoMemorySecondaryScenarios,
        string? NoMemoryTopDirection,
        string? NoMemoryTopEntryType,
        string? NoMemoryTopSelectionTier,
        decimal NoMemoryElapsedSeconds,
        bool? NoMemoryTriggered,
        string? NoMemoryPrimaryOutcome,
        decimal? NoMemoryResolvedR,
        string MemoryStatus,
        string MemoryRawVerdict,
        string MemoryStructuralVerdict,
        int MemoryValidScenarios,
        int MemoryPreferredScenarios,
        int MemorySecondaryScenarios,
        string? MemoryTopDirection,
        string? MemoryTopEntryType,
        string? MemoryTopSelectionTier,
        decimal MemoryElapsedSeconds,
        bool? MemoryTriggered,
        string? MemoryPrimaryOutcome,
        decimal? MemoryResolvedR,
        int MemoryReturnedAnalogues,
        int MemorySetupGroups,
        decimal? MemoryAverageDistance)
    {
        internal static BenchmarkRow Create(
            int stateNumber,
            BenchmarkCandidate c,
            ReplayObservation noMemory,
            ReplayObservation memory,
            LocalOutcomeSummary noMemoryOutcome,
            LocalOutcomeSummary memoryOutcome)
            => new(
                stateNumber,
                c.Ticker,
                c.AnalysisEt,
                c.SessionBucket,
                c.StratumKey,
                c.TeacherRawVerdict,
                c.TeacherStructuralVerdict,
                c.TeacherTopDirection,
                c.TeacherTopEntryType,
                c.TeacherTopSelectionTier,
                c.TeacherTopResolvedR,
                c.TeacherTopOutcome,
                noMemory.Status,
                noMemory.RawVerdict,
                noMemory.StructuralVerdict,
                noMemory.StructurallyValidScenarioCount,
                noMemory.PreferredScenarioCount,
                noMemory.SecondaryScenarioCount,
                noMemory.TopDirection,
                noMemory.TopEntryType,
                noMemory.TopSelectionTier,
                Math.Round(noMemory.ElapsedMs / 1000m, 2),
                noMemoryOutcome.Triggered,
                noMemoryOutcome.PrimaryOutcome,
                noMemoryOutcome.ResolvedR,
                memory.Status,
                memory.RawVerdict,
                memory.StructuralVerdict,
                memory.StructurallyValidScenarioCount,
                memory.PreferredScenarioCount,
                memory.SecondaryScenarioCount,
                memory.TopDirection,
                memory.TopEntryType,
                memory.TopSelectionTier,
                Math.Round(memory.ElapsedMs / 1000m, 2),
                memoryOutcome.Triggered,
                memoryOutcome.PrimaryOutcome,
                memoryOutcome.ResolvedR,
                memory.Analogue.ReturnedAnalogueCount,
                memory.Analogue.SetupGroups,
                memory.Analogue.AverageDistance);
    }

    public sealed record ArmSummary(
        int States,
        decimal TeacherRawAgreementRate,
        decimal TeacherStructuralAgreementRate,
        decimal StructuralTradeStateRate,
        int StructurallyValidScenarios,
        int PreferredScenarios,
        int SecondaryScenarios,
        decimal PreferredShareOfValid,
        decimal? MedianInferenceSeconds,
        decimal? MeanResolvedR,
        decimal? MeanDecisionRPerStateZeroNonResolved,
        decimal? ExpectancyRPerTriggeredZeroUnresolved,
        int TriggeredSelections,
        int ResolvedSelections);

    public sealed record OpportunityDiagnostics(
        int TeacherPositiveStates,
        int TeacherNegativeStates,
        int TradeOnTeacherPositiveStates,
        decimal TradeOnTeacherPositiveRate,
        int TriggeredOnTeacherPositiveStates,
        int AvoidedTeacherNegativeStates,
        decimal AvoidedTeacherNegativeRate,
        int DirectionMismatchWhenBothTrade,
        int ResolvedWins,
        int ResolvedLosses,
        decimal? TotalResolvedR);

    public sealed record BenchmarkSummary(
        string Stage,
        DateTime GeneratedUtc,
        string CorpusPath,
        string AnalogueIndexPath,
        string LocalModel,
        int Seed,
        string SelectionProfile,
        int RequestedStates,
        int SelectedStates,
        int CompletedPairs,
        ArmSummary NoMemory,
        ArmSummary Memory,
        OpportunityDiagnostics NoMemoryOpportunityDiagnostics,
        OpportunityDiagnostics MemoryOpportunityDiagnostics);
}
