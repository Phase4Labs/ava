using System.Globalization;
using System.Text.Json;

namespace get_assessment_no_graph;

/// <summary>
/// Live ingestion via Polygon WebSocket minute aggregates (AM.*).
///
/// Responsibilities:
/// - Subscribe to AM.TICKER feeds
/// - Upsert closed minute bars into minute_bars
/// - Compute + upsert per-bar features incrementally (VWAP anchored to session open)
///
/// This service is intended for LIVE mode. Replay/backfill should continue using REST.
/// </summary>
public sealed class PolygonRealtimeIngestionService
{
    private readonly PolygonRealtimeClient _ws;
    private readonly SupabaseRestClient _db;
    private readonly RealtimeFeatureComputer _features;

    public PolygonRealtimeIngestionService(PolygonRealtimeClient ws, SupabaseRestClient db, RealtimeFeatureComputer features)
    {
        _ws = ws;
        _db = db;
        _features = features;
    }

    /// <summary>
    /// Seeds per-ticker feature state from DB so VWAP + rolling volume are correct across restarts.
    /// </summary>
    public async Task SeedStateFromDbAsync(IEnumerable<string> tickers, DateTime utcNow, CancellationToken ct)
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
        }
    }

    public async Task RunAsync(string[] tickers, Action<string>? debugRaw, CancellationToken ct)
    {
        await _ws.ConnectAsync(ct);
        await _ws.SubscribeMinuteAggregatesAsync(tickers, ct);
        Console.WriteLine($"[poly-ws] subscribed: {string.Join(",", tickers.Select(t => "AM." + t.ToUpperInvariant()))}");

        await _ws.RunAsync(async ev => await HandleEventAsync(ev, ct), debugRaw, ct);
    }

    private async Task HandleEventAsync(JsonElement ev, CancellationToken ct)
    {
        // Expect: {"ev":"AM","sym":"INTC","o":..."h":..."l":..."c":..."v":..."s":<start_ms>,"e":<end_ms>,"vw":...}
        if (!ev.TryGetProperty("ev", out var evType)) return;
        if (!string.Equals(evType.GetString(), "AM", StringComparison.OrdinalIgnoreCase)) return;

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

        var startMs = sEl.ValueKind == JsonValueKind.Number ? sEl.GetInt64() : long.Parse(sEl.GetString()!, CultureInfo.InvariantCulture);
        var tsUtc = DateTimeOffset.FromUnixTimeMilliseconds(startMs).UtcDateTime;

        var v = (long)decimal.Truncate(vDec);

        var barRow = new MinuteBarRow
        {
            Ticker = ticker,
            TsUtc = tsUtc,
            O = o,
            H = h,
            L = l,
            C = c,
            V = v,
            Source = "polygon_ws"
        };

        // Upsert bar
        await _db.UpsertAsync("minute_bars", new[]
        {
            new {
                ticker = barRow.Ticker,
                ts_utc = barRow.TsUtc,
                o = barRow.O,
                h = barRow.H,
                l = barRow.L,
                c = barRow.C,
                v = barRow.V,
                source = barRow.Source
            }
        }, "ticker,ts_utc", ct);

        // Compute + upsert features for this bar incrementally
        MinuteBarFeaturesRow feat;
        try
        {
            feat = _features.ComputeNext(ticker, barRow);
        }
        catch
        {
            // If we weren't seeded yet (e.g., service started mid-session), seed on demand.
            // This is still cheap for 4 symbols.
            var now = DateTime.UtcNow;
            var sessionOpenUtc = MarketSession.GetSessionOpenUtcForDay(now);
            var seedBars = await _db.SelectAsync<MinuteBarRow>(
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
                ticker = feat.Ticker,
                ts_utc = feat.TsUtc,
                vwap = feat.Vwap,
                dist_to_vwap = feat.DistToVwap,
                delta_close = feat.DeltaClose,
                delta_vwap = feat.DeltaVwap,
                body = feat.Body,
                range = feat.Range,
                upper_wick = feat.UpperWick,
                lower_wick = feat.LowerWick,
                body_ratio = feat.BodyRatio,
                avg_volume_5 = feat.AvgVolume5,
                rel_volume = feat.RelVolume,
                above_vwap = feat.AboveVwap,
                below_vwap = feat.BelowVwap,
                vwap_cross_up = feat.VwapCrossUp,
                vwap_cross_down = feat.VwapCrossDown
            }
        }, "ticker,ts_utc", ct);
    }

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
        catch
        {
            return false;
        }
        return false;
    }
}
