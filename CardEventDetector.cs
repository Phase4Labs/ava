using System.Text.Json;

namespace get_assessment_no_graph;

/// <summary>
/// Detects meaningful market events from an already-built dataset JSON that justify
/// producing an OpenAI card outside of the regular schedule.
///
/// All detection is done from the dataset JSON already in memory — no extra DB or
/// API calls. Events are returned as named strings so the caller can log the reason.
///
/// Tunable thresholds are constants — adjust here to calibrate sensitivity.
/// </summary>
public static class CardEventDetector
{
    // ── Thresholds ────────────────────────────────────────────────────────────

    /// Relative volume on the last bar to consider a volume spike (vs recent 5-bar avg).
    private const decimal VolSpikeMult = 3.0m;

    /// Minimum bars elapsed before HOD/LOD break events fire (avoids first-minute noise).
    private const int MinBarsForHodLod = 5;

    /// Cooldown in bars between repeated HOD/LOD break triggers to avoid firing every
    /// bar during a sustained trend. Set to 0 to disable cooldown.
    private const int HodLodCooldownBars = 10;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Inspects the dataset JSON and returns a list of event names that fired.
    /// Empty list = no events, card should follow normal schedule.
    /// </summary>
    public static List<string> Detect(
        JsonDocument doc,
        DateTime? lastCardProducedAt,
        DateTime lastClosedBar)
    {
        var events = new List<string>();

        try
        {
            var root = doc.RootElement;

            if (!root.TryGetProperty("intraday_bars", out var barsEl) ||
                barsEl.ValueKind != JsonValueKind.Array)
                return events;

            var bars = barsEl.EnumerateArray().ToList();
            if (bars.Count < 2) return events;

            var last = bars[^1];
            var prev = bars[^2];

            // ── 1. Volume spike ───────────────────────────────────────────────
            // rel_volume is bar_volume / avg_volume_5 — computed by SessionFeatureCalculator.
            // A spike means the last bar traded at significantly elevated pace.
            if (last.TryGetProperty("rel_volume", out var rvEl) &&
                rvEl.ValueKind == JsonValueKind.Number)
            {
                var relVol = rvEl.GetDecimal();
                if (relVol >= VolSpikeMult)
                    events.Add($"VOL_SPIKE rvol={relVol:F1}x");
            }

            // ── 2. VWAP cross ─────────────────────────────────────────────────
            // vwap_cross_up / vwap_cross_down are set by SessionFeatureCalculator
            // when the close crosses VWAP relative to the prior bar's close.
            if (last.TryGetProperty("vwap_cross_up", out var xUp) &&
                xUp.ValueKind == JsonValueKind.True)
                events.Add("VWAP_CROSS_UP");

            if (last.TryGetProperty("vwap_cross_down", out var xDn) &&
                xDn.ValueKind == JsonValueKind.True)
                events.Add("VWAP_CROSS_DOWN");

            // ── 3. New session high / low (HOD / LOD break) ───────────────────
            // Only fires after MinBarsForHodLod bars to avoid first-minute whipsaws,
            // and respects a cooldown so it doesn't fire every bar during a trend.
            if (bars.Count >= MinBarsForHodLod)
            {
                // Compute session high/low EXCLUDING the last bar
                decimal priorHigh = decimal.MinValue;
                decimal priorLow  = decimal.MaxValue;
                for (int i = 0; i < bars.Count - 1; i++)
                {
                    if (bars[i].TryGetProperty("h", out var hEl) && hEl.ValueKind == JsonValueKind.Number)
                        priorHigh = Math.Max(priorHigh, hEl.GetDecimal());
                    if (bars[i].TryGetProperty("l", out var lEl) && lEl.ValueKind == JsonValueKind.Number)
                        priorLow = Math.Min(priorLow, lEl.GetDecimal());
                }

                // Cooldown: how many bars since last card?
                int barsSinceLastCard = int.MaxValue;
                if (lastCardProducedAt.HasValue)
                    barsSinceLastCard = (int)(lastClosedBar - lastCardProducedAt.Value).TotalMinutes;

                bool cooldownOk = barsSinceLastCard >= HodLodCooldownBars;

                if (last.TryGetProperty("h", out var lastH) && lastH.ValueKind == JsonValueKind.Number)
                {
                    if (lastH.GetDecimal() > priorHigh && cooldownOk)
                        events.Add($"NEW_HOD high={lastH.GetDecimal():F2} prev_hod={priorHigh:F2}");
                }

                if (last.TryGetProperty("l", out var lastL) && lastL.ValueKind == JsonValueKind.Number)
                {
                    if (lastL.GetDecimal() < priorLow && cooldownOk)
                        events.Add($"NEW_LOD low={lastL.GetDecimal():F2} prev_lod={priorLow:F2}");
                }
            }

            // ── 4. Key level approach ─────────────────────────────────────────
            // Fires when price comes within 0.3% of prior day close, prior day high,
            // or premarket high/low — levels OpenAI uses for entry zone identification.
            // Only fires if we weren't already near that level on the previous bar.
            const decimal ProximityPct = 0.003m; // 0.3%

            if (last.TryGetProperty("c", out var lastC) && lastC.ValueKind == JsonValueKind.Number &&
                prev.TryGetProperty("c", out var prevC) && prevC.ValueKind == JsonValueKind.Number)
            {
                var c    = lastC.GetDecimal();
                var pC   = prevC.GetDecimal();

                var levels = new List<(string name, decimal? price)>();

                if (root.TryGetProperty("reference_levels", out var refLevels))
                {
                    levels.Add(("PRIOR_DAY_CLOSE", TryGetDecimal(refLevels, "prior_day_close")));
                    levels.Add(("PRIOR_DAY_HIGH",  TryGetDecimal(refLevels, "prior_day_high")));
                    levels.Add(("PRIOR_DAY_LOW",   TryGetDecimal(refLevels, "prior_day_low")));
                    levels.Add(("PREMARKET_HIGH",  TryGetDecimal(refLevels, "premarket_high")));
                    levels.Add(("PREMARKET_LOW",   TryGetDecimal(refLevels, "premarket_low")));
                }

                foreach (var (name, price) in levels)
                {
                    if (price is null || price == 0) continue;

                    var distNow  = Math.Abs(c  - price.Value) / price.Value;
                    var distPrev = Math.Abs(pC - price.Value) / price.Value;

                    // Only trigger when approaching from outside proximity zone
                    if (distNow <= ProximityPct && distPrev > ProximityPct)
                        events.Add($"LEVEL_APPROACH {name}={price.Value:F2} dist={distNow * 100:F2}%");
                }
            }
        }
        catch (Exception ex)
        {
            // Detection is best-effort — never block card production on a detector error
            Console.WriteLine($"[WARN] CardEventDetector error: {ex.Message}");
        }

        return events;
    }

    private static decimal? TryGetDecimal(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number)
            return v.GetDecimal();
        return null;
    }
}
