namespace get_assessment_no_graph;

/// <summary>
/// Central configuration for signal filtering thresholds.
/// Change <see cref="MinimumGrade"/> to tighten or relax grade filtering:
///   "C" = accept A, B, C  (current default)
///   "B" = accept A, B only
///   "A" = accept A only
/// </summary>
public static class TriggerConfig
{
    /// <summary>
    /// Minimum acceptable grade for a scenario to be eligible for entry signal emission.
    /// Scenarios graded below this threshold are skipped before any detector logic runs.
    /// </summary>
    public const string MinimumGrade = "C";
}