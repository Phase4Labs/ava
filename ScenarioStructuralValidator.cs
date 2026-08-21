using System.Text.Json.Serialization;

namespace get_assessment_no_graph;

/// <summary>
/// Stage 2B.4 model-independent structural validation.
///
/// This validator answers only: "is this scenario executable at Entry/Stop/T1?"
/// It intentionally does NOT reject on R:R preference, RVOL, grade, catalyst,
/// anchor distance, or other quality/calibration features.
///
/// Safe repair policy:
/// - Entry/Stop/T1 defects are hard invalidations; never invent replacement levels.
/// - Invalid T2/runner ordering is repairable by omitting the offending later target.
///   The raw model card is never mutated.
/// </summary>
public static class ScenarioStructuralValidator
{
    public static CardStructuralValidationResult Validate(ExecutionCardJsonV1? card)
    {
        if (card is null)
        {
            return new CardStructuralValidationResult(
                RawVerdict: "INVALID",
                EffectiveVerdict: "NO_TRADE",
                RawScenarioCount: 0,
                StructurallyValidScenarioCount: 0,
                CardIssues: new[] { new ScenarioStructureIssue("missing_card", "hard", "No parsed execution card was available.") },
                Scenarios: Array.Empty<ScenarioStructuralResult>(),
                NormalizedCard: new ExecutionCardJsonV1 { SchemaVersion = 1, Verdict = "NO_TRADE", Scenarios = new() });
        }

        var cardIssues = new List<ScenarioStructureIssue>();
        var results = new List<ScenarioStructuralResult>();

        if (string.Equals(card.Verdict, "NO_TRADE", StringComparison.OrdinalIgnoreCase))
        {
            if (card.Scenarios.Count > 0)
                cardIssues.Add(new("no_trade_has_scenarios", "hard", "NO_TRADE must not contain executable scenarios."));

            return new CardStructuralValidationResult(
                RawVerdict: card.Verdict,
                EffectiveVerdict: "NO_TRADE",
                RawScenarioCount: card.Scenarios.Count,
                StructurallyValidScenarioCount: 0,
                CardIssues: cardIssues,
                Scenarios: results,
                NormalizedCard: new ExecutionCardJsonV1 { SchemaVersion = 1, Verdict = "NO_TRADE", Scenarios = new() });
        }

        foreach (var scenario in card.Scenarios.OrderBy(s => s.ScenarioRank))
            results.Add(ValidateScenario(scenario));

        var normalizedScenarios = results
            .Where(r => r.StructurallyValid && r.NormalizedScenario is not null)
            .Select(r => Clone(r.NormalizedScenario!))
            .ToList();

        var effectiveVerdict = normalizedScenarios.Count > 0 ? "TRADE" : "NO_TRADE";
        if (normalizedScenarios.Count == 0 && card.Scenarios.Count > 0)
            cardIssues.Add(new("all_scenarios_structurally_invalid", "info", "No model-proposed scenario has executable Entry/Stop/T1 geometry."));

        return new CardStructuralValidationResult(
            RawVerdict: card.Verdict,
            EffectiveVerdict: effectiveVerdict,
            RawScenarioCount: card.Scenarios.Count,
            StructurallyValidScenarioCount: normalizedScenarios.Count,
            CardIssues: cardIssues,
            Scenarios: results,
            NormalizedCard: new ExecutionCardJsonV1
            {
                SchemaVersion = 1,
                Verdict = effectiveVerdict,
                Scenarios = normalizedScenarios
            });
    }

    public static ScenarioStructuralResult ValidateScenario(ExecutionScenarioJsonV1 s)
    {
        var hard = new List<ScenarioStructureIssue>();
        var repairs = new List<ScenarioStructureIssue>();

        var dir = (s.Direction ?? "").Trim().ToLowerInvariant();
        if (dir != "long" && dir != "short")
            hard.Add(new("direction_invalid", "hard", $"Direction '{s.Direction}' is not long/short."));

        if (!s.EntryLow.HasValue || !s.EntryHigh.HasValue)
            hard.Add(new("missing_entry_bounds", "hard", "Both entry_low and entry_high are required for executable AVA entry detection."));
        else if (s.EntryLow.Value > s.EntryHigh.Value)
            hard.Add(new("entry_bounds_reversed", "hard", $"entry_low ({s.EntryLow}) > entry_high ({s.EntryHigh})."));

        if (!s.StopPrice.HasValue)
            hard.Add(new("missing_stop", "hard", "stop_price is required."));
        if (!s.T1.HasValue)
            hard.Add(new("missing_t1", "hard", "T1 is required."));

        decimal? conservativeEntry = null;
        decimal? risk = null;
        decimal? reward = null;
        decimal? rr = null;

        if (hard.Count == 0)
        {
            conservativeEntry = dir == "long" ? s.EntryHigh : s.EntryLow;

            if (dir == "long")
            {
                if (s.StopPrice!.Value >= s.EntryLow!.Value)
                    hard.Add(new("stop_wrong_side", "hard", $"LONG stop ({s.StopPrice}) must be below entry_low ({s.EntryLow})."));
                if (s.T1!.Value <= s.EntryHigh!.Value)
                    hard.Add(new("t1_wrong_side", "hard", $"LONG T1 ({s.T1}) must be above entry_high ({s.EntryHigh})."));
            }
            else if (dir == "short")
            {
                if (s.StopPrice!.Value <= s.EntryHigh!.Value)
                    hard.Add(new("stop_wrong_side", "hard", $"SHORT stop ({s.StopPrice}) must be above entry_high ({s.EntryHigh})."));
                if (s.T1!.Value >= s.EntryLow!.Value)
                    hard.Add(new("t1_wrong_side", "hard", $"SHORT T1 ({s.T1}) must be below entry_low ({s.EntryLow})."));
            }

            if (hard.Count == 0 && conservativeEntry.HasValue)
            {
                var riskValue = dir == "long"
                    ? conservativeEntry.Value - s.StopPrice!.Value
                    : s.StopPrice!.Value - conservativeEntry.Value;
                var rewardValue = dir == "long"
                    ? s.T1!.Value - conservativeEntry.Value
                    : conservativeEntry.Value - s.T1!.Value;
                risk = riskValue;
                reward = rewardValue;

                if (riskValue <= 0)
                    hard.Add(new("nonpositive_risk", "hard", $"Conservative initial risk is {riskValue:0.####}; it must be positive."));
                if (rewardValue <= 0)
                    hard.Add(new("nonpositive_reward", "hard", $"Conservative T1 reward is {rewardValue:0.####}; it must be positive."));
                if (riskValue > 0 && rewardValue > 0)
                    rr = Math.Round(rewardValue / riskValue, 3);
            }
        }

        if (hard.Count > 0)
        {
            return new ScenarioStructuralResult(
                ScenarioRank: s.ScenarioRank,
                StructurallyValid: false,
                ConservativeEntry: conservativeEntry,
                InitialRisk: risk,
                T1Reward: reward,
                ConservativeT1RiskReward: rr,
                HardIssues: hard,
                RepairWarnings: repairs,
                NormalizedScenario: null);
        }

        var normalized = Clone(s);

        // T2 is optional. If present, it must extend beyond T1 in the trade direction.
        if (normalized.T2.HasValue)
        {
            var t2Valid = dir == "long"
                ? normalized.T2.Value > normalized.T1!.Value
                : normalized.T2.Value < normalized.T1!.Value;
            if (!t2Valid)
            {
                repairs.Add(new("t2_removed_invalid_order", "repair", $"T2 ({normalized.T2}) is not beyond T1 ({normalized.T1}) for {dir}; normalized executable scenario omits T2."));
                normalized.T2 = null;
            }
        }

        // Runner is optional. It must extend beyond the furthest retained profit target.
        if (normalized.Runner.HasValue)
        {
            var baseTarget = normalized.T2 ?? normalized.T1!.Value;
            var runnerValid = dir == "long"
                ? normalized.Runner.Value > baseTarget
                : normalized.Runner.Value < baseTarget;
            if (!runnerValid)
            {
                repairs.Add(new("runner_removed_invalid_order", "repair", $"Runner ({normalized.Runner}) is not beyond retained target ({baseTarget}) for {dir}; normalized executable scenario omits runner."));
                normalized.Runner = null;
            }
        }

        return new ScenarioStructuralResult(
            ScenarioRank: s.ScenarioRank,
            StructurallyValid: true,
            ConservativeEntry: conservativeEntry,
            InitialRisk: risk,
            T1Reward: reward,
            ConservativeT1RiskReward: rr,
            HardIssues: hard,
            RepairWarnings: repairs,
            NormalizedScenario: normalized);
    }

    public static ExecutionScenarioJsonV1 Clone(ExecutionScenarioJsonV1 s) => new()
    {
        ScenarioRank = s.ScenarioRank,
        Direction = s.Direction,
        EntryType = s.EntryType,
        ScenarioProb = s.ScenarioProb,
        SuccessProb = s.SuccessProb,
        EntryLow = s.EntryLow,
        EntryHigh = s.EntryHigh,
        StopPrice = s.StopPrice,
        T1 = s.T1,
        T2 = s.T2,
        Runner = s.Runner,
        Grade = s.Grade,
        GradeRationale = s.GradeRationale
    };
}

public sealed record ScenarioStructureIssue(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("message")] string Message);

public sealed record ScenarioStructuralResult(
    [property: JsonPropertyName("scenario_rank")] int ScenarioRank,
    [property: JsonPropertyName("structurally_valid")] bool StructurallyValid,
    [property: JsonPropertyName("conservative_entry")] decimal? ConservativeEntry,
    [property: JsonPropertyName("initial_risk")] decimal? InitialRisk,
    [property: JsonPropertyName("t1_reward")] decimal? T1Reward,
    [property: JsonPropertyName("conservative_t1_rr")] decimal? ConservativeT1RiskReward,
    [property: JsonPropertyName("hard_issues")] IReadOnlyList<ScenarioStructureIssue> HardIssues,
    [property: JsonPropertyName("repair_warnings")] IReadOnlyList<ScenarioStructureIssue> RepairWarnings,
    [property: JsonPropertyName("normalized_scenario")] ExecutionScenarioJsonV1? NormalizedScenario);

public sealed record CardStructuralValidationResult(
    [property: JsonPropertyName("raw_verdict")] string RawVerdict,
    [property: JsonPropertyName("effective_verdict")] string EffectiveVerdict,
    [property: JsonPropertyName("raw_scenario_count")] int RawScenarioCount,
    [property: JsonPropertyName("structurally_valid_scenario_count")] int StructurallyValidScenarioCount,
    [property: JsonPropertyName("card_issues")] IReadOnlyList<ScenarioStructureIssue> CardIssues,
    [property: JsonPropertyName("scenarios")] IReadOnlyList<ScenarioStructuralResult> Scenarios,
    [property: JsonPropertyName("normalized_card")] ExecutionCardJsonV1 NormalizedCard);
