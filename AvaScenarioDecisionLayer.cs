using System.Text.Json.Serialization;

namespace get_assessment_no_graph;

/// <summary>
/// Stage 2B.4 shared deterministic decision layer used identically for cloud and local cards.
/// The live caller may run it in shadow or enforcement mode. The decision layer itself is pure:
/// it never mutates the raw model card and always returns a separate normalized executable card.
/// </summary>
public static class AvaScenarioDecisionLayer
{
    public static AvaScenarioDecisionResult Evaluate(ExecutionCardJsonV1? card, string datasetJson)
    {
        var structural = ScenarioStructuralValidator.Validate(card);
        var quality = ScenarioQualityProfiler.Analyze(structural, datasetJson);
        return new AvaScenarioDecisionResult(
            LayerVersion: "stage2b4_v1",
            Structural: structural,
            Quality: quality);
    }
}

public sealed record AvaScenarioDecisionResult(
    [property: JsonPropertyName("layer_version")] string LayerVersion,
    [property: JsonPropertyName("structural")] CardStructuralValidationResult Structural,
    [property: JsonPropertyName("quality")] CardQualityProfile Quality);
