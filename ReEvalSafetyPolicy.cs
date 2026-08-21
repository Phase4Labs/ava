namespace get_assessment_no_graph;

/// <summary>
/// Deterministic safety boundary for LLM-proposed open-position levels.
/// This policy is intentionally isolated from normal signal/card validation.
/// </summary>
public static class ReEvalSafetyPolicy
{
    public sealed record Levels(
        decimal? Stop,
        decimal? T1,
        decimal? T2,
        decimal? Runner);

    public sealed record Input(
        string Direction,
        decimal EntryPrice,
        decimal MarketPrice,
        bool T1Hit,
        bool T2Hit,
        Levels WorkingLevels);

    public sealed record Proposal(
        decimal? Stop,
        string? StopType,
        decimal? T1,
        decimal? T2,
        decimal? Runner,
        bool RunnerJustified);

    public sealed record Decision(
        bool IsValid,
        string Reason,
        Levels NormalizedLevels);

    public static Decision Validate(Input input, Proposal proposal)
    {
        var direction = (input.Direction ?? "").Trim().ToLowerInvariant();
        var normalizedRunner = proposal.RunnerJustified ? proposal.Runner : null;
        var normalized = new Levels(proposal.Stop, proposal.T1, proposal.T2, normalizedRunner);

        Decision Reject(string reason) => new(false, reason, normalized);

        if (direction != "long" && direction != "short")
            return Reject($"unsupported direction '{input.Direction}'");

        if (input.EntryPrice <= 0m)
            return Reject("entry price is missing or non-positive");

        if (input.MarketPrice <= 0m)
            return Reject("current executable market reference is missing or non-positive");

        if (!proposal.Stop.HasValue)
            return Reject("an open position must retain a protective stop");

        if (!proposal.T1.HasValue)
            return Reject("an open position must retain T1");

        var stop = proposal.Stop.Value;
        var t1 = proposal.T1.Value;
        var tick = MinimumTick(input.MarketPrice);
        var workingStop = input.WorkingLevels.Stop;

        if (!workingStop.HasValue || workingStop.Value <= 0m)
            return Reject("current working stop is missing or non-positive");

        if (direction == "long")
        {
            // A sell stop must remain below the executable market. Requiring one
            // minimum tick prevents a stop equal to the current bid/price.
            if (stop > input.MarketPrice - tick)
                return Reject($"LONG stop {stop} is not below market {input.MarketPrice} by at least {tick}");

            // Tightening a long stop moves it upward. Anything lower widens risk.
            if (stop < workingStop.Value)
                return Reject($"LONG stop {stop} widens current stop {workingStop.Value}");

            if (!input.T1Hit && stop >= input.EntryPrice)
                return Reject($"LONG stop {stop} cannot be at/above entry {input.EntryPrice} before T1 is hit");

            if (!input.T1Hit && t1 < input.MarketPrice + tick)
                return Reject($"LONG unhit T1 {t1} is not above market {input.MarketPrice}");

            if (t1 <= input.EntryPrice)
                return Reject($"LONG T1 {t1} is not above entry {input.EntryPrice}");

            if (proposal.T2.HasValue && proposal.T2.Value <= t1)
                return Reject($"LONG T2 {proposal.T2} is not above T1 {t1}");

            if (input.T1Hit && !input.T2Hit && proposal.T2.HasValue &&
                proposal.T2.Value < input.MarketPrice + tick)
                return Reject($"LONG unhit T2 {proposal.T2} is not above market {input.MarketPrice}");

            if (normalizedRunner.HasValue &&
                (!proposal.T2.HasValue || normalizedRunner.Value <= proposal.T2.Value))
                return Reject($"LONG runner {normalizedRunner} is not beyond T2 {proposal.T2}");
        }
        else
        {
            // A buy stop for a short must remain above the executable market.
            if (stop < input.MarketPrice + tick)
                return Reject($"SHORT stop {stop} is not above market {input.MarketPrice} by at least {tick}");

            // Tightening a short stop moves it downward. Anything higher widens risk.
            if (stop > workingStop.Value)
                return Reject($"SHORT stop {stop} widens current stop {workingStop.Value}");

            if (!input.T1Hit && stop <= input.EntryPrice)
                return Reject($"SHORT stop {stop} cannot be at/below entry {input.EntryPrice} before T1 is hit");

            if (!input.T1Hit && t1 > input.MarketPrice - tick)
                return Reject($"SHORT unhit T1 {t1} is not below market {input.MarketPrice}");

            if (t1 >= input.EntryPrice)
                return Reject($"SHORT T1 {t1} is not below entry {input.EntryPrice}");

            if (proposal.T2.HasValue && proposal.T2.Value >= t1)
                return Reject($"SHORT T2 {proposal.T2} is not below T1 {t1}");

            if (input.T1Hit && !input.T2Hit && proposal.T2.HasValue &&
                proposal.T2.Value > input.MarketPrice - tick)
                return Reject($"SHORT unhit T2 {proposal.T2} is not below market {input.MarketPrice}");

            if (normalizedRunner.HasValue &&
                (!proposal.T2.HasValue || normalizedRunner.Value >= proposal.T2.Value))
                return Reject($"SHORT runner {normalizedRunner} is not beyond T2 {proposal.T2}");
        }

        // Already-achieved levels are historical facts and must not be rewritten.
        if (input.T1Hit && input.WorkingLevels.T1.HasValue && t1 != input.WorkingLevels.T1.Value)
            return Reject($"T1 was already hit and cannot change from {input.WorkingLevels.T1} to {t1}");

        if (input.T2Hit && input.WorkingLevels.T2 != proposal.T2)
            return Reject($"T2 was already hit and cannot change from {input.WorkingLevels.T2} to {proposal.T2}");

        var stopInProtectionTerritory = direction == "long"
            ? stop >= input.EntryPrice
            : stop <= input.EntryPrice;

        if (string.Equals(proposal.StopType, "profit_protection", StringComparison.OrdinalIgnoreCase))
        {
            if (!input.T1Hit)
                return Reject("profit-protection stop is not allowed before T1 is hit");
            if (!stopInProtectionTerritory)
                return Reject("profit-protection stop is not at/through breakeven");
        }
        else if (stopInProtectionTerritory)
        {
            return Reject("a stop at/through breakeven must be classified as profit_protection");
        }

        if (normalizedRunner.HasValue)
        {
            if (!input.T1Hit)
                return Reject("runner is not allowed before T1 is hit");
            if (!stopInProtectionTerritory)
                return Reject("runner requires a profit-protection stop");
        }

        if (proposal.RunnerJustified && !normalizedRunner.HasValue)
            return Reject("runner_justified=true requires a runner price");

        return new Decision(true, "ok", normalized);
    }

    private static decimal MinimumTick(decimal price) => price >= 1m ? 0.01m : 0.0001m;
}
