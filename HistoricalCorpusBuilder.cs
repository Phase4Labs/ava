using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace get_assessment_no_graph;

/// <summary>
/// Stage 2B: turns the existing GPT eval_examples history into a causal AVA corpus.
/// This mode is read-only against Supabase. Realized outcomes are reconstructed from
/// Massive/Polygon minute bars strictly AFTER each stored decision timestamp.
/// </summary>
public static class HistoricalCorpusBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public static async Task<int> RunAsync(
        string[] args,
        string polygonKey,
        string supabaseUrl,
        string supabaseKey)
    {
        var options = CorpusOptions.Parse(args);
        if (options.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        using var db = new SupabaseRestClient(supabaseUrl, supabaseKey);
        using var polygon = new PolygonClient(polygonKey);

        if (options.InventoryOnly)
            return await RunInventoryAsync(db, options, CancellationToken.None);

        return await BuildCorpusAsync(db, polygon, options, CancellationToken.None);
    }

    private static async Task<int> RunInventoryAsync(
        SupabaseRestClient db,
        CorpusOptions options,
        CancellationToken ct)
    {
        Console.WriteLine("AVA Stage 2B corpus inventory");
        Console.WriteLine($"Model filter : {options.Model}");
        Console.WriteLine("Read-only     : yes");
        Console.WriteLine();

        foreach (var table in new[]
        {
            "eval_examples",
            "execution_cards",
            "execution_card_scenarios",
            "signal_events",
            "minute_bars",
            "minute_bar_features"
        })
        {
            try
            {
                var count = await db.CountAsync(table, "", ct);
                Console.WriteLine($"{table,-28} {count,10:N0}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{table,-28} unavailable ({Short(ex.Message)})");
            }
        }

        Console.WriteLine();

        long modelCount;
        var modelFilter = $"?model=eq.{Uri.EscapeDataString(options.Model)}";
        try
        {
            modelCount = await db.CountAsync("eval_examples", modelFilter, ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exact count header unavailable for filtered eval_examples ({Short(ex.Message)}).");
            Console.WriteLine("Falling back to a lightweight paged count...");
            modelCount = await CountEvalExamplesByPagingAsync(db, options.Model, ct);
        }

        Console.WriteLine($"eval_examples for {options.Model}: {modelCount:N0}");

        try
        {
            var first = await db.SelectAsync<EvalExampleRow>(
                "eval_examples",
                $"?select=ticker,asof_ts,input_sha256,model,framework_version,openai_response_id" +
                $"&model=eq.{Uri.EscapeDataString(options.Model)}&order=asof_ts.asc&limit=1",
                ct);
            var last = await db.SelectAsync<EvalExampleRow>(
                "eval_examples",
                $"?select=ticker,asof_ts,input_sha256,model,framework_version,openai_response_id" +
                $"&model=eq.{Uri.EscapeDataString(options.Model)}&order=asof_ts.desc&limit=1",
                ct);

            if (first.Count > 0) Console.WriteLine($"First example : {EnsureUtc(first[0].AsofTs):O}  {first[0].Ticker}");
            if (last.Count > 0) Console.WriteLine($"Last example  : {EnsureUtc(last[0].AsofTs):O}  {last[0].Ticker}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"eval_examples date-range inventory failed: {ex.Message}");
            return 2;
        }

        return 0;
    }

    private static async Task<long> CountEvalExamplesByPagingAsync(
        SupabaseRestClient db,
        string model,
        CancellationToken ct)
    {
        const int pageSize = 1000;
        long total = 0;
        var offset = 0;
        var encodedModel = Uri.EscapeDataString(model);

        while (true)
        {
            var page = await db.SelectAsync<CountProbeRow>(
                "eval_examples",
                $"?select=input_sha256&model=eq.{encodedModel}&limit={pageSize}&offset={offset}",
                ct);

            total += page.Count;
            if (page.Count < pageSize)
                return total;

            offset += page.Count;
        }
    }

    private sealed class CountProbeRow
    {
        [JsonPropertyName("input_sha256")]
        public string? InputSha256 { get; set; }
    }

    private static async Task<int> BuildCorpusAsync(
        SupabaseRestClient db,
        PolygonClient polygon,
        CorpusOptions options,
        CancellationToken ct)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var baseName = $"ava_corpus_{Sanitize(options.Ticker ?? "all")}_{stamp}";
        var jsonlPath = Path.Combine(options.OutputDirectory, baseName + ".jsonl");
        var csvPath = Path.Combine(options.OutputDirectory, baseName + "_scenarios.csv");
        var summaryPath = Path.Combine(options.OutputDirectory, baseName + "_summary.json");
        var diagnosticsPath = Path.Combine(options.OutputDirectory, baseName + "_diagnostics.csv");

        Console.WriteLine("AVA Stage 2B historical GPT corpus builder");
        Console.WriteLine($"Model           : {options.Model}");
        Console.WriteLine($"Ticker          : {options.Ticker ?? "ALL"}");
        Console.WriteLine($"From ET         : {FormatMaybe(options.FromUtc)}");
        Console.WriteLine($"To ET           : {FormatMaybe(options.ToUtc)}");
        Console.WriteLine($"Maximum examples: {(options.MaxExamples == 0 ? "ALL" : options.MaxExamples.ToString("N0"))}");
        Console.WriteLine($"Sampling        : {options.SampleMode} (seed={options.SampleSeed})");
        Console.WriteLine($"Outcomes        : {(options.ComputeOutcomes ? "Massive bars through session close" : "disabled")}");
        Console.WriteLine($"JSONL           : {Path.GetFullPath(jsonlPath)}");
        Console.WriteLine($"Scenario CSV    : {Path.GetFullPath(csvPath)}");
        Console.WriteLine($"Diagnostics CSV : {Path.GetFullPath(diagnosticsPath)}");
        Console.WriteLine();
        Console.WriteLine("Safety: read-only against Supabase; no GPT or local LLM calls; no production tables are modified.");
        Console.WriteLine();

        var loadResult = await LoadExamplesAsync(db, options, ct);
        var examples = loadResult.Examples;
        if (examples.Count == 0)
        {
            Console.WriteLine("No eval_examples matched the requested filters.");
            return 0;
        }

        Console.WriteLine($"Loaded {examples.Count:N0} stored GPT examples from {loadResult.CandidatePopulation:N0} matching candidate(s).");
        if (string.Equals(options.SampleMode, "stratified", StringComparison.OrdinalIgnoreCase))
            Console.WriteLine($"Stratified sample: {loadResult.StratumCount:N0} population strata, deterministic seed={options.SampleSeed}.");

        // Older eval_examples omitted grade/rationale. Load scenario rows once for the
        // requested interval and use them only as enrichment; the original levels and
        // probabilities remain those stored in model_output_json.
        var scenarioEnrichment = await LoadScenarioEnrichmentAsync(db, examples, options, ct);
        Console.WriteLine($"Loaded {scenarioEnrichment.Count:N0} scenario enrichment row(s).\n");

        var futureBarCache = new Dictionary<string, IReadOnlyList<MinuteBar>>(StringComparer.OrdinalIgnoreCase);
        var summary = new CorpusSummary();

        await using var jsonl = new StreamWriter(jsonlPath, false, new UTF8Encoding(false));
        await using var csv = new StreamWriter(csvPath, false, new UTF8Encoding(false));
        await using var diagnostics = new StreamWriter(diagnosticsPath, false, new UTF8Encoding(false));
        await csv.WriteLineAsync("ticker,asof_utc,model,framework_version,raw_verdict,effective_verdict,scenario_rank,direction,entry_type,grade,semantic_accepted,triggered,primary_outcome,t1_before_stop,minutes_to_trigger,minutes_to_t1,conservative_entry,stop,t1,t2,runner,conservative_t1_rr,mfe_r,mae_r,resolved_t1_or_stop_r,semantic_error_codes");
        await diagnostics.WriteLineAsync("diagnostic_reason,diagnostic_detail,reason_message,ticker,asof_utc,framework_version,scenario_rank,direction,entry_type,grade,scenario_prob,success_prob,entry_low,entry_high,conservative_entry,stop,t1,t2,runner,conservative_t1_rr,all_error_codes,triggered,trigger_ts_utc,primary_outcome,t1_before_stop,t1_ts_utc,stop_ts_utc,t2_ts_utc,runner_ts_utc,mfe_r,mae_r,resolved_t1_or_stop_r,trigger_reason");
        var diagnosticCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var n = 0;
        foreach (var example in examples)
        {
            n++;
            var asofUtc = EnsureUtc(example.AsofTs);
            Console.Write($"[{n}/{examples.Count}] {example.Ticker} {asofUtc:yyyy-MM-dd HH:mm}Z ... ");

            try
            {
                if (example.InputJson.ValueKind != JsonValueKind.Object || example.ModelOutputJson.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("input_json/model_output_json is missing or not an object");

                var inputJson = example.InputJson.GetRawText();
                var compactJson = CompactMarketStateBuilder.Build(inputJson);
                var card = JsonSerializer.Deserialize<ExecutionCardJsonV1>(example.ModelOutputJson.GetRawText(), JsonOptions)
                           ?? throw new InvalidOperationException("model_output_json could not be parsed");

                EnrichCard(card, example.Ticker, asofUtc, scenarioEnrichment);
                var metadataComplete = card.Scenarios.All(s => !string.IsNullOrWhiteSpace(s.Grade));
                var semantic = ScenarioSemanticValidator.Validate(card, inputJson);
                var stage2b4 = AvaScenarioDecisionLayer.Evaluate(card, inputJson);
                var futureContext = options.ComputeOutcomes
                    ? await EvaluateFutureContextAsync(example, inputJson, polygon, futureBarCache, ct)
                    : null;

                var scenarioRows = new List<CorpusScenarioRecord>();
                foreach (var scenario in card.Scenarios.OrderBy(s => s.ScenarioRank))
                {
                    var semanticScenario = semantic.Scenarios.FirstOrDefault(s => s.ScenarioRank == scenario.ScenarioRank);
                    ScenarioRealizedOutcome? outcome = null;
                    if (options.ComputeOutcomes && futureContext is not null)
                    {
                        outcome = CorpusOutcomeEvaluator.Evaluate(
                            scenario,
                            inputJson,
                            futureContext.SessionBars,
                            asofUtc);
                    }

                    var realizedR = ResolvedT1OrStopR(scenario, semanticScenario, outcome);
                    var record = new CorpusScenarioRecord(
                        scenario.ScenarioRank,
                        scenario,
                        semanticScenario,
                        outcome,
                        ClassifyScenario(scenario, semanticScenario, outcome),
                        realizedR);
                    scenarioRows.Add(record);
                    summary.AddScenario(record);
                    await WriteScenarioCsvAsync(csv, example, semantic, record);
                    await WriteDiagnosticRowsAsync(diagnostics, diagnosticCounts, options.DiagnosticLimit, example, record);
                }

                var cardBucket = ClassifyCard(card, semantic, scenarioRows, metadataComplete);
                summary.AddExample(card, semantic, cardBucket, futureContext, metadataComplete);
                summary.AddStage2B4(stage2b4);

                var corpusRecord = new
                {
                    corpus_version = 3,
                    source = new
                    {
                        ticker = example.Ticker,
                        asof_utc = asofUtc,
                        input_sha256 = example.InputSha256,
                        model = example.Model,
                        framework_version = example.FrameworkVersion,
                        openai_response_id = example.OpenAiResponseId
                    },
                    causal_boundary = new
                    {
                        decision_asof_utc = DatasetAsofUtc(inputJson) ?? asofUtc,
                        rule = "Only data at/before decision_asof_utc appears in model inputs. Massive bars after it are outcome labels only."
                    },
                    teacher = new
                    {
                        card,
                        semantic,
                        stage2b4,
                        metadata_complete = metadataComplete,
                        card_quality_bucket = cardBucket
                    },
                    future_context = futureContext?.Summary,
                    scenarios = scenarioRows,
                    inputs = new
                    {
                        compact_v1 = JsonDocument.Parse(compactJson).RootElement.Clone(),
                        full = options.IncludeFullInput ? example.InputJson : (JsonElement?)null
                    }
                };

                await jsonl.WriteLineAsync(JsonSerializer.Serialize(corpusRecord, JsonOptions));
                Console.WriteLine($"{card.Verdict}->{semantic.EffectiveVerdict}, structural={stage2b4.Structural.EffectiveVerdict}, valid={stage2b4.Structural.StructurallyValidScenarioCount}/{stage2b4.Structural.RawScenarioCount}, preferred={stage2b4.Quality.PreferredScenarioCount}, secondary={stage2b4.Quality.SecondaryScenarioCount}, bucket={cardBucket}");
            }
            catch (Exception ex)
            {
                summary.Errors++;
                Console.WriteLine($"ERROR {Short(ex.Message)}");
                if (options.StopOnError) throw;
            }
        }

        await jsonl.FlushAsync();
        await csv.FlushAsync();
        await diagnostics.FlushAsync();

        var finalSummary = summary.ToSerializable(examples.Count, loadResult, options, jsonlPath, csvPath, diagnosticsPath);
        await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(finalSummary, new JsonSerializerOptions { WriteIndented = true }), ct);

        Console.WriteLine();
        Console.WriteLine("Stage 2B corpus build complete.");
        Console.WriteLine($"Examples         : {summary.Examples:N0}");
        Console.WriteLine($"Raw NO_TRADE     : {summary.RawNoTrade:N0}");
        Console.WriteLine($"Raw TRADE        : {summary.RawTrade:N0}");
        Console.WriteLine($"Effective TRADE  : {summary.EffectiveTrade:N0}");
        Console.WriteLine($"Semantic rejected: {summary.SemanticRejected:N0}");
        Console.WriteLine($"Semantic legacy  : {summary.SemanticNotEvaluable:N0}");
        Console.WriteLine($"Scenarios        : {summary.Scenarios:N0}");
        Console.WriteLine($"Semantic accepted: {summary.SemanticAccepted:N0}");
        Console.WriteLine($"Triggered        : {summary.Triggered:N0}");
        Console.WriteLine($"T1 before stop   : {summary.T1BeforeStop:N0}");
        Console.WriteLine($"Stop before T1   : {summary.StopBeforeT1:N0}");
        Console.WriteLine($"Accepted mean R  : {Fmt(summary.AcceptedExpectancyRResolved)} (resolved T1/stop policy)");
        Console.WriteLine($"Rejected mean R  : {Fmt(summary.RejectedExpectancyRResolved)} (resolved T1/stop policy)");
        Console.WriteLine($"Ablation rules   : {summary.AblationRuleCount:N0}");
        Console.WriteLine($"Stage2B4 valid   : {summary.Stage2B4StructuralValidScenarioCount:N0}");
        Console.WriteLine($"Stage2B4 invalid : {summary.Stage2B4StructuralInvalidScenarioCount:N0}");
        Console.WriteLine($"Stage2B4 preferred: {summary.Stage2B4PreferredScenarioCount:N0}");
        Console.WriteLine($"Stage2B4 secondary: {summary.Stage2B4SecondaryScenarioCount:N0}");
        Console.WriteLine($"Errors           : {summary.Errors:N0}");
        Console.WriteLine($"Summary          : {Path.GetFullPath(summaryPath)}");
        return summary.Errors > 0 ? 1 : 0;
    }

    private static async Task<ExampleLoadResult> LoadExamplesAsync(
        SupabaseRestClient db,
        CorpusOptions options,
        CancellationToken ct)
    {
        if (!string.Equals(options.SampleMode, "stratified", StringComparison.OrdinalIgnoreCase))
        {
            var output = new List<EvalExampleRow>();
            var offset = 0;
            while (true)
            {
                var take = options.MaxExamples == 0
                    ? options.PageSize
                    : Math.Min(options.PageSize, options.MaxExamples - output.Count);
                if (take <= 0) break;

                var q = BuildExampleFilter(options,
                    "ticker,asof_ts,input_json,model_output_json,input_sha256,model,framework_version,openai_response_id,notes");
                q.Append("&order=asof_ts.asc&limit=").Append(take).Append("&offset=").Append(offset);

                var page = await db.SelectAsync<EvalExampleRow>("eval_examples", q.ToString(), ct);
                if (page.Count == 0) break;
                output.AddRange(page);
                offset += page.Count;
                if (page.Count < take) break;
                if (options.MaxExamples > 0 && output.Count >= options.MaxExamples) break;
            }
            return new ExampleLoadResult(output, output.Count, 1);
        }

        // Stratified mode intentionally loads only lightweight candidate metadata first.
        // Full input_json is fetched only for the selected rows so a five-month corpus does
        // not require holding every historical market payload in memory.
        var candidates = await LoadSampleCandidatesAsync(db, options, ct);
        if (candidates.Count == 0)
            return new ExampleLoadResult(new List<EvalExampleRow>(), 0, 0);

        var requested = options.MaxExamples == 0 ? candidates.Count : Math.Min(options.MaxExamples, candidates.Count);
        var selected = SelectStratifiedCandidates(candidates, requested, options.SampleSeed, out var stratumCount);
        var fullRows = await LoadSelectedExamplesAsync(db, options, selected, ct);
        fullRows.Sort((a, b) => EnsureUtc(a.AsofTs).CompareTo(EnsureUtc(b.AsofTs)));
        return new ExampleLoadResult(fullRows, candidates.Count, stratumCount);
    }

    private static StringBuilder BuildExampleFilter(CorpusOptions options, string select)
    {
        var q = new StringBuilder("?select=").Append(select);
        q.Append("&model=eq.").Append(Uri.EscapeDataString(options.Model));
        if (!string.IsNullOrWhiteSpace(options.Ticker))
            q.Append("&ticker=eq.").Append(Uri.EscapeDataString(options.Ticker.ToUpperInvariant()));
        if (options.FromUtc.HasValue)
            q.Append("&asof_ts=gte.").Append(Uri.EscapeDataString(options.FromUtc.Value.ToString("O")));
        if (options.ToUtc.HasValue)
            q.Append("&asof_ts=lte.").Append(Uri.EscapeDataString(options.ToUtc.Value.ToString("O")));
        return q;
    }

    private static async Task<List<SampleCandidateRow>> LoadSampleCandidatesAsync(
        SupabaseRestClient db,
        CorpusOptions options,
        CancellationToken ct)
    {
        var output = new List<SampleCandidateRow>();
        var offset = 0;
        while (true)
        {
            var q = BuildExampleFilter(options,
                "ticker,asof_ts,input_sha256,model_output_json,framework_version");
            q.Append("&order=asof_ts.asc&limit=").Append(options.PageSize).Append("&offset=").Append(offset);
            var page = await db.SelectAsync<SampleCandidateRow>("eval_examples", q.ToString(), ct);
            if (page.Count == 0) break;
            output.AddRange(page);
            offset += page.Count;
            if (page.Count < options.PageSize) break;
        }
        return output;
    }

    private static List<SampleCandidateRow> SelectStratifiedCandidates(
        IReadOnlyList<SampleCandidateRow> candidates,
        int sampleSize,
        int seed,
        out int stratumCount)
    {
        if (sampleSize >= candidates.Count)
        {
            stratumCount = candidates.Select(StratumKey).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            return candidates.OrderBy(x => x.AsofTs).ToList();
        }

        var groups = candidates
            .GroupBy(StratumKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SampleStratum(g.Key, g.ToList()))
            .ToList();
        stratumCount = groups.Count;

        // Proportional allocation preserves the historical population distribution across
        // week, time-of-day, ticker, verdict, direction, entry type and grade. Largest
        // remainder assignment makes the sample deterministic and exactly sampleSize.
        var allocations = new List<SampleAllocation>();
        var assigned = 0;
        foreach (var group in groups)
        {
            var exact = (decimal)group.Items.Count * sampleSize / candidates.Count;
            var floor = (int)Math.Floor(exact);
            allocations.Add(new SampleAllocation(group, floor, exact - floor));
            assigned += floor;
        }

        var remaining = sampleSize - assigned;
        foreach (var allocation in allocations
                     .OrderByDescending(x => x.Remainder)
                     .ThenBy(x => StableHash(seed, x.Stratum.Key)))
        {
            if (remaining <= 0) break;
            if (allocation.Take < allocation.Stratum.Items.Count)
            {
                allocation.Take++;
                remaining--;
            }
        }

        // If rounding plus zero-sized strata still leaves seats, fill them deterministically.
        while (remaining > 0)
        {
            var progressed = false;
            foreach (var allocation in allocations.OrderBy(x => StableHash(seed + 17, x.Stratum.Key)))
            {
                if (remaining <= 0) break;
                if (allocation.Take >= allocation.Stratum.Items.Count) continue;
                allocation.Take++;
                remaining--;
                progressed = true;
            }
            if (!progressed) break;
        }

        var selected = new List<SampleCandidateRow>(sampleSize);
        foreach (var allocation in allocations)
        {
            selected.AddRange(allocation.Stratum.Items
                .OrderBy(x => StableHash(seed, $"{x.InputSha256}|{x.Ticker}|{EnsureUtc(x.AsofTs):O}"))
                .Take(allocation.Take));
        }
        return selected.Take(sampleSize).ToList();
    }

    private static string StratumKey(SampleCandidateRow row)
    {
        var utc = EnsureUtc(row.AsofTs);
        var et = ToEastern(utc);
        var cal = CultureInfo.InvariantCulture.Calendar;
        var week = cal.GetWeekOfYear(et, CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
        var timeBucket = SessionBucket(et.TimeOfDay);
        var shape = CardShape(row.ModelOutputJson);
        return $"{et.Year}-W{week:00}|{timeBucket}|{row.Ticker.ToUpperInvariant()}|{shape.Verdict}|{shape.Direction}|{shape.EntryType}|{shape.Grade}";
    }

    private static string SessionBucket(TimeSpan t)
    {
        if (t < new TimeSpan(9, 30, 0)) return "premarket";
        if (t < new TimeSpan(10, 0, 0)) return "open30";
        if (t < new TimeSpan(11, 30, 0)) return "morning";
        if (t < new TimeSpan(14, 0, 0)) return "midday";
        if (t < new TimeSpan(15, 30, 0)) return "afternoon";
        return "close30";
    }

    private static CardShapeInfo CardShape(JsonElement modelOutput)
    {
        try
        {
            if (modelOutput.ValueKind != JsonValueKind.Object)
                return new CardShapeInfo("unknown", "none", "none", "unknown");
            var verdict = modelOutput.TryGetProperty("verdict", out var v) && v.ValueKind == JsonValueKind.String
                ? (v.GetString() ?? "unknown").ToUpperInvariant()
                : "unknown";
            if (!modelOutput.TryGetProperty("scenarios", out var scenarios) || scenarios.ValueKind != JsonValueKind.Array || scenarios.GetArrayLength() == 0)
                return new CardShapeInfo(verdict, "none", "none", "none");
            var top = scenarios[0];
            string Get(string name, string fallback) => top.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String
                ? (e.GetString() ?? fallback).ToLowerInvariant()
                : fallback;
            return new CardShapeInfo(verdict, Get("direction", "unknown"), Get("entry_type", "unknown"), Get("grade", "unknown").ToUpperInvariant());
        }
        catch
        {
            return new CardShapeInfo("unknown", "unknown", "unknown", "unknown");
        }
    }

    private static async Task<List<EvalExampleRow>> LoadSelectedExamplesAsync(
        SupabaseRestClient db,
        CorpusOptions options,
        IReadOnlyList<SampleCandidateRow> selected,
        CancellationToken ct)
    {
        var output = new List<EvalExampleRow>(selected.Count);
        var hashes = selected.Select(x => x.InputSha256).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        const int batchSize = 25;
        for (var i = 0; i < hashes.Count; i += batchSize)
        {
            var batch = hashes.Skip(i).Take(batchSize).ToList();
            var inList = string.Join(',', batch.Select(Uri.EscapeDataString));
            var q = new StringBuilder("?select=ticker,asof_ts,input_json,model_output_json,input_sha256,model,framework_version,openai_response_id,notes");
            q.Append("&model=eq.").Append(Uri.EscapeDataString(options.Model));
            q.Append("&input_sha256=in.(").Append(inList).Append(')');
            q.Append("&limit=").Append(batch.Count + 5);
            var page = await db.SelectAsync<EvalExampleRow>("eval_examples", q.ToString(), ct);
            output.AddRange(page);
        }

        // Defensive fallback for rows without a hash (not expected in current corpus).
        if (output.Count < selected.Count)
        {
            var selectedKeys = selected.Select(x => $"{x.Ticker.ToUpperInvariant()}|{NormalizeMinute(EnsureUtc(x.AsofTs)):O}").ToHashSet(StringComparer.OrdinalIgnoreCase);
            var foundKeys = output.Select(x => $"{x.Ticker.ToUpperInvariant()}|{NormalizeMinute(EnsureUtc(x.AsofTs)):O}").ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var missingKey in selectedKeys.Except(foundKeys, StringComparer.OrdinalIgnoreCase))
            {
                var parts = missingKey.Split('|', 2);
                var ts = DateTime.Parse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                var q = $"?select=ticker,asof_ts,input_json,model_output_json,input_sha256,model,framework_version,openai_response_id,notes" +
                        $"&model=eq.{Uri.EscapeDataString(options.Model)}&ticker=eq.{Uri.EscapeDataString(parts[0])}" +
                        $"&asof_ts=gte.{Uri.EscapeDataString(ts.ToString("O"))}&asof_ts=lt.{Uri.EscapeDataString(ts.AddMinutes(1).ToString("O"))}&limit=5";
                output.AddRange(await db.SelectAsync<EvalExampleRow>("eval_examples", q, ct));
            }
        }

        var wantedHashes = hashes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return output
            .Where(x => string.IsNullOrWhiteSpace(x.InputSha256) || wantedHashes.Contains(x.InputSha256))
            .GroupBy(x => x.InputSha256 ?? $"{x.Ticker}|{EnsureUtc(x.AsofTs):O}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(selected.Count)
            .ToList();
    }

    private static long StableHash(int seed, string text)
    {
        unchecked
        {
            ulong hash = 1469598103934665603UL ^ (uint)seed;
            foreach (var ch in text)
            {
                hash ^= ch;
                hash *= 1099511628211UL;
            }
            return (long)(hash & 0x7fffffffffffffffUL);
        }
    }

    private static async Task<Dictionary<ScenarioKey, ScenarioEnrichmentRow>> LoadScenarioEnrichmentAsync(
        SupabaseRestClient db,
        IReadOnlyList<EvalExampleRow> examples,
        CorpusOptions options,
        CancellationToken ct)
    {
        var map = new Dictionary<ScenarioKey, ScenarioEnrichmentRow>();
        if (examples.Count == 0) return map;

        var min = examples.Min(x => EnsureUtc(x.AsofTs));
        var max = examples.Max(x => EnsureUtc(x.AsofTs));
        var tickers = examples.Select(x => x.Ticker.ToUpperInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var ticker in tickers)
        {
            var offset = 0;
            while (true)
            {
                var q = "?select=ticker,asof_ts_utc,rank,direction,entry_type,entry_low,entry_high,stop,t1,t2,runner,scenario_prob,success_prob,grade,grade_rationale" +
                        $"&ticker=eq.{Uri.EscapeDataString(ticker)}" +
                        $"&asof_ts_utc=gte.{Uri.EscapeDataString(min.ToString("O"))}" +
                        $"&asof_ts_utc=lte.{Uri.EscapeDataString(max.ToString("O"))}" +
                        $"&order=asof_ts_utc.asc&limit={options.PageSize}&offset={offset}";
                var page = await db.SelectAsync<ScenarioEnrichmentRow>("execution_card_scenarios", q, ct);
                if (page.Count == 0) break;
                foreach (var row in page)
                {
                    var key = new ScenarioKey(row.Ticker.ToUpperInvariant(), NormalizeMinute(EnsureUtc(row.AsofTsUtc)), row.Rank);
                    map[key] = row;
                }
                offset += page.Count;
                if (page.Count < options.PageSize) break;
            }
        }
        return map;
    }

    private static void EnrichCard(
        ExecutionCardJsonV1 card,
        string ticker,
        DateTime asofUtc,
        IReadOnlyDictionary<ScenarioKey, ScenarioEnrichmentRow> enrichment)
    {
        foreach (var scenario in card.Scenarios)
        {
            var key = new ScenarioKey(ticker.ToUpperInvariant(), NormalizeMinute(asofUtc), scenario.ScenarioRank);
            if (!enrichment.TryGetValue(key, out var row)) continue;
            scenario.Grade ??= row.Grade;
            scenario.GradeRationale ??= row.GradeRationale;
        }
    }

    private static async Task<FutureContext> EvaluateFutureContextAsync(
        EvalExampleRow example,
        string inputJson,
        PolygonClient polygon,
        Dictionary<string, IReadOnlyList<MinuteBar>> cache,
        CancellationToken ct)
    {
        var asofUtc = DatasetAsofUtc(inputJson) ?? EnsureUtc(example.AsofTs);
        var sessionDateEt = ToEastern(asofUtc).Date;
        var cacheKey = $"{example.Ticker.ToUpperInvariant()}|{sessionDateEt:yyyy-MM-dd}";
        if (!cache.TryGetValue(cacheKey, out var sessionBars))
        {
            var openUtc = EtToUtc(sessionDateEt.AddHours(9).AddMinutes(30));
            var closeUtc = EtToUtc(sessionDateEt.AddHours(16));
            sessionBars = await polygon.GetMinuteBarsAsync(example.Ticker, openUtc, closeUtc.AddMinutes(-1), ct);
            cache[cacheKey] = sessionBars;
        }

        var after = sessionBars.Where(b => EnsureUtc(b.BarStartUtc) > asofUtc).OrderBy(b => b.BarStartUtc).ToList();
        var lastClose = GetLastClose(inputJson);
        FutureContextSummary? summary = null;
        if (lastClose.HasValue && lastClose.Value != 0 && after.Count > 0)
        {
            var high = after.Max(b => b.H);
            var low = after.Min(b => b.L);
            var close = after[^1].C;
            summary = new FutureContextSummary(
                after.Count,
                Math.Round((high - lastClose.Value) / lastClose.Value * 100m, 4),
                Math.Round((low - lastClose.Value) / lastClose.Value * 100m, 4),
                Math.Round((close - lastClose.Value) / lastClose.Value * 100m, 4));
        }

        return new FutureContext(sessionBars, summary);
    }

    private static string ClassifyScenario(ExecutionScenarioJsonV1 scenario, ScenarioSemanticResult? semantic, ScenarioRealizedOutcome? outcome)
    {
        if (string.IsNullOrWhiteSpace(scenario.Grade)) return "metadata_incomplete";
        if (semantic is null || !semantic.Accepted) return "semantic_rejected";
        if (outcome is null) return "valid_unlabeled";
        return outcome.PrimaryOutcome switch
        {
            "T1_BEFORE_STOP" or "T2_REACHED" or "RUNNER_REACHED" => "valid_positive",
            "STOP_BEFORE_T1" => "valid_negative",
            "NOT_TRIGGERED" => "valid_not_triggered",
            "AMBIGUOUS_STOP_T1_SAME_BAR" => "valid_ambiguous",
            _ => "valid_inconclusive"
        };
    }

    private static string ClassifyCard(
        ExecutionCardJsonV1 card,
        CardSemanticValidationResult semantic,
        IReadOnlyList<CorpusScenarioRecord> scenarios,
        bool metadataComplete)
    {
        if (string.Equals(card.Verdict, "NO_TRADE", StringComparison.OrdinalIgnoreCase))
            return "teacher_no_trade";
        if (!metadataComplete)
            return "trade_metadata_incomplete";
        if (semantic.AcceptedScenarioCount == 0)
            return "trade_all_semantically_rejected";
        if (scenarios.Any(s => s.QualityBucket == "valid_positive"))
            return "trade_valid_positive";
        if (scenarios.Any(s => s.QualityBucket == "valid_negative"))
            return "trade_valid_negative";
        if (scenarios.Any(s => s.QualityBucket == "valid_ambiguous"))
            return "trade_valid_ambiguous";
        return "trade_valid_inconclusive";
    }

    private static async Task WriteScenarioCsvAsync(
        StreamWriter csv,
        EvalExampleRow example,
        CardSemanticValidationResult semantic,
        CorpusScenarioRecord record)
    {
        var s = record.Scenario;
        var sem = record.Semantic;
        var o = record.Outcome;
        var errorCodes = sem is null
            ? ""
            : string.Join("|", sem.Issues.Where(i => string.Equals(i.Severity, "error", StringComparison.OrdinalIgnoreCase)).Select(i => i.Code).Distinct(StringComparer.OrdinalIgnoreCase));
        var fields = new[]
        {
            Csv(example.Ticker), Csv(EnsureUtc(example.AsofTs).ToString("O")), Csv(example.Model), Csv(example.FrameworkVersion),
            Csv(semantic.RawVerdict), Csv(semantic.EffectiveVerdict), s.ScenarioRank.ToString(CultureInfo.InvariantCulture),
            Csv(s.Direction), Csv(s.EntryType), Csv(s.Grade), Bool(sem?.Accepted), Bool(o?.Triggered), Csv(o?.PrimaryOutcome),
            Bool(o?.T1BeforeStop), Num(o?.MinutesToTrigger), Num(o?.MinutesToT1), Num(o?.ConservativeEntryPrice), Num(s.StopPrice),
            Num(s.T1), Num(s.T2), Num(s.Runner), Num(sem?.ConservativeT1RiskReward), Num(o?.MfeR), Num(o?.MaeR),
            Num(record.ResolvedT1OrStopR), Csv(errorCodes)
        };
        await csv.WriteLineAsync(string.Join(',', fields));
    }

    private static decimal? ResolvedT1OrStopR(
        ExecutionScenarioJsonV1 scenario,
        ScenarioSemanticResult? semantic,
        ScenarioRealizedOutcome? outcome)
    {
        if (outcome?.Triggered != true) return null;
        if (outcome.T1BeforeStop == true)
        {
            var rr = semantic?.ConservativeT1RiskReward ?? ConservativeT1RiskReward(scenario);
            return rr.HasValue && rr.Value > 0 ? Math.Round(rr.Value, 3) : null;
        }
        if (string.Equals(outcome.PrimaryOutcome, "STOP_BEFORE_T1", StringComparison.OrdinalIgnoreCase))
            return -1m;
        return null;
    }

    private static decimal? ConservativeT1RiskReward(ExecutionScenarioJsonV1 s)
    {
        if (!s.StopPrice.HasValue || !s.T1.HasValue) return null;
        var isLong = string.Equals(s.Direction, "long", StringComparison.OrdinalIgnoreCase);
        var entry = isLong ? (s.EntryHigh ?? s.EntryLow) : (s.EntryLow ?? s.EntryHigh);
        if (!entry.HasValue) return null;
        var risk = isLong ? entry.Value - s.StopPrice.Value : s.StopPrice.Value - entry.Value;
        var reward = isLong ? s.T1.Value - entry.Value : entry.Value - s.T1.Value;
        if (risk <= 0 || reward <= 0) return null;
        return Math.Round(reward / risk, 3);
    }

    private static async Task WriteDiagnosticRowsAsync(
        StreamWriter diagnostics,
        Dictionary<string, int> counts,
        int limitPerReason,
        EvalExampleRow example,
        CorpusScenarioRecord record)
    {
        if (limitPerReason <= 0 || record.Semantic is null) return;
        var targets = record.Semantic.Issues
            .Where(i => string.Equals(i.Severity, "error", StringComparison.OrdinalIgnoreCase) &&
                        (string.Equals(i.Code, "level_order", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(i.Code, "rr_unavailable", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (targets.Count == 0) return;

        var allErrors = string.Join("|", record.Semantic.Issues
            .Where(i => string.Equals(i.Severity, "error", StringComparison.OrdinalIgnoreCase))
            .Select(i => i.Code)
            .Distinct(StringComparer.OrdinalIgnoreCase));
        var s = record.Scenario;
        var o = record.Outcome;

        foreach (var issue in targets)
        {
            var used = counts.GetValueOrDefault(issue.Code);
            if (used >= limitPerReason) continue;
            counts[issue.Code] = used + 1;

            var fields = new[]
            {
                Csv(issue.Code), Csv(DiagnosticDetail(issue.Code, issue.Message, s)), Csv(issue.Message), Csv(example.Ticker), Csv(EnsureUtc(example.AsofTs).ToString("O")),
                Csv(example.FrameworkVersion), s.ScenarioRank.ToString(CultureInfo.InvariantCulture), Csv(s.Direction), Csv(s.EntryType), Csv(s.Grade),
                Num(s.ScenarioProb), Num(s.SuccessProb), Num(s.EntryLow), Num(s.EntryHigh), Num(o?.ConservativeEntryPrice), Num(s.StopPrice),
                Num(s.T1), Num(s.T2), Num(s.Runner), Num(record.Semantic.ConservativeT1RiskReward), Csv(allErrors), Bool(o?.Triggered),
                Csv(Iso(o?.TriggerTsUtc)), Csv(o?.PrimaryOutcome), Bool(o?.T1BeforeStop), Csv(Iso(o?.T1TsUtc)), Csv(Iso(o?.StopTsUtc)),
                Csv(Iso(o?.T2TsUtc)), Csv(Iso(o?.RunnerTsUtc)), Num(o?.MfeR), Num(o?.MaeR), Num(record.ResolvedT1OrStopR), Csv(o?.TriggerReason)
            };
            await diagnostics.WriteLineAsync(string.Join(',', fields));
        }
    }

    private static string DiagnosticDetail(string code, string message, ExecutionScenarioJsonV1 s)
    {
        if (string.Equals(code, "level_order", StringComparison.OrdinalIgnoreCase))
        {
            var m = message.ToLowerInvariant();
            if (m.StartsWith("stop") && m.Contains("entry")) return "stop_vs_entry";
            if (m.StartsWith("entry_low") && m.Contains("entry_high")) return "entry_bounds_reversed";
            if ((m.StartsWith("entry_high") || m.StartsWith("entry_low")) && m.Contains("t1")) return "entry_vs_t1";
            if (m.StartsWith("t1") && m.Contains("t2")) return "t1_vs_t2";
            if (m.StartsWith("t2") && m.Contains("runner")) return "t2_vs_runner";
            if ((m.StartsWith("entry_high") || m.StartsWith("entry_low")) && m.Contains("runner")) return "entry_vs_runner";
            if (m.StartsWith("stop") && m.Contains("t1")) return "stop_vs_t1";
            return "other_level_order";
        }

        if (string.Equals(code, "rr_unavailable", StringComparison.OrdinalIgnoreCase))
        {
            if (!s.StopPrice.HasValue) return "missing_stop";
            if (!s.T1.HasValue) return "missing_t1";
            var isLong = string.Equals(s.Direction, "long", StringComparison.OrdinalIgnoreCase);
            var entry = isLong ? (s.EntryHigh ?? s.EntryLow) : (s.EntryLow ?? s.EntryHigh);
            if (!entry.HasValue) return "missing_entry";
            var risk = isLong ? entry.Value - s.StopPrice.Value : s.StopPrice.Value - entry.Value;
            var reward = isLong ? s.T1.Value - entry.Value : entry.Value - s.T1.Value;
            if (risk <= 0 && reward <= 0) return "nonpositive_risk_and_reward";
            if (risk <= 0) return "nonpositive_risk";
            if (reward <= 0) return "nonpositive_reward";
            return "unknown_rr_unavailable";
        }

        return code;
    }

    private static string? Iso(DateTime? value) => value.HasValue ? EnsureUtc(value.Value).ToString("O") : null;
    private static string Fmt(decimal? value) => value.HasValue ? value.Value.ToString("0.000", CultureInfo.InvariantCulture) : "n/a";

    private static string Csv(string? value)
    {
        value ??= "";
        return '"' + value.Replace("\"", "\"\"") + '"';
    }
    private static string Bool(bool? value) => value.HasValue ? (value.Value ? "true" : "false") : "";
    private static string Num(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";
    private static string Num(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";

    private static DateTime? DatasetAsofUtc(string inputJson)
    {
        using var doc = JsonDocument.Parse(inputJson);
        if (!doc.RootElement.TryGetProperty("ts_asof_utc", out var el)) return null;
        if (el.ValueKind == JsonValueKind.String && DateTime.TryParse(el.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
            return EnsureUtc(dt);
        return null;
    }

    private static decimal? GetLastClose(string inputJson)
    {
        using var doc = JsonDocument.Parse(inputJson);
        if (!doc.RootElement.TryGetProperty("reference_levels", out var levels) || levels.ValueKind != JsonValueKind.Object) return null;
        if (levels.TryGetProperty("last_close", out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out var d)) return d;
        return null;
    }

    private static DateTime EnsureUtc(DateTime dt)
        => dt.Kind switch
        {
            DateTimeKind.Utc => dt,
            DateTimeKind.Local => dt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc)
        };

    private static DateTime NormalizeMinute(DateTime dt)
    {
        dt = EnsureUtc(dt);
        return new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, 0, DateTimeKind.Utc);
    }

    private static TimeZoneInfo Eastern => TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");
    private static DateTime ToEastern(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(EnsureUtc(utc), Eastern);
    private static DateTime EtToUtc(DateTime et) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(et, DateTimeKind.Unspecified), Eastern);

    private static string FormatMaybe(DateTime? utc) => utc.HasValue ? $"{ToEastern(utc.Value):yyyy-MM-dd HH:mm:ss}" : "ALL";
    private static string Short(string s) => s.Length <= 160 ? s : s[..160] + "...";
    private static string Sanitize(string s) => new(s.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());

    private static void PrintHelp()
    {
        Console.WriteLine("AVA Stage 2B historical corpus");
        Console.WriteLine();
        Console.WriteLine("Inventory:");
        Console.WriteLine("  dotnet run -- --corpus-inventory");
        Console.WriteLine();
        Console.WriteLine("Build a small corpus sample:");
        Console.WriteLine("  dotnet run -- --corpus-build --model=gpt-5.2 --limit=100");
        Console.WriteLine();
        Console.WriteLine("Filtered build:");
        Console.WriteLine("  dotnet run -- --corpus-build --ticker=AAPL --from-et=2026-07-01 --to-et=2026-08-07 --limit=500");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --model=<name>          default gpt-5.2");
        Console.WriteLine("  --ticker=<symbol>       optional");
        Console.WriteLine("  --from-et=<date/time>   optional, ET; date means 00:00");
        Console.WriteLine("  --to-et=<date/time>     optional, ET; date means 23:59:59");
        Console.WriteLine("  --limit=<n>             default 100; 0 means all matching examples");
        Console.WriteLine("  --sample=<mode>         chronological (default) or stratified");
        Console.WriteLine("  --seed=<n>              deterministic stratified sampling seed; default 42");
        Console.WriteLine("  --page-size=<n>         default 250");
        Console.WriteLine("  --output-dir=<path>     default historical_corpus");
        Console.WriteLine("  --no-outcomes           export teacher corpus without Massive future labels");
        Console.WriteLine("  --include-full-input    include full original input_json in JSONL (compact_v1 is always included)");
        Console.WriteLine("  --diagnostic-limit=<n>  default 20 per suspicious reason (level_order, rr_unavailable); 0 disables");
        Console.WriteLine("  --stop-on-error         stop at the first bad row");
    }

    private sealed record ScenarioKey(string Ticker, DateTime AsofMinuteUtc, int Rank);

    private sealed class EvalExampleRow
    {
        [JsonPropertyName("ticker")] public string Ticker { get; set; } = "";
        [JsonPropertyName("asof_ts")] public DateTime AsofTs { get; set; }
        [JsonPropertyName("input_json")] public JsonElement InputJson { get; set; }
        [JsonPropertyName("model_output_json")] public JsonElement ModelOutputJson { get; set; }
        [JsonPropertyName("input_sha256")] public string? InputSha256 { get; set; }
        [JsonPropertyName("model")] public string Model { get; set; } = "";
        [JsonPropertyName("framework_version")] public string? FrameworkVersion { get; set; }
        [JsonPropertyName("openai_response_id")] public string? OpenAiResponseId { get; set; }
        [JsonPropertyName("notes")] public string? Notes { get; set; }
    }

    private sealed class SampleCandidateRow
    {
        [JsonPropertyName("ticker")] public string Ticker { get; set; } = "";
        [JsonPropertyName("asof_ts")] public DateTime AsofTs { get; set; }
        [JsonPropertyName("input_sha256")] public string? InputSha256 { get; set; }
        [JsonPropertyName("model_output_json")] public JsonElement ModelOutputJson { get; set; }
        [JsonPropertyName("framework_version")] public string? FrameworkVersion { get; set; }
    }

    private sealed record CardShapeInfo(string Verdict, string Direction, string EntryType, string Grade);
    private sealed record SampleStratum(string Key, List<SampleCandidateRow> Items);
    private sealed class SampleAllocation
    {
        public SampleStratum Stratum { get; }
        public int Take { get; set; }
        public decimal Remainder { get; }
        public SampleAllocation(SampleStratum stratum, int take, decimal remainder)
            => (Stratum, Take, Remainder) = (stratum, take, remainder);
    }
    private sealed record ExampleLoadResult(List<EvalExampleRow> Examples, int CandidatePopulation, int StratumCount);

    private sealed class ScenarioEnrichmentRow
    {
        [JsonPropertyName("ticker")] public string Ticker { get; set; } = "";
        [JsonPropertyName("asof_ts_utc")] public DateTime AsofTsUtc { get; set; }
        [JsonPropertyName("rank")] public int Rank { get; set; }
        [JsonPropertyName("grade")] public string? Grade { get; set; }
        [JsonPropertyName("grade_rationale")] public string? GradeRationale { get; set; }
    }

    private sealed record FutureContext(IReadOnlyList<MinuteBar> SessionBars, FutureContextSummary? Summary);
    public sealed record FutureContextSummary(int FutureBarCount, decimal MaxUpPct, decimal MaxDownPct, decimal CloseChangePct);

    public sealed record CorpusScenarioRecord(
        int ScenarioRank,
        ExecutionScenarioJsonV1 Scenario,
        ScenarioSemanticResult? Semantic,
        ScenarioRealizedOutcome? Outcome,
        string QualityBucket,
        decimal? ResolvedT1OrStopR);

    private sealed class CorpusSummary
    {
        public int Examples;
        public int RawNoTrade;
        public int RawTrade;
        public int EffectiveTrade;
        public int SemanticNotEvaluableCards;
        public int Scenarios;
        public int SemanticAccepted;
        public int SemanticRejected;
        public int SemanticNotEvaluable;
        public int Triggered;
        public int T1BeforeStop;
        public int StopBeforeT1;
        public int Errors;
        private readonly Dictionary<string, int> _cardBuckets = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ScenarioMetric> _byDirection = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ScenarioMetric> _byEntryType = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ScenarioMetric> _byGrade = new(StringComparer.OrdinalIgnoreCase);
        private readonly ScenarioMetric _acceptedOutcomes = new();
        private readonly ScenarioMetric _rejectedOutcomes = new();
        private readonly ScenarioMetric _notEvaluableOutcomes = new();
        private readonly Dictionary<string, ScenarioMetric> _byRejectionReason = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ScenarioMetric> _byRejectionCombination = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<CorpusScenarioRecord> _allScenarioRecords = new();
        private readonly Dictionary<string, Dictionary<string, int>> _diagnosticShapes = new(StringComparer.OrdinalIgnoreCase);

        private int _stage2b4StructuralTradeCards;
        private int _stage2b4StructuralValidScenarios;
        private int _stage2b4StructuralInvalidScenarios;
        private int _stage2b4RepairedScenarios;
        private int _stage2b4PreferredScenarios;
        private int _stage2b4SecondaryScenarios;
        private readonly Dictionary<string, int> _stage2b4HardIssues = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _stage2b4Repairs = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _stage2b4SelectionPenalties = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _stage2b4Observations = new(StringComparer.OrdinalIgnoreCase);

        public decimal? AcceptedExpectancyRResolved => _acceptedOutcomes.ExpectancyRResolved;
        public decimal? RejectedExpectancyRResolved => _rejectedOutcomes.ExpectancyRResolved;
        public int AblationRuleCount => ObservedErrorCodes().Count;
        public int Stage2B4StructuralValidScenarioCount => _stage2b4StructuralValidScenarios;
        public int Stage2B4StructuralInvalidScenarioCount => _stage2b4StructuralInvalidScenarios;
        public int Stage2B4PreferredScenarioCount => _stage2b4PreferredScenarios;
        public int Stage2B4SecondaryScenarioCount => _stage2b4SecondaryScenarios;

        public void AddExample(
            ExecutionCardJsonV1 card,
            CardSemanticValidationResult semantic,
            string bucket,
            FutureContext? future,
            bool metadataComplete)
        {
            Examples++;
            if (string.Equals(card.Verdict, "TRADE", StringComparison.OrdinalIgnoreCase)) RawTrade++; else RawNoTrade++;

            if (string.Equals(card.Verdict, "TRADE", StringComparison.OrdinalIgnoreCase) && !metadataComplete)
            {
                SemanticNotEvaluableCards++;
            }
            else if (string.Equals(semantic.EffectiveVerdict, "TRADE", StringComparison.OrdinalIgnoreCase))
            {
                EffectiveTrade++;
            }

            _cardBuckets[bucket] = _cardBuckets.GetValueOrDefault(bucket) + 1;
        }

        public void AddStage2B4(AvaScenarioDecisionResult result)
        {
            if (string.Equals(result.Structural.EffectiveVerdict, "TRADE", StringComparison.OrdinalIgnoreCase))
                _stage2b4StructuralTradeCards++;

            foreach (var sr in result.Structural.Scenarios)
            {
                if (sr.StructurallyValid) _stage2b4StructuralValidScenarios++;
                else _stage2b4StructuralInvalidScenarios++;
                if (sr.RepairWarnings.Count > 0) _stage2b4RepairedScenarios++;
                foreach (var issue in sr.HardIssues)
                    _stage2b4HardIssues[issue.Code] = _stage2b4HardIssues.GetValueOrDefault(issue.Code) + 1;
                foreach (var issue in sr.RepairWarnings)
                    _stage2b4Repairs[issue.Code] = _stage2b4Repairs.GetValueOrDefault(issue.Code) + 1;
            }

            foreach (var qp in result.Quality.Scenarios)
            {
                if (string.Equals(qp.SelectionTier, "PREFERRED", StringComparison.OrdinalIgnoreCase)) _stage2b4PreferredScenarios++;
                else if (string.Equals(qp.SelectionTier, "SECONDARY", StringComparison.OrdinalIgnoreCase)) _stage2b4SecondaryScenarios++;
                foreach (var signal in qp.SelectionPenalties)
                    _stage2b4SelectionPenalties[signal.Code] = _stage2b4SelectionPenalties.GetValueOrDefault(signal.Code) + 1;
                foreach (var signal in qp.Observations)
                    _stage2b4Observations[signal.Code] = _stage2b4Observations.GetValueOrDefault(signal.Code) + 1;
            }
        }

        public void AddScenario(CorpusScenarioRecord r)
        {
            _allScenarioRecords.Add(r);
            Scenarios++;
            if (r.Outcome?.Triggered == true) Triggered++;
            if (r.Outcome?.T1BeforeStop == true) T1BeforeStop++;
            if (r.Outcome?.PrimaryOutcome == "STOP_BEFORE_T1") StopBeforeT1++;

            var metadataEvaluable = !string.IsNullOrWhiteSpace(r.Scenario.Grade);
            if (!metadataEvaluable)
            {
                SemanticNotEvaluable++;
                _notEvaluableOutcomes.Add(r);
            }
            else if (r.Semantic?.Accepted == true)
            {
                SemanticAccepted++;
                _acceptedOutcomes.Add(r);
            }
            else
            {
                SemanticRejected++;
                _rejectedOutcomes.Add(r);
                if (r.Semantic is not null)
                {
                    var errorCodes = r.Semantic.Issues
                        .Where(i => string.Equals(i.Severity, "error", StringComparison.OrdinalIgnoreCase))
                        .Select(i => i.Code)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    foreach (var code in errorCodes)
                    {
                        if (!_byRejectionReason.TryGetValue(code, out var metric))
                            _byRejectionReason[code] = metric = new ScenarioMetric();
                        metric.Add(r);

                        var issue = r.Semantic.Issues.First(i => string.Equals(i.Code, code, StringComparison.OrdinalIgnoreCase));
                        var detail = DiagnosticDetail(code, issue.Message, r.Scenario);
                        if (!_diagnosticShapes.TryGetValue(code, out var detailCounts))
                            _diagnosticShapes[code] = detailCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        detailCounts[detail] = detailCounts.GetValueOrDefault(detail) + 1;
                    }
                    if (errorCodes.Count > 0)
                    {
                        var combination = string.Join("+", errorCodes);
                        if (!_byRejectionCombination.TryGetValue(combination, out var comboMetric))
                            _byRejectionCombination[combination] = comboMetric = new ScenarioMetric();
                        comboMetric.Add(r);
                    }
                }
            }

            AddMetric(_byDirection, r.Scenario.Direction ?? "unknown", r);
            AddMetric(_byEntryType, r.Scenario.EntryType ?? "unknown", r);
            AddMetric(_byGrade, r.Scenario.Grade ?? "unknown", r);
        }

        private static void AddMetric(Dictionary<string, ScenarioMetric> dict, string key, CorpusScenarioRecord r)
        {
            if (!dict.TryGetValue(key, out var m)) dict[key] = m = new ScenarioMetric();
            m.Add(r);
        }

        public object ToSerializable(int loaded, ExampleLoadResult loadResult, CorpusOptions options, string jsonl, string csv, string diagnostics) => new
        {
            corpus_version = 5,
            generated_utc = DateTime.UtcNow,
            filters = new { options.Model, options.Ticker, options.FromUtc, options.ToUtc, options.MaxExamples, options.ComputeOutcomes, options.DiagnosticLimit, options.SampleMode, options.SampleSeed },
            sampling = new { mode = options.SampleMode, seed = options.SampleSeed, candidate_population = loadResult.CandidatePopulation, selected_examples = loaded, population_strata = loadResult.StratumCount },
            realized_r_policy = new
            {
                name = "resolved_t1_or_initial_stop",
                description = "For triggered scenarios resolved by T1 before stop or stop before T1: full exit at conservative T1 earns scenario conservative T1 R:R; initial stop loses -1R. Not-triggered, ambiguous, invalid, and open-at-close cases are excluded from resolved expectancy. expectancy_r_per_triggered_zero_unresolved additionally treats unresolved triggered cases as 0R."
            },
            loaded_examples = loaded,
            processed_examples = Examples,
            errors = Errors,
            cards = new
            {
                raw_no_trade = RawNoTrade,
                raw_trade = RawTrade,
                effective_trade_evaluable = EffectiveTrade,
                semantic_not_evaluable_legacy = SemanticNotEvaluableCards,
                buckets = _cardBuckets
            },
            scenarios = new
            {
                total = Scenarios,
                semantic_accepted = SemanticAccepted,
                semantic_rejected = SemanticRejected,
                semantic_not_evaluable_legacy = SemanticNotEvaluable,
                triggered = Triggered,
                t1_before_stop = T1BeforeStop,
                stop_before_t1 = StopBeforeT1,
                validation_efficacy = new
                {
                    accepted = _acceptedOutcomes.ToSerializable(),
                    rejected = _rejectedOutcomes.ToSerializable(),
                    not_evaluable_legacy = _notEvaluableOutcomes.ToSerializable(),
                    by_rejection_reason = _byRejectionReason.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToSerializable(), StringComparer.OrdinalIgnoreCase),
                    by_rejection_combination = _byRejectionCombination.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToSerializable(), StringComparer.OrdinalIgnoreCase),
                    rule_ablation = BuildRuleAblation(),
                    diagnostic_issue_shapes = _diagnosticShapes.ToDictionary(
                        kvp => kvp.Key,
                        kvp => kvp.Value.OrderByDescending(x => x.Value).ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase),
                        StringComparer.OrdinalIgnoreCase)
                },
                by_direction = _byDirection.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToSerializable(), StringComparer.OrdinalIgnoreCase),
                by_entry_type = _byEntryType.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToSerializable(), StringComparer.OrdinalIgnoreCase),
                by_grade = _byGrade.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToSerializable(), StringComparer.OrdinalIgnoreCase)
            },
            stage2b4 = new
            {
                mode = "shadow_structural_plus_quality",
                structural_trade_cards = _stage2b4StructuralTradeCards,
                structural_valid_scenarios = _stage2b4StructuralValidScenarios,
                structural_invalid_scenarios = _stage2b4StructuralInvalidScenarios,
                scenarios_with_safe_target_repairs = _stage2b4RepairedScenarios,
                preferred_scenarios = _stage2b4PreferredScenarios,
                secondary_scenarios = _stage2b4SecondaryScenarios,
                hard_issue_counts = _stage2b4HardIssues,
                repair_counts = _stage2b4Repairs,
                selection_penalty_counts = _stage2b4SelectionPenalties,
                observation_counts = _stage2b4Observations
            },
            files = new { jsonl = Path.GetFullPath(jsonl), scenario_csv = Path.GetFullPath(csv), diagnostics_csv = Path.GetFullPath(diagnostics) }
        };
        private List<string> ObservedErrorCodes()
            => _allScenarioRecords
                .Where(IsSemanticallyEvaluable)
                .SelectMany(ErrorCodes)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

        private Dictionary<string, object> BuildRuleAblation()
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var baselineRecords = _allScenarioRecords.Where(r => IsSemanticallyEvaluable(r) && r.Semantic?.Accepted == true).ToList();
            var baseline = Metric(baselineRecords);

            foreach (var code in ObservedErrorCodes())
            {
                // True one-rule ablation: a rejected scenario is newly admitted only when
                // removing this single rule leaves no other error-level failures.
                var newlyAdmitted = _allScenarioRecords
                    .Where(IsSemanticallyEvaluable)
                    .Where(r => r.Semantic?.Accepted != true)
                    .Where(r =>
                    {
                        var errors = ErrorCodes(r);
                        return errors.Contains(code, StringComparer.OrdinalIgnoreCase) &&
                               errors.All(e => string.Equals(e, code, StringComparison.OrdinalIgnoreCase));
                    })
                    .ToList();

                var combined = baselineRecords.Concat(newlyAdmitted).ToList();
                var combinedMetric = Metric(combined);
                var newlyMetric = Metric(newlyAdmitted);
                result[code] = new
                {
                    newly_admitted = newlyAdmitted.Count,
                    newly_admitted_metrics = newlyMetric.ToSerializable(),
                    accepted_if_rule_removed = combinedMetric.ToSerializable(),
                    delta_mean_realized_r_resolved = Delta(combinedMetric.ExpectancyRResolved, baseline.ExpectancyRResolved),
                    delta_profit_factor_resolved = Delta(combinedMetric.ProfitFactorResolved, baseline.ProfitFactorResolved),
                    delta_expectancy_r_per_triggered_zero_unresolved = Delta(combinedMetric.ExpectancyPerTriggeredZeroUnresolved, baseline.ExpectancyPerTriggeredZeroUnresolved),
                    interpretation = newlyAdmitted.Count == 0
                        ? "No scenario failed only this rule; single-rule removal does not change the accepted population in this sample."
                        : "Metrics compare the current accepted population with the additional scenarios that would become admissible if only this rule were removed."
                };
            }
            return result;
        }

        private static ScenarioMetric Metric(IEnumerable<CorpusScenarioRecord> records)
        {
            var metric = new ScenarioMetric();
            foreach (var record in records) metric.Add(record);
            return metric;
        }

        private static bool IsSemanticallyEvaluable(CorpusScenarioRecord r)
            => !string.IsNullOrWhiteSpace(r.Scenario.Grade) && r.Semantic is not null;

        private static List<string> ErrorCodes(CorpusScenarioRecord r)
            => r.Semantic?.Issues
                   .Where(i => string.Equals(i.Severity, "error", StringComparison.OrdinalIgnoreCase))
                   .Select(i => i.Code)
                   .Distinct(StringComparer.OrdinalIgnoreCase)
                   .ToList()
               ?? new List<string>();

        private static decimal? Delta(decimal? value, decimal? baseline)
            => value.HasValue && baseline.HasValue ? Math.Round(value.Value - baseline.Value, 4) : null;
    }

    private sealed class ScenarioMetric
    {
        public int Total { get; private set; }
        public int SemanticAccepted { get; private set; }
        public int Triggered { get; private set; }
        public int T1BeforeStop { get; private set; }
        public int StopBeforeT1 { get; private set; }
        public int NotTriggered { get; private set; }
        public int Ambiguous { get; private set; }
        public int T2Reached { get; private set; }
        public int RunnerReached { get; private set; }
        public int OpenAtClose { get; private set; }
        private readonly List<decimal> _mfe = new();
        private readonly List<decimal> _mae = new();
        private readonly List<decimal> _resolvedR = new();

        public decimal? ExpectancyRResolved => Mean(_resolvedR);
        public decimal? ProfitFactorResolved => ProfitFactor(_resolvedR);
        public decimal? ExpectancyPerTriggeredZeroUnresolved => Triggered == 0 ? (decimal?)null : Math.Round(_resolvedR.Sum() / Triggered, 4);

        public void Add(CorpusScenarioRecord r)
        {
            Total++;
            if (r.Semantic?.Accepted == true) SemanticAccepted++;
            if (r.Outcome?.Triggered == true) Triggered++;
            if (r.Outcome?.T1BeforeStop == true) T1BeforeStop++;
            if (r.Outcome?.PrimaryOutcome == "STOP_BEFORE_T1") StopBeforeT1++;
            if (r.Outcome?.PrimaryOutcome == "NOT_TRIGGERED") NotTriggered++;
            if (r.Outcome?.PrimaryOutcome == "AMBIGUOUS_STOP_T1_SAME_BAR") Ambiguous++;
            if (r.Outcome?.T2TsUtc.HasValue == true) T2Reached++;
            if (r.Outcome?.RunnerTsUtc.HasValue == true) RunnerReached++;
            if (r.Outcome?.PrimaryOutcome == "OPEN_AT_CLOSE") OpenAtClose++;
            if (r.Outcome?.MfeR is decimal mfe) _mfe.Add(mfe);
            if (r.Outcome?.MaeR is decimal mae) _mae.Add(mae);
            if (r.ResolvedT1OrStopR is decimal realizedR) _resolvedR.Add(realizedR);
        }

        public object ToSerializable()
        {
            var resolved = T1BeforeStop + StopBeforeT1;
            return new
            {
                total = Total,
                semantic_accepted = SemanticAccepted,
                triggered = Triggered,
                trigger_rate = Rate(Triggered, Total),
                t1_before_stop = T1BeforeStop,
                stop_before_t1 = StopBeforeT1,
                t1_rate_if_triggered = Rate(T1BeforeStop, Triggered),
                t1_rate_if_resolved = Rate(T1BeforeStop, resolved),
                stop_rate_if_resolved = Rate(StopBeforeT1, resolved),
                not_triggered = NotTriggered,
                ambiguous = Ambiguous,
                t2_reached = T2Reached,
                runner_reached = RunnerReached,
                open_at_close = OpenAtClose,
                resolved_t1_or_stop_samples = _resolvedR.Count,
                resolved_r_coverage_if_triggered = Rate(_resolvedR.Count, Triggered),
                mean_realized_r_resolved = Mean(_resolvedR),
                median_realized_r_resolved = Median(_resolvedR),
                profit_factor_resolved = ProfitFactor(_resolvedR),
                average_win_r_resolved = Mean(_resolvedR.Where(x => x > 0).ToList()),
                average_loss_r_abs_resolved = AbsMean(_resolvedR.Where(x => x < 0).ToList()),
                breakeven_win_rate_from_average_win = BreakEvenWinRate(_resolvedR),
                expectancy_r_per_triggered_zero_unresolved = Triggered == 0 ? (decimal?)null : Math.Round(_resolvedR.Sum() / Triggered, 4),
                median_mfe_r = Median(_mfe),
                median_mae_r = Median(_mae)
            };
        }

        private static decimal Rate(int numerator, int denominator)
            => denominator == 0 ? 0m : Math.Round((decimal)numerator / denominator, 4);

        private static decimal? Mean(List<decimal> values)
            => values.Count == 0 ? null : Math.Round(values.Average(), 4);

        private static decimal? AbsMean(List<decimal> values)
            => values.Count == 0 ? null : Math.Round(values.Select(x => Math.Abs(x)).Average(), 4);

        private static decimal? ProfitFactor(List<decimal> values)
        {
            var grossProfit = values.Where(x => x > 0).Sum();
            var grossLoss = Math.Abs(values.Where(x => x < 0).Sum());
            if (grossLoss == 0) return null;
            return Math.Round(grossProfit / grossLoss, 4);
        }

        private static decimal? BreakEvenWinRate(List<decimal> values)
        {
            var wins = values.Where(x => x > 0).ToList();
            if (wins.Count == 0) return null;
            var avgWin = wins.Average();
            return avgWin <= 0 ? null : Math.Round(1m / (1m + avgWin), 4);
        }

        private static decimal? Median(List<decimal> values)
        {
            if (values.Count == 0) return null;
            var a = values.OrderBy(x => x).ToArray();
            var mid = a.Length / 2;
            return a.Length % 2 == 1
                ? a[mid]
                : Math.Round((a[mid - 1] + a[mid]) / 2m, 3);
        }
    }

    private sealed record CorpusOptions(
        bool ShowHelp,
        bool InventoryOnly,
        string Model,
        string? Ticker,
        DateTime? FromUtc,
        DateTime? ToUtc,
        int MaxExamples,
        string SampleMode,
        int SampleSeed,
        int PageSize,
        string OutputDirectory,
        bool ComputeOutcomes,
        bool IncludeFullInput,
        int DiagnosticLimit,
        bool StopOnError)
    {
        public static CorpusOptions Parse(string[] args)
        {
            bool Has(string f) => args.Any(a => string.Equals(a, f, StringComparison.OrdinalIgnoreCase));
            string? Value(string prefix) => args.FirstOrDefault(a => a.StartsWith(prefix + "=", StringComparison.OrdinalIgnoreCase))?.Substring(prefix.Length + 1);
            var model = Value("--model") ?? "gpt-5.2";
            var ticker = Value("--ticker")?.Trim().ToUpperInvariant();
            var from = ParseEt(Value("--from-et"), endOfDate: false);
            var to = ParseEt(Value("--to-et"), endOfDate: true);
            var limit = ParseInt(Value("--limit"), 100, 0, 1_000_000);
            var sample = (Value("--sample") ?? "chronological").Trim().ToLowerInvariant();
            if (sample is not ("chronological" or "stratified"))
                throw new ArgumentException("--sample must be chronological or stratified");
            var seed = ParseInt(Value("--seed"), 42, int.MinValue, int.MaxValue);
            var page = ParseInt(Value("--page-size"), 250, 10, 1000);
            var dir = Value("--output-dir") ?? "historical_corpus";
            var diagnosticLimit = ParseInt(Value("--diagnostic-limit"), 20, 0, 1000);
            return new CorpusOptions(
                Has("--corpus-help") || Has("--help"),
                Has("--corpus-inventory"),
                model, ticker, from, to, limit, sample, seed, page, dir,
                !Has("--no-outcomes"),
                Has("--include-full-input"),
                diagnosticLimit,
                Has("--stop-on-error"));
        }

        private static int ParseInt(string? raw, int fallback, int min, int max)
            => int.TryParse(raw, out var n) ? Math.Clamp(n, min, max) : fallback;

        private static DateTime? ParseEt(string? raw, bool endOfDate)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dt))
                throw new ArgumentException($"Invalid ET date/time: {raw}");
            if (dt.TimeOfDay == TimeSpan.Zero && endOfDate && raw.Trim().Length <= 10)
                dt = dt.Date.AddDays(1).AddTicks(-1);
            dt = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
            return EtToUtc(dt);
        }
    }
}
