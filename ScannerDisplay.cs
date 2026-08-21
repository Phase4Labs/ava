using System.Collections.Concurrent;
using System.Drawing;
using System.Windows.Forms;

namespace get_assessment_no_graph;

// ── Pipeline state enums — public so ScannerService, TriggerEngine, AppLog can reference them ──

public enum ScoreGateState  { None, Cooldown, ThinData, Passed }
public enum LlmState        { None, InProgress, NoTrade, Error, Trade }
public enum CardGateState   { None, LowConf, NoBars, Passed }
public enum SignalState     { None, Pending, Emitted }

/// <summary>
/// WinForms scanner display window.
/// Replaces ScannerDisplay (console version) — same public API, zero changes to ScannerService.
///
/// Call Launch() once at startup. The form runs on a dedicated STA thread
/// so it never blocks the main async assessment loop.
/// </summary>
public sealed class ScannerDisplay
{
    private ScannerForm? _form;
    private Thread?      _uiThread;

    private readonly ConcurrentQueue<Action<ScannerForm>> _pending = new();
    private volatile bool _ready = false;

    // Kept for API compatibility with ScannerService (not used by WinForms version)
    internal readonly object _lock = new();
    internal int TableHeight => 0;

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    public void Launch()
    {
        _uiThread = new Thread(() =>
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            _form = new ScannerForm();
            _form.Shown += (_, _) =>
            {
                _ready = true;
                // Wire sensitivity buttons to SetTriggerPct
                _form.OnSensitivityChanged = pct => SetTriggerPct(pct);
                // Wire grade buttons to SetMinGrade
                _form.OnGradeChanged = grade => SetMinGrade(grade);
                while (_pending.TryDequeue(out var a)) _form.SafeInvoke(a);
            };
            Application.Run(_form);
        });
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.IsBackground = true;
        _uiThread.Name = "ScannerUI";
        _uiThread.Start();
    }

    /// <summary>No-op — console guard not needed in WinForms version.</summary>
    public void InstallConsoleGuard() { }

    // ── Public API (mirrors console ScannerDisplay) ────────────────────────────

    public void UpdateRow(
        string ticker, int shortScore, int longScore,
        TapeSignal tape, NbboSignal nbbo, StructureSignal structure,
        LiquiditySignal liquidity, MacroSignal macro,
        ScoreBreakdown? shortBd = null, ScoreBreakdown? longBd = null,
        bool isHot = false, bool isWarming = false)
        => Dispatch(f => f.UpdateRow(ticker, shortScore, longScore,
                                      tape, nbbo, structure, liquidity, macro,
                                      shortBd, longBd, isHot, isWarming));

    public void UpdateStatus(bool nbboAvailable, bool hasVix,
                              int maxScore, int threshold, int scannerTriggers, int llmCalls)
        => Dispatch(f => f.UpdateStatus(nbboAvailable, hasVix, maxScore, threshold, scannerTriggers, llmCalls));

    public void SetTriggerPct(double pct)
        => _scanner?.SetTriggerPct(pct);

    public void SetMinGrade(string grade)
    {
        ScannerConfig.MinGrade = grade;
        Log($"Min grade → {grade} (signals require {grade} or better)");
    }

    // Keep a reference to ScannerService so we can call SetTriggerPct
    private ScannerService? _scanner;
    public void SetScanner(ScannerService scanner) => _scanner = scanner;

    public void Log(string message)
        => Dispatch(f => f.AppendLog(message));

    public void UpdatePipeline(string ticker,
        ScoreGateState? scoreGate = null, string? scoreLabel = null,
        LlmState?       llm       = null, string? llmLabel   = null,
        CardGateState?  cardGate  = null, string? cardLabel  = null,
        SignalState?    signal    = null)
        => Dispatch(f => f.UpdatePipeline(ticker,
               scoreGate, scoreLabel, llm, llmLabel, cardGate, cardLabel, signal));

    public void AddTicker(string ticker)
        => Dispatch(f => f.EnsureTicker(ticker));

    public void RemoveTicker(string ticker)
        => Dispatch(f => f.RemoveTicker(ticker));

    private void Dispatch(Action<ScannerForm> action)
    {
        if (_ready && _form != null) _form.SafeInvoke(action);
        else _pending.Enqueue(action);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// WINFORMS FORM
// ═══════════════════════════════════════════════════════════════════════════════

internal sealed class ScannerForm : Form
{
    // ── Colors ─────────────────────────────────────────────────────────────────
    static readonly Color BgDark      = Color.FromArgb(18, 18, 24);
    static readonly Color BgPanel     = Color.FromArgb(26, 26, 36);
    static readonly Color BgRow       = Color.FromArgb(30, 30, 42);
    static readonly Color BgRowHot    = Color.FromArgb(50, 42, 10);
    static readonly Color BgRowWarm   = Color.FromArgb(22, 22, 30);
    static readonly Color ColHeader   = Color.FromArgb(40, 40, 58);
    static readonly Color TextPrimary = Color.FromArgb(220, 220, 235);
    static readonly Color TextMuted   = Color.FromArgb(110, 110, 140);
    static readonly Color TextHot     = Color.FromArgb(255, 210, 60);
    static readonly Color TextGreen   = Color.FromArgb(60, 210, 120);
    static readonly Color TextRed     = Color.FromArgb(220, 70, 80);
    static readonly Color TextCyan    = Color.FromArgb(60, 200, 230);
    static readonly Color AccentBlue  = Color.FromArgb(45, 122, 240);
    static readonly Color BarEmpty    = Color.FromArgb(40, 40, 58);
    static readonly Color BorderColor = Color.FromArgb(55, 55, 78);

    // ── Fonts ──────────────────────────────────────────────────────────────────
    static readonly Font FontMono     = new("Consolas", 9f);
    static readonly Font FontMonoBold = new("Consolas", 9f, FontStyle.Bold);
    static readonly Font FontSm       = new("Consolas", 8f);
    static readonly Font FontStatus   = new("Consolas", 8.5f);

    // ── Layout ─────────────────────────────────────────────────────────────────
    const int RowH   = 22;
    const int HdrH   = 26;
    const int StatH  = 24;
    const int LogH   = 160;
    const int BarW   = 80;
    const int Pad    = 6;

    // Column widths
    const int CW_Ticker = 60;
    const int CW_Score  = 140;
    const int CW_Tape   = 130;
    const int CW_Struct = 170;
    const int CW_Age    = 45;

    // ── Controls ───────────────────────────────────────────────────────────────
    readonly Label       _statusLabel;
    readonly Label       _timeLabel;
    readonly DataGridView _grid;
    readonly RichTextBox  _log;
    readonly System.Windows.Forms.Timer _clock;

    // ── Row data ───────────────────────────────────────────────────────────────
    // ── Pipeline state ─────────────────────────────────────────────────────────

    sealed record PipelineState(
        // Score gate
        ScoreGateState ScoreGate   = ScoreGateState.None,
        string         ScoreLabel  = "",        // e.g. "72/72"
        // LLM
        LlmState       Llm         = LlmState.None,
        string         LlmLabel    = "",        // e.g. "TRADE B"
        // Card gate
        CardGateState  CardGate    = CardGateState.None,
        string         CardLabel   = "",        // e.g. "LOW CONF"
        // Signal — has its own timestamp so it decays independently of StateAt
        SignalState    Signal      = SignalState.None,
        DateTime       SignalAt    = default,   // when Signal was last set to non-None
        // When this state was last updated (for age display)
        DateTime       StateAt     = default);

    sealed record RowData(
        int ShortScore, int LongScore,
        string Tape, string Nbbo, string Structure, string Liquidity, string Macro,
        bool IsHot, bool IsWarming, DateTime UpdatedAt,
        ScoreBreakdown? ShortBd, ScoreBreakdown? LongBd,
        PipelineState Pipeline);

    readonly Dictionary<string, RowData> _rows =
        new(StringComparer.OrdinalIgnoreCase);

    // ── Constructor ────────────────────────────────────────────────────────────

    public ScannerForm()
    {
        SuspendLayout();

        Text            = "AVA  |  Pre-qualification";
        BackColor       = BgDark;
        ForeColor       = TextPrimary;
        Size            = new Size(920, 620);
        MinimumSize     = new Size(700, 400);
        StartPosition   = FormStartPosition.Manual;
        Location        = new Point(20, 20);

        // ── Status bar ────────────────────────────────────────────────────────
        var statusBar = new Panel
        {
            Dock      = DockStyle.Top,
            Height    = StatH,
            BackColor = BgPanel
        };
        _statusLabel = new Label
        {
            Dock      = DockStyle.Fill,
            Font      = FontStatus,
            ForeColor = TextCyan,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding   = new Padding(Pad, 0, 0, 0),
            Text      = "SCANNER   warming up..."
        };
        _timeLabel = new Label
        {
            Dock      = DockStyle.Right,
            Width     = 70,
            Font      = FontStatus,
            ForeColor = TextMuted,
            TextAlign = ContentAlignment.MiddleRight,
            Padding   = new Padding(0, 0, Pad, 0)
        };

        // Sensitivity buttons — let user change TriggerPct at runtime
        var btn90 = MakeSensBtn("90%", 0.90);
        var btn80 = MakeSensBtn("80%", 0.80);
        var btn75 = MakeSensBtn("75%", 0.75);

        // Grade filter buttons — gate signal emission by minimum card grade
        var btnGradeA = MakeGradeBtn("A",  "A");
        var btnGradeB = MakeGradeBtn("B",  "B");   // default — highlighted at startup
        var btnGradeC = MakeGradeBtn("C+", "C");
        var btnGradeD = MakeGradeBtn("D+", "D");

        // Separator label between the two button groups
        var sepLabel = new Label
        {
            Text      = " | ",
            Dock      = DockStyle.Right,
            Width     = 16,
            Font      = FontSm,
            ForeColor = BorderColor,
            TextAlign = ContentAlignment.MiddleCenter
        };

        statusBar.Controls.Add(_statusLabel);
        statusBar.Controls.Add(_timeLabel);
        statusBar.Controls.Add(btn75);
        statusBar.Controls.Add(btn80);
        statusBar.Controls.Add(btn90);
        statusBar.Controls.Add(sepLabel);
        statusBar.Controls.Add(btnGradeD);
        statusBar.Controls.Add(btnGradeC);
        statusBar.Controls.Add(btnGradeB);
        statusBar.Controls.Add(btnGradeA);

        // ── Events panel ──────────────────────────────────────────────────────
        var eventsLabel = new Label
        {
            Dock      = DockStyle.Top,
            Height    = 18,
            BackColor = ColHeader,
            ForeColor = TextMuted,
            Font      = FontSm,
            Text      = "  EVENTS",
            TextAlign = ContentAlignment.MiddleLeft,
            Padding   = new Padding(Pad, 0, 0, 0)
        };
        _log = new RichTextBox
        {
            Dock        = DockStyle.Fill,
            BackColor   = BgPanel,
            ForeColor   = TextPrimary,
            Font        = FontMono,
            ReadOnly    = true,
            BorderStyle = BorderStyle.None,
            ScrollBars  = RichTextBoxScrollBars.Vertical,
            WordWrap    = false
        };
        var logPanel = new Panel { Dock = DockStyle.Bottom, Height = LogH };
        logPanel.Controls.Add(_log);
        logPanel.Controls.Add(eventsLabel);

        var splitter = new Splitter
        {
            Dock = DockStyle.Bottom, Height = 4, BackColor = BorderColor
        };

        // ── Grid ──────────────────────────────────────────────────────────────
        _grid = new DataGridView
        {
            Dock              = DockStyle.Fill,
            BackgroundColor   = BgDark,
            GridColor         = BorderColor,
            BorderStyle       = BorderStyle.None,
            RowHeadersVisible = false,
            AllowUserToAddRows    = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly          = true,
            SelectionMode     = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect       = false,
            Font              = FontMono,
            CellBorderStyle   = DataGridViewCellBorderStyle.SingleHorizontal,
            ScrollBars        = ScrollBars.Vertical,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ColumnHeadersHeight         = HdrH,
            AutoSizeRowsMode  = DataGridViewAutoSizeRowsMode.None,
            EnableHeadersVisualStyles   = false,
            DefaultCellStyle  = new DataGridViewCellStyle
            {
                BackColor          = BgRow,
                ForeColor          = TextPrimary,
                Font               = FontMono,
                SelectionBackColor = Color.FromArgb(50, 80, 140),
                SelectionForeColor = TextPrimary,
                Padding            = new Padding(4, 0, 4, 0)
            },
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor          = ColHeader,
                ForeColor          = TextMuted,
                Font               = FontSm,
                SelectionBackColor = ColHeader,
                SelectionForeColor = TextMuted,
                Padding            = new Padding(4, 0, 4, 0)
            }
        };
        _grid.RowTemplate.Height = RowH;
        _grid.ShowCellToolTips   = true;

        AddCol("Ticker",     "TICKER",      CW_Ticker);
        AddCol("Short",      "SHORT",       CW_Score);
        AddCol("Long",       "LONG",        CW_Score);
        AddCol("Tape",       "TAPE",        CW_Tape);
        AddCol("Struct",     "STRUCT/LIQ",  CW_Struct);
        AddCol("Components", "COMPONENTS",  180);
        AddCol("ScoreGate",  "SCORE GATE",  90);
        AddCol("Llm",        "LLM",         80);
        AddCol("CardGate",   "CARD GATE",   80);
        AddCol("Signal",     "SIGNAL",      70);
        AddCol("PipeAge",    "STATE AGE",   65);
        AddCol("Age",        "DATA AGE",    CW_Age);
        _grid.Columns["Age"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;

        _grid.RowPrePaint  += OnRowPrePaint;
        _grid.CellPainting += OnCellPainting;

        // ── Wire layout ───────────────────────────────────────────────────────
        Controls.Add(_grid);
        Controls.Add(splitter);
        Controls.Add(logPanel);
        Controls.Add(statusBar);

        // ── Clock ─────────────────────────────────────────────────────────────
        _clock = new System.Windows.Forms.Timer { Interval = 1000 };
        _clock.Tick += (_, _) =>
        {
            _timeLabel.Text = DateTime.Now.ToString("HH:mm:ss");
            TickAgeColumn();
        };
        _clock.Start();

        ResumeLayout();
    }

    public Action<double>? OnSensitivityChanged;
    public Action<string>? OnGradeChanged;

    Button MakeSensBtn(string label, double pct) => new Button
    {
        Text      = label,
        Dock      = DockStyle.Right,
        Width     = 40,
        Height    = StatH,
        Font      = FontSm,
        BackColor = Color.FromArgb(45, 45, 65),
        ForeColor = TextMuted,
        FlatStyle = FlatStyle.Flat,
        Cursor    = Cursors.Hand,
        Tag       = pct
    }.Also(b =>
    {
        b.FlatAppearance.BorderColor = BorderColor;
        b.Click += (_, _) =>
        {
            OnSensitivityChanged?.Invoke(pct);
            // Visual feedback — highlight active button
            foreach (Control c in b.Parent!.Controls)
                if (c is Button btn && btn.Tag is double)
                    btn.ForeColor = TextMuted;
            b.ForeColor = TextHot;
        };
    });

    Button MakeGradeBtn(string label, string grade) => new Button
    {
        Text      = label,
        Dock      = DockStyle.Right,
        Width     = 32,
        Height    = StatH,
        Font      = FontSm,
        BackColor = Color.FromArgb(45, 45, 65),
        // Highlight the default grade (B) at startup
        ForeColor = grade == ScannerConfig.MinGrade ? TextHot : TextMuted,
        FlatStyle = FlatStyle.Flat,
        Cursor    = Cursors.Hand,
        Tag       = grade   // string tag — distinct from sens buttons which use double
    }.Also(b =>
    {
        b.FlatAppearance.BorderColor = BorderColor;
        b.Click += (_, _) =>
        {
            OnGradeChanged?.Invoke(grade);
            // Visual feedback — highlight active grade button only
            // (sens buttons use double Tag — don't touch them)
            foreach (Control c in b.Parent!.Controls)
                if (c is Button btn && btn.Tag is string)
                    btn.ForeColor = TextMuted;
            b.ForeColor = TextHot;
        };
    });

    void AddCol(string name, string header, int width)
    {
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name     = name,
            HeaderText = header,
            Width    = width,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
    }

    // ── Public update methods ──────────────────────────────────────────────────

    public void EnsureTicker(string ticker)
    {
        ticker = ticker.ToUpperInvariant();
        if (_rows.ContainsKey(ticker)) return;
        _rows[ticker] = new RowData(0, 0, "–", "–", "–", "–", "–", false, true, DateTime.Now, null, null, new PipelineState());
        RebuildGrid();
    }

    public void RemoveTicker(string ticker)
    {
        _rows.Remove(ticker.ToUpperInvariant());
        RebuildGrid();
    }

    public void UpdateRow(
        string ticker, int shortScore, int longScore,
        TapeSignal tape, NbboSignal nbbo, StructureSignal structure,
        LiquiditySignal liquidity, MacroSignal macro,
        ScoreBreakdown? shortBd, ScoreBreakdown? longBd,
        bool isHot, bool isWarming)
    {
        ticker = ticker.ToUpperInvariant();
        // Preserve existing pipeline state — score updates don't reset it
        var existingPipeline = _rows.TryGetValue(ticker, out var existing)
            ? existing.Pipeline : new PipelineState();

        _rows[ticker] = new RowData(shortScore, longScore,
            tape.ToString(), nbbo.ToString(),
            structure.ToString(), liquidity.ToString(), macro.ToString(),
            isHot, isWarming, DateTime.Now, shortBd, longBd,
            existingPipeline);

        var keys = _rows.Keys.OrderBy(k => k).ToList();
        int idx  = keys.IndexOf(ticker);
        if (idx < 0 || idx >= _grid.Rows.Count)
        {
            RebuildGrid();
            return;
        }
        PopulateRow(_grid.Rows[idx], ticker, _rows[ticker]);
        _grid.InvalidateRow(idx);
    }

    public void UpdatePipeline(string ticker,
        ScoreGateState? scoreGate = null, string? scoreLabel = null,
        LlmState?       llm       = null, string? llmLabel   = null,
        CardGateState?  cardGate  = null, string? cardLabel  = null,
        SignalState?    signal    = null)
    {
        ticker = ticker.ToUpperInvariant();
        if (!_rows.TryGetValue(ticker, out var r)) return;

        var p   = r.Pipeline;
        var now = DateTime.Now;

        // Stamp SignalAt only when Signal is explicitly set to a non-None value.
        // Preserves the original timestamp if signal arg is null (no change) or None (reset).
        var newSignal   = signal ?? p.Signal;
        var newSignalAt = (signal.HasValue && signal.Value != SignalState.None)
            ? now          // new non-None signal: start its expiry clock
            : (signal.HasValue && signal.Value == SignalState.None)
                ? default  // explicit reset: clear the clock
                : p.SignalAt; // no change to signal: keep existing clock

        var newP = p with
        {
            ScoreGate  = scoreGate  ?? p.ScoreGate,
            ScoreLabel = scoreLabel ?? p.ScoreLabel,
            Llm        = llm        ?? p.Llm,
            LlmLabel   = llmLabel   ?? p.LlmLabel,
            CardGate   = cardGate   ?? p.CardGate,
            CardLabel  = cardLabel  ?? p.CardLabel,
            Signal     = newSignal,
            SignalAt   = newSignalAt,
            StateAt    = now
        };
        _rows[ticker] = r with { Pipeline = newP };

        var keys = _rows.Keys.OrderBy(k => k).ToList();
        int idx2 = keys.IndexOf(ticker);
        if (idx2 >= 0 && idx2 < _grid.Rows.Count)
        {
            PopulateRow(_grid.Rows[idx2], ticker, _rows[ticker]);
            _grid.InvalidateRow(idx2);
        }
    }

    public void UpdateStatus(bool nbboAvailable, bool hasVix,
                              int maxScore, int threshold, int scannerTriggers, int llmCalls)
    {
        _statusLabel.Text =
            $"SCANNER   " +
            $"nbbo={(nbboAvailable ? "YES" : "no")}   " +
            $"macro={(hasVix ? "VIX+SPY" : "SPY-only")}   " +
            $"threshold={threshold}/{maxScore}   " +
            $"scanner={scannerTriggers}   " +
            $"llm={llmCalls}";
    }

    public void AppendLog(string message)
    {
        var ts  = DateTime.Now.ToString("HH:mm:ss");
        var col = message.Contains("🔥")       ? TextHot
                : message.Contains("🔔")       ? Color.FromArgb(120, 220, 120)
                : message.Contains("❌")       ? TextRed
                : message.Contains("[LLM]")    ? TextCyan
                : message.Contains("error")    ? TextRed
                : message.Contains("unavail")  ? Color.Orange
                : message.Contains("ready")    ? TextGreen
                : message.Contains("confirmed")? TextGreen
                : message.Contains("heartbeat")? TextMuted
                :                               TextPrimary;

        _log.SelectionStart  = _log.TextLength;
        _log.SelectionLength = 0;
        _log.SelectionColor  = TextMuted;
        _log.AppendText($"{ts}  ");
        _log.SelectionColor  = col;
        _log.AppendText(message + "\n");
        _log.ScrollToCaret();

        // Trim to 500 lines
        if (_log.Lines.Length > 500)
        {
            _log.SelectionStart  = 0;
            _log.SelectionLength = _log.GetFirstCharIndexFromLine(50);
            _log.SelectedText    = "";
        }
    }

    // ── Grid helpers ──────────────────────────────────────────────────────────

    void RebuildGrid()
    {
        _grid.Rows.Clear();
        foreach (var key in _rows.Keys.OrderBy(k => k))
        {
            int i = _grid.Rows.Add();
            PopulateRow(_grid.Rows[i], key, _rows[key]);
        }
    }

    static void PopulateRow(DataGridViewRow row, string ticker, RowData r)
    {
        row.Tag                   = r;
        row.Cells["Ticker"].Value = ticker;

        if (r.IsWarming)
        {
            row.Cells["Short"].Value      = "warming…";
            row.Cells["Long"].Value       = "warming…";
            row.Cells["Tape"].Value       = "–";
            row.Cells["Struct"].Value     = "–";
            row.Cells["Components"].Value = "–";
            row.Cells["ScoreGate"].Value  = "–";
            row.Cells["Llm"].Value        = "–";
            row.Cells["CardGate"].Value   = "–";
            row.Cells["Signal"].Value     = "–";
            row.Cells["PipeAge"].Value    = "–";
        }
        else
        {
            row.Cells["Short"].Value  = $"{r.ShortScore,3}/100";
            row.Cells["Long"].Value   = $"{r.LongScore,3}/100";
            row.Cells["Tape"].Value   = r.Tape;
            row.Cells["Struct"].Value = $"{r.Structure} / {r.Liquidity}";

            bool shortLeads = r.ShortScore >= r.LongScore;
            var  bd         = shortLeads ? r.ShortBd : r.LongBd;
            var  dir        = shortLeads ? "S" : "L";
            row.Cells["Components"].Value = bd != null ? $"{dir}: {bd.ActiveFlags()}" : "–";

            // ── Pipeline columns ──────────────────────────────────────────
            var p = r.Pipeline;

            row.Cells["ScoreGate"].Value = p.ScoreGate switch
            {
                ScoreGateState.Passed   => $"✓ {p.ScoreLabel}",
                ScoreGateState.Cooldown => "COOL",
                ScoreGateState.ThinData => $"THIN({p.ScoreLabel})",
                _                       => "—"
            };

            row.Cells["Llm"].Value = p.Llm switch
            {
                LlmState.InProgress => "⏳",
                LlmState.Trade      => $"TRADE {p.LlmLabel}",
                LlmState.NoTrade    => "NO_TRADE",
                LlmState.Error      => "❌ ERR",
                _                   => "—"
            };

            row.Cells["CardGate"].Value = p.CardGate switch
            {
                CardGateState.Passed   => "✓",
                CardGateState.LowConf  => p.CardLabel,
                CardGateState.NoBars   => "NO_BARS",
                _                      => "—"
            };

            // Signals auto-decay so stale PENDING/ENTRY don't mislead.
            // Emitted (alert already sent): clear after 90s — it's been actioned or missed.
            // Pending (entry not yet presented): clear after 3min — conditions have changed.
            const double EmittedExpirySeconds = 90;
            const double PendingExpirySeconds = 180;
            var signalAge = p.SignalAt == default ? double.MaxValue
                          : (DateTime.Now - p.SignalAt).TotalSeconds;
            var effectiveSignal = p.Signal switch
            {
                SignalState.Emitted when signalAge > EmittedExpirySeconds => SignalState.None,
                SignalState.Pending when signalAge > PendingExpirySeconds => SignalState.None,
                _                                                         => p.Signal
            };
            row.Cells["Signal"].Value = effectiveSignal switch
            {
                SignalState.Emitted => $"🔔 ENTRY",
                SignalState.Pending => "PENDING",
                _                  => "—"
            };

            // Pipeline state age — how long since this state was set
            row.Cells["PipeAge"].Value = p.StateAt == default ? "—"
                : FormatAge(DateTime.Now - p.StateAt);

            // Tooltip — full breakdown
            string tooltip =
                $"── SHORT ({r.ShortScore}/100) ──\n" +
                (r.ShortBd?.ToTooltip() ?? "no data") +
                $"\n\n── LONG ({r.LongScore}/100) ──\n" +
                (r.LongBd?.ToTooltip() ?? "no data");
            foreach (DataGridViewCell cell in row.Cells)
                cell.ToolTipText = tooltip;
        }

        row.Cells["Age"].Value = r.IsWarming ? "–"
            : $"{(DateTime.Now - r.UpdatedAt).TotalSeconds:F0}s";
    }

    static string FormatAge(TimeSpan ts)
    {
        if (ts.TotalSeconds < 60)  return $"{ts.TotalSeconds:F0}s";
        if (ts.TotalMinutes < 60)  return $"{(int)ts.TotalMinutes}m{ts.Seconds:D2}s";
        return $"{(int)ts.TotalHours}h{ts.Minutes:D2}m";
    }

    void TickAgeColumn()
    {
        var keys = _rows.Keys.OrderBy(k => k).ToList();
        for (int i = 0; i < _grid.Rows.Count && i < keys.Count; i++)
        {
            var r = _rows[keys[i]];
            _grid.Rows[i].Cells["Age"].Value = r.IsWarming ? "–"
                : $"{(DateTime.Now - r.UpdatedAt).TotalSeconds:F0}s";
            _grid.Rows[i].Cells["PipeAge"].Value =
                (!r.IsWarming && r.Pipeline.StateAt != default)
                ? FormatAge(DateTime.Now - r.Pipeline.StateAt)
                : "—";
        }
    }

    // ── Custom painting ────────────────────────────────────────────────────────

    void OnRowPrePaint(object? s, DataGridViewRowPrePaintEventArgs e)
    {
        var keys = _rows.Keys.OrderBy(k => k).ToList();
        if (e.RowIndex < 0 || e.RowIndex >= keys.Count) return;
        var r = _rows[keys[e.RowIndex]];

        _grid.Rows[e.RowIndex].DefaultCellStyle.BackColor =
            r.IsHot ? BgRowHot : r.IsWarming ? BgRowWarm : BgRow;
        _grid.Rows[e.RowIndex].DefaultCellStyle.ForeColor =
            r.IsHot ? TextHot : r.IsWarming ? TextMuted : TextPrimary;
    }

    void OnCellPainting(object? s, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0) return;

        var colName = _grid.Columns[e.ColumnIndex].Name;
        if (colName != "Short" && colName != "Long") return;

        var keys = _rows.Keys.OrderBy(k => k).ToList();
        if (e.RowIndex >= keys.Count) return;
        var r = _rows[keys[e.RowIndex]];

        e.PaintBackground(e.ClipBounds, true);

        if (r.IsWarming) { e.PaintContent(e.ClipBounds); e.Handled = true; return; }

        int score  = colName == "Short" ? r.ShortScore : r.LongScore;
        var bounds = e.CellBounds;
        int barX   = bounds.X + 62;
        int barY   = bounds.Y + bounds.Height / 2 - 4;

        // Score text
        var txtCol = score >= 75 ? TextHot : score >= 50 ? TextCyan : TextPrimary;
        TextRenderer.DrawText(e.Graphics, $"{score,3}/100", FontMonoBold,
            new Rectangle(bounds.X + 4, bounds.Y, 55, bounds.Height),
            txtCol, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);

        // Score bar background
        using var emptyBrush = new SolidBrush(BarEmpty);
        e.Graphics.FillRectangle(emptyBrush, barX, barY, BarW, 8);

        // Score bar fill
        int filled = (int)(BarW * score / 100.0);
        if (filled > 0)
        {
            var barCol = score >= 75 ? Color.FromArgb(220, 160, 30)
                       : score >= 50 ? AccentBlue
                       :               Color.FromArgb(50, 90, 150);
            using var fillBrush = new SolidBrush(barCol);
            e.Graphics.FillRectangle(fillBrush, barX, barY, filled, 8);
        }

        e.Handled = true;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    public void SafeInvoke(Action<ScannerForm> action)
    {
        if (IsDisposed) return;
        try
        {
            if (InvokeRequired) BeginInvoke(() => action(this));
            else action(this);
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Hide instead of close so the scanner keeps running in background
        e.Cancel = true;
        Hide();
    }
}

// ── Extension helper ───────────────────────────────────────────────────────────

internal static class Extensions
{
    public static T Also<T>(this T obj, Action<T> action) { action(obj); return obj; }
}