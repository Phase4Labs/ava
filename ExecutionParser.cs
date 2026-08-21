using System.Globalization;
using System.Text.RegularExpressions;
using get_assessment_no_graph;

public enum EntryType { ReclaimHold, BreakHold, FadePop, VwapReclaim, OverextensionFade }

public sealed record ParsedScenario(
    int Rank,
    string Direction,           // "long"|"short"
    decimal? EntryLow,
    decimal? EntryHigh,
    EntryType EntryType,
    decimal? Stop,
    decimal? T1,
    decimal? T2,
    decimal? Runner,
    decimal? ScenarioProb,
    decimal? SuccessProb,
    string RawEntryText,
    string? Grade = null,
    string? GradeRationale = null
);

public static class ExecutionCardParser
{
    private static readonly Regex ScenarioHeader = new(@"(?m)^\s*(\d+)\)\s*(LONG|SHORT)\s*$", RegexOptions.Compiled);
    private static readonly Regex EntryLine = new(@"(?m)^\s*-\s*Entry:\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex StopLine = new(@"(?m)^\s*-\s*Stop:\s*([0-9]+(\.[0-9]+)?)\s*$", RegexOptions.Compiled);
    private static readonly Regex TargetsLine = new(@"(?m)^\s*-\s*Targets:\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex ScenarioProbLine = new(@"(?m)^\s*-\s*Scenario probability:\s*([0-9]+(\.[0-9]+)?)\s*$", RegexOptions.Compiled);
    private static readonly Regex SuccessProbLine = new(@"(?m)^\s*-\s*Success probability:\s*([0-9]+(\.[0-9]+)?)\s*$", RegexOptions.Compiled);

    private static decimal? ParseDec(string s)
        => decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;

    public static List<ParsedScenario> ParseTop3(string cardText)
    {
        var matches = ScenarioHeader.Matches(cardText);
        var scenarios = new List<ParsedScenario>();

        for (int i = 0; i < matches.Count && scenarios.Count < 3; i++)
        {
            var start = matches[i].Index;
            var end = (i + 1 < matches.Count) ? matches[i + 1].Index : cardText.Length;
            var block = cardText.Substring(start, end - start);

            int rank = int.Parse(matches[i].Groups[1].Value, CultureInfo.InvariantCulture);
            var dir = matches[i].Groups[2].Value.ToLowerInvariant();

            var entryText = EntryLine.Match(block).Groups.Count > 1 ? EntryLine.Match(block).Groups[1].Value.Trim() : "";
            var (low, high) = ParseEntryRange(entryText);

            var stop = StopLine.Match(block).Success ? ParseDec(StopLine.Match(block).Groups[1].Value) : null;
            var (t1, t2, runner) = ParseTargets(TargetsLine.Match(block).Success ? TargetsLine.Match(block).Groups[1].Value : "");

            var sp = ScenarioProbLine.Match(block).Success ? ParseDec(ScenarioProbLine.Match(block).Groups[1].Value) : null;
            var suc = SuccessProbLine.Match(block).Success ? ParseDec(SuccessProbLine.Match(block).Groups[1].Value) : null;

            var type = ClassifyEntryType(entryText);

            scenarios.Add(new ParsedScenario(rank, dir, low, high, type, stop, t1, t2, runner, sp, suc, entryText));
        }

        return scenarios;
    }

    private static (decimal? low, decimal? high) ParseEntryRange(string entryText)
    {
        // Expect patterns like "13.07–13.09 (...)" OR "12.99 (....)"
        var main = entryText.Split('(')[0].Trim();
        main = main.Replace("–", "-").Replace("—", "-");

        var parts = main.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1) return (ParseDec(parts[0]), ParseDec(parts[0]));
        if (parts.Length >= 2) return (ParseDec(parts[0]), ParseDec(parts[1]));
        return (null, null);
    }

    private static (decimal? t1, decimal? t2, decimal? runner) ParseTargets(string s)
    {
        // "T1 13.03 | T2 13.00 | Runner 12.92"
        decimal? t1 = null, t2 = null, r = null;
        foreach (var chunk in s.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var m1 = Regex.Match(chunk, @"T1\s*([0-9]+(\.[0-9]+)?)", RegexOptions.IgnoreCase);
            var m2 = Regex.Match(chunk, @"T2\s*([0-9]+(\.[0-9]+)?)", RegexOptions.IgnoreCase);
            var mr = Regex.Match(chunk, @"Runner\s*([0-9]+(\.[0-9]+)?)", RegexOptions.IgnoreCase);

            if (m1.Success) t1 = ParseDec(m1.Groups[1].Value);
            if (m2.Success) t2 = ParseDec(m2.Groups[1].Value);
            if (mr.Success) r = ParseDec(mr.Groups[1].Value);
        }
        return (t1, t2, r);
    }

    private static EntryType ClassifyEntryType(string entryText)
    {
        var t = entryText.ToLowerInvariant();
        if (t.Contains("vwap reclaim") || t.Contains("vwap_reclaim") ||
            (t.Contains("vwap") && (t.Contains("reclaim") || t.Contains("hold"))))
            return EntryType.VwapReclaim;
        if (t.Contains("reclaim") || t.Contains("reclaim/hold") || (t.Contains("hold") && t.Contains("reclaim")))
            return EntryType.ReclaimHold;
        if (t.Contains("break-and-hold") || (t.Contains("break") && t.Contains("hold")))
            return EntryType.BreakHold;
        if (t.Contains("fade") || t.Contains("fade pops") || t.Contains("fade pushes") || t.Contains("rejection"))
            return EntryType.FadePop;

        // fallback heuristic
        if (t.Contains("break")) return EntryType.BreakHold;
        if (t.Contains("overextension")) return EntryType.OverextensionFade;
        if (t.Contains("fade")) return EntryType.FadePop;
        return EntryType.ReclaimHold;
    }
    /*public static class ExecutionCardValidator
    {
        private static readonly HashSet<string> ValidVerdicts = new(StringComparer.OrdinalIgnoreCase)
            { "TRADE", "NO_TRADE" };

        private static readonly HashSet<string> ValidDirections = new(StringComparer.OrdinalIgnoreCase)
            { "long", "short" };

        private static readonly HashSet<string> ValidEntryTypes = new(StringComparer.OrdinalIgnoreCase)
            { "reclaim_hold", "break_hold", "fade_pop", "overextension_fade" };

        public static (bool ok, string status, ExecutionCardJsonV1 normalized) ValidateAndNormalize(ExecutionCardJsonV1? card)
        {
            var norm = card ?? new ExecutionCardJsonV1 { Verdict = "NO_TRADE" };

            // schema_version: if missing/invalid, force to 1
            if (norm.SchemaVersion != 1) norm.SchemaVersion = 1;

            // verdict
            if (!ValidVerdicts.Contains(norm.Verdict ?? ""))
                norm.Verdict = "NO_TRADE";

            // If verdict is NO_TRADE => scenarios must be empty
            if (string.Equals(norm.Verdict, "NO_TRADE", StringComparison.OrdinalIgnoreCase))
            {
                norm.Scenarios = new List<ExecutionScenarioJsonV1>();
                return (true, "valid", norm);
            }

            // verdict TRADE => validate scenarios
            var cleaned = new List<ExecutionScenarioJsonV1>();
            foreach (var s in norm.Scenarios ?? new())
            {
                // rank
                if (s.ScenarioRank < 1 || s.ScenarioRank > 3) continue;

                // direction/entry_type
                if (!ValidDirections.Contains(s.Direction ?? "")) continue;
                if (!ValidEntryTypes.Contains(s.EntryType ?? "")) continue;

                // clamp probs
                s.ScenarioProb = Clamp01(s.ScenarioProb);
                s.SuccessProb = Clamp01(s.SuccessProb);

                // normalize strings
                s.Direction = s.Direction.ToLowerInvariant();
                s.EntryType = s.EntryType.ToLowerInvariant();

                cleaned.Add(s);
            }

            // Enforce unique ranks and take top 3 by rank
            norm.Scenarios = cleaned
                .GroupBy(x => x.ScenarioRank)
                .Select(g => g.First())
                .OrderBy(x => x.ScenarioRank)
                .Take(3)
                .ToList();

            // If after cleaning we have no scenarios => treat as NO_TRADE (per your rule)
            if (norm.Scenarios.Count == 0)
            {
                norm.Verdict = "NO_TRADE";
                return (true, "valid", norm);
            }

            return (true, "valid", norm);
        }

        private static decimal? Clamp01(decimal? v)
        {
            if (v is null) return null;
            if (v < 0m) return 0m;
            if (v > 1m) return 1m;
            return v;
        }
    }*/
}