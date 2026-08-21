namespace get_assessment_no_graph;

/// <summary>
/// Validates LLM-generated scenarios before they enter the trigger pipeline.
/// Catches hallucinated or incoherent level values and enforces minimum grade.
/// </summary>
public static class ScenarioValidator
{
    // Grade hierarchy — higher index = better grade.
    private static readonly string[] GradeOrder = { "F", "D", "C", "B", "A" };

    /// <summary>
    /// Returns true if <paramref name="scenarioGrade"/> is at or above
    /// <paramref name="minimumGrade"/> in the A > B > C > D > F hierarchy.
    /// Null or unrecognised grades always fail.
    /// </summary>
    public static bool MeetsMinGrade(string? scenarioGrade, string minimumGrade)
    {
        if (string.IsNullOrWhiteSpace(scenarioGrade)) return false;

        var sg = scenarioGrade.Trim().ToUpperInvariant();
        var mg = minimumGrade.Trim().ToUpperInvariant();

        int sgIdx = Array.IndexOf(GradeOrder, sg);
        int mgIdx = Array.IndexOf(GradeOrder, mg);

        // Unrecognised grade strings always fail.
        if (sgIdx < 0 || mgIdx < 0) return false;

        return sgIdx >= mgIdx;
    }

    /// <summary>
    /// Validates that price levels form a coherent ordered stack.
    ///
    /// Long:  stop &lt; entry_low &le; entry_high &lt; t1 &lt; t2 &lt; runner
    /// Short: stop &gt; entry_high &ge; entry_low &gt; t1 &gt; t2 &gt; runner
    ///
    /// Null levels are skipped (partial scenarios are permitted); only the
    /// levels that are present are checked against each other.
    ///
    /// Returns false and sets <paramref name="reason"/> when the check fails.
    /// </summary>
    public static bool IsLevelOrderValid(ParsedScenario s, out string reason)
    {
        reason = "";

        bool isLong = string.Equals(s.Direction, "long", StringComparison.OrdinalIgnoreCase);

        if (isLong)
        {
            // stop < entry_low
            if (s.Stop.HasValue && s.EntryLow.HasValue && s.Stop >= s.EntryLow)
            {
                reason = $"stop ({s.Stop}) >= entry_low ({s.EntryLow})";
                return false;
            }

            // entry_low <= entry_high
            if (s.EntryLow.HasValue && s.EntryHigh.HasValue && s.EntryLow > s.EntryHigh)
            {
                reason = $"entry_low ({s.EntryLow}) > entry_high ({s.EntryHigh})";
                return false;
            }

            // entry_high < t1
            if (s.EntryHigh.HasValue && s.T1.HasValue && s.EntryHigh >= s.T1)
            {
                reason = $"entry_high ({s.EntryHigh}) >= t1 ({s.T1})";
                return false;
            }

            // t1 < t2
            if (s.T1.HasValue && s.T2.HasValue && s.T1 >= s.T2)
            {
                reason = $"t1 ({s.T1}) >= t2 ({s.T2})";
                return false;
            }

            // t2 < runner
            if (s.T2.HasValue && s.Runner.HasValue && s.T2 >= s.Runner)
            {
                reason = $"t2 ({s.T2}) >= runner ({s.Runner})";
                return false;
            }

            // entry_high < runner (catches the reported case: runner below entry)
            if (s.EntryHigh.HasValue && s.Runner.HasValue && s.EntryHigh >= s.Runner)
            {
                reason = $"entry_high ({s.EntryHigh}) >= runner ({s.Runner})";
                return false;
            }

            // stop < t1 (catches t1 == stop, zero-reward target)
            if (s.Stop.HasValue && s.T1.HasValue && s.Stop >= s.T1)
            {
                reason = $"stop ({s.Stop}) >= t1 ({s.T1}) — zero or negative reward";
                return false;
            }
        }
        else // short
        {
            // stop > entry_high
            if (s.Stop.HasValue && s.EntryHigh.HasValue && s.Stop <= s.EntryHigh)
            {
                reason = $"stop ({s.Stop}) <= entry_high ({s.EntryHigh})";
                return false;
            }

            // entry_high >= entry_low
            if (s.EntryHigh.HasValue && s.EntryLow.HasValue && s.EntryHigh < s.EntryLow)
            {
                reason = $"entry_high ({s.EntryHigh}) < entry_low ({s.EntryLow})";
                return false;
            }

            // entry_low > t1
            if (s.EntryLow.HasValue && s.T1.HasValue && s.EntryLow <= s.T1)
            {
                reason = $"entry_low ({s.EntryLow}) <= t1 ({s.T1})";
                return false;
            }

            // t1 > t2
            if (s.T1.HasValue && s.T2.HasValue && s.T1 <= s.T2)
            {
                reason = $"t1 ({s.T1}) <= t2 ({s.T2})";
                return false;
            }

            // t2 > runner
            if (s.T2.HasValue && s.Runner.HasValue && s.T2 <= s.Runner)
            {
                reason = $"t2 ({s.T2}) <= runner ({s.Runner})";
                return false;
            }

            // entry_low > runner (catches runner above entry on shorts)
            if (s.EntryLow.HasValue && s.Runner.HasValue && s.EntryLow <= s.Runner)
            {
                reason = $"entry_low ({s.EntryLow}) <= runner ({s.Runner})";
                return false;
            }

            // stop > t1 (catches t1 == stop, zero-reward target)
            if (s.Stop.HasValue && s.T1.HasValue && s.Stop <= s.T1)
            {
                reason = $"stop ({s.Stop}) <= t1 ({s.T1}) — zero or negative reward";
                return false;
            }
        }

        return true;
    }
}