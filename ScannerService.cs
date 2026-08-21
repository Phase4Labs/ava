using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace get_assessment_no_graph;

/// <summary>
/// Pre-qualification scanner that runs alongside the existing assessment loop.
///
/// Architecture (consolidated — no PolicyViolation):
///   - BarIngestionService owns the SINGLE stocks WebSocket (AM.* + T.* + Q.*).
///   - ScannerService receives bar, trade, and quote data exclusively via callbacks:
///       OnBarCommitted  → bar state + VWAP
///       OnTradeReceived → tape scoring
///       OnQuoteReceived → NBBO state
///   - ScannerService keeps only the VIX indices WebSocket (different Polygon cluster).
///   - Scores each ticker on every trade tick using: tape flow, NBBO, bar structure,
///     liquidity traps, and macro (SPY + VIX).
///   - When a ticker scores >= ScannerConfig.ComputeThreshold(...) (A+++ threshold) and is not
///     in cooldown, it sets forceCardNext[ticker] = true, which the existing main loop
///     picks up on the next bar to fire a full LLM assessment.
///
/// What does NOT change:
///   - ProduceCardWorker, PayloadBuilder, ReEvalWorker, TriggerEngine — all untouched.
///   - The main loop's schedule-based and event-based triggers still fire normally.
///     The scanner is additive — it adds a third trigger path (A+++ score).
///   - No DB schema changes required.
/// </summary>
public sealed class ScannerService : IAsyncDisposable
{
    private readonly string _polygonKey;
    private readonly ConcurrentDictionary<string, bool> _forceCardNext;
    private readonly ScannerDisplay _display = new();
    public  ScannerDisplay Display => _display;

    /// <summary>Current active ticker list — used by BarIngestionService for seeding.</summary>
    public IEnumerable<string> ActiveTickers => _scanStates.Keys;

    // Per-symbol scanner state (isolated tape/quotes/bars + VWAP per ticker)
    private readonly ConcurrentDictionary<string, ScannerSymbolState> _scanStates = new(StringComparer.OrdinalIgnoreCase);

    // Re-evaluation-only realtime snapshot. Keeping this separate avoids
    // changing the scanner's scoring inputs or normal signal flow.
    private readonly ConcurrentDictionary<string, ReEvalLiveMarketSnapshot> _reevalMarket =
        new(StringComparer.OrdinalIgnoreCase);

    // Shared market context
    private readonly ScannerMarketContext _market = new();

    // Cooldown tracking: (ticker, side) -> UTC time of last trigger
    private readonly ConcurrentDictionary<string, DateTime> _lastTrigger = new(StringComparer.OrdinalIgnoreCase);

    // Heartbeat: log scanner status every N seconds so we know it's alive
    private const int HeartbeatSeconds  = 30;
    private const int PeriodicLogSeconds = 60;   // log all scores once per minute
    private const int HotZoneScore       = 75;   // log immediately when score crosses this

    private DateTime _lastHeartbeat   = DateTime.MinValue;
    private DateTime _lastPeriodicLog = DateTime.MinValue;

    // Per-symbol last logged score — for hot zone crossing detection
    private readonly ConcurrentDictionary<string, (int s, int l)> _lastLoggedScore = new(StringComparer.OrdinalIgnoreCase);

    // Per-symbol last log timestamp — for 60s periodic forced log
    private readonly ConcurrentDictionary<string, DateTime> _lastLogTime = new(StringComparer.OrdinalIgnoreCase);

    // Per-symbol best score seen since last heartbeat (for status log)
    private readonly ConcurrentDictionary<string, (int shortScore, int longScore)> _peakScores = new(StringComparer.OrdinalIgnoreCase);

    // Per-symbol current scores — updated every tick, flushed by periodic log
    private readonly ConcurrentDictionary<string, (int s, int l, TapeSignal tape, NbboSignal nbbo, StructureSignal structure, LiquiditySignal liq, MacroSignal macro, decimal vwap)> _currentScores = new(StringComparer.OrdinalIgnoreCase);

    // VIX failure tracking — back off after repeated failures
    private int _vixFailCount = 0;

    // NBBO availability — detected from whether quote events are arriving
    private bool _nbboAvailable   = false;
    private int  _quoteEventCount = 0;

    // Optional component availability — flips true the first time each fires
    private bool _absorptionSeen = false;
    private bool _macroSeen      = false;

    private CancellationTokenSource? _cts;
    private Task? _vixTask;

    // Expose for diagnostics
    public int TriggerCount  { get; private set; }
    public int LlmCallCount  { get; set; }

    public ReEvalLiveMarketSnapshot? GetReEvalMarketSnapshot(string ticker)
        => _reevalMarket.TryGetValue(ticker.ToUpperInvariant(), out var snapshot)
            ? snapshot
            : null;

    // ── Constructor ────────────────────────────────────────────────────────────

    /// <param name="polygonKey">Polygon API key (used only for VIX indices WS)</param>
    /// <param name="forceCardNext">Shared dict from main Program — scanner writes true to trigger LLM</param>
    /// <param name="initialTickers">Initial active ticker list</param>
    public ScannerService(
        string polygonKey,
        ConcurrentDictionary<string, bool> forceCardNext,
        IEnumerable<string> initialTickers)
    {
        _polygonKey    = polygonKey;
        _forceCardNext = forceCardNext;

        foreach (var t in initialTickers.Select(x => x.ToUpperInvariant()))
        {
            _scanStates.TryAdd(t, new ScannerSymbolState(t));
            _display.AddTicker(t);
        }
    }

    /// <summary>
    /// Injects a historical bar from the DB into ScannerSymbolState.
    /// Called by BarIngestionService.SeedScannerStateAsync on startup so the
    /// scanner has session bar history without waiting for new AM.* events.
    /// </summary>
    public void InjectBar(string ticker, MinuteBarRow bar)
    {
        ticker = ticker.ToUpperInvariant();
        if (ticker == "SPY")
        {
            _market.AddSpyBar(new ScannerBar(
                Open: bar.O, High: bar.H, Low: bar.L, Close: bar.C,
                Volume: bar.V, Time: bar.TsUtc));
            return;
        }
        if (!_scanStates.TryGetValue(ticker, out var state)) return;
        var scanBar = new ScannerBar(
            Open: bar.O, High: bar.H, Low: bar.L, Close: bar.C,
            Volume: bar.V, Time: bar.TsUtc);
        state.SeedVwapFromBar(scanBar);
        state.AddBar(scanBar);
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Change the trigger sensitivity at runtime.
    /// pct = 0.90 (default, high conviction), 0.80 (good setups), 0.75 (developing setups).
    /// </summary>
    public void SetTriggerPct(double pct)
    {
        pct = Math.Clamp(pct, 0.50, 1.0);
        ScannerConfig.TriggerPct = pct;
        int threshold = ScannerConfig.ComputeThreshold(_nbboAvailable, _absorptionSeen, _macroSeen);
        _display.Log($"Trigger sensitivity changed → {pct*100:F0}%  new threshold={threshold}");
    }

    // ── BarIngestionService wiring ─────────────────────────────────────────────

    private BarIngestionService? _barIngest;

    /// <summary>
    /// Wire in the BarIngestionService so the scanner receives all market data
    /// (bars, trades, quotes) via callbacks instead of its own stocks WebSocket.
    /// Call once after both services are started; safe to call again on WS restart.
    /// </summary>
    public void SetBarIngestionService(BarIngestionService ingest)
    {
        // Unsubscribe from old instance if any
        if (_barIngest != null)
        {
            _barIngest.OnBarCommitted  -= OnBarCommittedHandler;
            _barIngest.OnTradeReceived -= OnTradeReceivedHandler;
            _barIngest.OnQuoteReceived -= OnQuoteReceivedHandler;
        }

        _barIngest = ingest;
        ingest.OnBarCommitted  += OnBarCommittedHandler;
        ingest.OnTradeReceived += OnTradeReceivedHandler;
        ingest.OnQuoteReceived += OnQuoteReceivedHandler;
    }

    // ── Callback handlers (replacing the stocks WS event dispatchers) ──────────

    private void OnBarCommittedHandler(string ticker, MinuteBarRow bar, MinuteBarFeaturesRow feat)
    {
        ticker = ticker.ToUpperInvariant();

        if (ticker == "SPY")
        {
            _market.AddSpyBar(new ScannerBar(
                Open: bar.O, High: bar.H, Low: bar.L, Close: bar.C,
                Volume: bar.V, Time: bar.TsUtc));
            return;
        }

        if (!_scanStates.TryGetValue(ticker, out var state)) return;

        state.AddBar(new ScannerBar(
            Open: bar.O, High: bar.H, Low: bar.L, Close: bar.C,
            Volume: bar.V, Time: bar.TsUtc));

        Console.WriteLine($"[scanner] OnBarCommitted {ticker} barCount={state.BarCount} ts={bar.TsUtc:HH:mm}");

        if (state.BarCount == 8)
        {
            int dbBars = _barIngest?.DbBarCount.GetValueOrDefault(ticker, 0) ?? 0;
            _display.Log($"{ticker} bars ready — scoring active (mem={state.BarCount} db={dbBars})");
            _display.UpdateRow(ticker, 0, 0,
                TapeSignal.Neutral, NbboSignal.Neutral, StructureSignal.Neutral,
                LiquiditySignal.Neutral, MacroSignal.Neutral,
                isHot: false, isWarming: false);
        }
    }

    private void OnTradeReceivedHandler(string ticker, JsonElement item)
    {
        if (!_scanStates.TryGetValue(ticker, out var state)) return;
        HandleTrade(item, state);
    }

    private void OnQuoteReceivedHandler(string ticker, JsonElement item)
    {
        if (!_scanStates.TryGetValue(ticker, out var state)) return;
        HandleQuote(item, state);
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    public Task StartAsync()
    {
        _cts     = new CancellationTokenSource();
        _vixTask = Task.Run(() => RunVixLoopAsync(_cts.Token));

        // Launch the WinForms scanner window on its own STA thread
        _display.Launch();

        _display.Log($"Started for {_scanStates.Count} tickers");
        return Task.CompletedTask;
    }

    /// <summary>Called by SymbolsService.OnChanged to keep scanner in sync with active list.</summary>
    public void AddTicker(string ticker)
    {
        ticker = ticker.ToUpperInvariant();
        _scanStates.TryAdd(ticker, new ScannerSymbolState(ticker));
        _display.AddTicker(ticker);
        _display.Log($"Added: {ticker}");
    }

    public void RemoveTicker(string ticker)
    {
        ticker = ticker.ToUpperInvariant();
        _scanStates.TryRemove(ticker, out _);
        _display.RemoveTicker(ticker);
        _display.Log($"Removed: {ticker}");
    }

    // ── VIX WebSocket loop (indices cluster — separate from stocks) ────────────

    private async Task RunVixLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndRunVixAsync(ct);
                _vixFailCount = 0;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _vixFailCount++;

                int delaySec = _vixFailCount <= 3 ? 30 : 300;
                if (_vixFailCount <= 3)
                    _display.Log($"VIX unavailable — SPY-only macro, retry {delaySec}s - exception: {ex.Message}");
                else if (_vixFailCount == 4)
                    _display.Log("VIX unavailable — backing off to 5min retries (SPY-only)");

                await Task.Delay(delaySec * 1_000, ct);
            }
        }
    }

    private async Task ConnectAndRunVixAsync(CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri("wss://socket.polygon.io/indices"), ct);

        await WsSendAsync(ws, $"{{\"action\":\"auth\",\"params\":\"{_polygonKey}\"}}", ct);
        await WsSendAsync(ws, "{\"action\":\"subscribe\",\"params\":\"V.I:VIX\"}", ct);

        if (_vixFailCount == 0)
            _display.Log("VIX stream connected");

        var buffer = new byte[1024 * 16];
        var sb     = new StringBuilder();

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            sb.Clear();
            WebSocketReceiveResult res;
            do
            {
                res = await ws.ReceiveAsync(buffer, ct);
                if (res.MessageType == WebSocketMessageType.Close) return;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, res.Count));
            } while (!res.EndOfMessage);

            try
            {
                using var doc = JsonDocument.Parse(sb.ToString());
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.TryGetProperty("ev", out var e) && e.GetString() == "V" &&
                        item.TryGetProperty("val", out var v) && v.ValueKind == JsonValueKind.Number)
                        _market.AddVixValue(v.GetDecimal());
                }
            }
            catch { /* malformed frame */ }
        }
    }

    // ── Trade / Quote event handlers ───────────────────────────────────────────

    private void HandleTrade(JsonElement item, ScannerSymbolState state)
    {
        if (!item.TryGetProperty("p", out var p) || !item.TryGetProperty("s", out var s)) return;
        if (p.ValueKind != JsonValueKind.Number || s.ValueKind != JsonValueKind.Number) return;

        var tick = new ScannerTick(
            Price: p.GetDecimal(),
            Size:  s.GetInt32(),
            Time:  item.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.Number
                   ? DateTimeOffset.FromUnixTimeMilliseconds(t.GetInt64()).UtcDateTime
                   : DateTime.UtcNow
        );

        state.AddTick(tick);

        _reevalMarket.AddOrUpdate(
            state.Ticker,
            _ => new ReEvalLiveMarketSnapshot(tick.Price, tick.Time, null, null, null),
            (_, previous) => previous with
            {
                LastTrade = tick.Price,
                LastTradeAtUtc = tick.Time
            });

        if (state.TickCount == 100)
        {
            _display.Log($"{state.Ticker} tape ready — scoring active");
            _display.UpdateRow(state.Ticker, 0, 0,
                TapeSignal.Neutral, NbboSignal.Neutral, StructureSignal.Neutral,
                LiquiditySignal.Neutral, MacroSignal.Neutral, isHot: false, isWarming: false);
        }

        EvaluateTicker(tick.Price, state);
    }

    private void HandleQuote(JsonElement item, ScannerSymbolState state)
    {
        // Throttle to max 10 quotes/second per ticker.
        // NBBO signals (stacked size, rising bid, falling ask) are structural
        // conditions that persist for seconds — 100ms resolution is sufficient.
        var now = DateTime.UtcNow;
        if ((now - state.LastQuoteTime).TotalMilliseconds < 100) return;
        state.LastQuoteTime = now;

        var t = item.TryGetProperty("t", out var tEl) && tEl.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeMilliseconds(tEl.GetInt64()).UtcDateTime
                : now;

        var quote = new ScannerQuote(
            BidPrice: GetDecimalOrZero(item, "bp"),
            BidSize:  GetIntOrZero(item, "bs"),
            AskPrice: GetDecimalOrZero(item, "ap"),
            AskSize:  GetIntOrZero(item, "as"),
            Time:     t
        );
        state.AddQuote(quote);

        _reevalMarket.AddOrUpdate(
            state.Ticker,
            _ => new ReEvalLiveMarketSnapshot(
                null, null,
                quote.BidPrice > 0m ? quote.BidPrice : null,
                quote.AskPrice > 0m ? quote.AskPrice : null,
                quote.Time),
            (_, previous) => previous with
            {
                Bid = quote.BidPrice > 0m ? quote.BidPrice : previous.Bid,
                Ask = quote.AskPrice > 0m ? quote.AskPrice : previous.Ask,
                QuoteAtUtc = quote.Time
            });

        _quoteEventCount++;
        if (!_nbboAvailable && _quoteEventCount >= 10)
        {
            _nbboAvailable = true;
            int threshold = ScannerConfig.ComputeThreshold(true, _absorptionSeen, _macroSeen);
            _display.Log($"NBBO confirmed — {ScannerConfig.ThresholdDescription(true, _absorptionSeen, _macroSeen)}");
        }
    }

    // ── Evaluation ─────────────────────────────────────────────────────────────

    private void EvaluateTicker(decimal lastPrice, ScannerSymbolState state)
    {
        // Need minimum data for each module.
        // state.BarCount is fed from BarIngestionService.OnBarCommitted (DB-committed bars).
        // For low-volume tickers, few bars may exist even 2 hours into the session
        // because Polygon only emits AM.* when there's actual trading activity.
        // Use a hybrid gate: require 8 bars OR 30 minutes since session open — whichever
        // comes first. This handles both high-volume and low-volume names correctly.
        if (state.TickCount  < 100) return;
        if (state.QuoteCount <  50) return;

        var etNow       = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
                              TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));
        var sessionOpen = etNow.Date.AddHours(9).AddMinutes(30);
        var minsSinceOpen = (etNow - sessionOpen).TotalMinutes;

        if (state.BarCount < 8 && minsSinceOpen < 30) return;
        if (minsSinceOpen < 8) return;

        var vwap      = state.SessionVwap;
        var tape      = TapeModule.Analyze(state.RecentTicks(100));
        var nbbo      = NbboModule.Analyze(state.RecentQuotes(80));
        var structure = StructureModule.Analyze(state.RecentBars(12));
        var liquidity = LiquidityModule.Analyze(lastPrice, vwap, state.RecentTicks(100), state.RecentBars(10));
        var macro     = _market.Analyze();

        if (!_absorptionSeen &&
            (tape == TapeSignal.BuyingAbsorbed || tape == TapeSignal.SellingAbsorbed))
        {
            _absorptionSeen = true;
            _display.Log($"Absorption signal seen ({tape}) — max score updated");
        }
        if (!_macroSeen && macro != MacroSignal.Neutral)
        {
            _macroSeen = true;
            _display.Log($"Macro signal seen ({macro}) — max score updated");
        }

        var (shortScore, shortBd) = ScannerScorer.ScoreShort(lastPrice, vwap, tape, nbbo, structure, liquidity, macro);
        var (longScore,  longBd)  = ScannerScorer.ScoreLong (lastPrice, vwap, tape, nbbo, structure, liquidity, macro);

        _peakScores[state.Ticker] = (
            Math.Max(shortScore, _peakScores.TryGetValue(state.Ticker, out var p)  ? p.shortScore : 0),
            Math.Max(longScore,  _peakScores.TryGetValue(state.Ticker, out var p2) ? p2.longScore  : 0)
        );

        _currentScores[state.Ticker] = (shortScore, longScore, tape, nbbo, structure, liquidity, macro, vwap);

        bool isHotZone = shortScore >= HotZoneScore || longScore >= HotZoneScore;
        _lastLogTime.TryGetValue(state.Ticker, out var lastLog);
        bool isDue     = (DateTime.UtcNow - lastLog).TotalSeconds >= PeriodicLogSeconds;

        if (isDue || isHotZone || !_lastLoggedScore.ContainsKey(state.Ticker))
        {
            _lastLoggedScore[state.Ticker] = (shortScore, longScore);
            _lastLogTime[state.Ticker]     = DateTime.UtcNow;
        }

        _display.UpdateRow(state.Ticker, shortScore, longScore,
                           tape, nbbo, structure, liquidity, macro,
                           shortBd, longBd,
                           isHot: isHotZone, isWarming: false);

        MaybeUpdateStatus();

        if (shortScore >= ScannerConfig.ComputeThreshold(_nbboAvailable, _absorptionSeen, _macroSeen))
            TryTrigger(state.Ticker, "short", shortScore);

        if (longScore >= ScannerConfig.ComputeThreshold(_nbboAvailable, _absorptionSeen, _macroSeen))
            TryTrigger(state.Ticker, "long", longScore);
    }

    private void MaybeUpdateStatus()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastHeartbeat).TotalSeconds < HeartbeatSeconds) return;
        _lastHeartbeat = now;

        int maxScore  = ScannerConfig.MaxScoreBase
                      + (_nbboAvailable  ? ScannerConfig.WtNbbo       : 0)
                      + (_absorptionSeen ? ScannerConfig.WtAbsorption  : 0)
                      + (_macroSeen      ? ScannerConfig.WtMacro       : 0);
        int threshold = ScannerConfig.ComputeThreshold(_nbboAvailable, _absorptionSeen, _macroSeen);

        _display.UpdateStatus(_nbboAvailable, _market.HasVix, maxScore, threshold, TriggerCount, LlmCallCount);
        _display.Log($"── heartbeat  {ScannerConfig.ThresholdDescription(_nbboAvailable, _absorptionSeen, _macroSeen)}  scanner={TriggerCount}  llm={LlmCallCount}");
    }

    private void TryTrigger(string ticker, string side, int score)
    {
        int threshold = ScannerConfig.ComputeThreshold(_nbboAvailable, _absorptionSeen, _macroSeen);
        if (score < threshold) return;

        var key      = $"{ticker}:{side}";
        var now      = DateTime.UtcNow;
        var cooldown = TimeSpan.FromMinutes(ScannerConfig.CooldownMinutes);

        if (_lastTrigger.TryGetValue(key, out var last) && (now - last) < cooldown)
            return;

        _lastTrigger[key] = now;
        TriggerCount++;

        _forceCardNext[ticker] = true;

        int max   = ScannerConfig.MaxScoreBase
                  + (_nbboAvailable  ? ScannerConfig.WtNbbo      : 0)
                  + (_absorptionSeen ? ScannerConfig.WtAbsorption : 0)
                  + (_macroSeen      ? ScannerConfig.WtMacro      : 0);
        string grade = score >= (int)(max * 0.95) ? "A+++" : score >= (int)(max * 0.90) ? "A++" : "A+";

        _display.UpdatePipeline(ticker,
            scoreGate:  ScoreGateState.Passed,
            scoreLabel: $"{score}/{max}",
            llm:        LlmState.None,  llmLabel:  "",
            cardGate:   CardGateState.None, cardLabel: "",
            signal:     SignalState.None);

        _display.Log($"🔥 {ticker} {side.ToUpperInvariant()} {grade} score={score}/{max} ({score*100/max}%) → LLM forced");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static decimal GetDecimalOrZero(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;

    private static int GetIntOrZero(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static async Task WsSendAsync(ClientWebSocket ws, string msg, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(msg);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
        }
        if (_vixTask is not null) try { await _vixTask; } catch { }
    }
}
