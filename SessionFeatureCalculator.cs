namespace get_assessment_no_graph;

public static class SessionFeatureCalculator
{
    /// <summary>
    /// Computes per-bar features for a full session.
    ///
    /// rel_volume baseline fix:
    ///   Previously used a 5-bar rolling window, which self-normalizes within 5 bars
    ///   and gives no meaningful signal after the first few minutes.
    ///
    ///   Now uses a hybrid baseline:
    ///     sessionAvg  = cumulative session volume / bars elapsed (true session pace)
    ///     window5Avg  = rolling 5-bar average (useful early when sessionAvg is unstable)
    ///     baseline    = max(sessionAvg, window5Avg) for the first 10 bars,
    ///                   sessionAvg alone after bar 10
    ///
    ///   This means rel_volume = 3.0 genuinely means this bar traded at 3x the
    ///   session's average pace, regardless of when in the session it occurs.
    ///   AvgVolume5 is kept in the output for backwards compatibility but is no longer
    ///   the denominator.
    /// </summary>
    public static List<MinuteBarFeatures> ComputeSessionFeatures(IReadOnlyList<MinuteBarRow> sessionBarsAsc)
    {
        var bars = sessionBarsAsc
            .OrderBy(b => b.TsUtc)
            .ToList();

        decimal cumPV  = 0m;
        long    cumV   = 0;
        long    cumVolBars = 0;   // count of bars in cumulative vol — for session avg

        var volWindow5 = new Queue<long>(5);

        decimal? prevClose = null;
        decimal? prevVwap  = null;

        var output = new List<MinuteBarFeatures>(bars.Count);

        for (int i = 0; i < bars.Count; i++)
        {
            var b = bars[i];

            // ── Candle geometry ───────────────────────────────────────────
            var range     = b.H - b.L;
            var body      = Math.Abs(b.C - b.O);
            var upperWick = b.H - Math.Max(b.O, b.C);
            var lowerWick = Math.Min(b.O, b.C) - b.L;
            var bodyRatio = range == 0 ? 0m : body / range;

            // ── VWAP ──────────────────────────────────────────────────────
            var typical = (b.H + b.L + b.C) / 3m;
            cumPV += typical * b.V;
            cumV  += b.V;
            cumVolBars++;

            var vwap      = cumV == 0 ? 0m : cumPV / cumV;
            var distToVwap = b.C - vwap;

            decimal? deltaClose = prevClose.HasValue ? b.C - prevClose.Value : null;
            decimal? deltaVwap  = prevVwap.HasValue  ? vwap - prevVwap.Value : null;

            // ── Relative volume (hybrid baseline) ─────────────────────────
            // 5-bar window kept for AvgVolume5 field (backwards compat)
            volWindow5.Enqueue(b.V);
            if (volWindow5.Count > 5) volWindow5.Dequeue();

            var avgVol5 = volWindow5.Average(x => (decimal)x);

            // Session average: cumulative volume / bars elapsed
            // Use prior bars only (exclude current bar to avoid self-reference)
            decimal sessionAvg = cumVolBars > 1
                ? (decimal)(cumV - b.V) / (cumVolBars - 1)
                : avgVol5;

            // Blend: for first 10 bars use the higher of the two (more conservative = fewer false spikes)
            // After bar 10 the session average is stable enough to use alone
            decimal baseline = cumVolBars <= 10
                ? Math.Max(sessionAvg, avgVol5)
                : sessionAvg;

            var relVol = baseline <= 0 ? 0m : (decimal)b.V / baseline;

            // ── VWAP position & crosses ───────────────────────────────────
            var above     = b.C > vwap;
            var below     = b.C < vwap;
            var crossUp   = prevClose.HasValue && prevVwap.HasValue
                            && (prevClose.Value < prevVwap.Value) && (b.C > vwap);
            var crossDown = prevClose.HasValue && prevVwap.HasValue
                            && (prevClose.Value > prevVwap.Value) && (b.C < vwap);

            output.Add(new MinuteBarFeatures(
                Ticker      : b.Ticker,
                TsUtc       : b.TsUtc,

                Vwap        : vwap,
                DistToVwap  : distToVwap,
                DeltaClose  : deltaClose,
                DeltaVwap   : deltaVwap,

                Body        : body,
                Range       : range,
                UpperWick   : upperWick,
                LowerWick   : lowerWick,
                BodyRatio   : bodyRatio,

                AvgVolume5  : avgVol5,      // kept for backwards compat; not the rel_volume denominator
                RelVolume   : relVol,

                AboveVwap   : above,
                BelowVwap   : below,
                VwapCrossUp : crossUp,
                VwapCrossDown: crossDown
            ));

            prevClose = b.C;
            prevVwap  = vwap;
        }

        return output;
    }
}
