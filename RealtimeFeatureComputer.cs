namespace get_assessment_no_graph;

/// <summary>
/// Incremental (per-bar) session-anchored feature computation.
///
/// This replaces the "recompute all bars" approach in live mode.
/// It is robust across restarts by seeding state from DB at startup.
/// </summary>
public sealed class RealtimeFeatureComputer
{
    private sealed class TickerState
    {
        public bool Initialized;
        public DateTime SessionOpenUtc;

        public decimal CumPV;      // cumulative typical_price * volume
        public long CumV;
        public long CumVolBars;    // bars included in CumV — for session avg baseline

        public readonly Queue<long> VolWindow5 = new(5);

        public decimal? PrevClose;
        public decimal? PrevVwap;
        public DateTime? LastTsUtc;
    }

    private readonly Dictionary<string, TickerState> _state = new(StringComparer.OrdinalIgnoreCase);

    public void SeedFromSessionBars(string ticker, DateTime sessionOpenUtc, IReadOnlyList<MinuteBarRow> sessionBarsAsc)
    {
        var st = GetOrCreate(ticker);
        st.SessionOpenUtc = sessionOpenUtc;
        st.Initialized = true;

        st.CumPV = 0m;
        st.CumV = 0;
        st.CumVolBars = 0;
        st.VolWindow5.Clear();
        st.PrevClose = null;
        st.PrevVwap = null;
        st.LastTsUtc = null;

        var ordered = sessionBarsAsc.OrderBy(b => b.TsUtc).ToList();
        foreach (var b in ordered)
        {
            // advance cumulative VWAP
            var typical = (b.H + b.L + b.C) / 3m;
            st.CumPV += typical * b.V;
            st.CumV += b.V;

            var vwap = st.CumV == 0 ? 0m : st.CumPV / st.CumV;

            st.VolWindow5.Enqueue(b.V);
            if (st.VolWindow5.Count > 5) st.VolWindow5.Dequeue();

            st.PrevClose = b.C;
            st.PrevVwap = vwap;
            st.LastTsUtc = b.TsUtc;
        }
    }

    public MinuteBarFeaturesRow ComputeNext(string ticker, MinuteBarRow bar)
    {
        var st = GetOrCreate(ticker);
        if (!st.Initialized)
            throw new InvalidOperationException($"RealtimeFeatureComputer not initialized for {ticker}. Call SeedFromSessionBars first.");

        // Enforce monotonic time: the WebSocket can replay the current bar (e.g. a mid-minute update)
        // or occasionally deliver an out-of-order bar. We must not apply the same bar twice to the
        // cumulative VWAP accumulators (CumPV / CumV), as that would permanently corrupt the session VWAP.
        // Instead we fall through to ComputeStatelessUsingCurrentState which projects a best-effort
        // feature row WITHOUT mutating state, then returns it for upsert. The next in-sequence bar
        // will resume from the last valid committed state as normal.
        if (st.LastTsUtc.HasValue && bar.TsUtc <= st.LastTsUtc.Value)
        {
            Console.WriteLine($"[RealtimeFeat] {ticker} duplicate/out-of-order bar {bar.TsUtc:o} <= last {st.LastTsUtc:o} — stateless compute, state not advanced");
            return ComputeStatelessUsingCurrentState(ticker, bar, st);
        }

        // Candle geometry
        var range = bar.H - bar.L;
        var body = Math.Abs(bar.C - bar.O);
        var upperWick = bar.H - Math.Max(bar.O, bar.C);
        var lowerWick = Math.Min(bar.O, bar.C) - bar.L;
        var bodyRatio = range == 0 ? 0m : body / range;

        // VWAP anchored to session open
        var typical = (bar.H + bar.L + bar.C) / 3m;
        st.CumPV += typical * bar.V;
        st.CumV += bar.V;
        var vwap = st.CumV == 0 ? 0m : st.CumPV / st.CumV;
        var distToVwap = bar.C - vwap;

        decimal? deltaClose = st.PrevClose.HasValue ? bar.C - st.PrevClose.Value : null;
        decimal? deltaVwap = st.PrevVwap.HasValue ? vwap - st.PrevVwap.Value : null;

        st.VolWindow5.Enqueue(bar.V);
        if (st.VolWindow5.Count > 5) st.VolWindow5.Dequeue();
        var avgVol5 = st.VolWindow5.Count == 0 ? 0m : st.VolWindow5.Average(x => (decimal)x);

        // Hybrid baseline: session avg (prior bars only) blended with 5-bar window
        // CumVolBars is incremented after this block, so it reflects prior-bar count here
        decimal sessionAvg = st.CumVolBars > 0
            ? (decimal)(st.CumV - bar.V) / st.CumVolBars
            : avgVol5;
        decimal baseline = st.CumVolBars < 10
            ? Math.Max(sessionAvg, avgVol5)
            : sessionAvg;
        var relVol = baseline <= 0 ? 0m : (decimal)bar.V / baseline;

        st.CumVolBars++;

        var above = bar.C > vwap;
        var below = bar.C < vwap;
        var crossUp = st.PrevClose.HasValue && st.PrevVwap.HasValue && (st.PrevClose.Value < st.PrevVwap.Value) && (bar.C > vwap);
        var crossDown = st.PrevClose.HasValue && st.PrevVwap.HasValue && (st.PrevClose.Value > st.PrevVwap.Value) && (bar.C < vwap);

        // commit state
        st.PrevClose = bar.C;
        st.PrevVwap = vwap;
        st.LastTsUtc = bar.TsUtc;

        return new MinuteBarFeaturesRow
        {
            Ticker = ticker.ToUpperInvariant(),
            TsUtc = bar.TsUtc,
            Vwap = vwap,
            DistToVwap = distToVwap,
            DeltaClose = deltaClose,
            DeltaVwap = deltaVwap,
            Body = body,
            Range = range,
            UpperWick = upperWick,
            LowerWick = lowerWick,
            BodyRatio = bodyRatio,
            AvgVolume5 = avgVol5,
            RelVolume = relVol,
            AboveVwap = above,
            BelowVwap = below,
            VwapCrossUp = crossUp,
            VwapCrossDown = crossDown
        };
    }

    /// <summary>
    /// Computes a feature row for a duplicate or out-of-order bar WITHOUT mutating state.
    ///
    /// VWAP projection: st.CumPV and st.CumV already include all bars up to st.LastTsUtc.
    /// Adding this bar's contribution (typical * V) gives the VWAP as if the bar were applied —
    /// a read-only projection. We do NOT write back to st.CumPV / st.CumV, so the running
    /// session VWAP remains correct for all subsequent in-sequence bars.
    ///
    /// Volume window: same principle — we snapshot the current window, add the bar, and
    /// trim to 5, but never enqueue into st.VolWindow5.
    ///
    /// DeltaClose / DeltaVwap use st.PrevClose / st.PrevVwap (the last committed bar),
    /// so they reflect the delta from the last real bar, not from this duplicate.
    /// </summary>
    private static MinuteBarFeaturesRow ComputeStatelessUsingCurrentState(string ticker, MinuteBarRow bar, TickerState st)
    {
        var range = bar.H - bar.L;
        var body = Math.Abs(bar.C - bar.O);
        var upperWick = bar.H - Math.Max(bar.O, bar.C);
        var lowerWick = Math.Min(bar.O, bar.C) - bar.L;
        var bodyRatio = range == 0 ? 0m : body / range;

        // Project VWAP: st.CumPV/CumV hold all prior bars; add this bar's contribution
        // as a local variable only — do NOT assign back to st.CumPV / st.CumV.
        var typical = (bar.H + bar.L + bar.C) / 3m;
        var cumPV = st.CumPV + typical * bar.V;
        var cumV = st.CumV + bar.V;
        var vwap = cumV == 0 ? 0m : cumPV / cumV;
        var distToVwap = bar.C - vwap;

        // Delta from last committed bar (st.PrevClose/PrevVwap), not from this duplicate
        decimal? deltaClose = st.PrevClose.HasValue ? bar.C - st.PrevClose.Value : null;
        decimal? deltaVwap = st.PrevVwap.HasValue ? vwap - st.PrevVwap.Value : null;

        // Snapshot volume window without mutating st.VolWindow5
        var vols = st.VolWindow5.ToList();
        vols.Add(bar.V);
        if (vols.Count > 5) vols = vols.Skip(vols.Count - 5).ToList();
        var avgVol5 = vols.Count == 0 ? 0m : vols.Average(x => (decimal)x);

        // Hybrid baseline — read-only projection, same logic as stateful path
        decimal sessionAvg = st.CumVolBars > 0
            ? (decimal)(st.CumV - bar.V) / st.CumVolBars
            : avgVol5;
        decimal baseline = st.CumVolBars < 10
            ? Math.Max(sessionAvg, avgVol5)
            : sessionAvg;
        var relVol = baseline <= 0 ? 0m : (decimal)bar.V / baseline;

        var above = bar.C > vwap;
        var below = bar.C < vwap;
        var crossUp = st.PrevClose.HasValue && st.PrevVwap.HasValue && (st.PrevClose.Value < st.PrevVwap.Value) && (bar.C > vwap);
        var crossDown = st.PrevClose.HasValue && st.PrevVwap.HasValue && (st.PrevClose.Value > st.PrevVwap.Value) && (bar.C < vwap);

        return new MinuteBarFeaturesRow
        {
            Ticker = ticker.ToUpperInvariant(),
            TsUtc = bar.TsUtc,
            Vwap = vwap,
            DistToVwap = distToVwap,
            DeltaClose = deltaClose,
            DeltaVwap = deltaVwap,
            Body = body,
            Range = range,
            UpperWick = upperWick,
            LowerWick = lowerWick,
            BodyRatio = bodyRatio,
            AvgVolume5 = avgVol5,
            RelVolume = relVol,
            AboveVwap = above,
            BelowVwap = below,
            VwapCrossUp = crossUp,
            VwapCrossDown = crossDown
        };
    }

    private TickerState GetOrCreate(string ticker)
    {
        if (!_state.TryGetValue(ticker, out var st))
        {
            st = new TickerState();
            _state[ticker] = st;
        }
        return st;
    }
}