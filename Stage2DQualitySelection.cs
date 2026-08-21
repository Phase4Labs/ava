using System.Text.Json.Serialization;

namespace get_assessment_no_graph;

/// <summary>
/// Stage 2D quality-aware execution ordering.
///
/// This layer never changes structural validity, scenario contents, scenario ranks,
/// or the raw/stored model card. It only orders the already-normalized executable
/// scenarios using the Stage 2B.4 quality tier that validated out of sample:
/// PREFERRED before SECONDARY, then original scenario_rank as the stable tie-break.
///
/// Stage 2C empirical evidence is intentionally not consulted here. Stage 2C.8 showed
/// that evidence-aware reordering did not hold up on the temporal holdout.
/// </summary>
public static class Stage2DQualitySelector
{
    public const string Version = "stage2d_quality_order_v1";

    public static Stage2DQualitySelectionResult Select(AvaScenarioDecisionResult decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        var structuralCard = decision.Structural.NormalizedCard;
        var tierByRank = decision.Quality.Scenarios
            .Where(q => q.SelectionTier is not null)
            .GroupBy(q => q.ScenarioRank)
            .ToDictionary(g => g.Key, g => NormalizeTier(g.Last().SelectionTier));

        var entries = structuralCard.Scenarios
            .Select(s => new Stage2DScenarioSelectionEntry(
                ScenarioRank: s.ScenarioRank,
                SelectionTier: tierByRank.GetValueOrDefault(s.ScenarioRank, "UNKNOWN"),
                OriginalStructuralOrder: 0,
                QualityExecutionOrder: 0))
            .ToList();

        for (var i = 0; i < entries.Count; i++)
            entries[i] = entries[i] with { OriginalStructuralOrder = i + 1 };

        var orderedScenarios = structuralCard.Scenarios
            .OrderBy(s => TierPriority(tierByRank.GetValueOrDefault(s.ScenarioRank, "UNKNOWN")))
            .ThenBy(s => s.ScenarioRank)
            .ToList();

        var orderedRanks = orderedScenarios.Select(s => s.ScenarioRank).ToList();
        var orderByRank = orderedRanks
            .Select((rank, index) => new { rank, order = index + 1 })
            .ToDictionary(x => x.rank, x => x.order);

        entries = entries
            .Select(e => e with { QualityExecutionOrder = orderByRank.GetValueOrDefault(e.ScenarioRank) })
            .OrderBy(e => e.QualityExecutionOrder)
            .ToList();

        var originalRanks = structuralCard.Scenarios.Select(s => s.ScenarioRank).ToList();
        var changed = !originalRanks.SequenceEqual(orderedRanks);

        // New list, same normalized scenario objects. No scenario field is mutated and
        // the Stage 2B.4 normalized card remains unchanged for audit/telemetry.
        var orderedCard = new ExecutionCardJsonV1
        {
            SchemaVersion = structuralCard.SchemaVersion,
            Verdict = structuralCard.Verdict,
            Scenarios = orderedScenarios
        };

        return new Stage2DQualitySelectionResult(
            Version: Version,
            RawVerdict: decision.Structural.RawVerdict,
            StructuralEffectiveVerdict: decision.Structural.EffectiveVerdict,
            OriginalStructuralOrder: originalRanks,
            QualityExecutionOrder: orderedRanks,
            SelectionChanged: changed,
            Scenarios: entries,
            OrderedExecutableCard: orderedCard);
    }

    private static int TierPriority(string tier) => NormalizeTier(tier) switch
    {
        "PREFERRED" => 0,
        "SECONDARY" => 1,
        _ => 2
    };

    private static string NormalizeTier(string? tier)
    {
        var value = (tier ?? "UNKNOWN").Trim().ToUpperInvariant();
        return value is "PREFERRED" or "SECONDARY" ? value : "UNKNOWN";
    }
}

public sealed record Stage2DScenarioSelectionEntry(
    [property: JsonPropertyName("scenario_rank")] int ScenarioRank,
    [property: JsonPropertyName("selection_tier")] string SelectionTier,
    [property: JsonPropertyName("original_structural_order")] int OriginalStructuralOrder,
    [property: JsonPropertyName("quality_execution_order")] int QualityExecutionOrder);

public sealed record Stage2DQualitySelectionResult(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("raw_verdict")] string RawVerdict,
    [property: JsonPropertyName("structural_effective_verdict")] string StructuralEffectiveVerdict,
    [property: JsonPropertyName("original_structural_order")] IReadOnlyList<int> OriginalStructuralOrder,
    [property: JsonPropertyName("quality_execution_order")] IReadOnlyList<int> QualityExecutionOrder,
    [property: JsonPropertyName("selection_changed")] bool SelectionChanged,
    [property: JsonPropertyName("scenarios")] IReadOnlyList<Stage2DScenarioSelectionEntry> Scenarios,
    [property: JsonPropertyName("ordered_executable_card")] ExecutionCardJsonV1 OrderedExecutableCard);
