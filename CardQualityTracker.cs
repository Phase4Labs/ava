namespace get_assessment_no_graph;

/// <summary>
/// Summary of a single card produced by OpenAI.
/// Returned by RunOnceAsync so Program.cs can feed it into CardQualityTracker.
/// </summary>
public sealed record CardQualitySummary(
    string  Verdict,            // "TRADE" or "NO_TRADE"
    int     ScenarioCount,      // 0..3
    decimal AvgScenarioProb,    // mean scenario_prob across scenarios (0 if NO_TRADE)
    decimal AvgSuccessProb,     // mean success_prob across scenarios (0 if NO_TRADE)
    bool    AnyScenarioQualified // true if any scenario passed TriggerEngine thresholds
);

/// <summary>
/// Tracks recent card quality per ticker and recommends cadence adjustments.
///
/// A ticker is considered "cold" when its recent cards are consistently low quality:
///   - verdict = NO_TRADE, OR
///   - all scenarios below TriggerEngine probability thresholds
///
/// Cold tickers get their cadence backed off (longer interval between cards).
/// Cadence restores immediately when:
///   - a meaningful event fires (CardEventDetector handles this in Program.cs), OR
///   - a TRADE card with qualified scenarios is produced
///
/// All state is in-memory — resets on process restart, which is fine since
/// quality state should be re-evaluated fresh each session.
/// </summary>
public sealed class CardQualityTracker
{
    // ── Thresholds ────────────────────────────────────────────────────────────

    /// How many recent cards to evaluate for cold detection.
    private const int WindowSize = 3;

    /// Normal card interval (minutes). Matches cardIntervalMinutes in Program.cs.
    public const int NormalIntervalMinutes = 5;

    /// Backed-off interval when ticker is cold.
    public const int ColdIntervalMinutes = 15;

    // ── State ─────────────────────────────────────────────────────────────────

    private readonly Dictionary<string, Queue<CardQualitySummary>> _history =
        new(StringComparer.OrdinalIgnoreCase);

    // ── Public API ────────────────────────────────────────────────────────────

    /// Record a card result for a ticker.
    public void Record(string ticker, CardQualitySummary summary)
    {
        if (!_history.TryGetValue(ticker, out var q))
        {
            q = new Queue<CardQualitySummary>(WindowSize);
            _history[ticker] = q;
        }

        q.Enqueue(summary);
        if (q.Count > WindowSize) q.Dequeue();
    }

    /// Returns the effective card interval for this ticker (minutes).
    /// Use this instead of cardIntervalMinutes in the cadence gate.
    public int GetEffectiveInterval(string ticker)
        => IsCold(ticker) ? ColdIntervalMinutes : NormalIntervalMinutes;

    /// True if the ticker's recent card history is consistently low quality.
    public bool IsCold(string ticker)
    {
        if (!_history.TryGetValue(ticker, out var q) || q.Count < WindowSize)
            return false;   // not enough history yet — assume warm

        return q.All(IsLowQuality);
    }

    /// Summary string for console logging.
    public string GetStatusLine(string ticker)
    {
        if (!_history.TryGetValue(ticker, out var q) || q.Count == 0)
            return "no history";

        var cold    = IsCold(ticker);
        var recent  = q.Last();
        var trades  = q.Count(c => c.Verdict == "TRADE");
        var interval = GetEffectiveInterval(ticker);

        return $"{(cold ? "COLD" : "warm")} " +
               $"trades={trades}/{q.Count} " +
               $"last={recent.Verdict} scenarios={recent.ScenarioCount} " +
               $"interval={interval}min";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsLowQuality(CardQualitySummary c)
    {
        if (c.Verdict == "NO_TRADE") return true;
        if (!c.AnyScenarioQualified) return true;
        return false;
    }
}
