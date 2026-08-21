namespace get_assessment_no_graph;

/// <summary>
/// Isolated per-symbol state for the pre-qualification scanner.
/// Holds rolling tick tape, quotes, and minute bars independently per ticker.
/// All VWAP calculations are O(1) incremental — no per-tick list allocations.
/// </summary>
public sealed class ScannerSymbolState
{
    public string Ticker { get; }

    // ── Tick tape ──────────────────────────────────────────────────────────
    private readonly Queue<ScannerTick>  _tape   = new();
    private readonly Queue<ScannerQuote> _quotes = new();
    private readonly Queue<ScannerBar>   _bars   = new();

    private const int MaxTape   = 3_000;
    private const int MaxQuotes = 3_000;
    private const int MaxBars   = 300;

    // ── Session VWAP (from first tick of session) ──────────────────────────
    private decimal _sessionCumPV  = 0m;
    private decimal _sessionCumVol = 0m;
    public  decimal SessionVwap    { get; private set; }

    // ── Rolling VWAP (incremental, O(1)) ──────────────────────────────────
    private decimal _rollingCumPV  = 0m;
    private decimal _rollingCumVol = 0m;
    public  decimal RollingVwap    { get; private set; }

    public int TickCount  => _tape.Count;
    public int QuoteCount => _quotes.Count;

    // Throttle timestamp — quotes arriving faster than 100ms apart are dropped
    public DateTime LastQuoteTime { get; set; } = DateTime.MinValue;
    public int BarCount   => _bars.Count;

    public ScannerSymbolState(string ticker) => Ticker = ticker;

    // ── Add methods ────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds the session VWAP from historical bar data so the scanner has an
    /// accurate VWAP from the start of the session rather than just recent ticks.
    /// Uses typical price (H+L+C)/3 × Volume — same formula as SessionFeatureCalculator.
    /// Call this during InjectBar seeding before any ticks arrive.
    /// </summary>
    public void SeedVwapFromBar(ScannerBar bar)
    {
        if (bar.Volume <= 0) return;
        decimal typicalPrice = (bar.High + bar.Low + bar.Close) / 3m;
        _sessionCumPV  += typicalPrice * bar.Volume;
        _sessionCumVol += bar.Volume;
        SessionVwap     = _sessionCumVol > 0 ? _sessionCumPV / _sessionCumVol : bar.Close;
    }

    public void AddTick(ScannerTick tick)
    {
        // Session VWAP
        _sessionCumPV  += tick.Price * tick.Size;
        _sessionCumVol += tick.Size;
        SessionVwap     = _sessionCumVol > 0 ? _sessionCumPV / _sessionCumVol : tick.Price;

        // Rolling VWAP — evict oldest when over window
        _rollingCumPV  += tick.Price * tick.Size;
        _rollingCumVol += tick.Size;
        _tape.Enqueue(tick);
        if (_tape.Count > ScannerConfig.RollingVwapWindow)
        {
            var e = _tape.Dequeue();
            _rollingCumPV  -= e.Price * e.Size;
            _rollingCumVol -= e.Size;
        }
        RollingVwap = _rollingCumVol > 0 ? _rollingCumPV / _rollingCumVol : tick.Price;

        // Cap tape for other analyses
        while (_tape.Count > MaxTape) _tape.Dequeue();
    }

    public void AddQuote(ScannerQuote quote)
    {
        _quotes.Enqueue(quote);
        while (_quotes.Count > MaxQuotes) _quotes.Dequeue();
    }

    public void AddBar(ScannerBar bar)
    {
        _bars.Enqueue(bar);
        while (_bars.Count > MaxBars) _bars.Dequeue();
    }

    // ── Accessors ─────────────────────────────────────────────────────────

    public ScannerTick[]  RecentTicks (int n) => Tail(_tape.ToArray(),  n);
    public ScannerQuote[] RecentQuotes(int n) => Tail(_quotes.ToArray(), n);
    public ScannerBar[]   RecentBars  (int n) => Tail(_bars.ToArray(),   n);

    private static T[] Tail<T>(T[] arr, int n)
    {
        int start = Math.Max(0, arr.Length - n);
        return arr[start..];
    }
}

// ── Data types ─────────────────────────────────────────────────────────────────

public sealed record ScannerTick(decimal Price, int Size, DateTime Time);

public sealed record ScannerQuote(
    decimal BidPrice, int BidSize,
    decimal AskPrice, int AskSize,
    DateTime Time);

public sealed record ScannerBar(
    decimal Open, decimal High, decimal Low, decimal Close,
    decimal Volume, DateTime Time);