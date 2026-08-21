using System.Globalization;
using System.Text;
using System.Text.Json;

namespace get_assessment_no_graph;

/// <summary>
/// Stage 2C.8 offline empirical-support ranking simulation.
///
/// It compares three scenario-selection policies without changing runtime behavior:
///   1) RAW_RANK: current executable-card ordering (lowest scenario_rank first)
///   2) TIER_ONLY: PREFERRED before SECONDARY, then scenario_rank
///   3) EVIDENCE_AWARE: same Stage 2B.4 tier ordering, then frozen empirical-support
///      band (STRONG > NEUTRAL > NEGATIVE > INSUFFICIENT), then scenario_rank
///
/// The empirical bands are fixed inputs. They are not re-fit from the holdout.
/// For holdout rows, the outcome-bearing evidence pool is frozen before holdout start.
/// No LLM, market-data, Supabase, or OpenAI calls are made.
/// </summary>
public static class Stage2C8EvidenceRankingSimulationRunner
{
    private static readonly JsonSerializerOptions PrettyJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly TimeZoneInfo EasternTz =
        TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        RankingOptions options;
        try
        {
            options = ParseOptions(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Stage 2C.8 ranking-simulation configuration error: {ex.Message}");
            Console.Error.WriteLine();
            PrintHelp();
            return 2;
        }

        if (!File.Exists(options.CorpusPath))
        {
            Console.Error.WriteLine($"Corpus not found: {options.CorpusPath}");
            return 2;
        }
        if (!File.Exists(options.IndexPath))
        {
            Console.Error.WriteLine($"Candidate evidence index not found: {options.IndexPath}");
            return 2;
        }

        Directory.CreateDirectory(options.OutputDir);
        var index = HistoricalAnalogueIndex.Load(options.IndexPath);

        Console.WriteLine("AVA Stage 2C.8 empirical-support ranking simulation");
        Console.WriteLine($"Corpus             : {Path.GetFullPath(options.CorpusPath)}");
        Console.WriteLine($"Evidence index     : {Path.GetFullPath(options.IndexPath)} ({index.RecordCount:N0} records)");
        Console.WriteLine($"Training ET        : {options.TrainFromEt:yyyy-MM-dd} through {options.TrainToEt:yyyy-MM-dd}");
        Console.WriteLine($"Holdout ET         : {options.HoldoutFromEt:yyyy-MM-dd} through {options.HoldoutToEt:yyyy-MM-dd}");
        Console.WriteLine($"Frozen holdout pool: sessions strictly before {options.HoldoutFromEt:yyyy-MM-dd}");
        Console.WriteLine($"Negative threshold : {options.NegativeThreshold:0.####} R/trigger");
        Console.WriteLine($"Strong threshold   : {options.StrongThreshold:0.####} R/trigger (FROZEN; not tuned here)");
        Console.WriteLine($"Minimum evidence   : {options.MinimumReturnedRecords} returned records");
        Console.WriteLine($"Evidence top N     : {options.TopN}");
        Console.WriteLine($"Output             : {Path.GetFullPath(options.OutputDir)}");
        Console.WriteLine();
        Console.WriteLine("Policies:");
        Console.WriteLine("  RAW_RANK       : lowest structurally-valid scenario_rank");
        Console.WriteLine("  TIER_ONLY      : PREFERRED > SECONDARY > other, then scenario_rank");
        Console.WriteLine("  EVIDENCE_AWARE : same tier ordering, then STRONG > NEUTRAL > NEGATIVE > INSUFFICIENT, then scenario_rank");
        Console.WriteLine();
        Console.WriteLine("Safety: offline corpus/index only; no Massive, Supabase, OpenAI, or Ollama calls. No production decision changes.");
        Console.WriteLine();

        var rows = new List<RankingSimulationRow>();
        var corpusLines = 0;
        var parseErrors = 0;
        var cardsWithAnyValid = 0;
        var cardsWithMultipleValid = 0;

        await foreach (var line in File.ReadLinesAsync(options.CorpusPath, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            corpusLines++;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!TryGetObject(root, "inputs", out var inputs) ||
                    !TryGetObject(inputs, "compact_v1", out var compact))
                    continue;

                var compactJson = compact.GetRawText();
                var ticker = GetString(root, "source", "ticker") ?? GetString(compact, "ticker") ?? "";
                var asofUtc = GetDateTime(root, "source", "asof_utc") ?? GetDateTime(compact, "ts_asof_utc");
                if (!asofUtc.HasValue || string.IsNullOrWhiteSpace(ticker)) continue;

                var sessionDateEt = ToEastern(asofUtc.Value).Date;
                var window = WindowFor(sessionDateEt, options);
                if (window is null) continue;

                var structuralByRank = new Dictionary<int, bool>();
                var tierByRank = new Dictionary<int, string>();
                ReadStage2B4(root, structuralByRank, tierByRank);

                if (!TryGetArray(root, "scenarios", out var scenarioRows)) continue;

                var candidates = new List<RankingCandidate>();
                var ordinal = 0;
                foreach (var scenarioRow in scenarioRows.EnumerateArray())
                {
                    ordinal++;
                    var rank = GetInt(scenarioRow, "ScenarioRank") ?? GetInt(scenarioRow, "scenarioRank") ?? GetInt(scenarioRow, "scenario_rank") ?? ordinal;
                    if (!TryGetObject(scenarioRow, "Scenario", out var scenario) && !TryGetObject(scenarioRow, "scenario", out scenario))
                        continue;

                    var valid = structuralByRank.TryGetValue(rank, out var validByRank)
                        ? validByRank
                        : structuralByRank.TryGetValue(ordinal, out var validByOrdinal) && validByOrdinal;
                    if (!valid) continue;

                    var direction = (GetString(scenario, "direction") ?? GetString(scenario, "Direction") ?? "").Trim().ToLowerInvariant();
                    var entryType = (GetString(scenario, "entry_type") ?? GetString(scenario, "entryType") ?? GetString(scenario, "EntryType") ?? "").Trim().ToLowerInvariant();
                    if (string.IsNullOrWhiteSpace(direction) || string.IsNullOrWhiteSpace(entryType)) continue;

                    var tier = tierByRank.GetValueOrDefault(rank, tierByRank.GetValueOrDefault(ordinal, "UNKNOWN"));
                    var actualR = GetDecimal(scenarioRow, "ResolvedT1OrStopR") ??
                                  GetDecimal(scenarioRow, "resolvedT1OrStopR") ??
                                  GetDecimal(scenarioRow, "resolved_t1_or_stop_r");
                    var outcome = GetStringFromNestedEither(scenarioRow, "Outcome", "outcome", "PrimaryOutcome", "primaryOutcome", "primary_outcome");
                    var triggered = GetBoolFromNestedEither(scenarioRow, "Outcome", "outcome", "Triggered", "triggered");

                    candidates.Add(new RankingCandidate(
                        Rank: rank,
                        Direction: direction,
                        EntryType: entryType,
                        Tier: NormalizeTier(tier),
                        ActualResolvedR: actualR,
                        ActualOutcome: outcome,
                        ActualTriggered: triggered,
                        Evidence: null,
                        EvidenceBand: "UNASSIGNED"));
                }

                if (candidates.Count == 0) continue;
                cardsWithAnyValid++;
                if (candidates.Count < 2) continue;
                cardsWithMultipleValid++;

                DateTime? evidenceBeforeEt = string.Equals(window, "holdout", StringComparison.Ordinal)
                    ? options.HoldoutFromEt.Date
                    : null;

                var withEvidence = new List<RankingCandidate>(candidates.Count);
                foreach (var candidate in candidates)
                {
                    var evidence = index.QueryCandidateEvidence(
                        compactJson,
                        candidate.Rank,
                        candidate.Direction,
                        candidate.EntryType,
                        options.TopN,
                        evidenceBeforeEt);

                    var band = ClassifyEvidence(
                        evidence,
                        options.MinimumReturnedRecords,
                        options.NegativeThreshold,
                        options.StrongThreshold);

                    withEvidence.Add(candidate with
                    {
                        Evidence = evidence,
                        EvidenceBand = band
                    });
                }

                var rawRank = withEvidence
                    .OrderBy(c => c.Rank)
                    .First();

                var tierOnly = withEvidence
                    .OrderBy(c => TierPriority(c.Tier))
                    .ThenBy(c => c.Rank)
                    .First();

                var evidenceAware = withEvidence
                    .OrderBy(c => TierPriority(c.Tier))
                    .ThenBy(c => EvidenceBandPriority(c.EvidenceBand))
                    .ThenBy(c => c.Rank)
                    .First();

                var topTier = withEvidence
                    .OrderBy(c => TierPriority(c.Tier))
                    .Select(c => c.Tier)
                    .First();
                var topTierCandidateCount = withEvidence.Count(c => string.Equals(c.Tier, topTier, StringComparison.OrdinalIgnoreCase));

                rows.Add(new RankingSimulationRow(
                    Window: window,
                    Ticker: ticker,
                    AsOfUtc: EnsureUtc(asofUtc.Value),
                    SessionDateEt: sessionDateEt,
                    ValidScenarioCount: withEvidence.Count,
                    TopTier: topTier,
                    TopTierCandidateCount: topTierCandidateCount,
                    RawRank: Snapshot(rawRank),
                    TierOnly: Snapshot(tierOnly),
                    EvidenceAware: Snapshot(evidenceAware),
                    TierChangedVsRaw: tierOnly.Rank != rawRank.Rank,
                    EvidenceChangedVsTier: evidenceAware.Rank != tierOnly.Rank,
                    EvidenceChangedDirection: evidenceAware.Rank != tierOnly.Rank && !string.Equals(evidenceAware.Direction, tierOnly.Direction, StringComparison.OrdinalIgnoreCase),
                    EvidenceChangedSetup: evidenceAware.Rank != tierOnly.Rank && !string.Equals(evidenceAware.EntryType, tierOnly.EntryType, StringComparison.OrdinalIgnoreCase),
                    TierVsRawPairedResolvedDeltaR: PairedDelta(tierOnly.ActualResolvedR, rawRank.ActualResolvedR),
                    EvidenceVsTierPairedResolvedDeltaR: PairedDelta(evidenceAware.ActualResolvedR, tierOnly.ActualResolvedR)));

                if (corpusLines % 250 == 0)
                    Console.WriteLine($"  progress corpus={corpusLines:N0} multi_valid_cards={rows.Count:N0}");
            }
            catch (Exception)
            {
                parseErrors++;
            }
        }

        var trainingRows = rows.Where(r => r.Window == "training").ToList();
        var holdoutRows = rows.Where(r => r.Window == "holdout").ToList();

        var summary = new RankingSimulationSummary(
            Stage: "stage2c8_evidence_ranking_simulation_v1",
            GeneratedUtc: DateTime.UtcNow,
            CorpusPath: Path.GetFullPath(options.CorpusPath),
            IndexPath: Path.GetFullPath(options.IndexPath),
            EvidenceTopN: options.TopN,
            MinimumReturnedRecords: options.MinimumReturnedRecords,
            NegativeThreshold: options.NegativeThreshold,
            FrozenStrongThreshold: options.StrongThreshold,
            TrainFromEt: options.TrainFromEt,
            TrainToEt: options.TrainToEt,
            HoldoutFromEt: options.HoldoutFromEt,
            HoldoutToEt: options.HoldoutToEt,
            HoldoutEvidenceFrozenBeforeEt: options.HoldoutFromEt,
            CorpusLines: corpusLines,
            CardsWithAnyStructurallyValidScenario: cardsWithAnyValid,
            CardsWithMultipleStructurallyValidScenarios: cardsWithMultipleValid,
            SimulationRows: rows.Count,
            Training: SummarizeWindow(trainingRows),
            Holdout: SummarizeWindow(holdoutRows),
            ParseErrors: parseErrors);

        var csvPath = Path.Combine(options.OutputDir, "stage2c8_evidence_ranking_rows.csv");
        var jsonPath = Path.Combine(options.OutputDir, "stage2c8_evidence_ranking_rows.json");
        var summaryPath = Path.Combine(options.OutputDir, "stage2c8_evidence_ranking_summary.json");
        await WriteOutputsAsync(rows, summary, csvPath, jsonPath, summaryPath, ct);

        Console.WriteLine();
        Console.WriteLine("Stage 2C.8 empirical-support ranking simulation complete.");
        Console.WriteLine($"Corpus lines                     : {corpusLines:N0}");
        Console.WriteLine($"Cards with any valid scenario   : {cardsWithAnyValid:N0}");
        Console.WriteLine($"Cards with multiple valid       : {cardsWithMultipleValid:N0}");
        Console.WriteLine($"Training simulation cards       : {trainingRows.Count:N0}");
        Console.WriteLine($"Holdout simulation cards        : {holdoutRows.Count:N0}");
        PrintWindow("TRAINING", summary.Training);
        PrintWindow("HOLDOUT ", summary.Holdout);
        Console.WriteLine($"CSV                              : {Path.GetFullPath(csvPath)}");
        Console.WriteLine($"Summary                          : {Path.GetFullPath(summaryPath)}");

        return parseErrors == 0 ? 0 : 1;
    }

    private static void PrintWindow(string label, RankingWindowSummary s)
    {
        Console.WriteLine($"{label} evidence changed selection : {s.EvidenceChangedSelections:N0}/{s.Cards:N0} ({FmtPct(s.EvidenceChangedSelectionRate)})");
        Console.WriteLine($"{label} paired resolved cards      : {s.EvidenceVsTierPairedResolvedCards:N0}");
        Console.WriteLine($"{label} TIER_ONLY mean R            : {Fmt(s.TierOnlyMeanRPaired)}");
        Console.WriteLine($"{label} EVIDENCE_AWARE mean R       : {Fmt(s.EvidenceAwareMeanRPaired)}");
        Console.WriteLine($"{label} evidence delta total R      : {Fmt(s.EvidenceVsTierDeltaTotalR)}");
        Console.WriteLine($"{label} changed+resolved better/worse/equal: {s.ChangedResolvedBetter:N0}/{s.ChangedResolvedWorse:N0}/{s.ChangedResolvedEqual:N0}");
        Console.WriteLine($"{label} changed+resolved delta R    : {Fmt(s.ChangedResolvedDeltaTotalR)}");
    }

    private static RankingWindowSummary SummarizeWindow(IReadOnlyList<RankingSimulationRow> rows)
    {
        var tierVsRawPaired = rows.Where(r => r.RawRank.ActualResolvedR.HasValue && r.TierOnly.ActualResolvedR.HasValue).ToList();
        var evidenceVsTierPaired = rows.Where(r => r.TierOnly.ActualResolvedR.HasValue && r.EvidenceAware.ActualResolvedR.HasValue).ToList();
        var changed = rows.Where(r => r.EvidenceChangedVsTier).ToList();
        var changedResolved = changed.Where(r => r.TierOnly.ActualResolvedR.HasValue && r.EvidenceAware.ActualResolvedR.HasValue).ToList();

        var tierOnlyR = evidenceVsTierPaired.Select(r => r.TierOnly.ActualResolvedR!.Value).ToList();
        var evidenceR = evidenceVsTierPaired.Select(r => r.EvidenceAware.ActualResolvedR!.Value).ToList();
        var rawR = tierVsRawPaired.Select(r => r.RawRank.ActualResolvedR!.Value).ToList();
        var tierRForRawPair = tierVsRawPaired.Select(r => r.TierOnly.ActualResolvedR!.Value).ToList();

        var changedDeltas = changedResolved
            .Select(r => r.EvidenceAware.ActualResolvedR!.Value - r.TierOnly.ActualResolvedR!.Value)
            .ToList();

        return new RankingWindowSummary(
            Cards: rows.Count,
            RawVsTierChangedSelections: rows.Count(r => r.TierChangedVsRaw),
            RawVsTierChangedSelectionRate: Rate(rows.Count(r => r.TierChangedVsRaw), rows.Count),
            EvidenceChangedSelections: changed.Count,
            EvidenceChangedSelectionRate: Rate(changed.Count, rows.Count),
            EvidenceChangedDirection: rows.Count(r => r.EvidenceChangedDirection),
            EvidenceChangedSetup: rows.Count(r => r.EvidenceChangedSetup),
            TierVsRawPairedResolvedCards: tierVsRawPaired.Count,
            RawRankMeanRPaired: Mean(rawR),
            TierOnlyMeanRVsRawPaired: Mean(tierRForRawPair),
            TierVsRawDeltaTotalR: tierVsRawPaired.Count == 0 ? null : tierRForRawPair.Sum() - rawR.Sum(),
            EvidenceVsTierPairedResolvedCards: evidenceVsTierPaired.Count,
            TierOnlyMeanRPaired: Mean(tierOnlyR),
            EvidenceAwareMeanRPaired: Mean(evidenceR),
            TierOnlyWinRatePaired: WinRate(tierOnlyR),
            EvidenceAwareWinRatePaired: WinRate(evidenceR),
            TierOnlyTotalRPaired: evidenceVsTierPaired.Count == 0 ? null : tierOnlyR.Sum(),
            EvidenceAwareTotalRPaired: evidenceVsTierPaired.Count == 0 ? null : evidenceR.Sum(),
            EvidenceVsTierDeltaTotalR: evidenceVsTierPaired.Count == 0 ? null : evidenceR.Sum() - tierOnlyR.Sum(),
            ChangedResolvedCards: changedResolved.Count,
            ChangedResolvedBetter: changedDeltas.Count(d => d > 0m),
            ChangedResolvedWorse: changedDeltas.Count(d => d < 0m),
            ChangedResolvedEqual: changedDeltas.Count(d => d == 0m),
            ChangedResolvedDeltaTotalR: changedResolved.Count == 0 ? null : changedDeltas.Sum(),
            ChangedResolvedMeanDeltaR: Mean(changedDeltas),
            TierOnlyBandCounts: BandCounts(rows.Select(r => r.TierOnly.EvidenceBand)),
            EvidenceAwareBandCounts: BandCounts(rows.Select(r => r.EvidenceAware.EvidenceBand)));
    }

    private static IReadOnlyDictionary<string, int> BandCounts(IEnumerable<string> bands)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["STRONG"] = 0,
            ["NEUTRAL"] = 0,
            ["NEGATIVE"] = 0,
            ["INSUFFICIENT"] = 0
        };
        foreach (var band in bands)
            result[NormalizeBand(band)] = result.GetValueOrDefault(NormalizeBand(band)) + 1;
        return result;
    }

    private static CandidateSnapshot Snapshot(RankingCandidate c)
    {
        var e = c.Evidence;
        return new CandidateSnapshot(
            Rank: c.Rank,
            Direction: c.Direction,
            EntryType: c.EntryType,
            Tier: c.Tier,
            EvidenceBand: c.EvidenceBand,
            ReturnedEvidenceRecords: e?.ReturnedAnalogueRecords ?? 0,
            EvidenceExpectancyPerTriggered: e?.ExpectancyRPerTriggeredZeroUnresolved,
            EvidenceMeanR: e?.MeanRealizedR,
            EvidenceAverageDistance: e?.AverageDistance,
            ActualTriggered: c.ActualTriggered,
            ActualOutcome: c.ActualOutcome,
            ActualResolvedR: c.ActualResolvedR);
    }

    private static void ReadStage2B4(JsonElement root, Dictionary<int, bool> structuralByRank, Dictionary<int, string> tierByRank)
    {
        if (!TryGetObject(root, "teacher", out var teacher) ||
            !TryGetObject(teacher, "stage2b4", out var stage2b4))
            return;

        if (TryGetObject(stage2b4, "structural", out var structural) &&
            TryGetArray(structural, "scenarios", out var structuralScenarios))
        {
            foreach (var s in structuralScenarios.EnumerateArray())
            {
                var rank = GetInt(s, "scenario_rank") ?? GetInt(s, "scenarioRank") ?? GetInt(s, "ScenarioRank");
                var valid = GetBool(s, "structurally_valid") ?? GetBool(s, "structurallyValid") ?? GetBool(s, "StructurallyValid");
                if (rank.HasValue && valid.HasValue) structuralByRank[rank.Value] = valid.Value;
            }
        }

        if (TryGetObject(stage2b4, "quality", out var quality) &&
            TryGetArray(quality, "scenarios", out var qualityScenarios))
        {
            foreach (var q in qualityScenarios.EnumerateArray())
            {
                var rank = GetInt(q, "scenario_rank") ?? GetInt(q, "scenarioRank") ?? GetInt(q, "ScenarioRank");
                var tier = GetString(q, "selection_tier") ?? GetString(q, "selectionTier") ?? GetString(q, "SelectionTier");
                if (rank.HasValue && !string.IsNullOrWhiteSpace(tier)) tierByRank[rank.Value] = NormalizeTier(tier!);
            }
        }
    }

    private static string ClassifyEvidence(
        CandidateScenarioHistoricalEvidence evidence,
        int minReturned,
        decimal negativeThreshold,
        decimal strongThreshold)
    {
        if (evidence.ReturnedAnalogueRecords < minReturned || !evidence.ExpectancyRPerTriggeredZeroUnresolved.HasValue)
            return "INSUFFICIENT";
        var value = evidence.ExpectancyRPerTriggeredZeroUnresolved.Value;
        if (value < negativeThreshold) return "NEGATIVE";
        if (value >= strongThreshold) return "STRONG";
        return "NEUTRAL";
    }

    private static int TierPriority(string tier) => NormalizeTier(tier) switch
    {
        "PREFERRED" => 0,
        "SECONDARY" => 1,
        _ => 2
    };

    private static int EvidenceBandPriority(string band) => NormalizeBand(band) switch
    {
        "STRONG" => 0,
        "NEUTRAL" => 1,
        "NEGATIVE" => 2,
        _ => 3
    };

    private static string NormalizeTier(string? tier)
    {
        var value = (tier ?? "UNKNOWN").Trim().ToUpperInvariant();
        return value is "PREFERRED" or "SECONDARY" ? value : "UNKNOWN";
    }

    private static string NormalizeBand(string? band)
    {
        var value = (band ?? "INSUFFICIENT").Trim().ToUpperInvariant();
        return value is "STRONG" or "NEUTRAL" or "NEGATIVE" or "INSUFFICIENT" ? value : "INSUFFICIENT";
    }

    private static decimal? PairedDelta(decimal? selected, decimal? baseline)
        => selected.HasValue && baseline.HasValue ? selected.Value - baseline.Value : null;

    private static decimal? Mean(IReadOnlyList<decimal> values)
        => values.Count == 0 ? null : Math.Round(values.Average(), 4);

    private static decimal? WinRate(IReadOnlyList<decimal> values)
        => values.Count == 0 ? null : Math.Round((decimal)values.Count(v => v > 0m) / values.Count, 4);

    private static decimal? Rate(int numerator, int denominator)
        => denominator == 0 ? null : Math.Round((decimal)numerator / denominator, 4);

    private static string? WindowFor(DateTime sessionDateEt, RankingOptions options)
    {
        var date = sessionDateEt.Date;
        if (date >= options.TrainFromEt.Date && date <= options.TrainToEt.Date) return "training";
        if (date >= options.HoldoutFromEt.Date && date <= options.HoldoutToEt.Date) return "holdout";
        return null;
    }

    private static async Task WriteOutputsAsync(
        IReadOnlyList<RankingSimulationRow> rows,
        RankingSimulationSummary summary,
        string csvPath,
        string jsonPath,
        string summaryPath,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("window,ticker,asof_utc,session_date_et,valid_scenarios,top_tier,top_tier_candidates,raw_rank,raw_tier,raw_band,raw_r,tier_rank,tier_tier,tier_band,tier_r,evidence_rank,evidence_tier,evidence_band,evidence_r,evidence_expectancy,evidence_records,evidence_avg_distance,tier_changed_vs_raw,evidence_changed_vs_tier,evidence_changed_direction,evidence_changed_setup,tier_vs_raw_delta_r,evidence_vs_tier_delta_r");

        foreach (var row in rows.OrderBy(r => r.SessionDateEt).ThenBy(r => r.AsOfUtc).ThenBy(r => r.Ticker))
        {
            sb.Append(Csv(row.Window)).Append(',')
              .Append(Csv(row.Ticker)).Append(',')
              .Append(row.AsOfUtc.ToString("o", CultureInfo.InvariantCulture)).Append(',')
              .Append(row.SessionDateEt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
              .Append(row.ValidScenarioCount).Append(',')
              .Append(Csv(row.TopTier)).Append(',')
              .Append(row.TopTierCandidateCount).Append(',')
              .Append(row.RawRank.Rank).Append(',')
              .Append(Csv(row.RawRank.Tier)).Append(',')
              .Append(Csv(row.RawRank.EvidenceBand)).Append(',')
              .Append(FmtCsv(row.RawRank.ActualResolvedR)).Append(',')
              .Append(row.TierOnly.Rank).Append(',')
              .Append(Csv(row.TierOnly.Tier)).Append(',')
              .Append(Csv(row.TierOnly.EvidenceBand)).Append(',')
              .Append(FmtCsv(row.TierOnly.ActualResolvedR)).Append(',')
              .Append(row.EvidenceAware.Rank).Append(',')
              .Append(Csv(row.EvidenceAware.Tier)).Append(',')
              .Append(Csv(row.EvidenceAware.EvidenceBand)).Append(',')
              .Append(FmtCsv(row.EvidenceAware.ActualResolvedR)).Append(',')
              .Append(FmtCsv(row.EvidenceAware.EvidenceExpectancyPerTriggered)).Append(',')
              .Append(row.EvidenceAware.ReturnedEvidenceRecords).Append(',')
              .Append(FmtCsv(row.EvidenceAware.EvidenceAverageDistance)).Append(',')
              .Append(row.TierChangedVsRaw ? "true" : "false").Append(',')
              .Append(row.EvidenceChangedVsTier ? "true" : "false").Append(',')
              .Append(row.EvidenceChangedDirection ? "true" : "false").Append(',')
              .Append(row.EvidenceChangedSetup ? "true" : "false").Append(',')
              .Append(FmtCsv(row.TierVsRawPairedResolvedDeltaR)).Append(',')
              .Append(FmtCsv(row.EvidenceVsTierPairedResolvedDeltaR)).AppendLine();
        }

        await File.WriteAllTextAsync(csvPath, sb.ToString(), ct);
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(rows, PrettyJsonOptions), ct);
        await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(summary, PrettyJsonOptions), ct);
    }

    private static RankingOptions ParseOptions(string[] args)
    {
        var corpus = ReadOption(args, "--corpus");
        if (string.IsNullOrWhiteSpace(corpus))
            throw new ArgumentException("--candidate-evidence-rank-sim requires --corpus=<Stage2B.4 corpus JSONL>.");

        var index = ReadOption(args, "--candidate-evidence-index");
        if (string.IsNullOrWhiteSpace(index))
            throw new ArgumentException("--candidate-evidence-rank-sim requires --candidate-evidence-index=<analogue index JSON>.");

        var trainFrom = ParseDate(ReadOption(args, "--train-from-et") ?? "2026-07-01", "--train-from-et");
        var trainTo = ParseDate(ReadOption(args, "--train-to-et") ?? "2026-07-31", "--train-to-et");
        var holdoutFrom = ParseDate(ReadOption(args, "--holdout-from-et") ?? "2026-08-01", "--holdout-from-et");
        var holdoutTo = ParseDate(ReadOption(args, "--holdout-to-et") ?? "2026-08-07", "--holdout-to-et");
        if (trainTo < trainFrom) throw new ArgumentException("Training end must be on/after training start.");
        if (holdoutTo < holdoutFrom) throw new ArgumentException("Holdout end must be on/after holdout start.");
        if (trainTo >= holdoutFrom) throw new ArgumentException("Training and holdout windows must not overlap; training must end before holdout begins.");

        var topN = ParseInt(ReadOption(args, "--candidate-evidence-top"), 24, 1, 100, "--candidate-evidence-top");
        var minReturned = ParseInt(ReadOption(args, "--minimum-evidence-records"), 5, 0, 100, "--minimum-evidence-records");
        var negativeThreshold = ParseDecimal(ReadOption(args, "--negative-threshold"), 0m, -10m, 10m, "--negative-threshold");
        var strongThreshold = ParseDecimal(ReadOption(args, "--strong-threshold"), 0.9255m, -10m, 10m, "--strong-threshold");
        if (strongThreshold <= negativeThreshold)
            throw new ArgumentException("--strong-threshold must be greater than --negative-threshold.");

        var outputDir = ReadOption(args, "--output-dir") ?? "stage2c8_evidence_ranking";
        return new RankingOptions(corpus, index, outputDir, topN, minReturned, negativeThreshold, strongThreshold, trainFrom, trainTo, holdoutFrom, holdoutTo);
    }

    public static void PrintHelp()
    {
        Console.WriteLine("AVA Stage 2C.8 empirical-support ranking simulation");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run -- --historical-shadow --candidate-evidence-rank-sim `");
        Console.WriteLine("    --corpus=.\\historical_corpus_stage2c_source\\ava_corpus_all_....jsonl `");
        Console.WriteLine("    --candidate-evidence-index=.\\historical_corpus_stage2c_source\\ava_analogue_index.json `");
        Console.WriteLine("    --train-from-et=2026-07-01 --train-to-et=2026-07-31 `");
        Console.WriteLine("    --holdout-from-et=2026-08-01 --holdout-to-et=2026-08-07 `");
        Console.WriteLine("    --negative-threshold=0 --strong-threshold=0.9255 `");
        Console.WriteLine("    --minimum-evidence-records=5 --candidate-evidence-top=24 `");
        Console.WriteLine("    --output-dir=.\\stage2c8_evidence_ranking");
        Console.WriteLine();
        Console.WriteLine("The 0 / 0.9255 empirical-support thresholds are frozen inputs from Stage 2C.7;");
        Console.WriteLine("this simulation does not re-fit them. Holdout evidence is frozen before holdout start.");
        Console.WriteLine("EVIDENCE_AWARE never overrides Stage 2B.4 tier ordering; evidence only breaks ties within tier.");
        Console.WriteLine("No LLM or network calls are made and no runtime selection behavior changes.");
    }

    private static string? GetStringFromNestedEither(JsonElement obj, string parent1, string parent2, params string[] names)
    {
        JsonElement nested;
        if (!TryGetObject(obj, parent1, out nested) && !TryGetObject(obj, parent2, out nested)) return null;
        foreach (var name in names)
        {
            var value = GetString(nested, name);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    private static bool? GetBoolFromNestedEither(JsonElement obj, string parent1, string parent2, params string[] names)
    {
        JsonElement nested;
        if (!TryGetObject(obj, parent1, out nested) && !TryGetObject(obj, parent2, out nested)) return null;
        foreach (var name in names)
        {
            var value = GetBool(nested, name);
            if (value.HasValue) return value;
        }
        return null;
    }

    private static bool TryGetPropertyInsensitive(JsonElement obj, string name, out JsonElement value)
    {
        value = default;
        if (obj.ValueKind != JsonValueKind.Object) return false;
        if (obj.TryGetProperty(name, out value)) return true;
        foreach (var p in obj.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }
        return false;
    }

    private static bool TryGetObject(JsonElement obj, string name, out JsonElement value)
        => TryGetPropertyInsensitive(obj, name, out value) && value.ValueKind == JsonValueKind.Object;

    private static bool TryGetArray(JsonElement obj, string name, out JsonElement value)
        => TryGetPropertyInsensitive(obj, name, out value) && value.ValueKind == JsonValueKind.Array;

    private static string? GetString(JsonElement obj, string name)
    {
        if (!TryGetPropertyInsensitive(obj, name, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static string? GetString(JsonElement obj, string parent, string name)
        => TryGetObject(obj, parent, out var nested) ? GetString(nested, name) : null;

    private static int? GetInt(JsonElement obj, string name)
    {
        if (!TryGetPropertyInsensitive(obj, name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i)) return i;
        return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out i) ? i : null;
    }

    private static bool? GetBool(JsonElement obj, string name)
    {
        if (!TryGetPropertyInsensitive(obj, name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.True) return true;
        if (value.ValueKind == JsonValueKind.False) return false;
        return bool.TryParse(value.ToString(), out var b) ? b : null;
    }

    private static decimal? GetDecimal(JsonElement obj, string name)
    {
        if (!TryGetPropertyInsensitive(obj, name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var d)) return d;
        return decimal.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : null;
    }

    private static DateTime? GetDateTime(JsonElement obj, string name)
    {
        var raw = GetString(obj, name);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)) return null;
        return EnsureUtc(dt);
    }

    private static DateTime? GetDateTime(JsonElement obj, string parent, string name)
        => TryGetObject(obj, parent, out var nested) ? GetDateTime(nested, name) : null;

    private static DateTime EnsureUtc(DateTime dt)
        => dt.Kind switch
        {
            DateTimeKind.Utc => dt,
            DateTimeKind.Local => dt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc)
        };

    private static DateTime ToEastern(DateTime utc)
        => TimeZoneInfo.ConvertTimeFromUtc(EnsureUtc(utc), EasternTz);

    private static DateTime ParseDate(string raw, string label)
    {
        if (!DateTime.TryParseExact(raw.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            throw new ArgumentException($"{label} must be YYYY-MM-DD.");
        return DateTime.SpecifyKind(dt.Date, DateTimeKind.Unspecified);
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

    private static int ParseInt(string? raw, int fallback, int min, int max, string label)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new ArgumentException($"{label} must be an integer.");
        if (value < min || value > max)
            throw new ArgumentException($"{label} must be between {min} and {max}.");
        return value;
    }

    private static decimal ParseDecimal(string? raw, decimal fallback, decimal min, decimal max, string label)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (!decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw new ArgumentException($"{label} must be numeric.");
        if (value < min || value > max)
            throw new ArgumentException($"{label} must be between {min} and {max}.");
        return value;
    }

    private static string Csv(string? value)
    {
        value ??= "";
        return value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? '"' + value.Replace("\"", "\"\"") + '"'
            : value;
    }

    private static string FmtCsv(decimal? value)
        => value.HasValue ? value.Value.ToString("0.####", CultureInfo.InvariantCulture) : "";

    private static string Fmt(decimal? value)
        => value.HasValue ? value.Value.ToString("0.0000", CultureInfo.InvariantCulture) : "n/a";

    private static string FmtPct(decimal? value)
        => value.HasValue ? $"{value.Value * 100m:0.0}%" : "n/a";

    private sealed record RankingOptions(
        string CorpusPath,
        string IndexPath,
        string OutputDir,
        int TopN,
        int MinimumReturnedRecords,
        decimal NegativeThreshold,
        decimal StrongThreshold,
        DateTime TrainFromEt,
        DateTime TrainToEt,
        DateTime HoldoutFromEt,
        DateTime HoldoutToEt);

    private sealed record RankingCandidate(
        int Rank,
        string Direction,
        string EntryType,
        string Tier,
        decimal? ActualResolvedR,
        string? ActualOutcome,
        bool? ActualTriggered,
        CandidateScenarioHistoricalEvidence? Evidence,
        string EvidenceBand);
}

public sealed record CandidateSnapshot(
    int Rank,
    string Direction,
    string EntryType,
    string Tier,
    string EvidenceBand,
    int ReturnedEvidenceRecords,
    decimal? EvidenceExpectancyPerTriggered,
    decimal? EvidenceMeanR,
    decimal? EvidenceAverageDistance,
    bool? ActualTriggered,
    string? ActualOutcome,
    decimal? ActualResolvedR);

public sealed record RankingSimulationRow(
    string Window,
    string Ticker,
    DateTime AsOfUtc,
    DateTime SessionDateEt,
    int ValidScenarioCount,
    string TopTier,
    int TopTierCandidateCount,
    CandidateSnapshot RawRank,
    CandidateSnapshot TierOnly,
    CandidateSnapshot EvidenceAware,
    bool TierChangedVsRaw,
    bool EvidenceChangedVsTier,
    bool EvidenceChangedDirection,
    bool EvidenceChangedSetup,
    decimal? TierVsRawPairedResolvedDeltaR,
    decimal? EvidenceVsTierPairedResolvedDeltaR);

public sealed record RankingWindowSummary(
    int Cards,
    int RawVsTierChangedSelections,
    decimal? RawVsTierChangedSelectionRate,
    int EvidenceChangedSelections,
    decimal? EvidenceChangedSelectionRate,
    int EvidenceChangedDirection,
    int EvidenceChangedSetup,
    int TierVsRawPairedResolvedCards,
    decimal? RawRankMeanRPaired,
    decimal? TierOnlyMeanRVsRawPaired,
    decimal? TierVsRawDeltaTotalR,
    int EvidenceVsTierPairedResolvedCards,
    decimal? TierOnlyMeanRPaired,
    decimal? EvidenceAwareMeanRPaired,
    decimal? TierOnlyWinRatePaired,
    decimal? EvidenceAwareWinRatePaired,
    decimal? TierOnlyTotalRPaired,
    decimal? EvidenceAwareTotalRPaired,
    decimal? EvidenceVsTierDeltaTotalR,
    int ChangedResolvedCards,
    int ChangedResolvedBetter,
    int ChangedResolvedWorse,
    int ChangedResolvedEqual,
    decimal? ChangedResolvedDeltaTotalR,
    decimal? ChangedResolvedMeanDeltaR,
    IReadOnlyDictionary<string, int> TierOnlyBandCounts,
    IReadOnlyDictionary<string, int> EvidenceAwareBandCounts);

public sealed record RankingSimulationSummary(
    string Stage,
    DateTime GeneratedUtc,
    string CorpusPath,
    string IndexPath,
    int EvidenceTopN,
    int MinimumReturnedRecords,
    decimal NegativeThreshold,
    decimal FrozenStrongThreshold,
    DateTime TrainFromEt,
    DateTime TrainToEt,
    DateTime HoldoutFromEt,
    DateTime HoldoutToEt,
    DateTime HoldoutEvidenceFrozenBeforeEt,
    int CorpusLines,
    int CardsWithAnyStructurallyValidScenario,
    int CardsWithMultipleStructurallyValidScenarios,
    int SimulationRows,
    RankingWindowSummary Training,
    RankingWindowSummary Holdout,
    int ParseErrors);
