using System.Text.Json;

namespace get_assessment_no_graph;

/// <summary>
/// Stage 2A compact representation of the existing Stage-1 market dataset.
/// It is intentionally a pure transformation of an already look-ahead-safe payload:
/// no new market data is fetched and no future bars can be introduced here.
///
/// Design:
///   - Preserve reference/volume/VP/news context.
///   - Keep the most recent 60 regular-session 1m bars with decision-relevant features.
///   - Aggregate older regular-session bars to 5m.
///   - Aggregate premarket bars to 15m plus a deterministic premarket summary.
///   - Add a small deterministic market-state summary so the LLM does less arithmetic.
/// </summary>
public static class CompactMarketStateBuilder
{
    private const int RecentOneMinuteBars = 60;

    public static string Build(string fullDatasetJson)
    {
        using var doc = JsonDocument.Parse(fullDatasetJson);
        var root = doc.RootElement;

        var ticker = GetString(root, "ticker") ?? "";
        var tsAsof = GetDateTime(root, "ts_asof_utc");
        var barsElapsed = GetInt(root, "bars_elapsed") ?? 0;

        var intraday = ReadBars(root, "intraday_bars");
        var premarket = ReadBars(root, "premarket_bars");

        var recent = intraday.TakeLast(RecentOneMinuteBars).Select(ToCompactMinuteBar).ToArray();
        var older = intraday.Count > RecentOneMinuteBars
            ? AggregateBars(intraday.Take(intraday.Count - RecentOneMinuteBars).ToList(), 5)
            : Array.Empty<object>();
        var premarket15 = AggregateBars(premarket, 15);

        var reference = Clone(root, "reference_levels");
        var volume = Clone(root, "volume_context");
        var priorDays = Clone(root, "prior_days");
        var sessionVp = Clone(root, "session_vp");
        var compositeVp = Clone(root, "composite_vp");
        var vpContext = Clone(root, "vp_context");

        var payload = new
        {
            payload_mode = "compact_v1",
            ticker,
            ts_asof_utc = tsAsof,
            bars_elapsed = barsElapsed,
            representation = new
            {
                recent_intraday = $"last {RecentOneMinuteBars} regular-session bars at 1-minute resolution (or all bars if fewer)",
                earlier_intraday = "older regular-session bars aggregated to 5-minute OHLCV",
                premarket = "premarket bars aggregated to 15-minute OHLCV plus summary",
                causality = "all data is at or before ts_asof_utc"
            },
            reference_levels = reference,
            volume_context = volume,
            deterministic_state = BuildDeterministicState(intraday, root),
            news_context = CompactNews(root),
            prior_days = priorDays,
            premarket_summary = BuildPremarketSummary(premarket),
            premarket_bars = premarket15,
            earlier_intraday_5m = older,
            intraday_bars = recent,
            session_vp = sessionVp,
            composite_vp = compositeVp,
            vp_context = vpContext,
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
    }

    private static object BuildDeterministicState(IReadOnlyList<BarView> bars, JsonElement root)
    {
        decimal? last = bars.Count > 0 ? bars[^1].C : null;
        decimal? open = bars.Count > 0 ? bars[0].O : null;
        decimal? high = bars.Count > 0 ? bars.Max(b => b.H) : null;
        decimal? low = bars.Count > 0 ? bars.Min(b => b.L) : null;
        decimal? vwap = bars.Count > 0 ? bars[^1].Vwap : null;
        decimal? priorClose = GetNestedDecimal(root, "reference_levels", "prior_day_close");

        decimal? dayChangePct = Pct(last, priorClose);
        decimal? sessionChangePct = Pct(last, open);
        decimal? distVwapPct = Pct(last, vwap);
        decimal? ret5 = ReturnOverBars(bars, 5);
        decimal? ret15 = ReturnOverBars(bars, 15);

        var last5 = bars.TakeLast(5).ToList();
        var prev5 = bars.Count > 5 ? bars.Skip(Math.Max(0, bars.Count - 10)).Take(Math.Min(5, bars.Count - 5)).ToList() : new List<BarView>();
        var last5Vol = last5.Sum(b => b.V);
        var prev5Vol = prev5.Sum(b => b.V);
        decimal? volAccel = prev5Vol > 0 ? Math.Round((decimal)last5Vol / prev5Vol, 3) : null;

        var recent15 = bars.TakeLast(15).ToList();
        var crossesUp = bars.Count(b => b.VwapCrossUp == true);
        var crossesDown = bars.Count(b => b.VwapCrossDown == true);
        var maxRel15 = recent15.Where(b => b.RelVolume.HasValue).Select(b => b.RelVolume!.Value).DefaultIfEmpty().Max();

        string regime = "unknown";
        if (last.HasValue && open.HasValue && vwap.HasValue)
        {
            if (last > open && last > vwap) regime = "above_open_above_vwap";
            else if (last < open && last < vwap) regime = "below_open_below_vwap";
            else regime = "mixed_open_vwap";
        }

        return new
        {
            last_close = last,
            session_open = open,
            session_high = high,
            session_low = low,
            last_vwap = vwap,
            day_change_pct = dayChangePct,
            session_change_pct = sessionChangePct,
            distance_to_vwap_pct = distVwapPct,
            return_last_5_bars_pct = ret5,
            return_last_15_bars_pct = ret15,
            last_5_bar_volume = last5Vol,
            previous_5_bar_volume = prev5Vol > 0 ? (long?)prev5Vol : null,
            volume_acceleration_ratio = volAccel,
            vwap_cross_up_count = crossesUp,
            vwap_cross_down_count = crossesDown,
            max_rel_volume_last_15 = maxRel15 == 0 ? (decimal?)null : maxRel15,
            recent_15_high = recent15.Count > 0 ? recent15.Max(b => b.H) : (decimal?)null,
            recent_15_low = recent15.Count > 0 ? recent15.Min(b => b.L) : (decimal?)null,
            last_bar_rel_volume = bars.Count > 0 ? bars[^1].RelVolume : null,
            last_bar_body_ratio = bars.Count > 0 ? bars[^1].BodyRatio : null,
            regime
        };
    }

    private static object BuildPremarketSummary(IReadOnlyList<BarView> bars)
    {
        if (bars.Count == 0)
            return new { available = false } as object;

        var high = bars.Max(b => b.H);
        var low = bars.Min(b => b.L);
        var highBar = bars.First(b => b.H == high);
        var lowBar = bars.First(b => b.L == low);
        var volume = bars.Sum(b => b.V);
        return new
        {
            available = true,
            bars = bars.Count,
            open = bars[0].O,
            high,
            high_ts_utc = highBar.TsUtc,
            low,
            low_ts_utc = lowBar.TsUtc,
            close = bars[^1].C,
            volume,
            change_pct = Pct(bars[^1].C, bars[0].O)
        };
    }

    private static object CompactNews(JsonElement root)
    {
        if (!root.TryGetProperty("news_context", out var news) || news.ValueKind != JsonValueKind.Object)
            return new { enabled = false, article_count = 0 };

        var items = new List<object>();
        if (news.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                items.Add(new
                {
                    published_utc = GetDateTime(item, "published_utc"),
                    publisher = GetString(item, "publisher"),
                    title = Clip(GetString(item, "title"), 260),
                    description = Clip(GetString(item, "description"), 320),
                    insights = Clone(item, "insights")
                });
            }
        }

        return new
        {
            enabled = GetBool(news, "enabled"),
            provider = GetString(news, "provider"),
            asof_utc = GetDateTime(news, "asof_utc"),
            article_count = GetInt(news, "article_count") ?? items.Count,
            active_halt = GetNullableBool(news, "active_halt"),
            binary_event_pending = GetNullableBool(news, "binary_event_pending"),
            items = items.ToArray()
        };
    }

    private static object ToCompactMinuteBar(BarView b) => new
    {
        ts_utc = b.TsUtc,
        o = b.O,
        h = b.H,
        l = b.L,
        c = b.C,
        v = b.V,
        vwap = b.Vwap,
        dist_to_vwap = b.DistToVwap,
        body_ratio = b.BodyRatio,
        upper_wick = b.UpperWick,
        lower_wick = b.LowerWick,
        rel_volume = b.RelVolume,
        above_vwap = b.AboveVwap,
        below_vwap = b.BelowVwap,
        vwap_cross_up = b.VwapCrossUp,
        vwap_cross_down = b.VwapCrossDown
    };

    private static object[] AggregateBars(IReadOnlyList<BarView> bars, int minutes)
    {
        if (bars.Count == 0) return Array.Empty<object>();
        return bars
            .GroupBy(b => Floor(b.TsUtc, minutes))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var list = g.OrderBy(b => b.TsUtc).ToList();
                var rv = list.Where(b => b.RelVolume.HasValue).Select(b => b.RelVolume!.Value).DefaultIfEmpty().Max();
                return (object)new
                {
                    ts_utc = g.Key,
                    minutes,
                    o = list[0].O,
                    h = list.Max(b => b.H),
                    l = list.Min(b => b.L),
                    c = list[^1].C,
                    v = list.Sum(b => b.V),
                    last_vwap = list[^1].Vwap,
                    max_rel_volume = rv != 0 ? (decimal?)rv : null,
                    vwap_cross_up_count = list.Count(b => b.VwapCrossUp == true),
                    vwap_cross_down_count = list.Count(b => b.VwapCrossDown == true)
                };
            })
            .ToArray();
    }

    private static DateTime Floor(DateTime ts, int minutes)
    {
        ts = ts.Kind == DateTimeKind.Utc ? ts : DateTime.SpecifyKind(ts, DateTimeKind.Utc);
        var minute = (ts.Minute / minutes) * minutes;
        return new DateTime(ts.Year, ts.Month, ts.Day, ts.Hour, minute, 0, DateTimeKind.Utc);
    }

    private static decimal? ReturnOverBars(IReadOnlyList<BarView> bars, int count)
    {
        if (bars.Count < 2) return null;
        var start = bars[Math.Max(0, bars.Count - count)].C;
        return Pct(bars[^1].C, start);
    }

    private static decimal? Pct(decimal? value, decimal? baseline)
    {
        if (!value.HasValue || !baseline.HasValue || baseline.Value == 0) return null;
        return Math.Round((value.Value - baseline.Value) / baseline.Value * 100m, 3);
    }

    private static List<BarView> ReadBars(JsonElement root, string property)
    {
        var result = new List<BarView>();
        if (!root.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array) return result;
        foreach (var b in arr.EnumerateArray())
        {
            var ts = GetDateTime(b, "ts_utc");
            var o = GetDecimal(b, "o");
            var h = GetDecimal(b, "h");
            var l = GetDecimal(b, "l");
            var c = GetDecimal(b, "c");
            var v = GetLong(b, "v");
            if (!ts.HasValue || !o.HasValue || !h.HasValue || !l.HasValue || !c.HasValue || !v.HasValue) continue;
            result.Add(new BarView(
                ts.Value, o.Value, h.Value, l.Value, c.Value, v.Value,
                GetDecimal(b, "vwap"), GetDecimal(b, "dist_to_vwap"),
                GetDecimal(b, "body_ratio"), GetDecimal(b, "upper_wick"), GetDecimal(b, "lower_wick"),
                GetDecimal(b, "rel_volume"), GetBoolNullable(b, "above_vwap"), GetBoolNullable(b, "below_vwap"),
                GetBoolNullable(b, "vwap_cross_up"), GetBoolNullable(b, "vwap_cross_down")));
        }
        return result;
    }

    private sealed record BarView(
        DateTime TsUtc, decimal O, decimal H, decimal L, decimal C, long V,
        decimal? Vwap, decimal? DistToVwap, decimal? BodyRatio, decimal? UpperWick, decimal? LowerWick,
        decimal? RelVolume, bool? AboveVwap, bool? BelowVwap, bool? VwapCrossUp, bool? VwapCrossDown);

    private static object? Clone(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var e)) return null;
        return JsonSerializer.Deserialize<object>(e.GetRawText());
    }

    private static decimal? GetNestedDecimal(JsonElement root, string obj, string prop)
        => root.TryGetProperty(obj, out var e) && e.ValueKind == JsonValueKind.Object ? GetDecimal(e, prop) : null;

    private static string? GetString(JsonElement e, string p)
        => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static DateTime? GetDateTime(JsonElement e, string p)
        => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String && v.TryGetDateTime(out var d) ? d : null;
    private static decimal? GetDecimal(JsonElement e, string p)
        => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;
    private static long? GetLong(JsonElement e, string p)
        => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var d) ? d : null;
    private static int? GetInt(JsonElement e, string p)
        => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var d) ? d : null;
    private static bool? GetBoolNullable(JsonElement e, string p)
        => e.TryGetProperty(p, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : null;
    private static bool GetBool(JsonElement e, string p)
        => GetBoolNullable(e, p) ?? false;
    private static bool? GetNullableBool(JsonElement e, string p) => GetBoolNullable(e, p);
    private static string? Clip(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max] + "…";
}
