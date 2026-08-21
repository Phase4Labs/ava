using System.Text.Json.Nodes;

namespace get_assessment_no_graph;

/// <summary>
/// Builds session_vp, composite_vp, and vp_context blocks from existing bar data.
/// Called from PayloadBuilder after all bar data is fetched.
/// </summary>
public static class VolumeProfileDatasetBuilder
{
    public sealed record VpBlocks(
        JsonObject SessionVp,
        JsonObject CompositeVp,
        JsonObject VpContext
    );

    public static VpBlocks Build(
        IReadOnlyList<MinuteBar>    intradayBars,
        IReadOnlyList<MinuteBar>    premarketBars,
        IReadOnlyList<DailyBar>     priorDayBars,
        decimal                     lastClose)
    {
        // Session VP = premarket + intraday bars
        var sessionBars = premarketBars
            .Select(b => new VolumeProfile.Bar(b.H, b.L, b.C, b.V))
            .Concat(intradayBars.Select(b => new VolumeProfile.Bar(b.H, b.L, b.C, b.V)))
            .ToList();

        var sessionVp = VolumeProfile.Compute(sessionBars, sessions: 1);

        // Composite VP = prior daily bars
        var compositeBars = priorDayBars
            .Select(b => new VolumeProfile.Bar(b.H, b.L, b.C, b.V))
            .ToList();

        var compositeVp = VolumeProfile.Compute(compositeBars, sessions: compositeBars.Count);

        return new VpBlocks(
            SessionVp:   ToJson(sessionVp),
            CompositeVp: ToJson(compositeVp),
            VpContext:   BuildContext(lastClose, sessionVp, compositeVp)
        );
    }

    // Overload for MinuteBarRow (DB rows, used in PayloadBuilder's bars list)
    public static VpBlocks BuildFromRows(
        IReadOnlyList<MinuteBarRow> intradayRows,
        IReadOnlyList<MinuteBar>    premarketBars,
        IReadOnlyList<DailyBar>     priorDayBars,
        decimal                     lastClose)
    {
        var sessionBars = premarketBars
            .Select(b => new VolumeProfile.Bar(b.H, b.L, b.C, b.V))
            .Concat(intradayRows.Select(b => new VolumeProfile.Bar(b.H, b.L, b.C, b.V)))
            .ToList();

        var sessionVp = VolumeProfile.Compute(sessionBars, sessions: 1);

        var compositeBars = priorDayBars
            .Select(b => new VolumeProfile.Bar(b.H, b.L, b.C, b.V))
            .ToList();

        var compositeVp = VolumeProfile.Compute(compositeBars, sessions: compositeBars.Count);

        return new VpBlocks(
            SessionVp:   ToJson(sessionVp),
            CompositeVp: ToJson(compositeVp),
            VpContext:   BuildContext(lastClose, sessionVp, compositeVp)
        );
    }

    // ── JSON helpers ──────────────────────────────────────────────

    private static JsonObject ToJson(VolumeProfile.VolumeProfileResult vp)
    {
        var obj = new JsonObject
        {
            ["poc"]              = vp.Poc,
            ["vah"]              = vp.Vah,
            ["val"]              = vp.Val,
            ["value_area_width"] = vp.ValueAreaWidth,
            ["bin_size"]         = vp.BinSize,
            ["sessions"]         = vp.Sessions,
        };
        var hvn = new JsonArray();
        foreach (var h in vp.Hvn) hvn.Add(h);
        obj["hvn"] = hvn;

        var lvn = new JsonArray();
        foreach (var l in vp.Lvn) lvn.Add(l);
        obj["lvn"] = lvn;

        return obj;
    }

    private static JsonObject BuildContext(
        decimal price,
        VolumeProfile.VolumeProfileResult session,
        VolumeProfile.VolumeProfileResult composite)
    {
        var obj = new JsonObject
        {
            ["price_vs_session_va"]   = LocationLabel(price, session.Val,   session.Vah),
            ["price_vs_composite_va"] = LocationLabel(price, composite.Val, composite.Vah),
        };

        if (session.Poc > 0 && composite.Poc > 0)
            obj["session_poc_vs_composite_poc"] = session.Poc > composite.Poc ? "above"
                : session.Poc < composite.Poc   ? "below" : "at";

        if (session.Val > 0 && composite.Val > 0)
        {
            obj["migration"] =
                session.Val > composite.Vah  ? "migrating_higher"  :
                session.Vah < composite.Val  ? "migrating_lower"   :
                session.Poc > composite.Poc  ? "developing_higher" :
                session.Poc < composite.Poc  ? "developing_lower"  :
                                               "overlapping";
        }

        if (composite.Hvn.Length > 0)
        {
            var above = composite.Hvn.Where(h => h > price).OrderBy(h => h).FirstOrDefault();
            var below = composite.Hvn.Where(h => h < price).OrderByDescending(h => h).FirstOrDefault();
            if (above > 0) obj["nearest_hvn_above"] = above;
            if (below > 0) obj["nearest_hvn_below"] = below;
        }

        if (composite.Lvn.Length > 0)
        {
            var above = composite.Lvn.Where(l => l > price).OrderBy(l => l).FirstOrDefault();
            var below = composite.Lvn.Where(l => l < price).OrderByDescending(l => l).FirstOrDefault();
            if (above > 0) obj["nearest_lvn_above"] = above;
            if (below > 0) obj["nearest_lvn_below"] = below;
        }

        return obj;
    }

    private static string LocationLabel(decimal price, decimal val, decimal vah)
    {
        if (val == 0 && vah == 0) return "unknown";
        if (price > vah) return "above_va";
        if (price < val) return "below_va";
        return "inside_va";
    }
}
