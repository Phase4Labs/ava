using System.Text.Json;
using System.Text.Json.Serialization;

namespace get_assessment_no_graph;

/// <summary>
/// Stage 2B.4 quality/calibration annotations for structurally valid scenarios.
///
/// These are NOT hard rejections. They expose deterministic features that the
/// recent-framework corpus showed to have different degrees of selection value.
/// Historical expectancy is intentionally not hard-coded here; Stage 2C will
/// attach analogue-based empirical probabilities/expectancy.
/// </summary>
public static class ScenarioQualityProfiler
{
    public const decimal PreferredConservativeT1RiskReward = 1.50m;
    public const decimal ActionableRvolThreshold = 1.50m;
    public const decimal MaxAnchorDistancePctReference = 0.50m;

    public static CardQualityProfile Analyze(
        CardStructuralValidationResult structural,
        string datasetJson)
    {
        var facts = QualityDatasetFacts.Parse(datasetJson);
        var profiles = new List<ScenarioQualityProfile>();
        var earlier = new List<ExecutionScenarioJsonV1>();

        foreach (var sr in structural.Scenarios.OrderBy(s => s.ScenarioRank))
        {
            if (!sr.StructurallyValid || sr.NormalizedScenario is null)
            {
                profiles.Add(new ScenarioQualityProfile(
                    sr.ScenarioRank,
                    "STRUCTURALLY_INVALID",
                    sr.ConservativeT1RiskReward,
                    null,
                    facts.Rvol,
                    Array.Empty<ScenarioQualitySignal>(),
                    Array.Empty<ScenarioQualitySignal>()));
                continue;
            }

            var s = sr.NormalizedScenario;
            var penalties = new List<ScenarioQualitySignal>();
            var observations = new List<ScenarioQualitySignal>();

            var rr = sr.ConservativeT1RiskReward;
            if (rr.HasValue && rr.Value < PreferredConservativeT1RiskReward)
                penalties.Add(new("rr_below_preferred", "selection_penalty", $"Conservative T1 R:R {rr.Value:0.00}:1 is below the preferred {PreferredConservativeT1RiskReward:0.00}:1 cohort threshold; Stage 2B.3 showed this is lower-expectancy, not structurally invalid."));

            if (facts.Rvol.HasValue && facts.Rvol.Value < ActionableRvolThreshold && IsActionableGrade(s.Grade))
                penalties.Add(new("rvol_below_actionable_reference", "selection_penalty", $"RVOL {facts.Rvol.Value:0.00}x is below the 1.50x A/B reference; use as a modest quality penalty, not a hard invalidation."));

            var anchorDistance = NearestAnchorDistancePct(s, facts);
            if (anchorDistance.HasValue && anchorDistance.Value > MaxAnchorDistancePctReference && IsActionableGrade(s.Grade))
                observations.Add(new("entry_far_from_anchor", "observation", $"Entry is {anchorDistance.Value:0.00}% from nearest measured structural/VP anchor; current corpus did not justify this as a hard gate."));
            else if (!anchorDistance.HasValue && IsActionableGrade(s.Grade))
                observations.Add(new("anchor_unavailable", "observation", "No deterministic structural/VP anchor distance could be measured."));

            if (!ScenarioValidator.MeetsMinGrade(s.Grade, TriggerConfig.MinimumGrade))
                observations.Add(new("grade_below_trigger_reference", "observation", $"Grade {s.Grade ?? "null"} is below current TriggerConfig.MinimumGrade={TriggerConfig.MinimumGrade}; Stage 2B.3 did not justify a hard semantic rejection."));

            if ((s.ScenarioProb ?? 0m) < 0.35m)
                observations.Add(new("scenario_prob_below_trigger_reference", "observation", $"scenario_prob {s.ScenarioProb?.ToString("0.00") ?? "null"} is below the current TriggerEngine 0.35 reference."));
            if ((s.SuccessProb ?? 0m) < 0.55m)
                observations.Add(new("success_prob_below_trigger_reference", "observation", $"success_prob {s.SuccessProb?.ToString("0.00") ?? "null"} is below the current TriggerEngine 0.55 reference."));

            if (facts.Rvol.HasValue && facts.Rvol.Value < 1.0m && !string.Equals(s.Grade, "F", StringComparison.OrdinalIgnoreCase))
                observations.Add(new("rvol_below_1x_grade_reference", "observation", $"RVOL {facts.Rvol.Value:0.00}x is below the historical 1.0x grade-F reference."));

            ValidateGradeProbabilityObservation(s, observations);
            ValidateCatalystAndOverextensionObservations(s, facts, observations);
            ValidateRationaleObservations(s, facts, observations);

            var duplicate = earlier.FirstOrDefault(x => IsNearDuplicate(x, s));
            if (duplicate is not null)
                observations.Add(new("near_duplicate_scenario", "observation", $"Near-duplicate of scenario rank {duplicate.ScenarioRank}; preserve for analysis but de-duplicate before presentation/execution if desired."));

            var tier = penalties.Count == 0 ? "PREFERRED" : "SECONDARY";
            profiles.Add(new ScenarioQualityProfile(
                sr.ScenarioRank,
                tier,
                rr,
                anchorDistance,
                facts.Rvol,
                penalties,
                observations));
            earlier.Add(s);
        }

        return new CardQualityProfile(
            structural.EffectiveVerdict,
            profiles.Count(p => p.SelectionTier == "PREFERRED"),
            profiles.Count(p => p.SelectionTier == "SECONDARY"),
            profiles);
    }

    private static void ValidateGradeProbabilityObservation(ExecutionScenarioJsonV1 s, List<ScenarioQualitySignal> observations)
    {
        var grade = (s.Grade ?? "").Trim().ToUpperInvariant();
        if (grade == "A" && ((s.ScenarioProb ?? -1m) < 0.65m || (s.SuccessProb ?? -1m) < 0.65m))
            observations.Add(new("grade_probability_mismatch", "observation", "Grade A is inconsistent with prior AVA probability thresholds."));
        else if (grade == "B" && ((s.ScenarioProb ?? -1m) < 0.55m || (s.SuccessProb ?? -1m) < 0.60m))
            observations.Add(new("grade_probability_mismatch", "observation", "Grade B is inconsistent with prior AVA probability thresholds."));
    }

    private static void ValidateCatalystAndOverextensionObservations(ExecutionScenarioJsonV1 s, QualityDatasetFacts facts, List<ScenarioQualitySignal> observations)
    {
        if (string.Equals(s.EntryType, "overextension_fade", StringComparison.OrdinalIgnoreCase))
        {
            if (facts.Rvol.HasValue && facts.Rvol.Value < 2.5m)
                observations.Add(new("overextension_rvol", "observation", $"overextension_fade with RVOL {facts.Rvol.Value:0.00}x < 2.5x."));
            if (facts.DayChangePct.HasValue && facts.DayChangePct.Value < 3m)
                observations.Add(new("overextension_day_gain", "observation", $"overextension_fade with day gain {facts.DayChangePct.Value:0.00}% < 3%."));
        }

        if (string.Equals(s.Direction, "long", StringComparison.OrdinalIgnoreCase) &&
            facts.DayChangePct.HasValue && facts.DayChangePct.Value > 10m)
        {
            if (facts.NewsContextKnown && facts.NewsArticleCount == 0)
                observations.Add(new("long_without_catalyst", "observation", $"LONG while day gain is {facts.DayChangePct.Value:0.00}% and supplied news_context has no articles; current corpus did not justify hard rejection."));
            else if (!facts.NewsContextKnown)
                observations.Add(new("catalyst_context_unavailable", "observation", "LONG >10% catalyst context cannot be evaluated because news_context is unavailable."));
        }
    }

    private static void ValidateRationaleObservations(ExecutionScenarioJsonV1 s, QualityDatasetFacts facts, List<ScenarioQualitySignal> observations)
    {
        var rationale = (s.GradeRationale ?? "").ToLowerInvariant();
        if (rationale.Length == 0)
        {
            observations.Add(new("missing_rationale", "observation", "grade_rationale is empty."));
            return;
        }

        if ((rationale.Contains("catalyst") || rationale.Contains("news") || rationale.Contains("earnings") || rationale.Contains("fda")) &&
            facts.NewsContextKnown && facts.NewsArticleCount == 0)
            observations.Add(new("unsupported_news_claim", "observation", "Rationale mentions news/catalyst information but supplied news_context contains no articles."));

        if (rationale.Contains("divergence"))
            observations.Add(new("derived_claim_divergence", "observation", "Rationale asserts divergence; verify independently from deterministic bar features."));
    }

    private static bool IsActionableGrade(string? grade)
        => string.Equals(grade, "A", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(grade, "B", StringComparison.OrdinalIgnoreCase);

    private static decimal? NearestAnchorDistancePct(ExecutionScenarioJsonV1 s, QualityDatasetFacts facts)
    {
        var entry = Mid(s.EntryLow, s.EntryHigh);
        if (!entry.HasValue || entry.Value == 0) return null;

        var anchors = new List<decimal>();
        var dir = (s.Direction ?? "").ToLowerInvariant();
        var type = (s.EntryType ?? "").ToLowerInvariant();

        if (type == "break_hold") Add(anchors, dir == "long" ? facts.SessionVah : facts.SessionVal);
        else if (type == "reclaim_hold") Add(anchors, dir == "long" ? facts.SessionVal : facts.SessionVah);
        else if (type == "vwap_reclaim") Add(anchors, facts.LastVwap);
        else if (type == "fade_pop")
        {
            Add(anchors, facts.SessionVah); Add(anchors, facts.SessionVal); AddRange(anchors, facts.CompositeHvn);
        }
        else if (type == "overextension_fade")
        {
            Add(anchors, dir == "short" ? facts.SessionVah : facts.SessionVal);
            Add(anchors, facts.LastVwap); Add(anchors, facts.SessionPoc);
        }

        Add(anchors, facts.PremarketHigh); Add(anchors, facts.PremarketLow);
        Add(anchors, facts.PriorDayHigh); Add(anchors, facts.PriorDayLow);
        Add(anchors, facts.SessionHigh); Add(anchors, facts.SessionLow);

        return anchors.Count == 0
            ? null
            : Math.Round(anchors.Min(a => Math.Abs(entry.Value - a) / entry.Value * 100m), 3);
    }

    private static bool IsNearDuplicate(ExecutionScenarioJsonV1 a, ExecutionScenarioJsonV1 b)
    {
        if (!string.Equals(a.Direction, b.Direction, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(a.EntryType, b.EntryType, StringComparison.OrdinalIgnoreCase)) return false;
        var pairs = new[] { (a.EntryLow,b.EntryLow), (a.EntryHigh,b.EntryHigh), (a.StopPrice,b.StopPrice), (a.T1,b.T1), (a.T2,b.T2), (a.Runner,b.Runner) };
        var comparable = pairs.Where(p => p.Item1.HasValue && p.Item2.HasValue).ToList();
        if (comparable.Count < 3) return false;
        return comparable.All(p => RelativePctDifference(p.Item1!.Value, p.Item2!.Value) <= 0.10m);
    }

    private static decimal RelativePctDifference(decimal a, decimal b)
    {
        var baseline = Math.Max(Math.Abs(a), Math.Abs(b));
        return baseline == 0 ? 0 : Math.Abs(a - b) / baseline * 100m;
    }

    private static decimal? Mid(decimal? low, decimal? high)
        => low.HasValue && high.HasValue ? (low.Value + high.Value) / 2m : low ?? high;

    private static void Add(List<decimal> list, decimal? value)
    {
        if (value.HasValue && value.Value > 0) list.Add(value.Value);
    }

    private static void AddRange(List<decimal> list, IEnumerable<decimal> values)
    {
        foreach (var v in values) if (v > 0) list.Add(v);
    }

    private sealed record QualityDatasetFacts(
        decimal? PriorDayHigh,
        decimal? PriorDayLow,
        decimal? PremarketHigh,
        decimal? PremarketLow,
        decimal? SessionHigh,
        decimal? SessionLow,
        decimal? LastVwap,
        decimal? SessionPoc,
        decimal? SessionVah,
        decimal? SessionVal,
        IReadOnlyList<decimal> CompositeHvn,
        decimal? Rvol,
        decimal? DayChangePct,
        bool NewsContextKnown,
        int NewsArticleCount)
    {
        public static QualityDatasetFacts Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            decimal? Ref(string p) => GetNestedDecimal(r, "reference_levels", p);
            var priorClose = Ref("prior_day_close");
            var lastClose = Ref("last_close") ?? GetNestedDecimal(r, "deterministic_state", "last_close");
            var dayPct = lastClose.HasValue && priorClose.HasValue && priorClose.Value != 0
                ? Math.Round((lastClose.Value - priorClose.Value) / priorClose.Value * 100m, 3)
                : GetNestedDecimal(r, "deterministic_state", "day_change_pct");

            var newsKnown = r.TryGetProperty("news_context", out var news) &&
                            news.ValueKind == JsonValueKind.Object &&
                            news.TryGetProperty("enabled", out var enabled) &&
                            enabled.ValueKind == JsonValueKind.True;

            return new QualityDatasetFacts(
                Ref("prior_day_high"), Ref("prior_day_low"), Ref("premarket_high"), Ref("premarket_low"),
                Ref("session_high") ?? GetNestedDecimal(r, "deterministic_state", "session_high"),
                Ref("session_low") ?? GetNestedDecimal(r, "deterministic_state", "session_low"),
                Ref("last_vwap") ?? GetNestedDecimal(r, "deterministic_state", "last_vwap"),
                GetNestedDecimal(r, "session_vp", "poc"), GetNestedDecimal(r, "session_vp", "vah"), GetNestedDecimal(r, "session_vp", "val"),
                GetDecimalArray(r, "composite_vp", "hvn"),
                GetNestedDecimal(r, "volume_context", "rvol_vs_adv"),
                dayPct,
                newsKnown,
                GetNestedInt(r, "news_context", "article_count") ?? 0);
        }
    }

    private static decimal? GetNestedDecimal(JsonElement root, string obj, string prop)
    {
        if (!root.TryGetProperty(obj, out var o) || o.ValueKind != JsonValueKind.Object) return null;
        return o.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;
    }

    private static int? GetNestedInt(JsonElement root, string obj, string prop)
    {
        if (!root.TryGetProperty(obj, out var o) || o.ValueKind != JsonValueKind.Object) return null;
        return o.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var d) ? d : null;
    }

    private static IReadOnlyList<decimal> GetDecimalArray(JsonElement root, string obj, string prop)
    {
        var list = new List<decimal>();
        if (!root.TryGetProperty(obj, out var o) || o.ValueKind != JsonValueKind.Object) return list;
        if (!o.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var v in arr.EnumerateArray()) if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) list.Add(d);
        return list;
    }
}

public sealed record ScenarioQualitySignal(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("message")] string Message);

public sealed record ScenarioQualityProfile(
    [property: JsonPropertyName("scenario_rank")] int ScenarioRank,
    [property: JsonPropertyName("selection_tier")] string SelectionTier,
    [property: JsonPropertyName("conservative_t1_rr")] decimal? ConservativeT1RiskReward,
    [property: JsonPropertyName("entry_anchor_distance_pct")] decimal? EntryAnchorDistancePct,
    [property: JsonPropertyName("rvol")] decimal? Rvol,
    [property: JsonPropertyName("selection_penalties")] IReadOnlyList<ScenarioQualitySignal> SelectionPenalties,
    [property: JsonPropertyName("observations")] IReadOnlyList<ScenarioQualitySignal> Observations);

public sealed record CardQualityProfile(
    [property: JsonPropertyName("structural_effective_verdict")] string StructuralEffectiveVerdict,
    [property: JsonPropertyName("preferred_scenario_count")] int PreferredScenarioCount,
    [property: JsonPropertyName("secondary_scenario_count")] int SecondaryScenarioCount,
    [property: JsonPropertyName("scenarios")] IReadOnlyList<ScenarioQualityProfile> Scenarios);
