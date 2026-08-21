using System;
using System.Collections.Generic;
using System.Linq;

namespace get_assessment_no_graph;

public sealed record BarWithFeat(
    DateTime TsUtc,
    decimal O, decimal H, decimal L, decimal C,
    long V,
    decimal Vwap,
    decimal DistToVwap
);

public static class ScenarioDetectors
{
    // Dynamic tolerance:
    // - Minimum: $0.01
    // - Scales with price (5 bps)
    // - Maximum: $0.10
    private static decimal Tol(decimal price)
    {
        var t = price * 0.0005m;
        if (t < 0.01m) t = 0.01m;
        if (t > 0.10m) t = 0.10m;
        return t;
    }

    // -------------------------
    // Reclaim & Hold
    // -------------------------
    // LONG:
    // - must have entry_high
    // - some close was <= entry_high - tol recently
    // - last 2 closes >= entry_high + tol
    // - optional: last bar retested near entry_high (low <= entry_high + tol)
    //
    // SHORT:
    // - must have entry_low
    // - some close was >= entry_low + tol recently
    // - last 2 closes <= entry_low - tol
    // - optional: last bar retested near entry_low (high >= entry_low - tol)
    public static bool IsReclaimHoldPresented(IReadOnlyList<BarWithFeat> bars, ParsedScenario s, out string reason)
    {
        reason = "";
        // Need at least 3 bars: last, prev, prior — all three are accessed unconditionally below
        if (bars.Count < 3 || s.EntryLow is null || s.EntryHigh is null)
        {
            reason = "insufficient bars or null entry bounds";
            return false;
        }

        var last = bars[^1];
        var prev = bars[^2];
        var prior = bars[^3];

        var tol = Tol(last.C);

        // lookback window excludes last 2 bars (prev + last), capped at 10, minimum 1
        int lookback = Math.Min(10, bars.Count - 2);
        var recent = bars.Skip(bars.Count - 2 - lookback).Take(lookback).ToList();

        if (string.Equals(s.Direction, "long", StringComparison.OrdinalIgnoreCase))
        {
            if (s.EntryHigh is null) {
                reason = "null entry_high for LONG";
                return false;
            }

            var thr = s.EntryHigh.Value;

            bool nowHeld =
                prev.C >= thr + tol &&
                last.C >= thr + tol;

            if (!nowHeld) {
                reason = $"not now held above entry_high {prev.C:F4},{last.C:F4} < {thr + tol:F4}";
                return false;
            }

            // Ensure we were meaningfully below before the hold (avoid "always above" false positives)
            bool wasBelow = recent.Any(b => b.C <= thr - tol);
            if (!wasBelow) {
                reason = $"was not below entry_high {thr - tol:F4}";
                return false;
            }

            // Ensure there was a crossing from below to above recently
            bool crossed = prior.C <= thr - tol;
            if (!crossed)
            {
                // fallback: find any close below before prev/last
                int lastBelowIdx = -1;
                for (int i = 0; i < bars.Count - 2; i++)
                {
                    if (bars[i].C <= thr - tol) lastBelowIdx = i;
                }
                crossed = lastBelowIdx >= Math.Max(0, bars.Count - 2 - lookback);
            }
            if (!crossed)  {
                reason = $"no crossing from below to above entry_high {thr - tol:F4}";
                return false;
            }

            bool retestOk = last.L <= thr + tol; // mild "hold" feel
            reason = retestOk
                ? $"ReclaimHold LONG: reclaimed/held >= {thr:F4} (tol {tol:F4}) with retest"
                : $"ReclaimHold LONG: reclaimed/held >= {thr:F4} (tol {tol:F4})";
            return true;
        }
        else
        {
            if (s.EntryLow is null) {
                reason = "null entry_low for SHORT";
                return false;
            }

            var thr = s.EntryLow.Value;

            bool nowHeld =
                prev.C <= thr - tol &&
                last.C <= thr - tol;

            if (!nowHeld) {
                reason = $"not now held below entry_low {prev.C:F4},{last.C:F4} > {thr - tol:F4}";
                return false;
            }

            bool wasAbove = recent.Any(b => b.C >= thr + tol);
            if (!wasAbove) {
                reason = $"was not above entry_low {thr + tol:F4}";
                return false;
            }

            bool crossed = prior.C >= thr + tol;
            if (!crossed)
            {
                int lastAboveIdx = -1;
                for (int i = 0; i < bars.Count - 2; i++)
                {
                    if (bars[i].C >= thr + tol) lastAboveIdx = i;
                }
                crossed = lastAboveIdx >= Math.Max(0, bars.Count - 2 - lookback);
            }
            if (!crossed) {
                reason = $"no crossing from above to below entry_low {thr + tol:F4}";
                return false;
            }

            bool retestOk = last.H >= thr - tol;
            reason = retestOk
                ? $"ReclaimHold SHORT: reclaimed/held <= {thr:F4} (tol {tol:F4}) with retest"
                : $"ReclaimHold SHORT: reclaimed/held <= {thr:F4} (tol {tol:F4})";
            return true;
        }
    }

    // -------------------------
    // Break & Hold
    // -------------------------
    // A simpler, more robust "break + hold" definition:
    //
    // LONG:
    // - must have entry_high
    // - last 2 closes >= entry_high + tol
    // - last low >= entry_high - tol   (hold / acceptance)
    //
    // SHORT:
    // - must have entry_low
    // - last 2 closes <= entry_low - tol
    // - last high <= entry_low + tol   (hold / acceptance)
    //
    // If opposite bound exists, we only use it as a "deep failure" filter (not a strict noFailure).
    public static bool IsBreakHoldPresented(IReadOnlyList<BarWithFeat> bars, ParsedScenario s, out string reason)
    {
        reason = "";
        if (bars.Count < 2 || s.EntryLow is null || s.EntryHigh is null)
        {
            reason = "insufficient bars or null entry bounds";
            return false;
        }

        var last = bars[^1];
        var prev = bars[^2];

        var tol = Tol(last.C);

        if (string.Equals(s.Direction, "long", StringComparison.OrdinalIgnoreCase))
        {
            if (s.EntryHigh is null) {
                reason = "null entry_high for LONG";
                return false;
            }
            var thr = s.EntryHigh.Value;

            bool held2 = prev.C >= thr + tol && last.C >= thr + tol;
            if (!held2) {
                reason = $"not held2 above entry_high {prev.C:F4},{last.C:F4} < {thr + tol:F4}";
                return false;
            }

            bool hold = last.L >= thr - tol;
            if (!hold) {
                reason = $"not held below entry_high {thr - tol:F4}";
                return false;
            }

            // Optional deep-failure check if entry_low exists (only reject if it really dumped)
            if (s.EntryLow is not null)
            {
                var fail = last.C <= s.EntryLow.Value - (2m * tol);
                if (fail) {
                    reason = $"deep failure below entry_low {s.EntryLow.Value - (2m * tol):F4}";
                    return false;
                }
            }

            reason = $"BreakHold LONG: 2 closes >= {thr:F4} and low held (tol {tol:F4})";
            return true;
        }
        else
        {
            if (s.EntryLow is null) {
                reason = "null entry_low for SHORT";
                return false;
            }
            var thr = s.EntryLow.Value;

            bool held2 = prev.C <= thr - tol && last.C <= thr - tol;
            if (!held2) {
                reason = $"not held2 below entry_low {prev.C:F4},{last.C:F4} > {thr - tol:F4}";
                return false;
            }

            bool hold = last.H <= thr + tol;
            if (!hold) {
                reason = $"not held above entry_low {thr + tol:F4}";
                return false;
            }

            // Optional deep-failure check if entry_high exists (only reject if it really ripped up)
            if (s.EntryHigh is not null)
            {
                var fail = last.C >= s.EntryHigh.Value + (2m * tol);
                if (fail) {
                    reason = $"deep failure above entry_high {s.EntryHigh.Value + (2m * tol):F4}";
                    return false;
                }
            }

            reason = $"BreakHold SHORT: 2 closes <= {thr:F4} and high held (tol {tol:F4})";
            return true;
        }
    }

    // -------------------------
    // Fade Pops (rejection at entry zone)
    // -------------------------
    // SHORT:
    // - must have entry_low (threshold)
    // - last bar traded into zone (H >= entry_low - tol)
    // - closed back below entry_low - tol
    // - meaningful upper wick
    // - if entry_high exists: prefer body top not far above zoneHigh (avoid "not a zone rejection")
    //
    // LONG (mirror):
    // - must have entry_high
    // - last bar traded into zone (L <= entry_high + tol)
    // - closed back above entry_high + tol
    // - meaningful lower wick
    // - if entry_low exists: prefer body bottom not far below zoneLow
    public static bool IsFadePopPresented(IReadOnlyList<BarWithFeat> bars, ParsedScenario s, out string reason)
    {
        reason = "";
        if (bars.Count < 2 || s.EntryLow is null || s.EntryHigh is null)
        {
            reason = $"insufficient bars or null entry bounds {bars.Count}";
            return false;
        }

        var last = bars[^1];
        var tol = Tol(last.C);

        decimal range = last.H - last.L;
        decimal bodyTop = Math.Max(last.O, last.C);
        decimal bodyBot = Math.Min(last.O, last.C);
        decimal upperWick = last.H - bodyTop;
        decimal lowerWick = bodyBot - last.L;

        // Wick threshold: at least 25% of range OR at least 2*tolerance (avoid tiny-noise triggers)
        decimal wickNeed = Math.Max(range * 0.25m, 2m * tol);

        if (string.Equals(s.Direction, "short", StringComparison.OrdinalIgnoreCase))
        {
            if (s.EntryLow is null) {
                reason = "null entry_low for SHORT";
                return false;
            }

            var zoneLow = s.EntryLow.Value;
            var zoneHigh = s.EntryHigh; // optional

            bool tagged = last.H >= zoneLow - tol;
            bool rejected = last.C <= zoneLow - tol;

            if (!tagged || !rejected) {
                reason = $"not tagged/rejected at entry_low {zoneLow:F4}";
                return false;
            }

            // If zoneHigh exists, avoid cases where the real action was far above the intended zone
            if (zoneHigh is not null)
            {
                // Prefer that the candle body top is not far beyond zoneHigh
                if (bodyTop > zoneHigh.Value + tol && last.C > zoneLow) {
                    reason = $"body top far above zoneHigh {zoneHigh.Value + tol:F4}";
                    return false;
                }
            }

            bool wickOk = upperWick >= wickNeed;
            if (!wickOk) {
                reason = $"upper wick not sufficient {upperWick:F4} < {wickNeed:F4}";
                return false;
            }

            reason = $"FadePop SHORT: tagged >= {zoneLow:F4} then closed <= {zoneLow:F4} with upper wick (tol {tol:F4})";
            return true;
        }
        else
        {
            if (s.EntryHigh is null) {
                reason = "null entry_high for LONG";
                return false;
            }

            var zoneHigh = s.EntryHigh.Value;
            var zoneLow = s.EntryLow; // optional

            bool tagged = last.L <= zoneHigh + tol;
            bool rejected = last.C >= zoneHigh + tol;

            if (!tagged || !rejected) {
                reason = $"not tagged/rejected at entry_high {zoneHigh:F4}";
                return false;
            }

            if (zoneLow is not null)
            {
                // Prefer body bottom not far below zoneLow
                if (bodyBot < zoneLow.Value - tol && last.C < zoneHigh) return false;
            }

            bool wickOk = lowerWick >= wickNeed;
            if (!wickOk) {
                reason = $"lower wick not sufficient {lowerWick:F4} < {wickNeed:F4}";
                return false;
            }

            reason = $"FadePop LONG: tagged <= {zoneHigh:F4} then closed >= {zoneHigh:F4} with lower wick (tol {tol:F4})";
            return true;
        }
    }

    // -------------------------
    // VWAP Reclaim & Hold
    // -------------------------
    // Distinct from ReclaimHold: the level is VWAP itself (dynamic), not a fixed entry zone.
    //
    // LONG:
    // - price was below VWAP recently (within last 10 bars)
    // - last 2 closes are above VWAP
    // - last bar's low stayed within tolerance of VWAP (holding above)
    //
    // SHORT (mirror):
    // - price was above VWAP recently
    // - last 2 closes are below VWAP
    // - last bar's high stayed within tolerance of VWAP (holding below)
    public static bool IsVwapReclaimPresented(IReadOnlyList<BarWithFeat> bars, ParsedScenario s, out string reason)
    {
        reason = "";
        if (bars.Count < 3)
        {
            reason = "insufficient bars";
            return false;
        }

        var last = bars[^1];
        var prev = bars[^2];
        var tol  = Tol(last.C);

        var vwap = last.Vwap;
        if (vwap <= 0)
        {
            reason = "vwap not available";
            return false;
        }

        int lookback = Math.Min(10, bars.Count - 2);
        var recent = bars.Skip(bars.Count - 2 - lookback).Take(lookback).ToList();

        if (string.Equals(s.Direction, "long", StringComparison.OrdinalIgnoreCase))
        {
            bool nowAbove = prev.C > vwap + tol && last.C > vwap + tol;
            if (!nowAbove)
            {
                reason = $"not now holding above VWAP {vwap:F4} (prev={prev.C:F4}, last={last.C:F4})";
                return false;
            }

            bool wasBelow = recent.Any(b => b.C < vwap - tol);
            if (!wasBelow)
            {
                reason = $"price was not below VWAP {vwap:F4} recently";
                return false;
            }

            bool holdingAbove = last.L >= vwap - tol;
            reason = holdingAbove
                ? $"VwapReclaim LONG: reclaimed and holding above VWAP {vwap:F4} with retest (tol {tol:F4})"
                : $"VwapReclaim LONG: reclaimed and holding above VWAP {vwap:F4} (tol {tol:F4})";
            return true;
        }
        else
        {
            bool nowBelow = prev.C < vwap - tol && last.C < vwap - tol;
            if (!nowBelow)
            {
                reason = $"not now holding below VWAP {vwap:F4} (prev={prev.C:F4}, last={last.C:F4})";
                return false;
            }

            bool wasAbove = recent.Any(b => b.C > vwap + tol);
            if (!wasAbove)
            {
                reason = $"price was not above VWAP {vwap:F4} recently";
                return false;
            }

            bool holdingBelow = last.H <= vwap + tol;
            reason = holdingBelow
                ? $"VwapReclaim SHORT: reclaimed and holding below VWAP {vwap:F4} with retest (tol {tol:F4})"
                : $"VwapReclaim SHORT: reclaimed and holding below VWAP {vwap:F4} (tol {tol:F4})";
            return true;
        }
    }
    // -------------------------
    // Overextension Fade
    // -------------------------
    // Triggered when price has run far from VWAP (overextended) and the last bar
    // shows a rejection candle — upper wick (SHORT) or lower wick (LONG) — indicating
    // exhaustion at the extreme. Entry zone (entry_low/entry_high) marks the fade
    // target level; we require the bar to have tagged into or beyond the zone and
    // then closed back inside it.
    //
    // SHORT: price extended well above VWAP, last bar tagged >= entry_low and closed
    //        below it with a meaningful upper wick.
    // LONG:  price extended well below VWAP, last bar tagged <= entry_high and closed
    //        above it with a meaningful lower wick.
    //
    // If entry_low / entry_high are null the LLM didn't specify a precise zone —
    // fall back to requiring only the wick rejection on the last bar while price
    // is on the correct side of VWAP.
    public static bool IsOverextensionFadePresented(IReadOnlyList<BarWithFeat> bars, ParsedScenario s, out string reason)
    {
        reason = "";
        if (bars.Count < 3)
        {
            reason = $"insufficient bars ({bars.Count})";
            return false;
        }

        var last = bars[^1];
        var tol  = Tol(last.C);

        decimal range    = last.H - last.L;
        decimal bodyTop  = Math.Max(last.O, last.C);
        decimal bodyBot  = Math.Min(last.O, last.C);
        decimal upperWick = last.H - bodyTop;
        decimal lowerWick = bodyBot - last.L;

        // Require a meaningful rejection wick — at least 30% of range or 2*tol
        decimal wickNeed = Math.Max(range * 0.30m, 2m * tol);

        bool isShort = string.Equals(s.Direction, "short", StringComparison.OrdinalIgnoreCase);

        if (isShort)
        {
            // With entry zone: require tag + close-back-inside + upper wick
            if (s.EntryLow is not null)
            {
                var zoneLow = s.EntryLow.Value;
                bool tagged   = last.H >= zoneLow - tol;
                bool closedIn = last.C <= zoneLow + tol;
                bool wickOk   = upperWick >= wickNeed;

                if (!tagged)  { reason = $"OE-Fade SHORT: bar did not tag entry_low {zoneLow:F4}"; return false; }
                if (!closedIn){ reason = $"OE-Fade SHORT: closed above entry_low {zoneLow:F4} (close={last.C:F4})"; return false; }
                if (!wickOk)  { reason = $"OE-Fade SHORT: upper wick insufficient {upperWick:F4} < {wickNeed:F4}"; return false; }

                reason = $"OE-Fade SHORT: tagged {zoneLow:F4}, closed={last.C:F4}, wick={upperWick:F4}";
                return true;
            }
            else
            {
                // No zone — just require upper wick rejection while above VWAP
                bool aboveVwap = last.C > last.Vwap;
                bool wickOk    = upperWick >= wickNeed;

                if (!aboveVwap){ reason = $"OE-Fade SHORT (no-zone): not above VWAP (close={last.C:F4} vwap={last.Vwap:F4})"; return false; }
                if (!wickOk)   { reason = $"OE-Fade SHORT (no-zone): upper wick insufficient {upperWick:F4} < {wickNeed:F4}"; return false; }

                reason = $"OE-Fade SHORT (no-zone): close={last.C:F4} above vwap={last.Vwap:F4}, wick={upperWick:F4}";
                return true;
            }
        }
        else
        {
            // LONG: price overextended below VWAP, fading back up
            if (s.EntryHigh is not null)
            {
                var zoneHigh = s.EntryHigh.Value;
                bool tagged   = last.L <= zoneHigh + tol;
                bool closedIn = last.C >= zoneHigh - tol;
                bool wickOk   = lowerWick >= wickNeed;

                if (!tagged)  { reason = $"OE-Fade LONG: bar did not tag entry_high {zoneHigh:F4}"; return false; }
                if (!closedIn){ reason = $"OE-Fade LONG: closed below entry_high {zoneHigh:F4} (close={last.C:F4})"; return false; }
                if (!wickOk)  { reason = $"OE-Fade LONG: lower wick insufficient {lowerWick:F4} < {wickNeed:F4}"; return false; }

                reason = $"OE-Fade LONG: tagged {zoneHigh:F4}, closed={last.C:F4}, wick={lowerWick:F4}";
                return true;
            }
            else
            {
                bool belowVwap = last.C < last.Vwap;
                bool wickOk    = lowerWick >= wickNeed;

                if (!belowVwap){ reason = $"OE-Fade LONG (no-zone): not below VWAP (close={last.C:F4} vwap={last.Vwap:F4})"; return false; }
                if (!wickOk)   { reason = $"OE-Fade LONG (no-zone): lower wick insufficient {lowerWick:F4} < {wickNeed:F4}"; return false; }

                reason = $"OE-Fade LONG (no-zone): close={last.C:F4} below vwap={last.Vwap:F4}, wick={lowerWick:F4}";
                return true;
            }
        }
    }

}