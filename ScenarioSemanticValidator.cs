using System.Text.Json;

namespace get_assessment_no_graph;

public sealed record ScenarioValidationIssue(string Code, string Severity, string Message);

public sealed record ScenarioSemanticResult(
    int ScenarioRank,
    bool Accepted,
    decimal? ConservativeT1RiskReward,
    decimal? EntryAnchorDistancePct,
    IReadOnlyList<ScenarioValidationIssue> Issues);

public sealed record CardSemanticValidationResult(
    string RawVerdict,
    string EffectiveVerdict,
    int RawScenarioCount,
    int AcceptedScenarioCount,
    IReadOnlyList<ScenarioValidationIssue> CardIssues,
    IReadOnlyList<ScenarioSemanticResult> Scenarios);

/// <summary>
/// Legacy Stage 2A/2B research gate retained for historical comparability and ablation.
/// Stage 2B.4 introduces ScenarioStructuralValidator + ScenarioQualityProfiler as the
/// forward architecture. Do not promote this monolithic gate into live execution.
/// </summary>
public static class ScenarioSemanticValidator
{
    public const decimal MinimumConservativeT1RiskReward = 1.50m;
    private const decimal MaxAnchorDistancePctForActionableGrade = 0.50m;

    public static CardSemanticValidationResult Validate(ExecutionCardJsonV1? card, string datasetJson)
    {
        if (card is null)
        {
            return new CardSemanticValidationResult(
                "INVALID", "NO_TRADE", 0, 0,
                new[] { new ScenarioValidationIssue("missing_card", "error", "No parsed execution card was available.") },
                Array.Empty<ScenarioSemanticResult>());
        }

        var facts = DatasetFacts.Parse(datasetJson);
        var cardIssues = new List<ScenarioValidationIssue>();
        var results = new List<ScenarioSemanticResult>();
        var earlier = new List<ExecutionScenarioJsonV1>();

        if (string.Equals(card.Verdict, "NO_TRADE", StringComparison.OrdinalIgnoreCase))
        {
            if (card.Scenarios.Count > 0)
                cardIssues.Add(new("no_trade_has_scenarios", "error", "NO_TRADE must not contain scenarios."));

            return new CardSemanticValidationResult(
                card.Verdict, "NO_TRADE", card.Scenarios.Count, 0, cardIssues, results);
        }

        foreach (var s in card.Scenarios.OrderBy(x => x.ScenarioRank))
        {
            var issues = new List<ScenarioValidationIssue>();

            var parsed = ToParsed(s);
            if (!ScenarioValidator.IsLevelOrderValid(parsed, out var levelReason))
                issues.Add(new("level_order", "error", levelReason));

            var rr = ConservativeT1RiskReward(s);
            if (!rr.HasValue)
                issues.Add(new("rr_unavailable", "error", "Conservative T1 risk/reward could not be calculated from entry, stop, and T1."));
            else if (rr.Value < MinimumConservativeT1RiskReward)
                issues.Add(new("rr_below_minimum", "error", $"Conservative T1 R:R {rr.Value:0.00}:1 is below AVA minimum {MinimumConservativeT1RiskReward:0.00}:1."));

            var duplicate = earlier.FirstOrDefault(x => IsNearDuplicate(x, s));
            if (duplicate is not null)
                issues.Add(new("duplicate_scenario", "error", $"Near-duplicate of scenario rank {duplicate.ScenarioRank}; alternatives must represent materially different setups."));

            ValidateGradeProbabilities(s, issues);
            if (!ScenarioValidator.MeetsMinGrade(s.Grade, TriggerConfig.MinimumGrade))
                issues.Add(new("grade_below_trigger_minimum", "error", $"Grade {s.Grade ?? "null"} is below TriggerConfig.MinimumGrade={TriggerConfig.MinimumGrade}."));

            var anchorDistance = NearestAnchorDistancePct(s, facts);
            if (IsActionableGrade(s.Grade))
            {
                if (!anchorDistance.HasValue)
                    issues.Add(new("anchor_unavailable", "warning", "No deterministic structural/VP anchor could be measured for this setup."));
                else if (anchorDistance.Value > MaxAnchorDistancePctForActionableGrade)
                    issues.Add(new("entry_far_from_anchor", "error", $"Entry is {anchorDistance.Value:0.00}% from the nearest applicable anchor; A/B setups require <= {MaxAnchorDistancePctForActionableGrade:0.00}%."));
            }

            ValidateRvolAndCatalystRules(s, facts, issues);
            ValidateRationaleClaims(s, facts, issues);

            var accepted = !issues.Any(i => string.Equals(i.Severity, "error", StringComparison.OrdinalIgnoreCase));
            results.Add(new ScenarioSemanticResult(s.ScenarioRank, accepted, rr, anchorDistance, issues));
            earlier.Add(s);
        }

        var acceptedCount = results.Count(r => r.Accepted);
        var effective = acceptedCount > 0 ? "TRADE" : "NO_TRADE";
        if (acceptedCount == 0 && card.Scenarios.Count > 0)
            cardIssues.Add(new("all_scenarios_rejected", "info", "All model-proposed scenarios failed deterministic semantic validation."));

        return new CardSemanticValidationResult(
            card.Verdict,
            effective,
            card.Scenarios.Count,
            acceptedCount,
            cardIssues,
            results);
    }

    private static ParsedScenario ToParsed(ExecutionScenarioJsonV1 s)
    {
        var entryType = (s.EntryType ?? "").ToLowerInvariant() switch
        {
            "break_hold" => EntryType.BreakHold,
            "fade_pop" => EntryType.FadePop,
            "vwap_reclaim" => EntryType.VwapReclaim,
            "overextension_fade" => EntryType.OverextensionFade,
            _ => EntryType.ReclaimHold
        };

        return new ParsedScenario(
            s.ScenarioRank,
            s.Direction ?? "",
            s.EntryLow,
            s.EntryHigh,
            entryType,
            s.StopPrice,
            s.T1,
            s.T2,
            s.Runner,
            s.ScenarioProb,
            s.SuccessProb,
            $"{s.EntryLow}-{s.EntryHigh}",
            s.Grade,
            s.GradeRationale);
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

    private static bool IsNearDuplicate(ExecutionScenarioJsonV1 a, ExecutionScenarioJsonV1 b)
    {
        if (!string.Equals(a.Direction, b.Direction, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(a.EntryType, b.EntryType, StringComparison.OrdinalIgnoreCase)) return false;

        var prices = new[]
        {
            (a.EntryLow, b.EntryLow), (a.EntryHigh, b.EntryHigh), (a.StopPrice, b.StopPrice),
            (a.T1, b.T1), (a.T2, b.T2), (a.Runner, b.Runner)
        };

        var comparable = prices.Where(p => p.Item1.HasValue && p.Item2.HasValue).ToList();
        if (comparable.Count < 3) return false;
        return comparable.All(p => RelativePctDifference(p.Item1!.Value, p.Item2!.Value) <= 0.10m);
    }

    private static decimal RelativePctDifference(decimal a, decimal b)
    {
        var baseline = Math.Max(Math.Abs(a), Math.Abs(b));
        if (baseline == 0) return 0;
        return Math.Abs(a - b) / baseline * 100m;
    }

    private static void ValidateGradeProbabilities(ExecutionScenarioJsonV1 s, List<ScenarioValidationIssue> issues)
    {
        var grade = (s.Grade ?? "").Trim().ToUpperInvariant();
        if (grade == "A")
        {
            if ((s.ScenarioProb ?? -1m) < 0.65m || (s.SuccessProb ?? -1m) < 0.65m)
                issues.Add(new("grade_probability_mismatch", "error", "Grade A requires scenario_prob >= 0.65 and success_prob >= 0.65."));
        }
        else if (grade == "B")
        {
            if ((s.ScenarioProb ?? -1m) < 0.55m || (s.SuccessProb ?? -1m) < 0.60m)
                issues.Add(new("grade_probability_mismatch", "error", "Grade B requires scenario_prob >= 0.55 and success_prob >= 0.60."));
        }
    }

    private static bool IsActionableGrade(string? grade)
        => string.Equals(grade, "A", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(grade, "B", StringComparison.OrdinalIgnoreCase);

    private static decimal? NearestAnchorDistancePct(ExecutionScenarioJsonV1 s, DatasetFacts facts)
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
            Add(anchors, facts.SessionVah); Add(anchors, facts.SessionVal);
            AddRange(anchors, facts.CompositeHvn);
        }
        else if (type == "overextension_fade")
        {
            Add(anchors, dir == "short" ? facts.SessionVah : facts.SessionVal);
            Add(anchors, facts.LastVwap); Add(anchors, facts.SessionPoc);
        }

        // Structural fallbacks are allowed for any entry type.
        Add(anchors, facts.PremarketHigh); Add(anchors, facts.PremarketLow);
        Add(anchors, facts.PriorDayHigh); Add(anchors, facts.PriorDayLow);
        Add(anchors, facts.SessionHigh); Add(anchors, facts.SessionLow);

        if (anchors.Count == 0) return null;
        return Math.Round(anchors.Min(a => Math.Abs(entry.Value - a) / entry.Value * 100m), 3);
    }

    private static void ValidateRvolAndCatalystRules(ExecutionScenarioJsonV1 s, DatasetFacts facts, List<ScenarioValidationIssue> issues)
    {
        if (facts.Rvol.HasValue)
        {
            if (facts.Rvol.Value < 1.0m && !string.Equals(s.Grade, "F", StringComparison.OrdinalIgnoreCase))
                issues.Add(new("rvol_grade_violation", "error", $"RVOL {facts.Rvol.Value:0.00}x is below 1.0x; AVA rules require grade F."));
            else if (facts.Rvol.Value < 1.5m && IsActionableGrade(s.Grade))
                issues.Add(new("rvol_actionable_violation", "error", $"RVOL {facts.Rvol.Value:0.00}x is below the 1.5x B/A threshold."));

            if (string.Equals(s.EntryType, "overextension_fade", StringComparison.OrdinalIgnoreCase) && facts.Rvol.Value < 2.5m)
                issues.Add(new("overextension_rvol", "error", $"overextension_fade requires RVOL >= 2.5x; observed {facts.Rvol.Value:0.00}x."));
        }

        if (string.Equals(s.EntryType, "overextension_fade", StringComparison.OrdinalIgnoreCase) &&
            facts.DayChangePct.HasValue && facts.DayChangePct.Value < 3m)
            issues.Add(new("overextension_day_gain", "error", $"overextension_fade disqualified because day gain {facts.DayChangePct.Value:0.00}% is below 3%."));

        if (string.Equals(s.Direction, "long", StringComparison.OrdinalIgnoreCase) &&
            facts.DayChangePct.HasValue && facts.DayChangePct.Value > 10m)
        {
            if (facts.NewsContextKnown && facts.NewsArticleCount == 0)
                issues.Add(new("long_without_catalyst", "error", $"LONG disqualified: day gain {facts.DayChangePct.Value:0.00}% exceeds 10% and supplied news_context has no direct catalyst."));
            else if (!facts.NewsContextKnown)
                issues.Add(new("catalyst_context_unavailable", "warning", "LONG >10% catalyst rule could not be verified because this historical input did not contain an enabled news_context."));
        }
    }

    private static void ValidateRationaleClaims(ExecutionScenarioJsonV1 s, DatasetFacts facts, List<ScenarioValidationIssue> issues)
    {
        var rationale = (s.GradeRationale ?? "").ToLowerInvariant();
        if (rationale.Length == 0)
        {
            issues.Add(new("missing_rationale", "warning", "grade_rationale is empty."));
            return;
        }

        if (rationale.Contains("catalyst") || rationale.Contains("news") || rationale.Contains("earnings") || rationale.Contains("fda"))
        {
            if (facts.NewsContextKnown && facts.NewsArticleCount == 0)
                issues.Add(new("unsupported_news_claim", "warning", "Rationale mentions a catalyst/news event but supplied news_context contains no articles."));
            else if (!facts.NewsContextKnown)
                issues.Add(new("news_context_unavailable", "warning", "Rationale mentions news/catalyst information but this historical input did not contain an enabled news_context."));
        }

        if (rationale.Contains("divergence"))
            issues.Add(new("derived_claim_divergence", "warning", "Rationale asserts divergence; this is not an explicit deterministic field and should be independently verified from bars."));
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

    private sealed record DatasetFacts(
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
        public static DatasetFacts Parse(string json)
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

            return new DatasetFacts(
                Ref("prior_day_high"), Ref("prior_day_low"), Ref("premarket_high"), Ref("premarket_low"),
                Ref("session_high"), Ref("session_low"), Ref("last_vwap"),
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
