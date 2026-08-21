using get_assessment_no_graph;
/*public static class ExecutionCardValidator
{
    public static (bool ok, string reason) Validate(ExecutionCardJsonV1 card)
    {
        if (card.SchemaVersion != 1) return (false, "schema_version != 1");
        if (card.Verdict is not ("TRADE" or "NO_TRADE")) return (false, "bad verdict");

        if (card.Verdict == "NO_TRADE")
        {
            if (card.Scenarios.Count != 0) return (false, "NO_TRADE must have 0 scenarios");
            return (true, "");
        }

        if (card.Scenarios.Count is < 1 or > 3) return (false, "TRADE must have 1..3 scenarios");

        // ranks unique and 1..3
        var ranks = card.Scenarios.Select(s => s.ScenarioRank).ToList();
        if (ranks.Any(r => r < 1 || r > 3)) return (false, "scenario_rank out of range");
        if (ranks.Distinct().Count() != ranks.Count) return (false, "duplicate scenario_rank");

        foreach (var s in card.Scenarios)
        {
            if (s.Direction is not ("long" or "short")) return (false, $"bad direction rank={s.ScenarioRank}");
            if (s.EntryType is not ("reclaim_hold" or "break_hold" or "fade_pop" or "vwap_reclaim")) return (false, $"bad entry_type rank={s.ScenarioRank}");
            if (s.ScenarioProb is null or < 0m or > 1m) return (false, $"bad scenario_prob rank={s.ScenarioRank}");
            if (s.SuccessProb is null or < 0m or > 1m) return (false, $"bad success_prob rank={s.ScenarioRank}");
            if (s.StopPrice is null || s.T1 is null) return (false, $"missing stop/t1 rank={s.ScenarioRank}");

            // entry range sanity
            if (s.EntryLow is null && s.EntryHigh is null) return (false, $"missing entry range rank={s.ScenarioRank}");
            if (s.EntryLow is not null && s.EntryHigh is not null && s.EntryLow > s.EntryHigh)
                return (false, $"entry_low > entry_high rank={s.ScenarioRank}");
        }

        return (true, "");
    }
}
*/