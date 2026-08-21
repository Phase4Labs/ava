namespace get_assessment_no_graph;

/// <summary>
/// Computes session and composite volume profiles from OHLCV bar data.
/// Uses price-volume approximation: volume distributed across bar range
/// weighted toward close (60% at close bin, 40% spread across range).
/// </summary>
public static class VolumeProfile
{
    public sealed record Bar(decimal High, decimal Low, decimal Close, long Volume);

    public sealed record VolumeProfileResult(
        decimal Poc,               // Price of Control — highest volume bin
        decimal Vah,               // Value Area High  (70% of volume above POC side)
        decimal Val,               // Value Area Low   (70% of volume below POC side)
        decimal[] Hvn,             // High Volume Nodes (bins > 1.5x mean volume)
        decimal[] Lvn,             // Low Volume Nodes  (bins < 0.5x mean volume, inside VA)
        decimal ValueAreaWidth,    // VAH - VAL
        decimal BinSize,           // Bin increment used
        int Sessions               // Number of sessions used (1 = intraday, N = composite)
    );

    /// <summary>
    /// Compute VP from a set of bars. Works for both intraday and multi-day.
    /// </summary>
    public static VolumeProfileResult Compute(IReadOnlyList<Bar> bars, int sessions = 1)
    {
        if (bars.Count == 0)
            return Empty(sessions);

        var binSize = ChooseBinSize(bars);
        var profile = BuildProfile(bars, binSize);

        if (profile.Count == 0)
            return Empty(sessions);

        // POC = bin with most volume
        var poc = profile.MaxBy(kv => kv.Value).Key;

        // Value Area = 70% of total volume centered on POC
        var totalVol = profile.Values.Sum(v => (double)v);
        var target   = totalVol * 0.70;

        var sorted  = profile.Keys.OrderBy(k => k).ToList();
        var pocIdx  = sorted.IndexOf(poc);

        double accumulated = profile[poc];
        int lo = pocIdx, hi = pocIdx;

        while (accumulated < target && (lo > 0 || hi < sorted.Count - 1))
        {
            var loVol = lo > 0                   ? (double)profile.GetValueOrDefault(sorted[lo - 1]) : 0;
            var hiVol = hi < sorted.Count - 1    ? (double)profile.GetValueOrDefault(sorted[hi + 1]) : 0;

            if (loVol >= hiVol && lo > 0)
            {
                lo--;
                accumulated += loVol;
            }
            else if (hi < sorted.Count - 1)
            {
                hi++;
                accumulated += hiVol;
            }
            else break;
        }

        var val = sorted[lo];
        var vah = sorted[hi];

        // HVN: bins with volume > 1.5x mean
        var mean = totalVol / profile.Count;
        var hvn = profile
            .Where(kv => (double)kv.Value > mean * 1.5)
            .Select(kv => kv.Key)
            .OrderByDescending(p => profile[p])
            .Take(5)
            .OrderBy(p => p)
            .ToArray();

        // LVN: bins inside VA with volume < 0.5x mean (thin zones)
        var lvn = profile
            .Where(kv => kv.Key >= val && kv.Key <= vah && (double)kv.Value < mean * 0.5)
            .Select(kv => kv.Key)
            .OrderBy(p => p)
            .ToArray();

        return new VolumeProfileResult(
            Poc:              Round(poc, binSize),
            Vah:              Round(vah, binSize),
            Val:              Round(val, binSize),
            Hvn:              hvn.Select(p => Round(p, binSize)).ToArray(),
            Lvn:              lvn.Select(p => Round(p, binSize)).ToArray(),
            ValueAreaWidth:   Math.Round(vah - val, 4),
            BinSize:          binSize,
            Sessions:         sessions
        );
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static Dictionary<decimal, long> BuildProfile(
        IReadOnlyList<Bar> bars, decimal binSize)
    {
        var profile = new Dictionary<decimal, long>();

        foreach (var bar in bars)
        {
            if (bar.Volume <= 0) continue;

            var closeBin = Bin(bar.Close, binSize);

            // 60% of volume at close bin
            var closeVol = (long)(bar.Volume * 0.60);
            Add(profile, closeBin, closeVol);

            // 40% spread uniformly across range bins
            var rangeBins = RangeBins(bar.Low, bar.High, binSize);
            if (rangeBins.Count > 0)
            {
                var perBin = (long)Math.Max(1, (bar.Volume * 0.40) / rangeBins.Count);
                foreach (var rb in rangeBins)
                    Add(profile, rb, perBin);
            }
        }

        return profile;
    }

    private static List<decimal> RangeBins(decimal low, decimal high, decimal binSize)
    {
        var bins = new List<decimal>();
        var start = Bin(low, binSize);
        var end   = Bin(high, binSize);
        for (var p = start; p <= end; p += binSize)
            bins.Add(p);
        return bins;
    }

    private static decimal Bin(decimal price, decimal binSize) =>
        Math.Floor(price / binSize) * binSize;

    private static void Add(Dictionary<decimal, long> d, decimal key, long vol)
    {
        d.TryGetValue(key, out var existing);
        d[key] = existing + vol;
    }

    private static decimal Round(decimal price, decimal binSize)
    {
        var decimals = binSize switch
        {
            <= 0.01m  => 2,
            <= 0.05m  => 2,
            <= 0.10m  => 2,
            <= 0.25m  => 2,
            _         => 1
        };
        return Math.Round(price, decimals);
    }

    /// <summary>
    /// Adaptive bin size: tighter bins for low-priced stocks, wider for high-priced.
    /// </summary>
    public static decimal ChooseBinSize(IReadOnlyList<Bar> bars)
    {
        if (bars.Count == 0) return 0.05m;

        var midPrice = bars.Average(b => (double)((b.High + b.Low) / 2m));
        return midPrice switch
        {
            < 2    => 0.02m,
            < 5    => 0.05m,
            < 10   => 0.05m,
            < 20   => 0.05m,
            < 50   => 0.10m,
            < 100  => 0.25m,
            < 200  => 0.50m,
            _      => 1.00m
        };
    }

    private static VolumeProfileResult Empty(int sessions) =>
        new(0m, 0m, 0m, Array.Empty<decimal>(), Array.Empty<decimal>(), 0m, 0.05m, sessions);
}
