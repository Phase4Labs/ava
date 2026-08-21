namespace get_assessment_no_graph;

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;

public sealed class PolygonClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly ConcurrentDictionary<string, NewsCacheEntry> _newsCache = new(StringComparer.OrdinalIgnoreCase);

    public PolygonClient(string apiKey, HttpClient? http = null)
    {
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? throw new ArgumentException("POLYGON_API_KEY required") : apiKey;
        _http = http ?? new HttpClient { BaseAddress = new Uri("https://api.polygon.io/"), Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<IReadOnlyList<MinuteBar>> GetMinuteBarsAsync(
        string ticker,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct = default)
    {
        if (toUtc < fromUtc) throw new ArgumentException("toUtc must be >= fromUtc");

        var fromDate = fromUtc.ToString("yyyy-MM-dd");
        var toDate = toUtc.ToString("yyyy-MM-dd");

        var url = $"v2/aggs/ticker/{Uri.EscapeDataString(ticker)}/range/1/minute/{fromDate}/{toDate}" +
                  $"?adjusted=true&sort=asc&limit=50000&apiKey={Uri.EscapeDataString(_apiKey)}";

        using var resp = await _http.GetAsync(url, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Polygon HTTP {(int)resp.StatusCode}: {text}");

        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var parsed = JsonSerializer.Deserialize<AggsResponse>(text, opts) ?? throw new Exception("Polygon deserialize failed");

        var results = parsed.Results ?? [];
        var bars = new List<MinuteBar>(results.Count);

        foreach (var r in results)
        {
            // Polygon "t" is the aggregate window timestamp (ms). We treat it as bar START time.
            var tsUtc = DateTimeOffset.FromUnixTimeMilliseconds(r.T).UtcDateTime;
            if (tsUtc < fromUtc || tsUtc > toUtc) continue;

            long volume = (long)decimal.Truncate(r.V);   // or Round if you prefer

            var barStartUtc = tsUtc;                 // the agg timestamp as minute start
            var barCloseUtc = barStartUtc.AddMinutes(1);

            bars.Add(new MinuteBar(
                Ticker: ticker.ToUpperInvariant(),
                BarStartUtc: barStartUtc,
                BarCloseUtc: barCloseUtc,
                O: r.O,
                H: r.H,
                L: r.L,
                C: r.C,
                V: volume,
                IsFinal: true,                // REST aggs are closed bars
                ProviderTsUtc: DateTime.UtcNow,
                Source: "polygon"
            ));
        }

        return bars;
    }

    private sealed class AggsResponse
    {
        [JsonPropertyName("ticker")]
        public string? Ticker { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("queryCount")]
        public int? QueryCount { get; set; }

        [JsonPropertyName("resultsCount")]
        public int? ResultsCount { get; set; }

        [JsonPropertyName("adjusted")]
        public bool? Adjusted { get; set; }

        [JsonPropertyName("count")]
        public int? Count { get; set; }

        [JsonPropertyName("results")]
        public List<AggResult>? Results { get; set; }

        [JsonPropertyName("request_id")]
        public string? RequestId { get; set; }
    }

    /*private sealed class AggResult
    {
        [JsonPropertyName("o")]
        public decimal O { get; set; }

        [JsonPropertyName("h")]
        public decimal H { get; set; }

        [JsonPropertyName("l")]
        public decimal L { get; set; }

        [JsonPropertyName("c")]
        public decimal C { get; set; }

        [JsonPropertyName("v")]
        public long V { get; set; }

        // Optional but useful
        [JsonPropertyName("vw")]
        public decimal? Vw { get; set; }

        [JsonPropertyName("n")]
        public int? TradeCount { get; set; }

        [JsonPropertyName("t")]
        public long T { get; set; } // Unix ms
    }*/
    private sealed class AggResult
    {
        [JsonPropertyName("o")] public decimal O { get; set; }
        [JsonPropertyName("h")] public decimal H { get; set; }
        [JsonPropertyName("l")] public decimal L { get; set; }
        [JsonPropertyName("c")] public decimal C { get; set; }

        // Volume sometimes arrives as 1190881.0 -> decimal handles both.
        [JsonPropertyName("v")] public decimal V { get; set; }

        [JsonPropertyName("t")] public long T { get; set; } // Unix ms is safe Int64

        // Optional fields
        [JsonPropertyName("vw")] public decimal? Vw { get; set; }
        [JsonPropertyName("n")] public int? N { get; set; }
    }

    /// <summary>
    /// Fetches pre-market minute bars for a given date (4:00am–9:29am ET).
    /// Polygon returns extended-hours bars when extended_hours=true is passed.
    /// fromUtc / toUtc should bracket the pre-market window for the target date.
    /// </summary>
    public async Task<IReadOnlyList<MinuteBar>> GetPreMarketBarsAsync(
        string ticker,
        DateTime sessionDateEt,      // the trading date (ET) whose pre-market we want
        CancellationToken ct = default)
    {
        // Pre-market window: 4:00am ET → 9:29am ET on sessionDateEt
        var nyTz = TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");

        var preMarketOpenEt  = new DateTime(sessionDateEt.Year, sessionDateEt.Month, sessionDateEt.Day,
                                             4, 0, 0, DateTimeKind.Unspecified);
        var preMarketCloseEt = new DateTime(sessionDateEt.Year, sessionDateEt.Month, sessionDateEt.Day,
                                             9, 29, 0, DateTimeKind.Unspecified);

        var fromUtc = TimeZoneInfo.ConvertTimeToUtc(preMarketOpenEt, nyTz);
        var toUtc   = TimeZoneInfo.ConvertTimeToUtc(preMarketCloseEt, nyTz);

        var fromDate = fromUtc.ToString("yyyy-MM-dd");
        var toDate   = toUtc.ToString("yyyy-MM-dd");

        var url = $"v2/aggs/ticker/{Uri.EscapeDataString(ticker)}/range/1/minute/{fromDate}/{toDate}" +
                  $"?adjusted=true&sort=asc&limit=10000&extended_hours=true" +
                  $"&apiKey={Uri.EscapeDataString(_apiKey)}";

        using var resp = await _http.GetAsync(url, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Polygon pre-market HTTP {(int)resp.StatusCode}: {text}");

        var opts   = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var parsed = JsonSerializer.Deserialize<AggsResponse>(text, opts)
                     ?? throw new Exception("Polygon pre-market deserialize failed");

        var results = parsed.Results ?? [];
        var bars    = new List<MinuteBar>(results.Count);

        foreach (var r in results)
        {
            var tsUtc = DateTimeOffset.FromUnixTimeMilliseconds(r.T).UtcDateTime;
            // Keep only bars within the pre-market window
            if (tsUtc < fromUtc || tsUtc > toUtc) continue;

            bars.Add(new MinuteBar(
                Ticker       : ticker.ToUpperInvariant(),
                BarStartUtc  : tsUtc,
                BarCloseUtc  : tsUtc.AddMinutes(1),
                O            : r.O,
                H            : r.H,
                L            : r.L,
                C            : r.C,
                V            : (long)decimal.Truncate(r.V),
                IsFinal      : true,
                ProviderTsUtc: DateTime.UtcNow,
                Source       : "polygon_premarket"
            ));
        }

        return bars;
    }

    /// <summary>
    /// Fetches daily OHLCV bars. Use for prior-day context (pass last 5-10 trading days).
    /// </summary>
    public async Task<IReadOnlyList<DailyBar>> GetDailyBarsAsync(
        string ticker,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct = default)
    {
        var fromDate = fromUtc.ToString("yyyy-MM-dd");
        var toDate   = toUtc.ToString("yyyy-MM-dd");

        var url = $"v2/aggs/ticker/{Uri.EscapeDataString(ticker)}/range/1/day/{fromDate}/{toDate}" +
                  $"?adjusted=true&sort=asc&limit=50&apiKey={Uri.EscapeDataString(_apiKey)}";

        using var resp = await _http.GetAsync(url, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Polygon daily HTTP {(int)resp.StatusCode}: {text}");

        var opts   = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var parsed = JsonSerializer.Deserialize<AggsResponse>(text, opts)
                     ?? throw new Exception("Polygon daily deserialize failed");

        var results = parsed.Results ?? [];
        var bars    = new List<DailyBar>(results.Count);

        foreach (var r in results)
        {
            var date = DateTimeOffset.FromUnixTimeMilliseconds(r.T).UtcDateTime.Date;
            bars.Add(new DailyBar(
                Ticker : ticker.ToUpperInvariant(),
                Date   : date,
                O      : r.O,
                H      : r.H,
                L      : r.L,
                C      : r.C,
                V      : (long)decimal.Truncate(r.V),
                Vw     : r.Vw
            ));
        }

        return bars;
    }

    /// <summary>
    /// Returns today's running volume and previous day's volume from the Polygon snapshot.
    /// Lightweight — single REST call, no bar data.
    /// Returns null if snapshot is unavailable (pre-market, weekend, API error).
    /// </summary>
    public async Task<(long? TodayVol, long? PrevDayVol)?> GetDaySnapshotVolumeAsync(
        string ticker,
        CancellationToken ct = default)
    {
        var url = $"v2/snapshot/locale/us/markets/stocks/tickers/{Uri.EscapeDataString(ticker)}" +
                  $"?apiKey={Uri.EscapeDataString(_apiKey)}";

        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;

        var text = await resp.Content.ReadAsStringAsync(ct);

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("ticker", out var t)) return null;

            long? todayVol   = null;
            long? prevDayVol = null;

            if (t.TryGetProperty("day", out var day) &&
                day.TryGetProperty("v", out var v) &&
                v.ValueKind == JsonValueKind.Number)
                todayVol = (long)v.GetDouble();

            if (t.TryGetProperty("prevDay", out var prev) &&
                prev.TryGetProperty("v", out var pv) &&
                pv.ValueKind == JsonValueKind.Number)
                prevDayVol = (long)pv.GetDouble();

            return (todayVol, prevDayVol);
        }
        catch
        {
            return null;
        }
    }


    /// <summary>
    /// Retrieves timestamped ticker news from Massive. Results are filtered so no
    /// article published after asOfUtc can enter the payload. Live requests are
    /// cached briefly because PayloadBuilder runs more often than the news feed updates.
    /// Historical/replay requests are cached by ticker/hour.
    /// </summary>
    public async Task<IReadOnlyList<TickerNewsItem>> GetTickerNewsAsync(
        string ticker,
        DateTime fromUtc,
        DateTime asOfUtc,
        int limit = 6,
        CancellationToken ct = default)
    {
        ticker = ticker.ToUpperInvariant();
        fromUtc = fromUtc.ToUniversalTime();
        asOfUtc = asOfUtc.ToUniversalTime();
        limit = Math.Clamp(limit, 1, 50);

        var nowUtc = DateTime.UtcNow;
        var isLive = asOfUtc >= nowUtc.AddHours(-2);
        var cacheKey = isLive
            ? $"live:{ticker}"
            : $"replay:{ticker}:{asOfUtc:yyyyMMddHH}";

        if (_newsCache.TryGetValue(cacheKey, out var cached) &&
            nowUtc - cached.FetchedAtUtc < TimeSpan.FromMinutes(10) &&
            cached.FromUtc <= fromUtc)
        {
            return FilterNews(cached.Items, ticker, fromUtc, asOfUtc, limit);
        }

        // For live use, fetch through current UTC time and filter back to asOfUtc.
        // This lets the same cached result safely serve subsequent closed bars.
        var queryToUtc = isLive
            ? nowUtc
            : new DateTime(asOfUtc.Year, asOfUtc.Month, asOfUtc.Day, asOfUtc.Hour, 59, 59, DateTimeKind.Utc);

        var url = "https://api.massive.com/v2/reference/news" +
                  $"?ticker={Uri.EscapeDataString(ticker)}" +
                  $"&published_utc.gte={Uri.EscapeDataString(fromUtc.ToString("o"))}" +
                  $"&published_utc.lte={Uri.EscapeDataString(queryToUtc.ToString("o"))}" +
                  "&order=desc&sort=published_utc" +
                  $"&limit={Math.Max(limit, 20)}" +
                  $"&apiKey={Uri.EscapeDataString(_apiKey)}";

        using var resp = await _http.GetAsync(url, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Massive news HTTP {(int)resp.StatusCode}: {text}");

        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var parsed = JsonSerializer.Deserialize<TickerNewsResponse>(text, opts)
                     ?? throw new Exception("Massive news deserialize failed");

        var items = (IReadOnlyList<TickerNewsItem>)(parsed.Results ?? new List<TickerNewsItem>());
        _newsCache[cacheKey] = new NewsCacheEntry(nowUtc, fromUtc, queryToUtc, items);

        return FilterNews(items, ticker, fromUtc, asOfUtc, limit);
    }

    private static IReadOnlyList<TickerNewsItem> FilterNews(
        IReadOnlyList<TickerNewsItem> items,
        string ticker,
        DateTime fromUtc,
        DateTime asOfUtc,
        int limit)
        => items
            .Where(item => item.PublishedUtc >= fromUtc && item.PublishedUtc <= asOfUtc)
            .Where(item => item.Tickers is null || item.Tickers.Count == 0 ||
                           item.Tickers.Any(t => string.Equals(t, ticker, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(item => item.PublishedUtc)
            .Take(limit)
            .ToArray();

    private sealed record NewsCacheEntry(
        DateTime FetchedAtUtc,
        DateTime FromUtc,
        DateTime QueryToUtc,
        IReadOnlyList<TickerNewsItem> Items);

    public void Dispose() => _http.Dispose();
}

