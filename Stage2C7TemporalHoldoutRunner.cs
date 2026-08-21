using System.Globalization;
using System.Text;
using System.Text.Json;

namespace get_assessment_no_graph;

/// <summary>
/// Stage 2C.7 temporal holdout validation for candidate-conditioned historical evidence.
///
/// The training window derives an evidence-expectancy strong-support threshold using
/// only causally prior records. The holdout window then freezes BOTH the threshold and
/// the outcome-bearing evidence pool at the holdout start date, so no holdout-session
/// outcomes can influence any holdout prediction.
///
/// No LLM, market-data, Supabase, or OpenAI calls are made.
/// </summary>
public static class Stage2C7TemporalHoldoutRunner
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
        TemporalOptions options;
        try
        {
            options = ParseOptions(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Stage 2C.7 holdout configuration error: {ex.Message}");
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

        Console.WriteLine("AVA Stage 2C.7 temporal holdout validation");
        Console.WriteLine($"Corpus             : {Path.GetFullPath(options.CorpusPath)}");
        Console.WriteLine($"Evidence index     : {Path.GetFullPath(options.IndexPath)} ({index.RecordCount:N0} records)");
        Console.WriteLine($"Training ET        : {options.TrainFromEt:yyyy-MM-dd} through {options.TrainToEt:yyyy-MM-dd}");
        Console.WriteLine($"Holdout ET         : {options.HoldoutFromEt:yyyy-MM-dd} through {options.HoldoutToEt:yyyy-MM-dd}");
        Console.WriteLine($"Frozen evidence    : sessions strictly before {options.HoldoutFromEt:yyyy-MM-dd} for ALL holdout rows");
        Console.WriteLine($"Strong quantile    : {options.StrongQuantile:P0} derived from TRAINING expectancy only");
        Console.WriteLine($"Evidence top N     : {options.TopN}");
        Console.WriteLine($"Minimum returned   : {options.MinimumReturnedRecords}");
        Console.WriteLine($"Output             : {Path.GetFullPath(options.OutputDir)}");
        Console.WriteLine();
        Console.WriteLine("Safety: offline corpus/index only; no Massive, Supabase, OpenAI, or Ollama calls.");
        Console.WriteLine();

        var trainingRows = new List<TemporalEvidenceRow>();
        var holdoutRows = new List<TemporalEvidenceRow>();
        var corpusLines = 0;
        var parseErrors = 0;

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
                if (TryGetObject(root, "teacher", out var teacher) &&
                    TryGetObject(teacher, "stage2b4", out var stage2b4))
                {
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
                            if (rank.HasValue && !string.IsNullOrWhiteSpace(tier)) tierByRank[rank.Value] = tier!;
                        }
                    }
                }

                if (!TryGetArray(root, "scenarios", out var scenarios))
                    continue;

                var ordinal = 0;
                foreach (var row in scenarios.EnumerateArray())
                {
                    ordinal++;
                    var rank = GetInt(row, "ScenarioRank") ?? GetInt(row, "scenarioRank") ?? GetInt(row, "scenario_rank") ?? ordinal;
                    if (!TryGetObject(row, "Scenario", out var scenario) && !TryGetObject(row, "scenario", out scenario))
                        continue;

                    var structurallyValid = structuralByRank.TryGetValue(rank, out var validByRank)
                        ? validByRank
                        : structuralByRank.TryGetValue(ordinal, out var validByOrdinal) && validByOrdinal;
                    if (!structurallyValid) continue;

                    var realizedR = GetDecimal(row, "ResolvedT1OrStopR") ?? GetDecimal(row, "resolvedT1OrStopR") ?? GetDecimal(row, "resolved_t1_or_stop_r");
                    if (!realizedR.HasValue) continue;

                    var direction = (GetString(scenario, "direction") ?? GetString(scenario, "Direction") ?? "").Trim().ToLowerInvariant();
                    var entryType = (GetString(scenario, "entry_type") ?? GetString(scenario, "entryType") ?? GetString(scenario, "EntryType") ?? "").Trim().ToLowerInvariant();
                    if (string.IsNullOrWhiteSpace(direction) || string.IsNullOrWhiteSpace(entryType)) continue;

                    // Training rows use only evidence prior to their own decision date.
                    // Holdout rows additionally freeze the evidence pool at holdout start,
                    // preventing earlier holdout outcomes from entering later holdout features.
                    DateTime? evidenceBeforeEt = string.Equals(window, "holdout", StringComparison.Ordinal)
                        ? options.HoldoutFromEt.Date
                        : null;

                    var evidence = index.QueryCandidateEvidence(
                        compactJson,
                        rank,
                        direction,
                        entryType,
                        options.TopN,
                        evidenceBeforeEt);

                    var temporalRow = new TemporalEvidenceRow(
                        Window: window,
                        Ticker: ticker,
                        AsOfUtc: EnsureUtc(asofUtc.Value),
                        SessionDateEt: sessionDateEt,
                        ScenarioRank: rank,
                        Direction: direction,
                        EntryType: entryType,
                        SelectionTier: tierByRank.GetValueOrDefault(rank, tierByRank.GetValueOrDefault(ordinal, "UNKNOWN")),
                        ActualResolvedR: realizedR.Value,
                        ActualWin: realizedR.Value > 0m,
                        EligibleMatchingRecords: evidence.EligibleMatchingRecords,
                        ReturnedRecords: evidence.ReturnedAnalogueRecords,
                        AverageDistance: evidence.AverageDistance,
                        EvidenceResolvedSamples: evidence.ResolvedRSamples,
                        EvidenceMeanR: evidence.MeanRealizedR,
                        EvidenceMedianR: evidence.MedianRealizedR,
                        EvidenceExpectancyPerTriggered: evidence.ExpectancyRPerTriggeredZeroUnresolved,
                        TriggerRate: evidence.TriggerRate,
                        T1RateIfResolved: evidence.T1RateIfResolved,
                        EvidenceBand: "UNASSIGNED");

                    if (string.Equals(window, "training", StringComparison.Ordinal))
                        trainingRows.Add(temporalRow);
                    else
                        holdoutRows.Add(temporalRow);
                }

                if (corpusLines % 500 == 0)
                    Console.WriteLine($"  progress corpus={corpusLines:N0} training={trainingRows.Count:N0} holdout={holdoutRows.Count:N0}");
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                parseErrors++;
                if (parseErrors <= 10)
                    Console.WriteLine($"  HOLDOUT skip line={corpusLines} error={ex.Message}");
            }
        }

        var thresholdSource = trainingRows
            .Where(r => r.ReturnedRecords >= options.MinimumReturnedRecords && r.EvidenceExpectancyPerTriggered.HasValue)
            .Select(r => r.EvidenceExpectancyPerTriggered!.Value)
            .OrderBy(x => x)
            .ToList();

        if (thresholdSource.Count == 0)
        {
            Console.Error.WriteLine("No training rows had sufficient non-null evidence expectancy; cannot derive a frozen strong-support threshold.");
            return 2;
        }

        var rawStrongThreshold = QuantileNearestRank(thresholdSource, options.StrongQuantile);
        var strongThreshold = Math.Max(0m, rawStrongThreshold);

        trainingRows = trainingRows.Select(r => r with
        {
            EvidenceBand = ClassifyBand(r, options.MinimumReturnedRecords, strongThreshold)
        }).ToList();
        holdoutRows = holdoutRows.Select(r => r with
        {
            EvidenceBand = ClassifyBand(r, options.MinimumReturnedRecords, strongThreshold)
        }).ToList();

        var summary = BuildSummary(
            options,
            corpusLines,
            parseErrors,
            rawStrongThreshold,
            strongThreshold,
            trainingRows,
            holdoutRows);

        await WriteOutputsAsync(options, trainingRows, holdoutRows, summary, ct);

        Console.WriteLine();
        Console.WriteLine("Stage 2C.7 temporal holdout validation complete.");
        Console.WriteLine($"Training resolved rows      : {trainingRows.Count:N0}");
        Console.WriteLine($"Holdout resolved rows       : {holdoutRows.Count:N0}");
        Console.WriteLine($"Training sufficient evidence: {summary.TrainingCoverage:P1}");
        Console.WriteLine($"Holdout sufficient evidence : {summary.HoldoutCoverage:P1}");
        Console.WriteLine($"Frozen strong threshold     : {strongThreshold:0.####} R/trigger (training {options.StrongQuantile:P0} quantile)");
        Console.WriteLine($"Training expectancy Pearson : {Fmt(summary.TrainingExpectancyPearson)}");
        Console.WriteLine($"Holdout expectancy Pearson  : {Fmt(summary.HoldoutExpectancyPearson)}");
        Console.WriteLine("Holdout evidence bands:");
        foreach (var band in summary.HoldoutBands)
        {
            Console.WriteLine(
                $"  {band.Label,-12} n={band.Count,4} meanR={Fmt(band.ActualMeanR),7} " +
                $"win={FmtPct(band.ActualWinRate),6} totalR={Fmt(band.ActualTotalR),8}");
        }
        Console.WriteLine($"Mean-R ordering STRONG > NEUTRAL > NEGATIVE: {(summary.HoldoutMeanROrderingPass == true ? "PASS" : summary.HoldoutMeanROrderingPass == false ? "FAIL" : "N/A")}");
        Console.WriteLine($"CSV     : {Path.GetFullPath(Path.Combine(options.OutputDir, "stage2c7_temporal_holdout_rows.csv"))}");
        Console.WriteLine($"Summary : {Path.GetFullPath(Path.Combine(options.OutputDir, "stage2c7_temporal_holdout_summary.json"))}");

        return parseErrors > 0 ? 1 : 0;
    }

    private static TemporalHoldoutSummary BuildSummary(
        TemporalOptions options,
        int corpusLines,
        int parseErrors,
        decimal rawStrongThreshold,
        decimal strongThreshold,
        IReadOnlyList<TemporalEvidenceRow> trainingRows,
        IReadOnlyList<TemporalEvidenceRow> holdoutRows)
    {
        var trainingSufficient = trainingRows
            .Where(r => r.ReturnedRecords >= options.MinimumReturnedRecords && r.EvidenceExpectancyPerTriggered.HasValue)
            .ToList();
        var holdoutSufficient = holdoutRows
            .Where(r => r.ReturnedRecords >= options.MinimumReturnedRecords && r.EvidenceExpectancyPerTriggered.HasValue)
            .ToList();

        var trainPairs = trainingSufficient
            .Select(r => (X: r.EvidenceExpectancyPerTriggered!.Value, Y: r.ActualResolvedR))
            .ToList();
        var holdoutPairs = holdoutSufficient
            .Select(r => (X: r.EvidenceExpectancyPerTriggered!.Value, Y: r.ActualResolvedR))
            .ToList();

        var trainingBands = BuildBandSummaries(trainingRows);
        var holdoutBands = BuildBandSummaries(holdoutRows);

        decimal? strongMean = holdoutBands.FirstOrDefault(b => b.Label == "STRONG")?.ActualMeanR;
        decimal? neutralMean = holdoutBands.FirstOrDefault(b => b.Label == "NEUTRAL")?.ActualMeanR;
        decimal? negativeMean = holdoutBands.FirstOrDefault(b => b.Label == "NEGATIVE")?.ActualMeanR;
        bool? ordering = strongMean.HasValue && neutralMean.HasValue && negativeMean.HasValue
            ? strongMean.Value > neutralMean.Value && neutralMean.Value > negativeMean.Value
            : null;

        return new TemporalHoldoutSummary(
            Stage: "stage2c7_temporal_holdout_v1",
            GeneratedUtc: DateTime.UtcNow,
            CorpusPath: Path.GetFullPath(options.CorpusPath),
            IndexPath: Path.GetFullPath(options.IndexPath),
            EvidenceTopN: options.TopN,
            MinimumReturnedRecords: options.MinimumReturnedRecords,
            StrongQuantile: options.StrongQuantile,
            NegativeThreshold: 0m,
            RawTrainingStrongThreshold: Math.Round(rawStrongThreshold, 4),
            FrozenStrongThreshold: Math.Round(strongThreshold, 4),
            TrainFromEt: options.TrainFromEt,
            TrainToEt: options.TrainToEt,
            HoldoutFromEt: options.HoldoutFromEt,
            HoldoutToEt: options.HoldoutToEt,
            HoldoutEvidenceFrozenBeforeEt: options.HoldoutFromEt,
            CorpusLines: corpusLines,
            TrainingRows: trainingRows.Count,
            HoldoutRows: holdoutRows.Count,
            TrainingCoverage: trainingRows.Count == 0 ? 0m : Math.Round((decimal)trainingSufficient.Count / trainingRows.Count, 4),
            HoldoutCoverage: holdoutRows.Count == 0 ? 0m : Math.Round((decimal)holdoutSufficient.Count / holdoutRows.Count, 4),
            TrainingExpectancyPearson: Pearson(trainPairs),
            HoldoutExpectancyPearson: Pearson(holdoutPairs),
            TrainingBands: trainingBands,
            HoldoutBands: holdoutBands,
            HoldoutMeanROrderingPass: ordering,
            HoldoutStrongMinusNeutralMeanR: strongMean.HasValue && neutralMean.HasValue ? Math.Round(strongMean.Value - neutralMean.Value, 4) : null,
            HoldoutNeutralMinusNegativeMeanR: neutralMean.HasValue && negativeMean.HasValue ? Math.Round(neutralMean.Value - negativeMean.Value, 4) : null,
            ParseErrors: parseErrors);
    }

    private static IReadOnlyList<TemporalEvidenceBandSummary> BuildBandSummaries(IReadOnlyList<TemporalEvidenceRow> rows)
    {
        var order = new[] { "INSUFFICIENT", "NEGATIVE", "NEUTRAL", "STRONG" };
        return order.Select(label =>
        {
            var slice = rows.Where(r => string.Equals(r.EvidenceBand, label, StringComparison.Ordinal)).ToList();
            if (slice.Count == 0)
                return new TemporalEvidenceBandSummary(label, 0, null, null, null, null, null, null);

            var evidenceValues = slice.Where(r => r.EvidenceExpectancyPerTriggered.HasValue)
                .Select(r => r.EvidenceExpectancyPerTriggered!.Value).ToList();
            return new TemporalEvidenceBandSummary(
                Label: label,
                Count: slice.Count,
                EvidenceExpectancyMin: evidenceValues.Count == 0 ? null : Math.Round(evidenceValues.Min(), 4),
                EvidenceExpectancyMax: evidenceValues.Count == 0 ? null : Math.Round(evidenceValues.Max(), 4),
                EvidenceExpectancyMean: evidenceValues.Count == 0 ? null : Math.Round(evidenceValues.Average(), 4),
                ActualMeanR: Math.Round(slice.Average(r => r.ActualResolvedR), 4),
                ActualWinRate: Math.Round((decimal)slice.Count(r => r.ActualWin) / slice.Count, 4),
                ActualTotalR: Math.Round(slice.Sum(r => r.ActualResolvedR), 4));
        }).ToList();
    }

    private static string ClassifyBand(TemporalEvidenceRow row, int minimumReturnedRecords, decimal strongThreshold)
    {
        if (row.ReturnedRecords < minimumReturnedRecords || !row.EvidenceExpectancyPerTriggered.HasValue)
            return "INSUFFICIENT";
        var evidence = row.EvidenceExpectancyPerTriggered.Value;
        if (evidence < 0m) return "NEGATIVE";
        if (evidence >= strongThreshold) return "STRONG";
        return "NEUTRAL";
    }

    private static decimal QuantileNearestRank(IReadOnlyList<decimal> orderedValues, decimal quantile)
    {
        if (orderedValues.Count == 0) throw new ArgumentException("Quantile source is empty.");
        var q = Math.Clamp(quantile, 0m, 1m);
        var rank = (int)Math.Ceiling((double)(q * orderedValues.Count));
        rank = Math.Clamp(rank, 1, orderedValues.Count);
        return orderedValues[rank - 1];
    }

    private static decimal? Pearson(IReadOnlyList<(decimal X, decimal Y)> pairs)
    {
        if (pairs.Count < 3) return null;
        var meanX = pairs.Average(p => p.X);
        var meanY = pairs.Average(p => p.Y);
        decimal covariance = 0m, varianceX = 0m, varianceY = 0m;
        foreach (var pair in pairs)
        {
            var dx = pair.X - meanX;
            var dy = pair.Y - meanY;
            covariance += dx * dy;
            varianceX += dx * dx;
            varianceY += dy * dy;
        }
        if (varianceX <= 0m || varianceY <= 0m) return null;
        return Math.Round((decimal)((double)covariance / Math.Sqrt((double)(varianceX * varianceY))), 4);
    }

    private static string? WindowFor(DateTime sessionDateEt, TemporalOptions options)
    {
        var d = sessionDateEt.Date;
        if (d >= options.TrainFromEt.Date && d <= options.TrainToEt.Date) return "training";
        if (d >= options.HoldoutFromEt.Date && d <= options.HoldoutToEt.Date) return "holdout";
        return null;
    }

    private static async Task WriteOutputsAsync(
        TemporalOptions options,
        IReadOnlyList<TemporalEvidenceRow> trainingRows,
        IReadOnlyList<TemporalEvidenceRow> holdoutRows,
        TemporalHoldoutSummary summary,
        CancellationToken ct)
    {
        var allRows = trainingRows.Concat(holdoutRows)
            .OrderBy(r => r.AsOfUtc)
            .ThenBy(r => r.Ticker, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.ScenarioRank)
            .ToList();

        var csvPath = Path.Combine(options.OutputDir, "stage2c7_temporal_holdout_rows.csv");
        var jsonPath = Path.Combine(options.OutputDir, "stage2c7_temporal_holdout_rows.json");
        var summaryPath = Path.Combine(options.OutputDir, "stage2c7_temporal_holdout_summary.json");

        var sb = new StringBuilder();
        sb.AppendLine("window,ticker,asof_utc,session_date_et,scenario_rank,direction,entry_type,selection_tier,actual_resolved_r,actual_win,evidence_band,eligible_matching_records,returned_records,average_distance,evidence_resolved_samples,evidence_mean_r,evidence_median_r,evidence_expectancy_per_triggered,trigger_rate,t1_rate_if_resolved");
        foreach (var row in allRows)
        {
            sb.Append(Csv(row.Window)).Append(',')
              .Append(Csv(row.Ticker)).Append(',')
              .Append(row.AsOfUtc.ToString("O", CultureInfo.InvariantCulture)).Append(',')
              .Append(row.SessionDateEt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
              .Append(row.ScenarioRank).Append(',')
              .Append(Csv(row.Direction)).Append(',')
              .Append(Csv(row.EntryType)).Append(',')
              .Append(Csv(row.SelectionTier)).Append(',')
              .Append(FmtCsv(row.ActualResolvedR)).Append(',')
              .Append(row.ActualWin ? "true" : "false").Append(',')
              .Append(Csv(row.EvidenceBand)).Append(',')
              .Append(row.EligibleMatchingRecords).Append(',')
              .Append(row.ReturnedRecords).Append(',')
              .Append(FmtCsv(row.AverageDistance)).Append(',')
              .Append(row.EvidenceResolvedSamples).Append(',')
              .Append(FmtCsv(row.EvidenceMeanR)).Append(',')
              .Append(FmtCsv(row.EvidenceMedianR)).Append(',')
              .Append(FmtCsv(row.EvidenceExpectancyPerTriggered)).Append(',')
              .Append(FmtCsv(row.TriggerRate)).Append(',')
              .Append(FmtCsv(row.T1RateIfResolved)).AppendLine();
        }

        await File.WriteAllTextAsync(csvPath, sb.ToString(), ct);
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(allRows, PrettyJsonOptions), ct);
        await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(summary, PrettyJsonOptions), ct);
    }

    private static TemporalOptions ParseOptions(string[] args)
    {
        var corpus = ReadOption(args, "--corpus");
        if (string.IsNullOrWhiteSpace(corpus))
            throw new ArgumentException("--candidate-evidence-holdout requires --corpus=<Stage2B.4 corpus JSONL>.");

        var index = ReadOption(args, "--candidate-evidence-index");
        if (string.IsNullOrWhiteSpace(index))
            throw new ArgumentException("--candidate-evidence-holdout requires --candidate-evidence-index=<analogue index JSON>.");

        var trainFrom = ParseDate(ReadOption(args, "--train-from-et") ?? "2026-07-01", "--train-from-et");
        var trainTo = ParseDate(ReadOption(args, "--train-to-et") ?? "2026-07-31", "--train-to-et");
        var holdoutFrom = ParseDate(ReadOption(args, "--holdout-from-et") ?? "2026-08-01", "--holdout-from-et");
        var holdoutTo = ParseDate(ReadOption(args, "--holdout-to-et") ?? "2026-08-07", "--holdout-to-et");

        if (trainTo < trainFrom) throw new ArgumentException("Training end must be on/after training start.");
        if (holdoutTo < holdoutFrom) throw new ArgumentException("Holdout end must be on/after holdout start.");
        if (trainTo >= holdoutFrom) throw new ArgumentException("Training and holdout windows must not overlap; training must end before holdout begins.");

        var topN = ParseInt(ReadOption(args, "--candidate-evidence-top"), 24, 1, 100, "--candidate-evidence-top");
        var minReturned = ParseInt(ReadOption(args, "--minimum-evidence-records"), 5, 0, 100, "--minimum-evidence-records");
        var strongQuantile = ParseDecimal(ReadOption(args, "--strong-quantile"), 0.80m, 0.50m, 0.99m, "--strong-quantile");
        var outputDir = ReadOption(args, "--output-dir") ?? "stage2c7_temporal_holdout";

        return new TemporalOptions(corpus, index, outputDir, topN, minReturned, strongQuantile, trainFrom, trainTo, holdoutFrom, holdoutTo);
    }

    public static void PrintHelp()
    {
        Console.WriteLine("AVA Stage 2C.7 temporal holdout validation");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run -- --historical-shadow --candidate-evidence-holdout `");
        Console.WriteLine("    --corpus=.\\historical_corpus_stage2c_source\\ava_corpus_all_....jsonl `");
        Console.WriteLine("    --candidate-evidence-index=.\\historical_corpus_stage2c_source\\ava_analogue_index.json `");
        Console.WriteLine("    --train-from-et=2026-07-01 --train-to-et=2026-07-31 `");
        Console.WriteLine("    --holdout-from-et=2026-08-01 --holdout-to-et=2026-08-07 `");
        Console.WriteLine("    --strong-quantile=0.80 --minimum-evidence-records=5 `");
        Console.WriteLine("    --output-dir=.\\stage2c7_temporal_holdout");
        Console.WriteLine();
        Console.WriteLine("The strong-support threshold is derived only from training expectancy values.");
        Console.WriteLine("For every holdout row the historical evidence pool is frozen before holdout start,");
        Console.WriteLine("so no holdout outcome can influence another holdout prediction.");
        Console.WriteLine("No LLM or network calls are made.");
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

    private sealed record TemporalOptions(
        string CorpusPath,
        string IndexPath,
        string OutputDir,
        int TopN,
        int MinimumReturnedRecords,
        decimal StrongQuantile,
        DateTime TrainFromEt,
        DateTime TrainToEt,
        DateTime HoldoutFromEt,
        DateTime HoldoutToEt);
}

public sealed record TemporalEvidenceRow(
    string Window,
    string Ticker,
    DateTime AsOfUtc,
    DateTime SessionDateEt,
    int ScenarioRank,
    string Direction,
    string EntryType,
    string SelectionTier,
    decimal ActualResolvedR,
    bool ActualWin,
    int EligibleMatchingRecords,
    int ReturnedRecords,
    decimal? AverageDistance,
    int EvidenceResolvedSamples,
    decimal? EvidenceMeanR,
    decimal? EvidenceMedianR,
    decimal? EvidenceExpectancyPerTriggered,
    decimal? TriggerRate,
    decimal? T1RateIfResolved,
    string EvidenceBand);

public sealed record TemporalEvidenceBandSummary(
    string Label,
    int Count,
    decimal? EvidenceExpectancyMin,
    decimal? EvidenceExpectancyMax,
    decimal? EvidenceExpectancyMean,
    decimal? ActualMeanR,
    decimal? ActualWinRate,
    decimal? ActualTotalR);

public sealed record TemporalHoldoutSummary(
    string Stage,
    DateTime GeneratedUtc,
    string CorpusPath,
    string IndexPath,
    int EvidenceTopN,
    int MinimumReturnedRecords,
    decimal StrongQuantile,
    decimal NegativeThreshold,
    decimal RawTrainingStrongThreshold,
    decimal FrozenStrongThreshold,
    DateTime TrainFromEt,
    DateTime TrainToEt,
    DateTime HoldoutFromEt,
    DateTime HoldoutToEt,
    DateTime HoldoutEvidenceFrozenBeforeEt,
    int CorpusLines,
    int TrainingRows,
    int HoldoutRows,
    decimal TrainingCoverage,
    decimal HoldoutCoverage,
    decimal? TrainingExpectancyPearson,
    decimal? HoldoutExpectancyPearson,
    IReadOnlyList<TemporalEvidenceBandSummary> TrainingBands,
    IReadOnlyList<TemporalEvidenceBandSummary> HoldoutBands,
    bool? HoldoutMeanROrderingPass,
    decimal? HoldoutStrongMinusNeutralMeanR,
    decimal? HoldoutNeutralMinusNegativeMeanR,
    int ParseErrors);
