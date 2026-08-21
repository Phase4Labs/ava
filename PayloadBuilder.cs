using System.Text.Json;

namespace get_assessment_no_graph;

/// <summary>
/// Builds the dataset JSON sent to OpenAI for execution card analysis.
///
/// Dataset includes:
///   - reference_levels : prior day close/high/low, session open/high/low, last vwap, gap%
///   - prior_days       : last 5 daily bars for trend/context
///   - intraday_bars    : full session minute bars from open to capTsUtc (no arbitrary limit)
///
/// This gives OpenAI the reference levels needed to correctly identify
/// reclaim_hold, break_hold, and fade_pop setups with real entry zones and stops.
/// </summary>
public sealed class PayloadBuilder
{
    private readonly SupabaseRestClient _db;
    private readonly PolygonClient      _polygon;

    private const int PriorDays = 5;

    private readonly bool _newsEnabled;
    private readonly int  _newsLookbackHours;
    private readonly int  _newsLimit;

    public PayloadBuilder(SupabaseRestClient db, PolygonClient polygon)
    {
        _db      = db;
        _polygon = polygon;

        _newsEnabled = !string.Equals(
            Environment.GetEnvironmentVariable("MASSIVE_NEWS_ENABLED"),
            "false",
            StringComparison.OrdinalIgnoreCase);
        _newsLookbackHours = ReadIntEnv("MASSIVE_NEWS_LOOKBACK_HOURS", 72, 1, 168);
        _newsLimit = ReadIntEnv("MASSIVE_NEWS_LIMIT", 6, 1, 20);
    }

    // ── Primary build method (used by all call sites) ─────────────────────────

    public async Task<string> BuildDatasetJsonUpToAsync(
        string ticker,
        DateTime utcNow,
        DateTime capTsUtc,
        int lastN = 0,               // ignored — full session always used
        CancellationToken ct = default,
        bool historicalAsOf = false)
    {
        ticker = ticker.ToUpperInvariant();
        var sessionOpenUtc = MarketSession.GetSessionOpenUtcForDay(utcNow);

        // 1. Full intraday minute bars: session open → cap (no limit)
        var bars = await _db.SelectAsync<MinuteBarRow>(
            "minute_bars",
            $"?select=ticker,ts_utc,o,h,l,c,v" +
            $"&ticker=eq.{Uri.EscapeDataString(ticker)}" +
            $"&ts_utc=gte.{Uri.EscapeDataString(sessionOpenUtc.ToString("o"))}" +
            $"&ts_utc=lte.{Uri.EscapeDataString(capTsUtc.ToString("o"))}" +
            $"&order=ts_utc.asc",
            ct);

        // 2. Features for the same window (all columns)
        var feats = await _db.SelectAsync<MinuteBarFeaturesRow>(
            "minute_bar_features",
            $"?select=ticker,ts_utc,vwap,dist_to_vwap,delta_close,delta_vwap," +
            $"body,range,upper_wick,lower_wick,body_ratio,avg_volume_5,rel_volume," +
            $"above_vwap,below_vwap,vwap_cross_up,vwap_cross_down" +
            $"&ticker=eq.{Uri.EscapeDataString(ticker)}" +
            $"&ts_utc=gte.{Uri.EscapeDataString(sessionOpenUtc.ToString("o"))}" +
            $"&ts_utc=lte.{Uri.EscapeDataString(capTsUtc.ToString("o"))}" +
            $"&order=ts_utc.asc",
            ct);

        var featByTs = feats.ToDictionary(f => f.TsUtc, f => f);

        // 3. Prior daily bars from Polygon (buffer window handles weekends/holidays)
        var dailyFrom = utcNow.Date.AddDays(-(PriorDays * 2));
        var dailyTo   = utcNow.Date.AddDays(-1);   // exclude today — that's the intraday section

        IReadOnlyList<DailyBar> dailyBars;
        try
        {
            var all = await _polygon.GetDailyBarsAsync(ticker, dailyFrom, dailyTo, ct);
            dailyBars = all.TakeLast(PriorDays).ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Daily bars unavailable for {ticker}: {ex.Message}");
            dailyBars = Array.Empty<DailyBar>();
        }

        var priorDay = dailyBars.Count > 0 ? dailyBars.Last() : null;

        // 4. Pre-market bars for today (4:00am–9:29am ET)
        var sessionDateEt = MarketSession.GetSessionDateNy(utcNow);
        IReadOnlyList<MinuteBar> preMarketBars;
        try
        {
            preMarketBars = await _polygon.GetPreMarketBarsAsync(ticker, sessionDateEt, ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Pre-market bars unavailable for {ticker}: {ex.Message}");
            preMarketBars = Array.Empty<MinuteBar>();
        }

        // 5. Timestamp-safe news context. The Massive endpoint is cached in PolygonClient,
        //    and all items are filtered to published_utc <= capTsUtc to prevent look-ahead.
        var newsAsOfUtc = bars.Count > 0 ? bars.Last().TsUtc.ToUniversalTime() : capTsUtc.ToUniversalTime();
        var newsFromUtc = newsAsOfUtc.AddHours(-_newsLookbackHours);
        IReadOnlyList<TickerNewsItem> newsItems = Array.Empty<TickerNewsItem>();
        if (_newsEnabled)
        {
            try
            {
                newsItems = await _polygon.GetTickerNewsAsync(
                    ticker, newsFromUtc, newsAsOfUtc, _newsLimit, ct);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] News unavailable for {ticker}: {ex.Message}");
            }
        }

        var preMarketHigh = preMarketBars.Count > 0 ? preMarketBars.Max(b => b.H) : (decimal?)null;
        var preMarketLow  = preMarketBars.Count > 0 ? preMarketBars.Min(b => b.L) : (decimal?)null;
        var preMarketOpen = preMarketBars.Count > 0 ? preMarketBars.First().O      : (decimal?)null;
        var preMarketClose = preMarketBars.Count > 0 ? preMarketBars.Last().C      : (decimal?)null;

        // 5. Volume context
        //    avg_daily_volume computed from the daily bars we already fetched (no extra API call).
        //    Live mode may use the snapshot endpoint; historical mode derives running volume
        //    only from bars at or before the replay timestamp.
        long? avgDailyVolume = null;
        if (dailyBars.Count > 0)
        {
            avgDailyVolume = (long)dailyBars.Select(d => (double)d.V).Average();
        }

        long? todayVolume   = null;
        long? prevDayVolume = null;

        if (historicalAsOf)
        {
            // Historical replay must never use the live snapshot endpoint: it contains
            // volume from after the replay timestamp. Derive running volume only from
            // bars that are already inside the capped historical dataset.
            todayVolume   = bars.Count > 0 ? bars.Sum(b => b.V) : null;
            prevDayVolume = priorDay?.V;
        }
        else
        {
            try
            {
                var snap = await _polygon.GetDaySnapshotVolumeAsync(ticker, ct);
                if (snap.HasValue)
                {
                    todayVolume   = snap.Value.TodayVol;
                    prevDayVolume = snap.Value.PrevDayVol;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] Snapshot volume unavailable for {ticker}: {ex.Message}");
            }
        }

        // RVOL = today's running volume vs same time yesterday (proxy: today vs prev day full volume)
        // More useful: today vs ADV scaled to time elapsed in session
        decimal? rvolVsAdv = null;
        if (avgDailyVolume.HasValue && avgDailyVolume > 0 && todayVolume.HasValue && bars.Count > 0)
        {
            // Scale ADV to fraction of session elapsed (bars elapsed / 390 total minutes)
            var sessionFraction = Math.Min(1.0m, bars.Count / 390m);
            var expectedVolAtThisPoint = avgDailyVolume.Value * sessionFraction;
            if (expectedVolAtThisPoint > 0)
                rvolVsAdv = Math.Round(todayVolume.Value / expectedVolAtThisPoint, 2);
        }

        // 6. Session-level summary
        var sessionHigh = bars.Count > 0 ? bars.Max(b => b.H) : (decimal?)null;
        var sessionLow  = bars.Count > 0 ? bars.Min(b => b.L) : (decimal?)null;
        var sessionOpen = bars.Count > 0 ? bars.First().O     : (decimal?)null;
        var lastClose   = bars.Count > 0 ? bars.Last().C      : (decimal?)null;
        var lastVwap    = feats.Count > 0 ? feats.Last().Vwap : (decimal?)null;
        var tsAsof      = bars.Count > 0 ? bars.Last().TsUtc  : capTsUtc;

        var priorDayClose = priorDay?.C;
        var priorDayHigh  = priorDay?.H;
        var priorDayLow   = priorDay?.L;

        decimal? gapPct = (sessionOpen.HasValue && priorDayClose.HasValue && priorDayClose != 0)
            ? Math.Round((sessionOpen.Value - priorDayClose.Value) / priorDayClose.Value * 100m, 2)
            : null;

        // 7. Assemble payload
        // Compute volume profiles from already-fetched bars (no extra API calls)
        var vpBlocks = VolumeProfileDatasetBuilder.BuildFromRows(
            intradayRows:  bars,
            premarketBars: preMarketBars,
            priorDayBars:  dailyBars,
            lastClose:     lastClose ?? 0m);

        var payload = new
        {
            ticker       = ticker,
            ts_asof_utc  = tsAsof,
            bars_elapsed = bars.Count,

            // Key reference levels — most critical for entry/stop identification
            reference_levels = new
            {
                prior_day_close  = priorDayClose,
                prior_day_high   = priorDayHigh,
                prior_day_low    = priorDayLow,
                premarket_high   = preMarketHigh,
                premarket_low    = preMarketLow,
                premarket_open   = preMarketOpen,
                premarket_close  = preMarketClose,
                session_open     = sessionOpen,
                session_high     = sessionHigh,
                session_low      = sessionLow,
                last_vwap        = lastVwap,
                last_close       = lastClose,
                gap_pct          = gapPct,
            },

            // Volume context — critical for distinguishing real moves from noise
            volume_context = new
            {
                today_volume      = todayVolume,
                prev_day_volume   = prevDayVolume,
                avg_daily_volume  = avgDailyVolume,
                rvol_vs_adv       = rvolVsAdv,
                adv_days_used     = dailyBars.Count,
            },

            // Current news explicitly supplied to the model. Unknown halt/binary-event
            // flags are null rather than guessed.
            news_context = new
            {
                enabled              = _newsEnabled,
                provider             = "massive_reference_news",
                provider_recency     = "hourly",
                asof_utc             = newsAsOfUtc,
                lookback_from_utc    = newsFromUtc,
                article_count        = newsItems.Count,
                active_halt          = (bool?)null,
                binary_event_pending = (bool?)null,
                items = newsItems.Select(n => new
                {
                    article_id   = n.Id,
                    published_utc = n.PublishedUtc,
                    publisher    = n.Publisher?.Name,
                    title        = Clip(n.Title, 300),
                    description  = Clip(n.Description, 700),
                    tickers      = n.Tickers?.Take(10).ToArray(),
                    keywords     = n.Keywords?.Take(10).ToArray(),
                    insights = n.Insights?.Where(i => string.Equals(i.Ticker, ticker, StringComparison.OrdinalIgnoreCase))
                        .Select(i => new
                        {
                            ticker              = i.Ticker,
                            sentiment           = i.Sentiment,
                            sentiment_reasoning = Clip(i.SentimentReasoning, 350),
                        })
                        .ToArray(),
                }).ToArray(),
            },

            // Prior trading days for trend/context
            prior_days = dailyBars.Select(d => new
            {
                date = d.Date.ToString("yyyy-MM-dd"),
                o    = d.O,
                h    = d.H,
                l    = d.L,
                c    = d.C,
                v    = d.V,
                vwap = d.Vw,
            }).ToArray(),

            // Pre-market bars (4:00am–9:29am ET)
            premarket_bars = preMarketBars.Select(b => new
            {
                ts_utc = b.BarStartUtc,
                o      = b.O,
                h      = b.H,
                l      = b.L,
                c      = b.C,
                v      = b.V,
            }).ToArray(),

            // Full intraday minute bars with all features
            intraday_bars = bars.Select(b =>
            {
                featByTs.TryGetValue(b.TsUtc, out var f);
                return new
                {
                    ts_utc          = b.TsUtc,
                    o               = b.O,
                    h               = b.H,
                    l               = b.L,
                    c               = b.C,
                    v               = b.V,
                    vwap            = f?.Vwap,
                    dist_to_vwap    = f?.DistToVwap,
                    delta_close     = f?.DeltaClose,
                    delta_vwap      = f?.DeltaVwap,
                    body            = f?.Body,
                    range           = f?.Range,
                    upper_wick      = f?.UpperWick,
                    lower_wick      = f?.LowerWick,
                    body_ratio      = f?.BodyRatio,
                    rel_volume      = f?.RelVolume,
                    above_vwap      = f?.AboveVwap,
                    below_vwap      = f?.BelowVwap,
                    vwap_cross_up   = f?.VwapCrossUp,
                    vwap_cross_down = f?.VwapCrossDown,
                };
            }).ToArray(),

            // Volume profiles — session (today) and composite (prior N sessions)
            session_vp   = vpBlocks.SessionVp,
            composite_vp = vpBlocks.CompositeVp,
            vp_context   = vpBlocks.VpContext,
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
    }


    private static string? Clip(string? value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maxChars ? normalized : normalized[..maxChars] + "…";
    }

    private static int ReadIntEnv(string key, int fallback, int min, int max)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        return int.TryParse(raw, out var value) ? Math.Clamp(value, min, max) : fallback;
    }

    // Legacy overload — keeps any other callers happy
    public Task<string> BuildDatasetJsonAsync(
        string ticker,
        DateTime utcNow,
        int lastN = 0,
        CancellationToken ct = default)
        => BuildDatasetJsonUpToAsync(ticker, utcNow, utcNow, lastN, ct);
}
