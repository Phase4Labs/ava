using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace get_assessment_no_graph;

/// <summary>
/// Stage 2C deterministic historical analogue retrieval.
///
/// The index is built from Stage 2B corpus JSONL and contains only compact numeric
/// market-state features plus teacher/outcome summaries. Queries are causal:
/// records from the query's own US/Eastern session date are excluded because their
/// outcome labels were evaluated through that session close.
/// </summary>
public sealed class HistoricalAnalogueIndex
{
    public const string Version = "stage2c_analogue_v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    private readonly AnalogueIndexDocument _doc;

    private HistoricalAnalogueIndex(AnalogueIndexDocument doc) => _doc = doc;

    public int RecordCount => _doc.Records.Count;

    public static async Task<int> BuildAsync(string corpusPath, string outputPath, CancellationToken ct = default)
    {
        if (!File.Exists(corpusPath))
            throw new FileNotFoundException("Corpus JSONL not found.", corpusPath);

        var records = new List<AnalogueIndexRecord>();
        var lineNumber = 0;
        await foreach (var line in File.ReadLinesAsync(corpusPath, ct))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!TryBuildRecord(root, out var record) || record is null) continue;
                records.Add(record);
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"ANALOGUE_INDEX skip line={lineNumber} invalid_json={ex.Message}");
            }
        }

        if (records.Count == 0)
            throw new InvalidOperationException("No usable Stage 2B corpus records were found.");

        var structurallyValidScenarioCount = records.Sum(r => r.Scenarios.Count(s => s.StructurallyValid));
        if (structurallyValidScenarioCount == 0)
        {
            throw new InvalidOperationException(
                "Stage 2B.4 metadata was present but no structurally valid scenarios could be aligned " +
                "to corpus scenarios by rank or ordinal position.");
        }

        var stats = BuildFeatureStats(records);
        var index = new AnalogueIndexDocument(
            Version,
            DateTime.UtcNow,
            Path.GetFullPath(corpusPath),
            records.Count,
            stats,
            records);

        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(index, JsonOptions), ct);

        Console.WriteLine("AVA Stage 2C analogue index build complete.");
        Console.WriteLine($"Corpus  : {Path.GetFullPath(corpusPath)}");
        Console.WriteLine($"Records : {records.Count:N0}");
        Console.WriteLine($"Valid scenarios: {structurallyValidScenarioCount:N0}");
        Console.WriteLine($"Features: {stats.Count:N0}");
        Console.WriteLine($"Output  : {Path.GetFullPath(outputPath)}");
        return 0;
    }

    public static HistoricalAnalogueIndex Load(string path)
    {
        var json = File.ReadAllText(path);
        var doc = JsonSerializer.Deserialize<AnalogueIndexDocument>(json, JsonOptions)
                  ?? throw new InvalidOperationException("Analogue index could not be parsed.");
        if (!string.Equals(doc.IndexVersion, Version, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unsupported analogue index version '{doc.IndexVersion}'. Expected '{Version}'.");
        return new HistoricalAnalogueIndex(doc);
    }

    public HistoricalAnalogueContext Query(string compactDatasetJson, int topN = 24)
    {
        using var queryDoc = JsonDocument.Parse(compactDatasetJson);
        var root = queryDoc.RootElement;
        var queryAsOf = GetDateTime(root, "ts_asof_utc") ?? DateTime.UtcNow;
        var queryTicker = GetString(root, "ticker") ?? "";
        var queryFeatures = ExtractFeatures(root);
        var queryRegime = GetNestedString(root, "deterministic_state", "regime") ?? "unknown";
        var querySessionDate = ToEastern(queryAsOf).Date;

        var scored = new List<(AnalogueIndexRecord Record, decimal Distance, int Shared)>();
        foreach (var record in _doc.Records)
        {
            // Strict causal boundary for outcome-bearing examples: only completed PRIOR
            // sessions may inform a query. Same-day corpus examples contain labels through
            // that day's close and would leak future information during replay.
            if (record.SessionDateEt.Date >= querySessionDate) continue;

            var (distance, shared) = Distance(queryFeatures, queryRegime, queryTicker, record);
            if (shared < 5) continue;
            scored.Add((record, distance, shared));
        }

        var diversified = new List<(AnalogueIndexRecord Record, decimal Distance, int Shared)>();
        var tickerDayCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var tickerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in scored.OrderBy(x => x.Distance))
        {
            var dayKey = $"{item.Record.Ticker}|{item.Record.SessionDateEt:yyyy-MM-dd}";
            if (tickerDayCounts.GetValueOrDefault(dayKey) >= 2) continue;
            if (tickerCounts.GetValueOrDefault(item.Record.Ticker) >= 4) continue;

            diversified.Add(item);
            tickerDayCounts[dayKey] = tickerDayCounts.GetValueOrDefault(dayKey) + 1;
            tickerCounts[item.Record.Ticker] = tickerCounts.GetValueOrDefault(item.Record.Ticker) + 1;
            if (diversified.Count >= Math.Max(1, topN)) break;
        }

        var setupGroups = diversified
            .SelectMany(x => x.Record.Scenarios.Select(s => new { x.Distance, Scenario = s }))
            .Where(x => x.Scenario.StructurallyValid)
            .GroupBy(x => $"{x.Scenario.Direction}|{x.Scenario.EntryType}", StringComparer.OrdinalIgnoreCase)
            .Select(g => BuildSetupAggregate(g.Key, g.Select(x => x.Scenario).ToList()))
            .OrderByDescending(x => x.ResolvedRSamples)
            .ThenByDescending(x => x.Triggered)
            .ToList();

        var examples = diversified.Take(8).Select(x => new HistoricalAnalogueExample(
            x.Record.Ticker,
            x.Record.AsOfUtc,
            Math.Round(x.Distance, 4),
            x.Shared,
            x.Record.RawVerdict,
            x.Record.StructuralVerdict,
            x.Record.PreferredScenarioCount,
            x.Record.SecondaryScenarioCount,
            x.Record.Scenarios.Where(s => s.StructurallyValid).Take(3).ToList())).ToList();

        return new HistoricalAnalogueContext(
            ContextVersion: Version,
            CausalityRule: "Only completed US/Eastern sessions strictly before the query session date are eligible; same-day outcome labels are excluded.",
            QueryTicker: queryTicker,
            QueryAsOfUtc: queryAsOf,
            EligiblePriorSessionRecords: scored.Count,
            ReturnedAnalogues: diversified.Count,
            AverageDistance: diversified.Count == 0 ? null : Math.Round(diversified.Average(x => x.Distance), 4),
            SetupOutcomes: setupGroups,
            Examples: examples);
    }

    /// <summary>
    /// Stage 2C.6 candidate-conditioned evidence query.
    ///
    /// Unlike Query(), this method is never intended to be attached to an LLM prompt.
    /// It evaluates one already-proposed, structurally valid direction/setup against
    /// causally eligible prior-session states. One matching scenario per historical
    /// record is retained so a single old card cannot overweight the evidence.
    /// Historical price levels are intentionally excluded from the returned sidecar.
    /// </summary>
    public CandidateScenarioHistoricalEvidence QueryCandidateEvidence(
        string compactDatasetJson,
        int scenarioRank,
        string direction,
        string entryType,
        int topN = 24,
        DateTime? maxEvidenceSessionDateExclusive = null)
    {
        direction = (direction ?? "").Trim().ToLowerInvariant();
        entryType = (entryType ?? "").Trim().ToLowerInvariant();
        topN = Math.Max(1, topN);

        using var queryDoc = JsonDocument.Parse(compactDatasetJson);
        var root = queryDoc.RootElement;
        var queryAsOf = GetDateTime(root, "ts_asof_utc") ?? DateTime.UtcNow;
        var queryTicker = GetString(root, "ticker") ?? "";
        var queryFeatures = ExtractFeatures(root);
        var queryRegime = GetNestedString(root, "deterministic_state", "regime") ?? "unknown";
        var querySessionDate = ToEastern(queryAsOf).Date;

        var eligible = new List<(AnalogueIndexRecord Record, AnalogueScenarioSummary Scenario, decimal Distance, int Shared)>();
        foreach (var record in _doc.Records)
        {
            // Same strict causal boundary as the original analogue query. Outcome-bearing
            // examples from the query session are never eligible.
            if (record.SessionDateEt.Date >= querySessionDate) continue;
            if (maxEvidenceSessionDateExclusive.HasValue &&
                record.SessionDateEt.Date >= maxEvidenceSessionDateExclusive.Value.Date) continue;

            var (distance, shared) = Distance(queryFeatures, queryRegime, queryTicker, record);
            if (shared < 5) continue;

            // Candidate conditioning happens AFTER the local model has proposed a setup.
            // Keep at most one same-direction/same-setup scenario from each historical
            // state to avoid correlated duplicates from one old execution card.
            var matchingScenario = record.Scenarios
                .Where(s => s.StructurallyValid &&
                            string.Equals(s.Direction, direction, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(s.EntryType, entryType, StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => string.Equals(s.SelectionTier, "PREFERRED", StringComparison.OrdinalIgnoreCase) ? 0 :
                              string.Equals(s.SelectionTier, "SECONDARY", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                .ThenBy(s => s.ScenarioRank)
                .FirstOrDefault();

            if (matchingScenario is null) continue;
            eligible.Add((record, matchingScenario, distance, shared));
        }

        var selected = new List<(AnalogueIndexRecord Record, AnalogueScenarioSummary Scenario, decimal Distance, int Shared)>();
        var tickerDayCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var tickerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in eligible.OrderBy(x => x.Distance))
        {
            var dayKey = $"{item.Record.Ticker}|{item.Record.SessionDateEt:yyyy-MM-dd}";
            if (tickerDayCounts.GetValueOrDefault(dayKey) >= 2) continue;
            if (tickerCounts.GetValueOrDefault(item.Record.Ticker) >= 4) continue;

            selected.Add(item);
            tickerDayCounts[dayKey] = tickerDayCounts.GetValueOrDefault(dayKey) + 1;
            tickerCounts[item.Record.Ticker] = tickerCounts.GetValueOrDefault(item.Record.Ticker) + 1;
            if (selected.Count >= topN) break;
        }

        var knownTrigger = selected.Count(x => x.Scenario.Triggered.HasValue);
        var triggered = selected.Count(x => x.Scenario.Triggered == true);
        var notTriggered = selected.Count(x => x.Scenario.Triggered == false);
        var t1 = selected.Count(x => x.Scenario.T1BeforeStop == true ||
                                     string.Equals(x.Scenario.PrimaryOutcome, "T1_BEFORE_STOP", StringComparison.OrdinalIgnoreCase));
        var stop = selected.Count(x => string.Equals(x.Scenario.PrimaryOutcome, "STOP_BEFORE_T1", StringComparison.OrdinalIgnoreCase));
        var resolved = selected.Where(x => x.Scenario.ResolvedR.HasValue).Select(x => x.Scenario.ResolvedR!.Value).ToList();
        var preferred = selected.Count(x => string.Equals(x.Scenario.SelectionTier, "PREFERRED", StringComparison.OrdinalIgnoreCase));
        var secondary = selected.Count(x => string.Equals(x.Scenario.SelectionTier, "SECONDARY", StringComparison.OrdinalIgnoreCase));
        var positiveResolved = resolved.Count(x => x > 0m);
        var negativeResolved = resolved.Count(x => x < 0m);

        var examples = selected.Take(6).Select(x => new CandidateHistoricalEvidenceExample(
            x.Record.Ticker,
            x.Record.AsOfUtc,
            Math.Round(x.Distance, 4),
            x.Shared,
            x.Scenario.SelectionTier,
            x.Scenario.Triggered,
            x.Scenario.PrimaryOutcome,
            x.Scenario.ResolvedR)).ToList();

        return new CandidateScenarioHistoricalEvidence(
            EvidenceVersion: "stage2c6_candidate_evidence_v1",
            CausalityRule: "Only completed US/Eastern sessions strictly before the query session date are eligible; historical price levels are not exposed.",
            ScenarioRank: scenarioRank,
            Direction: direction,
            EntryType: entryType,
            QueryTicker: queryTicker,
            QueryAsOfUtc: queryAsOf,
            EligibleMatchingRecords: eligible.Count,
            ReturnedAnalogueRecords: selected.Count,
            AverageDistance: selected.Count == 0 ? null : Math.Round(selected.Average(x => x.Distance), 4),
            SameTickerRecords: selected.Count(x => string.Equals(x.Record.Ticker, queryTicker, StringComparison.OrdinalIgnoreCase)),
            KnownTriggerOutcomes: knownTrigger,
            Triggered: triggered,
            NotTriggered: notTriggered,
            TriggerRate: knownTrigger == 0 ? null : Math.Round((decimal)triggered / knownTrigger, 4),
            T1BeforeStop: t1,
            StopBeforeT1: stop,
            ResolvedRSamples: resolved.Count,
            PositiveResolved: positiveResolved,
            NegativeResolved: negativeResolved,
            MeanRealizedR: resolved.Count == 0 ? null : Math.Round(resolved.Average(), 4),
            MedianRealizedR: resolved.Count == 0 ? null : Math.Round(MedianDecimal(resolved), 4),
            T1RateIfResolved: (t1 + stop) == 0 ? null : Math.Round((decimal)t1 / (t1 + stop), 4),
            ExpectancyRPerTriggeredZeroUnresolved: triggered == 0 ? null : Math.Round(resolved.Sum() / triggered, 4),
            PreferredCount: preferred,
            SecondaryCount: secondary,
            Examples: examples);
    }

    private static decimal MedianDecimal(IReadOnlyList<decimal> values)
    {
        if (values.Count == 0) return 0m;
        var ordered = values.OrderBy(x => x).ToArray();
        var mid = ordered.Length / 2;
        return ordered.Length % 2 == 1
            ? ordered[mid]
            : (ordered[mid - 1] + ordered[mid]) / 2m;
    }

    public static string AttachContext(string compactDatasetJson, HistoricalAnalogueContext context)
    {
        var node = JsonNode.Parse(compactDatasetJson) as JsonObject
                   ?? throw new InvalidOperationException("Compact dataset must be a JSON object.");
        node["historical_analogue_context"] = JsonSerializer.SerializeToNode(context, JsonOptions);
        return node.ToJsonString(JsonOptions);
    }

    private (decimal Distance, int Shared) Distance(
        IReadOnlyDictionary<string, decimal?> query,
        string queryRegime,
        string queryTicker,
        AnalogueIndexRecord record)
    {
        decimal sum = 0m;
        decimal weightSum = 0m;
        var shared = 0;
        foreach (var stat in _doc.FeatureStats)
        {
            if (!query.TryGetValue(stat.Key, out var q) || !q.HasValue) continue;
            if (!record.Features.TryGetValue(stat.Key, out var r) || !r.HasValue) continue;
            var scale = stat.Value.StdDev <= 0.0001m ? 1m : stat.Value.StdDev;
            var z = (q.Value - r.Value) / scale;
            var w = FeatureWeight(stat.Key);
            sum += w * z * z;
            weightSum += w;
            shared++;
        }

        var numeric = weightSum > 0 ? (decimal)Math.Sqrt((double)(sum / weightSum)) : 999m;
        var regimePenalty = string.Equals(queryRegime, record.Regime, StringComparison.OrdinalIgnoreCase) ? 0m : 0.35m;
        var tickerBonus = string.Equals(queryTicker, record.Ticker, StringComparison.OrdinalIgnoreCase) ? -0.05m : 0m;
        return (Math.Max(0m, numeric + regimePenalty + tickerBonus), shared);
    }

    private static decimal FeatureWeight(string key) => key switch
    {
        "distance_to_vwap_pct" => 1.6m,
        "session_change_pct" => 1.4m,
        "day_change_pct" => 1.3m,
        "return_last_5_bars_pct" => 1.4m,
        "return_last_15_bars_pct" => 1.3m,
        "rvol_vs_adv" => 1.4m,
        "position_in_session_range" => 1.2m,
        "minutes_since_open" => 0.8m,
        _ => 1m
    };

    private static AnalogueSetupAggregate BuildSetupAggregate(string key, IReadOnlyList<AnalogueScenarioSummary> scenarios)
    {
        var parts = key.Split('|', 2);
        var triggered = scenarios.Count(x => x.Triggered == true);
        var t1 = scenarios.Count(x => x.T1BeforeStop == true);
        var stop = scenarios.Count(x => string.Equals(x.PrimaryOutcome, "STOP_BEFORE_T1", StringComparison.OrdinalIgnoreCase));
        var resolved = scenarios.Where(x => x.ResolvedR.HasValue).Select(x => x.ResolvedR!.Value).ToList();
        var preferred = scenarios.Count(x => string.Equals(x.SelectionTier, "PREFERRED", StringComparison.OrdinalIgnoreCase));
        var secondary = scenarios.Count(x => string.Equals(x.SelectionTier, "SECONDARY", StringComparison.OrdinalIgnoreCase));

        return new AnalogueSetupAggregate(
            Direction: parts.ElementAtOrDefault(0) ?? "unknown",
            EntryType: parts.ElementAtOrDefault(1) ?? "unknown",
            ScenarioCount: scenarios.Count,
            PreferredCount: preferred,
            SecondaryCount: secondary,
            Triggered: triggered,
            T1BeforeStop: t1,
            StopBeforeT1: stop,
            ResolvedRSamples: resolved.Count,
            MeanRealizedR: resolved.Count == 0 ? null : Math.Round(resolved.Average(), 3),
            T1RateIfResolved: (t1 + stop) == 0 ? null : Math.Round((decimal)t1 / (t1 + stop), 4));
    }

    private static bool TryBuildRecord(JsonElement root, out AnalogueIndexRecord? record)
    {
        record = null;
        if (!TryNested(root, out var compact, "inputs", "compact_v1")) return false;
        if (!TryNested(root, out var source, "source")) return false;

        var ticker = GetString(source, "ticker") ?? "";
        var asOf = GetDateTime(source, "asof_utc");
        if (!asOf.HasValue || string.IsNullOrWhiteSpace(ticker)) return false;

        var features = ExtractFeatures(compact);
        var regime = GetNestedString(compact, "deterministic_state", "regime") ?? "unknown";

        string rawVerdict = "unknown", structuralVerdict = "unknown";
        var preferred = 0;
        var secondary = 0;
        var structuralByRank = new Dictionary<int, bool>();
        var tierByRank = new Dictionary<int, string>();
        var structuralByOrdinal = new List<bool>();
        var tierByOrdinal = new List<string>();

        if (TryNested(root, out var teacher, "teacher"))
        {
            if (teacher.TryGetProperty("card", out var card) && card.ValueKind == JsonValueKind.Object)
                rawVerdict = GetString(card, "verdict") ?? "unknown";

            if (teacher.TryGetProperty("stage2b4", out var stage) && stage.ValueKind == JsonValueKind.Object)
            {
                if (stage.TryGetProperty("structural", out var structural) && structural.ValueKind == JsonValueKind.Object)
                {
                    structuralVerdict = GetString(structural, "effective_verdict") ?? "unknown";
                    if (structural.TryGetProperty("scenarios", out var ss) && ss.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var s in ss.EnumerateArray())
                        {
                            var rank = GetInt(s, "scenario_rank") ?? GetInt(s, "scenarioRank");
                            var valid = GetBool(s, "structurally_valid") ?? GetBool(s, "structurallyValid");
                            structuralByOrdinal.Add(valid ?? false);
                            if (rank.HasValue && rank.Value > 0 && valid.HasValue)
                                structuralByRank[rank.Value] = valid.Value;
                        }
                    }
                }
                if (stage.TryGetProperty("quality", out var quality) && quality.ValueKind == JsonValueKind.Object)
                {
                    preferred = GetInt(quality, "preferred_scenario_count") ?? 0;
                    secondary = GetInt(quality, "secondary_scenario_count") ?? 0;
                    if (quality.TryGetProperty("scenarios", out var qs) && qs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var q in qs.EnumerateArray())
                        {
                            var rank = GetInt(q, "scenario_rank") ?? GetInt(q, "scenarioRank");
                            var tier = GetString(q, "selection_tier") ?? GetString(q, "selectionTier") ?? "UNKNOWN";
                            tierByOrdinal.Add(tier);
                            if (rank.HasValue && rank.Value > 0 && !string.IsNullOrWhiteSpace(tier))
                                tierByRank[rank.Value] = tier;
                        }
                    }
                }
            }
        }

        var scenarios = new List<AnalogueScenarioSummary>();
        if (root.TryGetProperty("scenarios", out var scenarioArray) && scenarioArray.ValueKind == JsonValueKind.Array)
        {
            var ordinal = 0;
            foreach (var sr in scenarioArray.EnumerateArray())
            {
                // CorpusScenarioRecord is serialized with the corpus builder's default
                // System.Text.Json naming (PascalCase), while nested AVA model objects use
                // explicit snake_case JsonPropertyName attributes. Accept all historical
                // spellings deliberately so the analogue index can read corpus v3 exactly
                // as it was written.
                var rank = GetIntAny(sr, "ScenarioRank", "scenarioRank", "scenario_rank") ?? 0;
                if (!TryGetAnyProperty(sr, out var s, "Scenario", "scenario") || s.ValueKind != JsonValueKind.Object)
                {
                    ordinal++;
                    continue;
                }
                JsonElement outcome = default;
                var hasOutcome = TryGetAnyProperty(sr, out outcome, "Outcome", "outcome") &&
                                 outcome.ValueKind == JsonValueKind.Object;

                // Stage 2B.4 was added after the original corpus shape. Some historical cards
                // carry scenario_rank=0 inside teacher.card even though the corpus scenario rows
                // have authoritative ranks 1..N. Prefer a positive rank match; when that is not
                // available, use the validator/quality array's stable ordinal position.
                var structurallyValid = structuralByRank.TryGetValue(rank, out var validByRank)
                    ? validByRank
                    : ordinal < structuralByOrdinal.Count && structuralByOrdinal[ordinal];
                var selectionTier = tierByRank.TryGetValue(rank, out var tierByRankValue)
                    ? tierByRankValue
                    : ordinal < tierByOrdinal.Count ? tierByOrdinal[ordinal] : "UNKNOWN";

                scenarios.Add(new AnalogueScenarioSummary(
                    ScenarioRank: rank,
                    Direction: GetString(s, "direction") ?? "unknown",
                    EntryType: GetString(s, "entry_type") ?? "unknown",
                    Grade: GetString(s, "grade"),
                    StructurallyValid: structurallyValid,
                    SelectionTier: selectionTier,
                    Triggered: hasOutcome ? GetBoolAny(outcome, "Triggered", "triggered") : null,
                    PrimaryOutcome: hasOutcome ? GetStringAny(outcome, "PrimaryOutcome", "primaryOutcome", "primary_outcome") : null,
                    T1BeforeStop: hasOutcome ? GetBoolAny(outcome, "T1BeforeStop", "t1BeforeStop", "t1_before_stop") : null,
                    ResolvedR: GetDecimalAny(sr, "ResolvedT1OrStopR", "resolvedT1OrStopR", "resolved_t1_or_stop_r")));
                ordinal++;
            }
        }

        record = new AnalogueIndexRecord(
            ticker,
            asOf.Value,
            ToEastern(asOf.Value).Date,
            regime,
            features,
            rawVerdict,
            structuralVerdict,
            preferred,
            secondary,
            scenarios);
        return true;
    }

    private static Dictionary<string, AnalogueFeatureStat> BuildFeatureStats(IReadOnlyList<AnalogueIndexRecord> records)
    {
        var keys = records.SelectMany(x => x.Features.Keys).Distinct(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, AnalogueFeatureStat>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            var values = records.Select(x => x.Features.GetValueOrDefault(key)).Where(x => x.HasValue).Select(x => x!.Value).ToList();
            if (values.Count < 2) continue;
            var mean = values.Average();
            var variance = values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1);
            result[key] = new AnalogueFeatureStat(Math.Round(mean, 6), Math.Round((decimal)Math.Sqrt((double)variance), 6), values.Count);
        }
        return result;
    }

    private static Dictionary<string, decimal?> ExtractFeatures(JsonElement compact)
    {
        var d = new Dictionary<string, decimal?>(StringComparer.OrdinalIgnoreCase)
        {
            ["day_change_pct"] = GetNestedDecimal(compact, "deterministic_state", "day_change_pct"),
            ["session_change_pct"] = GetNestedDecimal(compact, "deterministic_state", "session_change_pct"),
            ["distance_to_vwap_pct"] = GetNestedDecimal(compact, "deterministic_state", "distance_to_vwap_pct"),
            ["return_last_5_bars_pct"] = GetNestedDecimal(compact, "deterministic_state", "return_last_5_bars_pct"),
            ["return_last_15_bars_pct"] = GetNestedDecimal(compact, "deterministic_state", "return_last_15_bars_pct"),
            ["volume_acceleration_ratio"] = GetNestedDecimal(compact, "deterministic_state", "volume_acceleration_ratio"),
            ["max_rel_volume_last_15"] = GetNestedDecimal(compact, "deterministic_state", "max_rel_volume_last_15"),
            ["last_bar_rel_volume"] = GetNestedDecimal(compact, "deterministic_state", "last_bar_rel_volume"),
            ["rvol_vs_adv"] = GetNestedDecimal(compact, "volume_context", "rvol_vs_adv"),
            ["premarket_change_pct"] = GetNestedDecimal(compact, "premarket_summary", "change_pct")
        };

        var last = GetNestedDecimal(compact, "deterministic_state", "last_close");
        var hi = GetNestedDecimal(compact, "deterministic_state", "session_high");
        var lo = GetNestedDecimal(compact, "deterministic_state", "session_low");
        d["position_in_session_range"] = last.HasValue && hi.HasValue && lo.HasValue && hi.Value > lo.Value
            ? Math.Round((last.Value - lo.Value) / (hi.Value - lo.Value), 4)
            : null;

        var asOf = GetDateTime(compact, "ts_asof_utc");
        if (asOf.HasValue)
        {
            var et = ToEastern(asOf.Value);
            var open = et.Date.AddHours(9).AddMinutes(30);
            d["minutes_since_open"] = Math.Round((decimal)(et - open).TotalMinutes, 2);
        }
        else d["minutes_since_open"] = null;

        return d;
    }

    private static TimeZoneInfo EasternTz => OperatingSystem.IsWindows()
        ? TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")
        : TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static DateTime ToEastern(DateTime utc)
    {
        if (utc.Kind == DateTimeKind.Unspecified) utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        else if (utc.Kind == DateTimeKind.Local) utc = utc.ToUniversalTime();
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(utc, EasternTz), DateTimeKind.Unspecified);
    }

    private static bool TryGetAnyProperty(JsonElement root, out JsonElement value, params string[] names)
    {
        value = default;
        if (root.ValueKind != JsonValueKind.Object) return false;
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out value)) return true;
        }
        return false;
    }

    private static string? GetStringAny(JsonElement root, params string[] names)
        => TryGetAnyProperty(root, out var value, names) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetIntAny(JsonElement root, params string[] names)
        => TryGetAnyProperty(root, out var value, names) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var x)
            ? x
            : null;

    private static decimal? GetDecimalAny(JsonElement root, params string[] names)
        => TryGetAnyProperty(root, out var value, names) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var x)
            ? x
            : null;

    private static bool? GetBoolAny(JsonElement root, params string[] names)
        => TryGetAnyProperty(root, out var value, names) &&
           (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            ? value.GetBoolean()
            : null;

    private static bool TryNested(JsonElement root, out JsonElement value, params string[] path)
    {
        value = root;
        foreach (var part in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(part, out value)) return false;
        }
        return true;
    }

    private static decimal? GetNestedDecimal(JsonElement root, params string[] path)
        => TryNested(root, out var value, path) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var d) ? d : null;

    private static string? GetNestedString(JsonElement root, params string[] path)
        => TryNested(root, out var value, path) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? GetString(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? GetInt(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var x) ? x : null;

    private static decimal? GetDecimal(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var x) ? x : null;

    private static bool? GetBool(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : null;

    private static DateTime? GetDateTime(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.String && DateTime.TryParse(v.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            return dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
        return null;
    }
}

public sealed record AnalogueFeatureStat(decimal Mean, decimal StdDev, int SampleCount);

public sealed record AnalogueIndexRecord(
    string Ticker,
    DateTime AsOfUtc,
    DateTime SessionDateEt,
    string Regime,
    Dictionary<string, decimal?> Features,
    string RawVerdict,
    string StructuralVerdict,
    int PreferredScenarioCount,
    int SecondaryScenarioCount,
    List<AnalogueScenarioSummary> Scenarios);

public sealed record AnalogueScenarioSummary(
    int ScenarioRank,
    string Direction,
    string EntryType,
    string? Grade,
    bool StructurallyValid,
    string SelectionTier,
    bool? Triggered,
    string? PrimaryOutcome,
    bool? T1BeforeStop,
    decimal? ResolvedR);

public sealed record AnalogueIndexDocument(
    string IndexVersion,
    DateTime GeneratedUtc,
    string SourceCorpus,
    int RecordCount,
    Dictionary<string, AnalogueFeatureStat> FeatureStats,
    List<AnalogueIndexRecord> Records);

public sealed record AnalogueSetupAggregate(
    string Direction,
    string EntryType,
    int ScenarioCount,
    int PreferredCount,
    int SecondaryCount,
    int Triggered,
    int T1BeforeStop,
    int StopBeforeT1,
    int ResolvedRSamples,
    decimal? MeanRealizedR,
    decimal? T1RateIfResolved);

public sealed record HistoricalAnalogueExample(
    string Ticker,
    DateTime AsOfUtc,
    decimal Distance,
    int SharedFeatures,
    string RawVerdict,
    string StructuralVerdict,
    int PreferredScenarioCount,
    int SecondaryScenarioCount,
    IReadOnlyList<AnalogueScenarioSummary> Scenarios);

public sealed record HistoricalAnalogueContext(
    string ContextVersion,
    string CausalityRule,
    string QueryTicker,
    DateTime QueryAsOfUtc,
    int EligiblePriorSessionRecords,
    int ReturnedAnalogues,
    decimal? AverageDistance,
    IReadOnlyList<AnalogueSetupAggregate> SetupOutcomes,
    IReadOnlyList<HistoricalAnalogueExample> Examples);

public sealed record CandidateHistoricalEvidenceExample(
    string Ticker,
    DateTime AsOfUtc,
    decimal Distance,
    int SharedFeatures,
    string SelectionTier,
    bool? Triggered,
    string? PrimaryOutcome,
    decimal? ResolvedR);

public sealed record CandidateScenarioHistoricalEvidence(
    string EvidenceVersion,
    string CausalityRule,
    int ScenarioRank,
    string Direction,
    string EntryType,
    string QueryTicker,
    DateTime QueryAsOfUtc,
    int EligibleMatchingRecords,
    int ReturnedAnalogueRecords,
    decimal? AverageDistance,
    int SameTickerRecords,
    int KnownTriggerOutcomes,
    int Triggered,
    int NotTriggered,
    decimal? TriggerRate,
    int T1BeforeStop,
    int StopBeforeT1,
    int ResolvedRSamples,
    int PositiveResolved,
    int NegativeResolved,
    decimal? MeanRealizedR,
    decimal? MedianRealizedR,
    decimal? T1RateIfResolved,
    decimal? ExpectancyRPerTriggeredZeroUnresolved,
    int PreferredCount,
    int SecondaryCount,
    IReadOnlyList<CandidateHistoricalEvidenceExample> Examples);

public sealed record CandidateHistoricalEvidenceCard(
    string EvidenceVersion,
    string Mode,
    string DecisionEffect,
    IReadOnlyList<CandidateScenarioHistoricalEvidence> Scenarios);
