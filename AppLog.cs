namespace get_assessment_no_graph;

/// <summary>
/// Static application logger. Writes to System.Console.WriteLine and optionally to
/// the ScannerDisplay EVENTS panel simultaneously.
///
/// Usage:  AppLog.Info("ONDS PRODUCE_CARD reason=scanner-A+++");
///         AppLog.Trigger("ONDS ENTRY_EMITTED scenarioRank=1");
///         AppLog.Error("LLM failure for ONDS: timeout");
///
/// Call AppLog.SetDisplay(display) once at startup after the scanner is created.
/// All other code calls AppLog.* directly — no dependency injection needed.
/// </summary>
public static class AppLog
{
    private static ScannerDisplay? _display;

    /// <summary>Wire up the display once at startup.</summary>
    public static void SetDisplay(ScannerDisplay display) => _display = display;

    // ── Log levels ─────────────────────────────────────────────────────────────

    /// <summary>General info — white in display.</summary>
    public static void Info(string msg)
    {
        var line = $"{DateTime.UtcNow:HH:mm:ss}  {msg}";
        System.Console.WriteLine(line);
        _display?.Log(msg);
    }

    /// <summary>LLM call started or completed — cyan prefix in display.</summary>
    public static void Llm(string msg)
    {
        var line = $"{DateTime.UtcNow:HH:mm:ss}  [LLM] {msg}";
        System.Console.WriteLine(line);
        _display?.Log($"[LLM] {msg}");
    }

    /// <summary>Signal emitted — highlighted in display.</summary>
    public static void Trigger(string msg)
    {
        var line = $"{DateTime.UtcNow:HH:mm:ss}  🔔 {msg}";
        System.Console.WriteLine(line);
        _display?.Log($"🔔 {msg}");
    }

    /// <summary>Error or failure — red in display.</summary>
    public static void Error(string msg)
    {
        var line = $"{DateTime.UtcNow:HH:mm:ss}  ❌ {msg}";
        System.Console.WriteLine(line);
        _display?.Log($"❌ {msg}");
    }

    /// <summary>Update pipeline state in the display without logging a message.</summary>
    public static void UpdatePipeline(string ticker,
        ScoreGateState? scoreGate = null, string? scoreLabel = null,
        LlmState?       llm       = null, string? llmLabel   = null,
        CardGateState?  cardGate  = null, string? cardLabel  = null,
        SignalState?    signal    = null)
    {
        _display?.UpdatePipeline(ticker, scoreGate, scoreLabel,
                                  llm, llmLabel, cardGate, cardLabel, signal);
    }

    /// <summary>Write to console only — no display update.</summary>
    public static void Console(string msg) =>
        System.Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss}  {msg}");

    /// <summary>Scanner A+++ trigger — yellow in display.</summary>
    public static void ScannerFire(string msg)
    {
        var line = $"{DateTime.UtcNow:HH:mm:ss}  🔥 {msg}";
        System.Console.WriteLine(line);
        _display?.Log($"🔥 {msg}");
    }
}