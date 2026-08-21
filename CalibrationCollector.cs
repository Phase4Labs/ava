using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace get_assessment_no_graph;

public sealed class CalibrationCollector
{
    private readonly SupabaseRestClient _db;
    private readonly string _model;
    private readonly string _frameworkVersion;

    // Single-process “last decision” memory per ticker.
    private readonly ConcurrentDictionary<string, DecisionSig> _lastByTicker = new(StringComparer.OrdinalIgnoreCase);

    // Capture knobs (your choices)
    private const int BaselineEveryNMinutes = 15; // store baseline 1/15 minutes

    public CalibrationCollector(
        SupabaseRestClient db,
        string model,
        string frameworkVersion)
    {
        _db = db;
        _model = model;
        _frameworkVersion = frameworkVersion;
    }

    private sealed record DecisionSig(string Verdict, string? TopEntryType);

    public async Task TryCollectAsync(
        string ticker,
        DateTime asofTsUtc,
        string datasetJson,
        ExecutionCardJsonV1 card,
        string? openAiResponseId,
        CancellationToken ct)
    {
        // 1) Regular session only (US equities): 9:30–16:00 ET
        if (!IsRegularSessionEt(asofTsUtc))
            return;

        // 2) Normalize verdict + derive top entry type
        var verdict = NormalizeVerdict(card.Verdict);
        var topEntryType = GetTopEntryType(card);

        var sig = new DecisionSig(verdict, topEntryType);
        var isTrade = verdict == "TRADE";

        // 3) Decide whether to store this minute
        var shouldStore =
            isTrade
            || HasDecisionChanged(ticker, sig)
            || IsBaselineMinute(asofTsUtc);

        // update last decision regardless (so flip detection works)
        _lastByTicker[ticker] = sig;

        if (!shouldStore)
            return;

        // 4) Normalize + hash input dataset
        var (inputJsonElement, inputSha256Hex) = NormalizeAndHashDataset(datasetJson);

        // 5) Store model output json (use your already-normalized object shape)
        var modelOutputJson = new
        {
            schema_version = 1,
            verdict = verdict,
            scenarios = (card.Scenarios ?? new List<ExecutionScenarioJsonV1>())
                .Select(s => new
                {
                    scenario_rank = s.ScenarioRank,
                    direction = s.Direction,
                    entry_type = s.EntryType,
                    scenario_prob = s.ScenarioProb,
                    success_prob = s.SuccessProb,
                    entry_low = s.EntryLow,
                    entry_high = s.EntryHigh,
                    stop_price = s.StopPrice,
                    t1 = s.T1,
                    t2 = s.T2,
                    runner = s.Runner,
                    grade = s.Grade,
                    grade_rationale = s.GradeRationale
                })
                .ToList()
        };

        // 6) Upsert into eval_examples (idempotent via unique input_sha256)
        var row = new
        {
            ticker = ticker,
            asof_ts = asofTsUtc,

            input_json = inputJsonElement,
            model_output_json = modelOutputJson,

            input_sha256 = inputSha256Hex,

            model = _model,
            framework_version = _frameworkVersion,
            openai_response_id = openAiResponseId,

            notes = (string?)null
        };

        await _db.UpsertAsync("eval_examples", new[] { row }, "input_sha256", ct);
    }

    private static string NormalizeVerdict(string? v)
        => string.Equals(v, "TRADE", StringComparison.OrdinalIgnoreCase) ? "TRADE" : "NO_TRADE";

    private static string? GetTopEntryType(ExecutionCardJsonV1 card)
    {
        var scenarios = card.Scenarios ?? new List<ExecutionScenarioJsonV1>();
        if (scenarios.Count == 0) return null;

        // prefer scenario_rank==1
        var best = scenarios.FirstOrDefault(s => s.ScenarioRank == 1) ?? scenarios[0];
        return best.EntryType;
    }

    private bool HasDecisionChanged(string ticker, DecisionSig now)
    {
        if (!_lastByTicker.TryGetValue(ticker, out var prev))
            return true; // first seen -> store

        if (!string.Equals(prev.Verdict, now.Verdict, StringComparison.Ordinal))
            return true;

        if (!string.Equals(prev.TopEntryType, now.TopEntryType, StringComparison.Ordinal))
            return true;

        return false;
    }

    private static bool IsBaselineMinute(DateTime asofUtc)
        => (asofUtc.Minute % BaselineEveryNMinutes) == 0;

    private static (JsonElement normalizedInput, string sha256Hex) NormalizeAndHashDataset(string datasetJson)
    {
        using var doc = JsonDocument.Parse(datasetJson);

        // Re-serialize to a stable string (no indentation). This prevents hash drift due to whitespace.
        // Property order is preserved as the JSON was generated; if your generator is stable, this is sufficient.
        var normalizedText = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions
        {
            WriteIndented = false
        });

        var sha = ComputeSha256Hex(normalizedText);

        using var normDoc = JsonDocument.Parse(normalizedText);
        return (normDoc.RootElement.Clone(), sha);
    }

    private static string ComputeSha256Hex(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool IsRegularSessionEt(DateTime utc)
    {
        // Convert to America/New_York (ET). Works on Windows + Linux by trying both IDs.
        var et = GetEasternTimeZone();
        var etTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), et);

        // 9:30–16:00
        var t = etTime.TimeOfDay;
        var open = new TimeSpan(9, 30, 0);
        var close = new TimeSpan(16, 0, 0);

        // include open minute; exclude anything at/after close
        return t >= open && t < close;
    }

    private static TimeZoneInfo GetEasternTimeZone()
    {
        // Windows: "Eastern Standard Time"
        // Linux: "America/New_York"
        try { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
        catch { return TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
    }
}