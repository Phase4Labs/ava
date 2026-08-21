using System.Globalization;
using System.Text;
using System.Text.Json;

namespace get_assessment_no_graph;

/// <summary>
/// Stage 2C.6 offline calibration for candidate-conditioned historical evidence.
///
/// No LLM calls are made. For every resolved, structurally-valid historical scenario,
/// the runner reconstructs the scenario's compact decision state, asks the analogue
/// index only about PRIOR completed sessions, and compares the evidence sidecar with
/// the scenario's subsequently realized R label.
/// </summary>
public static class Stage2C6EvidenceCalibrationRunner
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

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        CalibrationOptions options;
        try
        {
            options = ParseOptions(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Stage 2C.6 calibration configuration error: {ex.Message}");
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

        Console.WriteLine("AVA Stage 2C.6 candidate-evidence calibration");
        Console.WriteLine($"Corpus          : {Path.GetFullPath(options.CorpusPath)}");
        Console.WriteLine($"Evidence index  : {Path.GetFullPath(options.IndexPath)} ({index.RecordCount:N0} records)");
        Console.WriteLine($"Evidence top N  : {options.TopN}");
        Console.WriteLine($"Minimum returned: {options.MinimumReturnedRecords} (summary threshold only; raw rows retain all)");
        Console.WriteLine($"Limit           : {(options.Limit == 0 ? "ALL resolved valid scenarios" : options.Limit.ToString("N0", CultureInfo.InvariantCulture))}");
        Console.WriteLine($"Output          : {Path.GetFullPath(options.OutputDir)}");
        Console.WriteLine();
        Console.WriteLine("Safety: offline corpus/index only; no Massive, Supabase, OpenAI, or Ollama calls.");
        Console.WriteLine();

        var rows = new List<CalibrationRow>();
        var corpusLines = 0;
        var structurallyValidScenarios = 0;
        var resolvedValidScenarios = 0;
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
                var asof = GetDateTime(root, "source", "asof_utc") ?? GetDateTime(compact, "ts_asof_utc");
                if (!asof.HasValue || string.IsNullOrWhiteSpace(ticker)) continue;

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
                    structurallyValidScenarios++;

                    var realizedR = GetDecimal(row, "ResolvedT1OrStopR") ?? GetDecimal(row, "resolvedT1OrStopR") ?? GetDecimal(row, "resolved_t1_or_stop_r");
                    if (!realizedR.HasValue) continue;
                    resolvedValidScenarios++;

                    var direction = (GetString(scenario, "direction") ?? GetString(scenario, "Direction") ?? "").Trim().ToLowerInvariant();
                    var entryType = (GetString(scenario, "entry_type") ?? GetString(scenario, "entryType") ?? GetString(scenario, "EntryType") ?? "").Trim().ToLowerInvariant();
                    if (string.IsNullOrWhiteSpace(direction) || string.IsNullOrWhiteSpace(entryType)) continue;

                    var evidence = index.QueryCandidateEvidence(compactJson, rank, direction, entryType, options.TopN);
                    rows.Add(new CalibrationRow(
                        Ticker: ticker,
                        AsOfUtc: EnsureUtc(asof.Value),
                        ScenarioRank: rank,
                        Direction: direction,
                        EntryType: entryType,
                        SelectionTier: tierByRank.GetValueOrDefault(rank, tierByRank.GetValueOrDefault(ordinal, "UNKNOWN")),
                        ActualResolvedR: realizedR.Value,
                        ActualWin: realizedR.Value > 0m,
                        EligibleMatchingRecords: evidence.EligibleMatchingRecords,
                        ReturnedRecords: evidence.ReturnedAnalogueRecords,
                        AverageDistance: evidence.AverageDistance,
                        TriggerRate: evidence.TriggerRate,
                        T1RateIfResolved: evidence.T1RateIfResolved,
                        EvidenceResolvedSamples: evidence.ResolvedRSamples,
                        EvidencePositiveResolved: evidence.PositiveResolved,
                        EvidenceNegativeResolved: evidence.NegativeResolved,
                        EvidenceMeanR: evidence.MeanRealizedR,
                        EvidenceMedianR: evidence.MedianRealizedR,
                        EvidenceExpectancyPerTriggered: evidence.ExpectancyRPerTriggeredZeroUnresolved,
                        EvidencePreferredCount: evidence.PreferredCount,
                        EvidenceSecondaryCount: evidence.SecondaryCount));

                    if (options.Limit > 0 && rows.Count >= options.Limit)
                        break;
                }

                if (options.Limit > 0 && rows.Count >= options.Limit)
                    break;

                if (corpusLines % 250 == 0)
                    Console.WriteLine($"  progress corpus={corpusLines:N0} calibration_rows={rows.Count:N0}");
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                parseErrors++;
                if (parseErrors <= 10)
                    Console.WriteLine($"  CALIBRATION skip line={corpusLines} error={ex.Message}");
            }
        }

        var summary = BuildSummary(rows, corpusLines, structurallyValidScenarios, resolvedValidScenarios, parseErrors, options);
        await WriteOutputsAsync(rows, summary, options, ct);

        Console.WriteLine();
        Console.WriteLine("Stage 2C.6 candidate-evidence calibration complete.");
        Console.WriteLine($"Corpus lines             : {corpusLines:N0}");
        Console.WriteLine($"Structurally valid       : {structurallyValidScenarios:N0}");
        Console.WriteLine($"Resolved valid scenarios : {resolvedValidScenarios:N0}");
        Console.WriteLine($"Calibration rows         : {rows.Count:N0}");
        Console.WriteLine($"Rows >= min evidence     : {summary.RowsMeetingMinimumEvidence:N0}");
        Console.WriteLine($"Evidence coverage        : {FmtPct(summary.MinimumEvidenceCoverage)}");
        Console.WriteLine($"Mean-R Pearson vs actual : {Fmt(summary.EvidenceMeanRPearson)}");
        Console.WriteLine($"Exp/trigger Pearson      : {Fmt(summary.EvidenceExpectancyPearson)}");
        Console.WriteLine($"Distance Pearson         : {Fmt(summary.DistancePearson)}");
        Console.WriteLine($"CSV                       : {Path.GetFullPath(Path.Combine(options.OutputDir, "stage2c6_candidate_evidence_calibration.csv"))}");
        Console.WriteLine($"Summary                   : {Path.GetFullPath(Path.Combine(options.OutputDir, "stage2c6_candidate_evidence_summary.json"))}");
        return parseErrors > 0 ? 1 : 0;
    }

    private static CalibrationSummary BuildSummary(
        IReadOnlyList<CalibrationRow> rows,
        int corpusLines,
        int structurallyValidScenarios,
        int resolvedValidScenarios,
        int parseErrors,
        CalibrationOptions options)
    {
        var usable = rows.Where(r => r.ReturnedRecords >= options.MinimumReturnedRecords).ToList();
        var meanRPairs = usable.Where(r => r.EvidenceMeanR.HasValue).Select(r => (X: r.EvidenceMeanR!.Value, Y: r.ActualResolvedR)).ToList();
        var expectancyPairs = usable.Where(r => r.EvidenceExpectancyPerTriggered.HasValue).Select(r => (X: r.EvidenceExpectancyPerTriggered!.Value, Y: r.ActualResolvedR)).ToList();
        var distancePairs = usable.Where(r => r.AverageDistance.HasValue).Select(r => (X: r.AverageDistance!.Value, Y: r.ActualResolvedR)).ToList();

        return new CalibrationSummary(
            Stage: "stage2c6_candidate_evidence_calibration_v1",
            GeneratedUtc: DateTime.UtcNow,
            CorpusPath: Path.GetFullPath(options.CorpusPath),
            IndexPath: Path.GetFullPath(options.IndexPath),
            EvidenceTopN: options.TopN,
            MinimumReturnedRecords: options.MinimumReturnedRecords,
            CorpusLines: corpusLines,
            StructurallyValidScenarios: structurallyValidScenarios,
            ResolvedValidScenarios: resolvedValidScenarios,
            CalibrationRows: rows.Count,
            RowsMeetingMinimumEvidence: usable.Count,
            MinimumEvidenceCoverage: rows.Count == 0 ? null : Math.Round((decimal)usable.Count / rows.Count, 4),
            ActualMeanResolvedR: rows.Count == 0 ? null : Math.Round(rows.Average(r => r.ActualResolvedR), 4),
            ActualWinRate: rows.Count == 0 ? null : Math.Round((decimal)rows.Count(r => r.ActualWin) / rows.Count, 4),
            EvidenceMeanRPearson: Pearson(meanRPairs),
            EvidenceExpectancyPearson: Pearson(expectancyPairs),
            DistancePearson: Pearson(distancePairs),
            EvidenceMeanRQuintiles: BuildQuintiles(usable.Where(r => r.EvidenceMeanR.HasValue).ToList(), r => r.EvidenceMeanR!.Value),
            EvidenceExpectancyQuintiles: BuildQuintiles(usable.Where(r => r.EvidenceExpectancyPerTriggered.HasValue).ToList(), r => r.EvidenceExpectancyPerTriggered!.Value),
            DistanceQuintiles: BuildQuintiles(usable.Where(r => r.AverageDistance.HasValue).ToList(), r => r.AverageDistance!.Value),
            SampleSizeBuckets: BuildSampleBuckets(rows),
            EvidenceMeanRSignBuckets: BuildSignBuckets(usable, r => r.EvidenceMeanR),
            EvidenceExpectancySignBuckets: BuildSignBuckets(usable, r => r.EvidenceExpectancyPerTriggered),
            ParseErrors: parseErrors);
    }

    private static IReadOnlyList<CalibrationBucket> BuildQuintiles(
        IReadOnlyList<CalibrationRow> rows,
        Func<CalibrationRow, decimal> selector)
    {
        if (rows.Count == 0) return Array.Empty<CalibrationBucket>();
        var ordered = rows.OrderBy(selector).ToList();
        var result = new List<CalibrationBucket>();
        for (var q = 0; q < 5; q++)
        {
            var start = q * ordered.Count / 5;
            var end = (q + 1) * ordered.Count / 5;
            if (end <= start) continue;
            var slice = ordered.GetRange(start, end - start);
            result.Add(BuildBucket($"Q{q + 1}", slice, selector));
        }
        return result;
    }

    private static IReadOnlyList<CalibrationBucket> BuildSampleBuckets(IReadOnlyList<CalibrationRow> rows)
    {
        var definitions = new[]
        {
            ("0-4", 0, 4),
            ("5-9", 5, 9),
            ("10-14", 10, 14),
            ("15-19", 15, 19),
            ("20+", 20, int.MaxValue)
        };
        return definitions.Select(d =>
        {
            var slice = rows.Where(r => r.ReturnedRecords >= d.Item2 && r.ReturnedRecords <= d.Item3).ToList();
            return BuildBucket(d.Item1, slice, r => (decimal)r.ReturnedRecords);
        }).Where(b => b.Count > 0).ToList();
    }

    private static IReadOnlyList<CalibrationBucket> BuildSignBuckets(
        IReadOnlyList<CalibrationRow> rows,
        Func<CalibrationRow, decimal?> selector)
    {
        var negative = rows.Where(r => selector(r).HasValue && selector(r)!.Value < 0m).ToList();
        var zero = rows.Where(r => selector(r).HasValue && selector(r)!.Value == 0m).ToList();
        var positive = rows.Where(r => selector(r).HasValue && selector(r)!.Value > 0m).ToList();
        return new[]
        {
            BuildBucket("negative", negative, r => selector(r) ?? 0m),
            BuildBucket("zero", zero, r => selector(r) ?? 0m),
            BuildBucket("positive", positive, r => selector(r) ?? 0m)
        }.Where(b => b.Count > 0).ToList();
    }

    private static CalibrationBucket BuildBucket(
        string label,
        IReadOnlyList<CalibrationRow> rows,
        Func<CalibrationRow, decimal> evidenceSelector)
    {
        if (rows.Count == 0)
            return new CalibrationBucket(label, 0, null, null, null, null, null, null);

        var evidence = rows.Select(evidenceSelector).ToList();
        return new CalibrationBucket(
            Label: label,
            Count: rows.Count,
            EvidenceMin: Math.Round(evidence.Min(), 4),
            EvidenceMax: Math.Round(evidence.Max(), 4),
            EvidenceMean: Math.Round(evidence.Average(), 4),
            ActualMeanR: Math.Round(rows.Average(r => r.ActualResolvedR), 4),
            ActualWinRate: Math.Round((decimal)rows.Count(r => r.ActualWin) / rows.Count, 4),
            ActualTotalR: Math.Round(rows.Sum(r => r.ActualResolvedR), 4));
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

    private static async Task WriteOutputsAsync(
        IReadOnlyList<CalibrationRow> rows,
        CalibrationSummary summary,
        CalibrationOptions options,
        CancellationToken ct)
    {
        var csvPath = Path.Combine(options.OutputDir, "stage2c6_candidate_evidence_calibration.csv");
        var jsonPath = Path.Combine(options.OutputDir, "stage2c6_candidate_evidence_calibration.json");
        var summaryPath = Path.Combine(options.OutputDir, "stage2c6_candidate_evidence_summary.json");

        var sb = new StringBuilder();
        sb.AppendLine("ticker,asof_utc,scenario_rank,direction,entry_type,selection_tier,actual_resolved_r,actual_win,eligible_matching_records,returned_records,average_distance,trigger_rate,t1_rate_if_resolved,evidence_resolved_samples,evidence_positive_resolved,evidence_negative_resolved,evidence_mean_r,evidence_median_r,evidence_expectancy_per_triggered,evidence_preferred_count,evidence_secondary_count");
        foreach (var row in rows)
        {
            sb.Append(Csv(row.Ticker)).Append(',')
              .Append(row.AsOfUtc.ToString("O", CultureInfo.InvariantCulture)).Append(',')
              .Append(row.ScenarioRank).Append(',')
              .Append(Csv(row.Direction)).Append(',')
              .Append(Csv(row.EntryType)).Append(',')
              .Append(Csv(row.SelectionTier)).Append(',')
              .Append(FmtCsv(row.ActualResolvedR)).Append(',')
              .Append(row.ActualWin ? "true" : "false").Append(',')
              .Append(row.EligibleMatchingRecords).Append(',')
              .Append(row.ReturnedRecords).Append(',')
              .Append(FmtCsv(row.AverageDistance)).Append(',')
              .Append(FmtCsv(row.TriggerRate)).Append(',')
              .Append(FmtCsv(row.T1RateIfResolved)).Append(',')
              .Append(row.EvidenceResolvedSamples).Append(',')
              .Append(row.EvidencePositiveResolved).Append(',')
              .Append(row.EvidenceNegativeResolved).Append(',')
              .Append(FmtCsv(row.EvidenceMeanR)).Append(',')
              .Append(FmtCsv(row.EvidenceMedianR)).Append(',')
              .Append(FmtCsv(row.EvidenceExpectancyPerTriggered)).Append(',')
              .Append(row.EvidencePreferredCount).Append(',')
              .Append(row.EvidenceSecondaryCount).AppendLine();
        }

        await File.WriteAllTextAsync(csvPath, sb.ToString(), ct);
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(rows, PrettyJsonOptions), ct);
        await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(summary, PrettyJsonOptions), ct);
    }

    private static CalibrationOptions ParseOptions(string[] args)
    {
        var corpus = ReadOption(args, "--corpus");
        if (string.IsNullOrWhiteSpace(corpus))
            throw new ArgumentException("--candidate-evidence-calibrate requires --corpus=<Stage2B.4 corpus JSONL>.");

        var index = ReadOption(args, "--candidate-evidence-index");
        if (string.IsNullOrWhiteSpace(index))
            throw new ArgumentException("--candidate-evidence-calibrate requires --candidate-evidence-index=<analogue index JSON>.");

        var outputDir = ReadOption(args, "--output-dir") ?? "stage2c6_candidate_evidence_calibration";
        var topN = ParseInt(ReadOption(args, "--candidate-evidence-top"), 24, 1, 100, "--candidate-evidence-top");
        var minReturned = ParseInt(ReadOption(args, "--minimum-evidence-records"), 5, 0, 100, "--minimum-evidence-records");
        var limit = ParseInt(ReadOption(args, "--limit"), 0, 0, 1000000, "--limit");
        return new CalibrationOptions(corpus, index, outputDir, topN, minReturned, limit);
    }

    public static void PrintHelp()
    {
        Console.WriteLine("AVA Stage 2C.6 candidate-conditioned evidence calibration");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run -- --historical-shadow --candidate-evidence-calibrate `");
        Console.WriteLine("    --corpus=.\\historical_corpus_stage2c_source\\ava_corpus_all_....jsonl `");
        Console.WriteLine("    --candidate-evidence-index=.\\historical_corpus_stage2c_source\\ava_analogue_index.json `");
        Console.WriteLine("    --candidate-evidence-top=24 --minimum-evidence-records=5 `");
        Console.WriteLine("    --output-dir=.\\stage2c6_candidate_evidence_calibration");
        Console.WriteLine();
        Console.WriteLine("No LLM or network calls are made. Every row is evaluated only against completed prior sessions.");
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

    private sealed record CalibrationOptions(
        string CorpusPath,
        string IndexPath,
        string OutputDir,
        int TopN,
        int MinimumReturnedRecords,
        int Limit);
}

public sealed record CalibrationRow(
    string Ticker,
    DateTime AsOfUtc,
    int ScenarioRank,
    string Direction,
    string EntryType,
    string SelectionTier,
    decimal ActualResolvedR,
    bool ActualWin,
    int EligibleMatchingRecords,
    int ReturnedRecords,
    decimal? AverageDistance,
    decimal? TriggerRate,
    decimal? T1RateIfResolved,
    int EvidenceResolvedSamples,
    int EvidencePositiveResolved,
    int EvidenceNegativeResolved,
    decimal? EvidenceMeanR,
    decimal? EvidenceMedianR,
    decimal? EvidenceExpectancyPerTriggered,
    int EvidencePreferredCount,
    int EvidenceSecondaryCount);

public sealed record CalibrationBucket(
    string Label,
    int Count,
    decimal? EvidenceMin,
    decimal? EvidenceMax,
    decimal? EvidenceMean,
    decimal? ActualMeanR,
    decimal? ActualWinRate,
    decimal? ActualTotalR);

public sealed record CalibrationSummary(
    string Stage,
    DateTime GeneratedUtc,
    string CorpusPath,
    string IndexPath,
    int EvidenceTopN,
    int MinimumReturnedRecords,
    int CorpusLines,
    int StructurallyValidScenarios,
    int ResolvedValidScenarios,
    int CalibrationRows,
    int RowsMeetingMinimumEvidence,
    decimal? MinimumEvidenceCoverage,
    decimal? ActualMeanResolvedR,
    decimal? ActualWinRate,
    decimal? EvidenceMeanRPearson,
    decimal? EvidenceExpectancyPearson,
    decimal? DistancePearson,
    IReadOnlyList<CalibrationBucket> EvidenceMeanRQuintiles,
    IReadOnlyList<CalibrationBucket> EvidenceExpectancyQuintiles,
    IReadOnlyList<CalibrationBucket> DistanceQuintiles,
    IReadOnlyList<CalibrationBucket> SampleSizeBuckets,
    IReadOnlyList<CalibrationBucket> EvidenceMeanRSignBuckets,
    IReadOnlyList<CalibrationBucket> EvidenceExpectancySignBuckets,
    int ParseErrors);
