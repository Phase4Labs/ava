using System.Globalization;
using System.Text.Json;

namespace get_assessment_no_graph;

public sealed record ScenarioRealizedOutcome(
    bool Triggered,
    DateTime? TriggerTsUtc,
    int? MinutesToTrigger,
    decimal? ConservativeEntryPrice,
    string PrimaryOutcome,
    bool? T1BeforeStop,
    DateTime? T1TsUtc,
    int? MinutesToT1,
    DateTime? StopTsUtc,
    DateTime? T2TsUtc,
    DateTime? RunnerTsUtc,
    decimal? MfeR,
    decimal? MaeR,
    string? TriggerReason,
    string OutcomeMethod);

/// <summary>
/// Causal historical scenario evaluator. Entry presentation reuses AVA's existing
/// ScenarioDetectors. Outcomes begin on the minute AFTER the trigger bar, because the
/// detector itself depends on the completed trigger bar. This prevents same-bar look-ahead.
/// </summary>
public static class CorpusOutcomeEvaluator
{
    public static ScenarioRealizedOutcome Evaluate(
        ExecutionScenarioJsonV1 scenario,
        string inputJson,
        IReadOnlyList<MinuteBar> sessionBars,
        DateTime fallbackAsofUtc)
    {
        var asofUtc = DatasetAsofUtc(inputJson) ?? EnsureUtc(fallbackAsofUtc);
        var inputBars = ParseInputBars(inputJson);
        if (inputBars.Count == 0)
            return Empty("NO_INPUT_BARS");

        var future = sessionBars
            .Where(b => EnsureUtc(b.BarStartUtc) > asofUtc)
            .OrderBy(b => b.BarStartUtc)
            .ToList();
        if (future.Count == 0)
            return Empty("NO_FUTURE_BARS");

        var combinedRows = inputBars
            .Concat(future.Select(b => new MinuteBarRow
            {
                Ticker = b.Ticker,
                TsUtc = EnsureUtc(b.BarStartUtc),
                O = b.O, H = b.H, L = b.L, C = b.C, V = b.V, Source = b.Source
            }))
            .GroupBy(b => EnsureUtc(b.TsUtc))
            .Select(g => g.OrderBy(x => x.Source == "polygon" ? 1 : 0).First())
            .OrderBy(b => b.TsUtc)
            .ToList();

        var features = SessionFeatureCalculator.ComputeSessionFeatures(combinedRows);
        var featByTs = features.ToDictionary(f => EnsureUtc(f.TsUtc));
        var series = combinedRows.Select(b =>
        {
            featByTs.TryGetValue(EnsureUtc(b.TsUtc), out var f);
            return new BarWithFeat(
                EnsureUtc(b.TsUtc), b.O, b.H, b.L, b.C, b.V,
                f?.Vwap ?? 0m,
                f?.DistToVwap ?? 0m);
        }).ToList();

        var parsed = ToParsed(scenario);
        var firstFutureIndex = series.FindIndex(b => b.TsUtc > asofUtc);
        if (firstFutureIndex < 0) return Empty("NO_FUTURE_SERIES");

        int triggerIndex = -1;
        string triggerReason = "";

        // The live TriggerEngine evaluates the newly produced card immediately against
        // the completed as-of bar. Preserve that behavior before searching later bars.
        var decisionIndex = firstFutureIndex - 1;
        if (decisionIndex >= 0)
        {
            var atDecision = series.Take(decisionIndex + 1).ToList();
            if (Presented(atDecision, parsed, out var decisionReason))
            {
                triggerIndex = decisionIndex;
                triggerReason = decisionReason;
            }
        }

        if (triggerIndex < 0)
        {
            for (var i = firstFutureIndex; i < series.Count; i++)
            {
                var prefix = series.Take(i + 1).ToList();
                if (Presented(prefix, parsed, out var reason))
                {
                    triggerIndex = i;
                    triggerReason = reason;
                    break;
                }
            }
        }

        if (triggerIndex < 0)
        {
            return new ScenarioRealizedOutcome(
                false, null, null, ConservativeEntry(scenario), "NOT_TRIGGERED", null,
                null, null, null, null, null, null, null, null,
                "scenario_detector_to_session_close");
        }

        var triggerTs = series[triggerIndex].TsUtc;
        var entry = ConservativeEntry(scenario);
        var minutesToTrigger = (int)Math.Round((triggerTs - asofUtc).TotalMinutes, MidpointRounding.AwayFromZero);

        if (!entry.HasValue || !scenario.StopPrice.HasValue || !scenario.T1.HasValue)
        {
            return new ScenarioRealizedOutcome(
                true, triggerTs, minutesToTrigger, entry, "INVALID_LEVELS", null,
                null, null, null, null, null, null, null, triggerReason,
                "scenario_detector_to_session_close");
        }

        var risk = InitialRisk(scenario, entry.Value);
        if (risk <= 0)
        {
            return new ScenarioRealizedOutcome(
                true, triggerTs, minutesToTrigger, entry, "INVALID_RISK", null,
                null, null, null, null, null, null, null, triggerReason,
                "scenario_detector_to_session_close");
        }

        DateTime? t1Ts = null, stopTs = null, t2Ts = null, runnerTs = null;
        string outcome = "OPEN_AT_CLOSE";
        bool? t1BeforeStop = null;
        decimal bestMfeR = 0m;
        decimal worstMaeR = 0m;

        // Trigger depends on the completed trigger bar. Count outcomes only from the
        // next bar forward, avoiding unknowable intrabar ordering on the trigger bar.
        for (var i = triggerIndex + 1; i < series.Count; i++)
        {
            var b = series[i];
            UpdateExcursions(scenario, entry.Value, risk, b, ref bestMfeR, ref worstMaeR);

            if (!t1Ts.HasValue)
            {
                var stopHit = StopHit(scenario, b);
                var t1Hit = T1Hit(scenario, b);
                if (stopHit && t1Hit)
                {
                    outcome = "AMBIGUOUS_STOP_T1_SAME_BAR";
                    t1BeforeStop = null;
                    stopTs = b.TsUtc;
                    t1Ts = b.TsUtc;
                    break;
                }
                if (stopHit)
                {
                    outcome = "STOP_BEFORE_T1";
                    t1BeforeStop = false;
                    stopTs = b.TsUtc;
                    break;
                }
                if (t1Hit)
                {
                    t1Ts = b.TsUtc;
                    t1BeforeStop = true;
                    outcome = "T1_BEFORE_STOP";
                    continue;
                }
            }
            else
            {
                if (!t2Ts.HasValue && scenario.T2.HasValue && TargetHit(scenario.Direction, scenario.T2.Value, b))
                {
                    t2Ts = b.TsUtc;
                    outcome = "T2_REACHED";
                }
                if (t2Ts.HasValue && !runnerTs.HasValue && scenario.Runner.HasValue && TargetHit(scenario.Direction, scenario.Runner.Value, b))
                {
                    runnerTs = b.TsUtc;
                    outcome = "RUNNER_REACHED";
                    break;
                }
                if (StopHit(scenario, b))
                {
                    stopTs = b.TsUtc;
                    break;
                }
            }
        }

        int? minutesToT1 = t1Ts.HasValue
            ? (int)Math.Round((t1Ts.Value - triggerTs).TotalMinutes, MidpointRounding.AwayFromZero)
            : null;

        return new ScenarioRealizedOutcome(
            true, triggerTs, minutesToTrigger, entry, outcome, t1BeforeStop,
            t1Ts, minutesToT1, stopTs, t2Ts, runnerTs,
            Math.Round(bestMfeR, 3), Math.Round(worstMaeR, 3), triggerReason,
            "scenario_detector_to_session_close");
    }

    private static bool Presented(IReadOnlyList<BarWithFeat> bars, ParsedScenario s, out string reason)
        => s.EntryType switch
        {
            EntryType.ReclaimHold => ScenarioDetectors.IsReclaimHoldPresented(bars, s, out reason),
            EntryType.BreakHold => ScenarioDetectors.IsBreakHoldPresented(bars, s, out reason),
            EntryType.FadePop => ScenarioDetectors.IsFadePopPresented(bars, s, out reason),
            EntryType.VwapReclaim => ScenarioDetectors.IsVwapReclaimPresented(bars, s, out reason),
            EntryType.OverextensionFade => ScenarioDetectors.IsOverextensionFadePresented(bars, s, out reason),
            _ => (reason = "unknown entry type", false).Item2
        };

    private static ParsedScenario ToParsed(ExecutionScenarioJsonV1 s)
    {
        var type = (s.EntryType ?? "").ToLowerInvariant() switch
        {
            "break_hold" => EntryType.BreakHold,
            "fade_pop" => EntryType.FadePop,
            "vwap_reclaim" => EntryType.VwapReclaim,
            "overextension_fade" => EntryType.OverextensionFade,
            _ => EntryType.ReclaimHold
        };
        return new ParsedScenario(
            s.ScenarioRank, (s.Direction ?? "").ToLowerInvariant(), s.EntryLow, s.EntryHigh,
            type, s.StopPrice, s.T1, s.T2, s.Runner, s.ScenarioProb, s.SuccessProb,
            $"{s.EntryLow}-{s.EntryHigh}", s.Grade, s.GradeRationale);
    }

    private static List<MinuteBarRow> ParseInputBars(string inputJson)
    {
        using var doc = JsonDocument.Parse(inputJson);
        var list = new List<MinuteBarRow>();
        if (!doc.RootElement.TryGetProperty("intraday_bars", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var el in arr.EnumerateArray())
        {
            if (!TryDate(el, "ts_utc", out var ts)) continue;
            if (!TryDecimal(el, "o", out var o) || !TryDecimal(el, "h", out var h) ||
                !TryDecimal(el, "l", out var l) || !TryDecimal(el, "c", out var c)) continue;
            var v = TryLong(el, "v", out var volume) ? volume : 0L;
            list.Add(new MinuteBarRow
            {
                Ticker = doc.RootElement.TryGetProperty("ticker", out var tk) ? tk.GetString() ?? "" : "",
                TsUtc = ts, O = o, H = h, L = l, C = c, V = v, Source = "stored_input"
            });
        }
        return list.OrderBy(x => x.TsUtc).ToList();
    }

    private static decimal? ConservativeEntry(ExecutionScenarioJsonV1 s)
    {
        var isLong = string.Equals(s.Direction, "long", StringComparison.OrdinalIgnoreCase);
        return isLong ? s.EntryHigh ?? s.EntryLow : s.EntryLow ?? s.EntryHigh;
    }

    private static decimal InitialRisk(ExecutionScenarioJsonV1 s, decimal entry)
        => string.Equals(s.Direction, "long", StringComparison.OrdinalIgnoreCase)
            ? entry - (s.StopPrice ?? entry)
            : (s.StopPrice ?? entry) - entry;

    private static bool StopHit(ExecutionScenarioJsonV1 s, BarWithFeat b)
    {
        if (!s.StopPrice.HasValue) return false;
        return string.Equals(s.Direction, "long", StringComparison.OrdinalIgnoreCase)
            ? b.L <= s.StopPrice.Value
            : b.H >= s.StopPrice.Value;
    }

    private static bool T1Hit(ExecutionScenarioJsonV1 s, BarWithFeat b)
        => s.T1.HasValue && TargetHit(s.Direction, s.T1.Value, b);

    private static bool TargetHit(string? direction, decimal target, BarWithFeat b)
        => string.Equals(direction, "long", StringComparison.OrdinalIgnoreCase)
            ? b.H >= target
            : b.L <= target;

    private static void UpdateExcursions(
        ExecutionScenarioJsonV1 s, decimal entry, decimal risk, BarWithFeat b,
        ref decimal bestMfeR, ref decimal worstMaeR)
    {
        decimal favorable, adverse;
        if (string.Equals(s.Direction, "long", StringComparison.OrdinalIgnoreCase))
        {
            favorable = (b.H - entry) / risk;
            adverse = (b.L - entry) / risk;
        }
        else
        {
            favorable = (entry - b.L) / risk;
            adverse = (entry - b.H) / risk;
        }
        if (favorable > bestMfeR) bestMfeR = favorable;
        if (adverse < worstMaeR) worstMaeR = adverse;
    }

    private static ScenarioRealizedOutcome Empty(string outcome)
        => new(false, null, null, null, outcome, null, null, null, null, null, null, null, null, null,
            "scenario_detector_to_session_close");

    private static DateTime? DatasetAsofUtc(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("ts_asof_utc", out var el)) return null;
        return el.ValueKind == JsonValueKind.String && DateTime.TryParse(el.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
            ? EnsureUtc(dt)
            : null;
    }

    private static bool TryDate(JsonElement el, string name, out DateTime value)
    {
        value = default;
        if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return false;
        if (!DateTime.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)) return false;
        value = EnsureUtc(parsed);
        return true;
    }

    private static bool TryDecimal(JsonElement el, string name, out decimal value)
    {
        value = 0;
        return el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out value);
    }

    private static bool TryLong(JsonElement el, string name, out long value)
    {
        value = 0;
        if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) return false;
        if (v.TryGetInt64(out value)) return true;
        if (v.TryGetDecimal(out var d)) { value = (long)decimal.Truncate(d); return true; }
        return false;
    }

    private static DateTime EnsureUtc(DateTime dt)
        => dt.Kind switch
        {
            DateTimeKind.Utc => dt,
            DateTimeKind.Local => dt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc)
        };
}
