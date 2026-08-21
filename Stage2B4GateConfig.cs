namespace get_assessment_no_graph;

public enum Stage2B4GateMode
{
    Shadow,
    Enforce
}

/// <summary>
/// Runtime switch for the promoted Stage 2B.4 structural gate.
/// AVA_STRUCTURAL_GATE_MODE accepts "shadow" or "enforce".
/// Promotion default is enforce; set shadow for immediate rollback without a code change.
/// </summary>
public static class Stage2B4GateConfig
{
    public static Stage2B4GateMode Mode
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("AVA_STRUCTURAL_GATE_MODE");
            if (string.Equals(raw, "shadow", StringComparison.OrdinalIgnoreCase))
                return Stage2B4GateMode.Shadow;
            return Stage2B4GateMode.Enforce;
        }
    }

    public static bool IsEnforced => Mode == Stage2B4GateMode.Enforce;
    public static string Label => IsEnforced ? "enforce" : "shadow";
}
