namespace get_assessment_no_graph;

public enum Stage2DQualitySelectionMode
{
    Shadow,
    Enforce
}

/// <summary>
/// Runtime switch for Stage 2D quality-aware executable ordering.
/// AVA_QUALITY_SELECTION_MODE accepts "shadow" or "enforce".
/// Promotion default is enforce; set shadow for immediate rollback to Stage 2B.4
/// structural ordering without changing code.
/// </summary>
public static class Stage2DQualitySelectionConfig
{
    public static Stage2DQualitySelectionMode Mode
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("AVA_QUALITY_SELECTION_MODE");
            if (string.Equals(raw, "shadow", StringComparison.OrdinalIgnoreCase))
                return Stage2DQualitySelectionMode.Shadow;
            return Stage2DQualitySelectionMode.Enforce;
        }
    }

    public static bool IsEnforced => Mode == Stage2DQualitySelectionMode.Enforce;
    public static string Label => IsEnforced ? "enforce" : "shadow";
}
