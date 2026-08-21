namespace get_assessment_no_graph;

// ── Configuration ──────────────────────────────────────────────────────────────

public static class ScannerConfig
{
    public const int RollingVwapWindow = 500;  // ticks
    public const int CooldownMinutes   = 5;    // per-symbol per-side cooldown after trigger

    // ── Trigger sensitivity — change this at runtime to tune selectivity ────────
    // 0.90 = only fire on very strong setups (default)
    // 0.80 = fire on good setups
    // 0.75 = fire on developing setups (more noise, more opportunities)
    public static double TriggerPct { get; set; } = 0.90;

    // ── Minimum card grade required for signal emission ────────────────────────
    // Cards are graded A–F by the LLM. Only scenarios whose grade >= MinGrade
    // will be allowed to emit an entry signal via TriggerEngine.
    // Order: A > B > C > D > F  (higher index = lower grade)
    private static readonly string[] GradeOrder = { "A", "B", "C", "D", "F" };
    public static string MinGrade { get; set; } = "B";  // default: B or better

    /// <summary>Returns true if <paramref name="grade"/> meets the minimum threshold.</summary>
    public static bool GradePasses(string? grade)
    {
        if (string.IsNullOrWhiteSpace(grade)) return false;  // ungraded cards never pass
        var g = grade.Trim().ToUpperInvariant();
        int scoreIdx  = Array.IndexOf(GradeOrder, g);
        int minIdx    = Array.IndexOf(GradeOrder, MinGrade.Trim().ToUpperInvariant());
        if (scoreIdx < 0 || minIdx < 0) return false;       // unknown grade string
        return scoreIdx <= minIdx;  // lower index = higher grade
    }

    // ── Component weights ───────────────────────────────────────────────────────
    public const int WtVwapSide     = 15;
    public const int WtAbsorption   = 20;  // optional — rare tape condition
    public const int WtDominantSide = 12;
    public const int WtNbbo         = 12;  // optional — requires quote subscription
    public const int WtStructure    = 15;
    public const int WtLiquidity    = 18;
    public const int WtMacro        =  8;  // optional — requires significant SPY/VIX move

    // Base = always-available components: VWAP(15) + Dominant(12) + Structure(15) + Liquidity(18)
    public const int MaxScoreBase = WtVwapSide + WtDominantSide + WtStructure + WtLiquidity; // 60
    public const int MaxScoreFull = MaxScoreBase + WtAbsorption + WtNbbo + WtMacro;          // 100

    /// <summary>
    /// Computes max achievable score from currently active optional components,
    /// then returns TriggerPct of that max.
    ///
    /// At TriggerPct=0.90:
    ///   all active              → max=100, threshold=90
    ///   no absorption           → max=80,  threshold=72
    ///   no macro                → max=92,  threshold=83
    ///   no absorption+macro     → max=72,  threshold=65
    ///   no nbbo                 → max=88,  threshold=79
    ///   base only               → max=60,  threshold=54
    /// </summary>
    public static int ComputeThreshold(bool nbboAvailable, bool absorptionSeen, bool macroSeen)
    {
        int max = MaxScoreBase;
        if (nbboAvailable)  max += WtNbbo;
        if (absorptionSeen) max += WtAbsorption;
        if (macroSeen)      max += WtMacro;
        return (int)Math.Round(max * TriggerPct);
    }

    /// <summary>Human-readable status for display.</summary>
    public static string ThresholdDescription(bool nbboAvailable, bool absorptionSeen, bool macroSeen)
    {
        int max = MaxScoreBase;
        var absent = new List<string>();
        if (nbboAvailable)  max += WtNbbo;       else absent.Add("nbbo");
        if (absorptionSeen) max += WtAbsorption;  else absent.Add("absrp");
        if (macroSeen)      max += WtMacro;       else absent.Add("macro");
        int thr = (int)Math.Round(max * TriggerPct);
        string miss = absent.Count > 0 ? $"  absent=[{string.Join(",", absent)}]" : "";
        return $"max={max}  thr={thr}  pct={TriggerPct*100:F0}%{miss}";
    }
}

// ── Shared market context (SPY + VIX — single instance, not per symbol) ───────

public sealed class ScannerMarketContext
{
    private readonly Queue<ScannerBar>  _spyBars = new();
    private readonly Queue<decimal>     _vixVals = new();
    private const int MaxBars = 300;
    private const int MaxVix  = 500;

    public bool HasVix => _vixVals.Count > 0;

    public void AddSpyBar(ScannerBar bar)
    {
        _spyBars.Enqueue(bar);
        while (_spyBars.Count > MaxBars) _spyBars.Dequeue();
    }

    public void AddVixValue(decimal value)
    {
        _vixVals.Enqueue(value);
        while (_vixVals.Count > MaxVix) _vixVals.Dequeue();
    }

    public MacroSignal Analyze()
    {
        int score = 0;

        if (_spyBars.Count >= 5)
        {
            var arr = _spyBars.ToArray();
            var tail = arr[^5..];
            decimal first = tail[0].Close, last = tail[^1].Close;
            if (last < first * 0.998m) score--;
            if (last > first * 1.002m) score++;
        }

        if (_vixVals.Count >= 5)
        {
            var arr = _vixVals.ToArray();
            decimal first = arr[^5], last = arr[^1];
            if (last > first * 1.003m) score--;
            if (last < first * 0.997m) score++;
        }

        return score <= -1 ? MacroSignal.RiskOff
             : score >=  1 ? MacroSignal.RiskOn
             : MacroSignal.Neutral;
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// ANALYSIS MODULES
// ═══════════════════════════════════════════════════════════════════════════════

public static class TapeModule
{
    /// <summary>
    /// Classifies tick flow: absorption, dominant side, or neutral.
    /// Absorption = high volume with minimal price movement = institutional taking the other side.
    /// </summary>
    public static TapeSignal Analyze(ScannerTick[] ticks)
    {
        if (ticks.Length < 2) return TapeSignal.Neutral;

        decimal priceMove = Math.Abs(ticks[^1].Price - ticks[0].Price);
        decimal avgSize   = (decimal)ticks.Average(t => t.Size);
        decimal totalVol  = ticks.Sum(t => (decimal)t.Size);
        int bigPrints     = ticks.Count(t => t.Size >= avgSize * 2.5m);

        decimal upVol = 0m, downVol = 0m;
        for (int i = 1; i < ticks.Length; i++)
        {
            if (ticks[i].Price > ticks[i - 1].Price) upVol   += ticks[i].Size;
            if (ticks[i].Price < ticks[i - 1].Price) downVol += ticks[i].Size;
        }

        bool highVolNoMove = totalVol > avgSize * ticks.Length * 1.25m && priceMove <= 0.035m;

        if (highVolNoMove && upVol   > downVol * 1.15m) return TapeSignal.BuyingAbsorbed;
        if (highVolNoMove && downVol > upVol   * 1.15m) return TapeSignal.SellingAbsorbed;
        if (downVol > upVol   * 1.4m && bigPrints >= 4) return TapeSignal.SellerDominant;
        if (upVol   > downVol * 1.4m && bigPrints >= 4) return TapeSignal.BuyerDominant;

        return TapeSignal.Neutral;
    }
}

public static class NbboModule
{
    public static NbboSignal Analyze(ScannerQuote[] quotes)
    {
        if (quotes.Length < 5) return NbboSignal.Neutral;

        var last    = quotes[^1];
        decimal avgBid = (decimal)quotes.Average(q => q.BidSize);
        decimal avgAsk = (decimal)quotes.Average(q => q.AskSize);

        bool stackedAsk = last.AskSize > avgAsk * 1.8m && last.AskSize > last.BidSize * 1.5m;
        bool stackedBid = last.BidSize > avgBid * 1.8m && last.BidSize > last.AskSize * 1.5m;

        var tail5 = quotes[^Math.Min(5, quotes.Length)..];
        bool bidRising  = tail5.Select(q => q.BidPrice).Distinct().Count() >= 3
                          && tail5[^1].BidPrice > tail5[0].BidPrice;
        bool askFalling = tail5.Select(q => q.AskPrice).Distinct().Count() >= 3
                          && tail5[^1].AskPrice < tail5[0].AskPrice;

        if (stackedAsk || askFalling) return NbboSignal.SellPressure;
        if (stackedBid || bidRising)  return NbboSignal.BuyPressure;

        return NbboSignal.Neutral;
    }
}

public static class StructureModule
{
    public static StructureSignal Analyze(ScannerBar[] bars)
    {
        if (bars.Length < 5) return StructureSignal.Neutral;

        var tail  = bars[^5..];
        var highs = tail.Select(b => b.High).ToArray();
        var lows  = tail.Select(b => b.Low).ToArray();

        bool lowerHighs  = highs[4] < highs[3] && highs[3] <= highs[2];
        bool higherLows  = lows[4]  > lows[3]  && lows[3]  >= lows[2];
        bool compression = tail.Max(b => b.High) - tail.Min(b => b.Low) < 0.12m;

        if (lowerHighs)  return StructureSignal.LowerHighs;
        if (higherLows)  return StructureSignal.HigherLows;
        if (compression) return StructureSignal.Compression;

        return StructureSignal.Neutral;
    }
}

public static class LiquidityModule
{
    public static LiquiditySignal Analyze(decimal lastPrice, decimal vwap,
                                           ScannerTick[] ticks, ScannerBar[] bars)
    {
        if (ticks.Length == 0 || bars.Length == 0) return LiquiditySignal.Neutral;

        var recentBars  = bars[^Math.Min(10, bars.Length)..];
        decimal hiRange = recentBars.Max(b => b.High);
        decimal loRange = recentBars.Min(b => b.Low);

        bool highSweepFailed   = ticks.Any(t => t.Price > hiRange) && lastPrice < hiRange;
        bool lowSweepReclaimed = ticks.Any(t => t.Price < loRange) && lastPrice > loRange;
        bool vwapReject        = ticks.Any(t => t.Price >= vwap)   && lastPrice < vwap;
        bool vwapReclaim       = ticks.Any(t => t.Price <= vwap)   && lastPrice > vwap;

        if (highSweepFailed  || vwapReject)   return LiquiditySignal.ShortTrap;
        if (lowSweepReclaimed || vwapReclaim) return LiquiditySignal.LongTrap;

        return LiquiditySignal.Neutral;
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// SCORE BREAKDOWN
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Which components fired in a given score evaluation.
/// Each bool corresponds to one scoring weight.
/// </summary>
public sealed record ScoreBreakdown(
    bool VwapSide,      // 15 pts
    bool Absorption,    // 20 pts
    bool DominantSide,  // 12 pts
    bool Nbbo,          // 12 pts
    bool Structure,     // 15 pts
    bool Liquidity,     // 18 pts
    bool Macro,         //  8 pts
    int  Total)
{
    /// <summary>Short label showing which components fired, e.g. "VWAP+TAPE+LIQ"</summary>
    public string ActiveFlags()
    {
        var parts = new List<string>(7);
        if (VwapSide)    parts.Add("VWAP");
        if (Absorption)  parts.Add("ABSRP");
        if (DominantSide)parts.Add("DOMIN");
        if (Nbbo)        parts.Add("NBBO");
        if (Structure)   parts.Add("STRUCT");
        if (Liquidity)   parts.Add("LIQ");
        if (Macro)       parts.Add("MACRO");
        return parts.Count == 0 ? "none" : string.Join("+", parts);
    }

    /// <summary>Multi-line tooltip showing each component and its contribution.</summary>
    public string ToTooltip()
    {
        return
            $"VWAP side    {(VwapSide    ? $"+{ScannerConfig.WtVwapSide,2}" : " --")}  pts\n" +
            $"Absorption   {(Absorption  ? $"+{ScannerConfig.WtAbsorption,2}" : " --")}  pts\n" +
            $"Dominant     {(DominantSide? $"+{ScannerConfig.WtDominantSide,2}" : " --")}  pts\n" +
            $"NBBO         {(Nbbo        ? $"+{ScannerConfig.WtNbbo,2}" : " --")}  pts\n" +
            $"Structure    {(Structure   ? $"+{ScannerConfig.WtStructure,2}" : " --")}  pts\n" +
            $"Liquidity    {(Liquidity   ? $"+{ScannerConfig.WtLiquidity,2}" : " --")}  pts\n" +
            $"Macro        {(Macro       ? $"+{ScannerConfig.WtMacro,2}" : " --")}  pts\n" +
            $"─────────────────\n" +
            $"TOTAL        +{Total,2}  pts";
    }
}



// ═══════════════════════════════════════════════════════════════════════════════
// SCORER
// ═══════════════════════════════════════════════════════════════════════════════

public static class ScannerScorer
{
    public static (int Score, ScoreBreakdown Breakdown) ScoreShort(
        decimal price, decimal vwap,
        TapeSignal tape, NbboSignal nbbo, StructureSignal structure,
        LiquiditySignal liquidity, MacroSignal macro)
    {
        bool bVwap    = price < vwap;
        bool bAbsorp  = tape      == TapeSignal.BuyingAbsorbed;
        bool bDomin   = tape      == TapeSignal.SellerDominant;
        bool bNbbo    = nbbo      == NbboSignal.SellPressure;
        bool bStruct  = structure == StructureSignal.LowerHighs;
        bool bLiq     = liquidity == LiquiditySignal.ShortTrap;
        bool bMacro   = macro     == MacroSignal.RiskOff;

        int s = 0;
        if (bVwap)   s += ScannerConfig.WtVwapSide;
        if (bAbsorp) s += ScannerConfig.WtAbsorption;
        if (bDomin)  s += ScannerConfig.WtDominantSide;
        if (bNbbo)   s += ScannerConfig.WtNbbo;
        if (bStruct) s += ScannerConfig.WtStructure;
        if (bLiq)    s += ScannerConfig.WtLiquidity;
        if (bMacro)  s += ScannerConfig.WtMacro;

        s = Math.Min(s, 100);
        return (s, new ScoreBreakdown(bVwap, bAbsorp, bDomin, bNbbo, bStruct, bLiq, bMacro, s));
    }

    public static (int Score, ScoreBreakdown Breakdown) ScoreLong(
        decimal price, decimal vwap,
        TapeSignal tape, NbboSignal nbbo, StructureSignal structure,
        LiquiditySignal liquidity, MacroSignal macro)
    {
        bool bVwap   = price > vwap;
        bool bAbsorp = tape      == TapeSignal.SellingAbsorbed;
        bool bDomin  = tape      == TapeSignal.BuyerDominant;
        bool bNbbo   = nbbo      == NbboSignal.BuyPressure;
        bool bStruct = structure == StructureSignal.HigherLows;
        bool bLiq    = liquidity == LiquiditySignal.LongTrap;
        bool bMacro  = macro     == MacroSignal.RiskOn;

        int s = 0;
        if (bVwap)   s += ScannerConfig.WtVwapSide;
        if (bAbsorp) s += ScannerConfig.WtAbsorption;
        if (bDomin)  s += ScannerConfig.WtDominantSide;
        if (bNbbo)   s += ScannerConfig.WtNbbo;
        if (bStruct) s += ScannerConfig.WtStructure;
        if (bLiq)    s += ScannerConfig.WtLiquidity;
        if (bMacro)  s += ScannerConfig.WtMacro;

        s = Math.Min(s, 100);
        return (s, new ScoreBreakdown(bVwap, bAbsorp, bDomin, bNbbo, bStruct, bLiq, bMacro, s));
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// ENUMS
// ═══════════════════════════════════════════════════════════════════════════════

public enum TapeSignal     { Neutral, BuyerDominant, SellerDominant, BuyingAbsorbed, SellingAbsorbed }
public enum NbboSignal     { Neutral, BuyPressure, SellPressure }
public enum StructureSignal{ Neutral, LowerHighs, HigherLows, Compression }
public enum LiquiditySignal{ Neutral, ShortTrap, LongTrap }
public enum MacroSignal    { Neutral, RiskOn, RiskOff }
