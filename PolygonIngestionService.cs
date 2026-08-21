namespace get_assessment_no_graph;

public sealed class PolygonIngestionService
{
    private readonly PolygonClient _polygon;
    private readonly SupabaseRestClient _db;

    public PolygonIngestionService(PolygonClient polygon, SupabaseRestClient db)
    {
        _polygon = polygon;
        _db = db;
    }

    public async Task<DateTime?> IngestTodayAndEnsureFeaturesUpToAsync(
    string ticker,
    DateTime utcNow,
    DateTime capTsUtc,
    CancellationToken ct = default)
    {
        var sessionOpenUtc = MarketSession.GetSessionOpenUtcForDay(utcNow);
        if (capTsUtc < sessionOpenUtc)
            return null;

        // IMPORTANT: fetch only up to capTsUtc (last closed minute), not utcNow
        var fetchFromUtc = sessionOpenUtc;
        var fetchToUtc = capTsUtc;

        var bars = await _polygon.GetMinuteBarsAsync(ticker, fetchFromUtc, fetchToUtc, ct);
        if (bars.Count > 0)
        {
            await _db.UpsertAsync("minute_bars", bars.Select(b => new
            {
                ticker = b.Ticker,
                ts_utc = b.BarStartUtc,
                o = b.O,
                h = b.H,
                l = b.L,
                c = b.C,
                v = b.V,
                source = b.Source
            }).ToArray(), "ticker,ts_utc", ct);
        }

        // Reload from DB, capped to capTsUtc (do not trust provider completeness)
        var sessionBars = await _db.SelectAsync<MinuteBarRow>(
            "minute_bars",
            $"?select=ticker,ts_utc,o,h,l,c,v" +
            $"&ticker=eq.{Uri.EscapeDataString(ticker.ToUpperInvariant())}" +
            $"&ts_utc=gte.{Uri.EscapeDataString(sessionOpenUtc.ToString("o"))}" +
            $"&ts_utc=lte.{Uri.EscapeDataString(capTsUtc.ToString("o"))}" +
            $"&order=ts_utc.asc",
            ct);

        if (sessionBars.Count == 0)
            return null;

        // Compute full 15-field features (body, wicks, volume, VWAP crosses, etc.)
        var features = SessionFeatureCalculator.ComputeSessionFeatures(sessionBars);
        var featureRows = features.Select(f => new MinuteBarFeaturesRow
        {
            Ticker = f.Ticker,
            TsUtc = f.TsUtc,
            Vwap = f.Vwap,
            DistToVwap = f.DistToVwap,
            DeltaClose = f.DeltaClose,
            DeltaVwap = f.DeltaVwap,
            Body = f.Body,
            Range = f.Range,
            UpperWick = f.UpperWick,
            LowerWick = f.LowerWick,
            BodyRatio = f.BodyRatio,
            AvgVolume5 = f.AvgVolume5,
            RelVolume = f.RelVolume,
            AboveVwap = f.AboveVwap,
            BelowVwap = f.BelowVwap,
            VwapCrossUp = f.VwapCrossUp,
            VwapCrossDown = f.VwapCrossDown
        }).ToList();
        await _db.UpsertAsync("minute_bar_features", featureRows, "ticker,ts_utc", ct);

        return sessionBars[^1].TsUtc;
    }


    public async Task<DateTime?> IngestTodayAndEnsureFeaturesAsync(
        string ticker,
        DateTime utcNow,
        CancellationToken ct = default)
    {
        var sessionOpenUtc = MarketSession.GetSessionOpenUtcForDay(utcNow);

        // 1) Get latest stored bar ts for today (>= session open)
        var latestBars = await _db.SelectAsync<MinuteBarRow>(
            "minute_bars",
            queryString:
                $"?select=ts_utc,ticker,o,h,l,c,v" +
                $"&ticker=eq.{Uri.EscapeDataString(ticker.ToUpperInvariant())}" +
                $"&ts_utc=gte.{Uri.EscapeDataString(sessionOpenUtc.ToString("o"))}" +
                $"&order=ts_utc.desc&limit=1",
            ct);

        DateTime fetchFromUtc = sessionOpenUtc;

        if (latestBars.Count == 1)
        {
            var lastTs = latestBars[0].TsUtc;
            // fetch next minute to avoid duplicates; upsert makes this safe anyway
            fetchFromUtc = lastTs.AddMinutes(1);
            if (fetchFromUtc < sessionOpenUtc) fetchFromUtc = sessionOpenUtc;
        }

        var fetchToUtc = utcNow;
        //var fetchToUtc = utcNow.AddDays(-2);

        // 2) Pull new bars (if any)
        var newBars = await _polygon.GetMinuteBarsAsync(ticker, fetchFromUtc, fetchToUtc, ct);

        if (newBars.Count > 0)
        {
            var upsertRows = newBars.Select(b => new MinuteBarRow
            {
                Ticker = b.Ticker,
                TsUtc = b.BarStartUtc,
                O = b.O, H = b.H, L = b.L, C = b.C, V = b.V,
                Source = b.Source
            }).ToList();

            await _db.UpsertAsync("minute_bars", upsertRows, "ticker,ts_utc", ct);
        }

        // 3) Load all bars from session open -> now from DB (robust across restarts)
        var sessionBars = await _db.SelectAsync<MinuteBarRow>(
            "minute_bars",
            queryString:
                $"?select=ticker,ts_utc,o,h,l,c,v" +
                $"&ticker=eq.{Uri.EscapeDataString(ticker.ToUpperInvariant())}" +
                $"&ts_utc=gte.{Uri.EscapeDataString(sessionOpenUtc.ToString("o"))}" +
                $"&order=ts_utc.asc&limit=50000",
            ct);

        if (sessionBars.Count == 0)
            return null;

        // 4) Compute + upsert features for the whole session
        var features = SessionFeatureCalculator.ComputeSessionFeatures(sessionBars);

        var featureRows = features.Select(f => new MinuteBarFeaturesRow
        {
            Ticker = f.Ticker,
            TsUtc = f.TsUtc,

            Vwap = f.Vwap,
            DistToVwap = f.DistToVwap,
            DeltaClose = f.DeltaClose,
            DeltaVwap = f.DeltaVwap,

            Body = f.Body,
            Range = f.Range,
            UpperWick = f.UpperWick,
            LowerWick = f.LowerWick,
            BodyRatio = f.BodyRatio,

            AvgVolume5 = f.AvgVolume5,
            RelVolume = f.RelVolume,

            AboveVwap = f.AboveVwap,
            BelowVwap = f.BelowVwap,
            VwapCrossUp = f.VwapCrossUp,
            VwapCrossDown = f.VwapCrossDown
        }).ToList();

        await _db.UpsertAsync("minute_bar_features", featureRows, "ticker,ts_utc", ct);

        // return latest bar time for as-of
        return sessionBars[^1].TsUtc;
    }
}