using get_assessment_no_graph;

public static class ExitDetectors
{
    // -------------------------
    // Stop hit (intrabar)
    // -------------------------
    public static bool IsStopHit(IReadOnlyList<BarWithFeat> bars, TraderStateRow st, out string reason)
    {
        reason = "";
        if (bars.Count < 1 || st.EffectiveStop is null) return false;

        var last = bars[^1];
        var stop = st.EffectiveStop.Value;

        if (st.Position == "long")
        {
            if (last.L <= stop)
            {
                reason = $"STOP_HIT long: L={last.L} <= stop={stop}";
                return true;
            }
        }
        else if (st.Position == "short")
        {
            if (last.H >= stop)
            {
                reason = $"STOP_HIT short: H={last.H} >= stop={stop}";
                return true;
            }
        }
        return false;
    }

    // -------------------------
    // Target hits (intrabar, sequential)
    //
    // Rules:
    //  - T1 is always the first eligible target
    //  - T2 only eligible after T1 has been hit (T1Hit == true)
    //  - Runner only eligible after T2 has been hit (T2Hit == true)
    //  - If a higher target is null, the lower one is NOT auto-promoted —
    //    instead ShouldEmitExitReminder handles the "you should exit" nudge
    //  - Position is NEVER auto-closed here; only MarkExitedAsync (TrayApp) does that
    // -------------------------
    public static bool IsTargetHit(
        IReadOnlyList<BarWithFeat> bars,
        TraderStateRow st,
        out string targetName,
        out decimal? targetPrice,
        out string reason)
    {
        targetName = "";
        targetPrice = null;
        reason = "";
        if (bars.Count < 1) return false;

        var last = bars[^1];

        if (st.Position == "long")
        {
            // T1 — always first, only if not yet hit
            if (!st.T1Hit && st.EffectiveT1 is not null && last.H >= st.EffectiveT1.Value)
            {
                targetName = "T1_HIT";
                targetPrice = st.EffectiveT1;
                reason = $"T1_HIT long: H={last.H} >= T1={st.EffectiveT1}";
                return true;
            }

            // T2 — only after T1 is confirmed hit
            if (st.T1Hit && !st.T2Hit && st.EffectiveT2 is not null && last.H >= st.EffectiveT2.Value)
            {
                targetName = "T2_HIT";
                targetPrice = st.EffectiveT2;
                reason = $"T2_HIT long: H={last.H} >= T2={st.EffectiveT2}";
                return true;
            }

            // Runner — only after T2 is confirmed hit
            if (st.T2Hit && !st.RunnerHit && st.EffectiveRunner is not null && last.H >= st.EffectiveRunner.Value)
            {
                targetName = "RUNNER_HIT";
                targetPrice = st.EffectiveRunner;
                reason = $"RUNNER_HIT long: H={last.H} >= Runner={st.EffectiveRunner}";
                return true;
            }
        }
        else if (st.Position == "short")
        {
            // T1 — always first, only if not yet hit
            if (!st.T1Hit && st.EffectiveT1 is not null && last.L <= st.EffectiveT1.Value)
            {
                targetName = "T1_HIT";
                targetPrice = st.EffectiveT1;
                reason = $"T1_HIT short: L={last.L} <= T1={st.EffectiveT1}";
                return true;
            }

            // T2 — only after T1 is confirmed hit
            if (st.T1Hit && !st.T2Hit && st.EffectiveT2 is not null && last.L <= st.EffectiveT2.Value)
            {
                targetName = "T2_HIT";
                targetPrice = st.EffectiveT2;
                reason = $"T2_HIT short: L={last.L} <= T2={st.EffectiveT2}";
                return true;
            }

            // Runner — only after T2 is confirmed hit
            if (st.T2Hit && !st.RunnerHit && st.EffectiveRunner is not null && last.L <= st.EffectiveRunner.Value)
            {
                targetName = "RUNNER_HIT";
                targetPrice = st.EffectiveRunner;
                reason = $"RUNNER_HIT short: L={last.L} <= Runner={st.EffectiveRunner}";
                return true;
            }
        }

        return false;
    }

    // -------------------------
    // Exit reminder
    //
    // Fires every bar when the trader is still in a position but has passed
    // their last defined target with no manual exit recorded.
    //
    // Cases that trigger a reminder:
    //   - T1 hit, T2 is null (no further targets defined) -> must exit
    //   - T2 hit, Runner is null (no runner defined) -> must exit
    //   - Runner hit (all targets exhausted) -> must exit
    //
    // Position is NEVER auto-closed here. This is purely a notification.
    // -------------------------
    public static bool ShouldEmitExitReminder(TraderStateRow st, out string reason)
    {
        reason = "";

        if (st.Position != "long" && st.Position != "short")
            return false;

        // Runner hit — all targets exhausted
        if (st.RunnerHit)
        {
            reason = $"EXIT_REMINDER: runner already hit, position still open ({st.Position}) — please exit";
            return true;
        }

        // T2 hit, no runner defined
        if (st.T2Hit && st.EffectiveRunner is null)
        {
            reason = $"EXIT_REMINDER: T2 already hit, no runner defined, position still open ({st.Position}) — please exit";
            return true;
        }

        // T1 hit, no T2 or runner defined
        if (st.T1Hit && st.EffectiveT2 is null && st.EffectiveRunner is null)
        {
            reason = $"EXIT_REMINDER: T1 already hit, no further targets defined, position still open ({st.Position}) — please exit";
            return true;
        }

        return false;
    }

    // -------------------------
    // Opposite scenario confirmed
    // -------------------------
    public static bool IsOppositeScenarioConfirmed(
        IReadOnlyList<BarWithFeat> bars,
        ParsedScenario oppositeScenario,
        out string reason)
    {
        reason = "";

        bool ok = oppositeScenario.EntryType switch
        {
            EntryType.ReclaimHold => ScenarioDetectors.IsReclaimHoldPresented(bars, oppositeScenario, out reason),
            EntryType.BreakHold   => ScenarioDetectors.IsBreakHoldPresented(bars, oppositeScenario, out reason),
            EntryType.FadePop     => ScenarioDetectors.IsFadePopPresented(bars, oppositeScenario, out reason),
            EntryType.VwapReclaim => ScenarioDetectors.IsVwapReclaimPresented(bars, oppositeScenario, out reason),
            _                     => (reason = "unknown entry_type", false).Item2
        };

        if (ok) reason = "OPPOSITE_" + reason;
        return ok;
    }
}
