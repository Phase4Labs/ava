using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace get_assessment_no_graph;

/// <summary>
/// Single source of truth for live minute bar ingestion AND the sole owner
/// of the stocks WebSocket connection.
///
/// Responsibilities:
///   - Own the SINGLE stocks WebSocket connection subscribing to AM.*, T.*, Q.*
///   - Write minute_bars + minute_bar_features to DB
///   - Maintain DbBarCount per ticker (committed DB rows this session)
///   - Fire OnBarCommitted   after each successful DB write  → ScannerService updates bar state
///   - Fire OnTradeReceived  on every T.* event              → ScannerService scores ticks
///   - Fire OnQuoteReceived  on every Q.* event              → ScannerService updates NBBO
///
/// ScannerService subscribes to these three callbacks instead of maintaining
/// its own stocks WebSocket — one connection, one auth slot, no PolicyViolation.
/// ScannerService keeps only its VIX indices WebSocket (different cluster).
/// </summary>
public sealed class BarIngestionService
{
    private readonly PolygonRealtimeClient   _ws;
    private readonly SupabaseRestClient      _db;
    private readonly RealtimeFeatureComputer _features;

    /// <summary>
    /// Number of bars committed to minute_bars in the DB this session, per ticker.
    /// Incremented on every successful upsert. ScannerService reads this to gate
    /// triggers on actual DB state rather than in-memory bar count.
    /// </summary>
    public ConcurrentDictionary<string, int> DbBarCount { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    // ── Outbound callbacks ─────────────────────────────────────────────────────

    /// <summary>
    /// Fired after a bar is successfully written to minute_bars AND features computed.
    /// Args: (ticker, bar, features)
    /// </summary>
    public event Action<string, MinuteBarRow, MinuteBarFeaturesRow>? OnBarCommitted;

    /// <summary>
    /// Fired on every T.* (trade) event received from Polygon.
    /// ScannerService subscribes to drive its tape scoring without its own WS.
    /// Args: (ticker, tradeEvent)
    /// </summary>
    public event Action<string, JsonElement>? OnTradeReceived;

    /// <summary>
    /// Fired on every Q.* (quote) event received from Polygon.
    /// ScannerService subscribes to update its NBBO state without its own WS.
    /// Args: (ticker, quoteEvent)
    /// </summary>
    public event Action<string, JsonElement>? OnQuoteReceived;

    public BarIngestionService(
        PolygonRealtimeClient    ws,
        SupabaseRestClient       db,
        RealtimeFeatureComputer  features)
    {
        _ws       = ws;
        _db       = db;
        _features = features;
    }

    // ── Seeding ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds per-ticker feature state from DB so VWAP + rolling volume are
    /// correct across restarts. Also seeds DbBarCount from actual DB rows.
    /// </summary>
    public async Task SeedStateFromDbAsync(
        IEnumerable<string> tickers, DateTime utcNow, CancellationToken ct)
    {
        var sessionOpenUtc = MarketSession.GetSessionOpenUtcForDay(utcNow);

        foreach (var t in tickers.Select(x => x.ToUpperInvariant()))
        {
            var bars = await _db.SelectAsync<MinuteBarRow>(
                "minute_bars",
                $"?select=ticker,ts_utc,o,h,l,c,v" +
                $"&ticker=eq.{Uri.EscapeDataString(t)}" +
                $"&ts_utc=gte.{Uri.EscapeDataString(sessionOpenUtc.ToString("o"))}" +
                $"&order=ts_utc.asc&limit=50000",
                ct);

            _features.SeedFromSessionBars(t, sessionOpenUtc, bars);
            DbBarCount[t] = bars.Count;
        }
    }

    /// <summary>
    /// Seeds ScannerService bar state from DB historical bars.
    /// Must be called after SetBarIngestionService so the scanner has session
    /// bar history from startup — OnBarCommitted only fires for new bars.
    /// </summary>
    public async Task SeedScannerStateAsync(
        ScannerService scanner, DateTime utcNow, CancellationToken ct)
    {
        var sessionOpenUtc = MarketSession.GetSessionOpenUtcForDay(utcNow);

        foreach (var ticker in scanner.ActiveTickers)
        {
            var bars = await _db.SelectAsync<MinuteBarRow>(
                "minute_bars",
                $"?select=ticker,ts_utc,o,h,l,c,v" +
                $"&ticker=eq.{Uri.EscapeDataString(ticker)}" +
                $"&ts_utc=gte.{Uri.EscapeDataString(sessionOpenUtc.ToString("o"))}" +
                $"&order=ts_utc.asc&limit=50000",
                ct);

            foreach (var bar in bars)
                scanner.InjectBar(ticker, bar);

            if (bars.Count > 0)
                DbBarCount[ticker] = bars.Count;

            Console.WriteLine($"[bar-ingest] seeded scanner {ticker} with {bars.Count} bars");
        }
    }

    // ── Run ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Main loop — connects to the stocks WS, subscribes AM+T+Q for all tickers,
    /// and routes events. Reconnects automatically on disconnect.
    /// </summary>
    public async Task RunAsync(string[] tickers, Action<string>? debugRaw, CancellationToken ct)
    {
        Console.WriteLine($"[bar-ingest] RunAsync starting for {tickers.Length} tickers: {string.Join(",", tickers)}");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _ws.ConnectAsync(ct);
                Console.WriteLine("[bar-ingest] WebSocket connected");

                // Single subscription: AM + T + Q in one message
                await _ws.SubscribeStocksAsync(tickers, ct);
                Console.WriteLine($"[bar-ingest] Subscribed AM+T+Q for {tickers.Length} tickers");

                await _ws.RunAsync(
                    async ev => await HandleEventAsync(ev, ct),
                    debugRaw,
                    ct);

                Console.WriteLine("[bar-ingest] WebSocket disconnected — reconnecting in 5s");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[bar-ingest] WebSocket error: {ex.Message} — reconnecting in 5s");
            }

            if (!ct.IsCancellationRequested)
                await Task.Delay(5000, ct).ConfigureAwait(false);
        }

        Console.WriteLine("[bar-ingest] RunAsync ended");
    }

    // ── Event routing ──────────────────────────────────────────────────────────

    private async Task HandleEventAsync(JsonElement ev, CancellationToken ct)
    {
        if (!ev.TryGetProperty("ev", out var evType)) return;
        var evStr = evType.GetString() ?? "";

        switch (evStr.ToUpperInvariant())
        {
            case "AM":
                await HandleBarAsync(ev, ct);
                break;

            case "T":
                RouteTradeEvent(ev);
                break;

            case "Q":
                RouteQuoteEvent(ev);
                break;

            // "status", "pong", etc. — silently ignore
        }
    }

    private void RouteTradeEvent(JsonElement ev)
    {
        if (!ev.TryGetProperty("sym", out var symEl)) return;
        var ticker = symEl.GetString();
        if (string.IsNullOrWhiteSpace(ticker)) return;

        try { OnTradeReceived?.Invoke(ticker.ToUpperInvariant(), ev); }
        catch (Exception ex)
        {
            Console.WriteLine($"[bar-ingest] OnTradeReceived handler error for {ticker}: {ex.Message}");
        }
    }

    private void RouteQuoteEvent(JsonElement ev)
    {
        if (!ev.TryGetProperty("sym", out var symEl)) return;
        var ticker = symEl.GetString();
        if (string.IsNullOrWhiteSpace(ticker)) return;

        try { OnQuoteReceived?.Invoke(ticker.ToUpperInvariant(), ev); }
        catch (Exception ex)
        {
            Console.WriteLine($"[bar-ingest] OnQuoteReceived handler error for {ticker}: {ex.Message}");
        }
    }

    // ── AM bar processing ──────────────────────────────────────────────────────

    private async Task HandleBarAsync(JsonElement ev, CancellationToken ct)
    {
        if (!ev.TryGetProperty("sym", out var symEl)) return;
        var ticker = symEl.GetString();
        if (string.IsNullOrWhiteSpace(ticker)) return;
        ticker = ticker!.ToUpperInvariant();

        if (!TryGetDecimal(ev, "o", out var o)) return;
        if (!TryGetDecimal(ev, "h", out var h)) return;
        if (!TryGetDecimal(ev, "l", out var l)) return;
        if (!TryGetDecimal(ev, "c", out var c)) return;
        if (!TryGetDecimal(ev, "v", out var vDec)) return;
        if (!ev.TryGetProperty("s", out var sEl)) return;

        var startMs = sEl.ValueKind == JsonValueKind.Number
            ? sEl.GetInt64()
            : long.Parse(sEl.GetString()!, CultureInfo.InvariantCulture);
        var tsUtc = DateTimeOffset.FromUnixTimeMilliseconds(startMs).UtcDateTime;
        var v     = (long)decimal.Truncate(vDec);

        var barRow = new MinuteBarRow
        {
            Ticker = ticker,
            TsUtc  = tsUtc,
            O      = o,
            H      = h,
            L      = l,
            C      = c,
            V      = v,
            Source = "polygon_ws"
        };

        // ── Write bar to DB ────────────────────────────────────────────────────
        await _db.UpsertAsync("minute_bars", new[]
        {
            new {
                ticker = barRow.Ticker,
                ts_utc = barRow.TsUtc,
                o      = barRow.O,
                h      = barRow.H,
                l      = barRow.L,
                c      = barRow.C,
                v      = barRow.V,
                source = barRow.Source
            }
        }, "ticker,ts_utc", ct);

        DbBarCount.AddOrUpdate(ticker, 1, (_, n) => n + 1);

        // ── Compute + write features ───────────────────────────────────────────
        MinuteBarFeaturesRow feat;
        try
        {
            feat = _features.ComputeNext(ticker, barRow);
        }
        catch
        {
            var now            = DateTime.UtcNow;
            var sessionOpenUtc = MarketSession.GetSessionOpenUtcForDay(now);
            var seedBars       = await _db.SelectAsync<MinuteBarRow>(
                "minute_bars",
                $"?select=ticker,ts_utc,o,h,l,c,v" +
                $"&ticker=eq.{Uri.EscapeDataString(ticker)}" +
                $"&ts_utc=gte.{Uri.EscapeDataString(sessionOpenUtc.ToString("o"))}" +
                $"&order=ts_utc.asc&limit=50000",
                ct);

            _features.SeedFromSessionBars(ticker, sessionOpenUtc, seedBars);
            feat = _features.ComputeNext(ticker, barRow);
        }

        await _db.UpsertAsync("minute_bar_features", new[]
        {
            new {
                ticker          = feat.Ticker,
                ts_utc          = feat.TsUtc,
                vwap            = feat.Vwap,
                dist_to_vwap    = feat.DistToVwap,
                delta_close     = feat.DeltaClose,
                delta_vwap      = feat.DeltaVwap,
                body            = feat.Body,
                range           = feat.Range,
                upper_wick      = feat.UpperWick,
                lower_wick      = feat.LowerWick,
                body_ratio      = feat.BodyRatio,
                avg_volume_5    = feat.AvgVolume5,
                rel_volume      = feat.RelVolume,
                above_vwap      = feat.AboveVwap,
                below_vwap      = feat.BelowVwap,
                vwap_cross_up   = feat.VwapCrossUp,
                vwap_cross_down = feat.VwapCrossDown
            }
        }, "ticker,ts_utc", ct);

        // ── Notify subscribers — bar is fully committed ────────────────────────
        Console.WriteLine($"[bar-ingest] OnBarCommitted firing for {ticker} ts={tsUtc:HH:mm} subscribers={OnBarCommitted?.GetInvocationList().Length ?? 0}");
        try { OnBarCommitted?.Invoke(ticker, barRow, feat); }
        catch (Exception ex) { Console.WriteLine($"[bar-ingest] OnBarCommitted handler error: {ex.Message}"); }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static bool TryGetDecimal(JsonElement ev, string name, out decimal value)
    {
        value = 0m;
        if (!ev.TryGetProperty(name, out var el)) return false;
        try
        {
            if (el.ValueKind == JsonValueKind.Number)
            {
                value = el.GetDecimal();
                return true;
            }
            if (el.ValueKind == JsonValueKind.String)
            {
                value = decimal.Parse(el.GetString()!, CultureInfo.InvariantCulture);
                return true;
            }
        }
        catch { }
        return false;
    }
}
