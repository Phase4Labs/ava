namespace get_assessment_no_graph;

public static class Program
{
    // Keep your existing framework prompt and question text here:
    /*private static string FrameworkSystemPrompt = """
    Eres un evaluador de ejecución para trading intradía en acciones altamente volátiles.

    Este chat usa un framework separado.
    - NO reutilices reglas, objetivos o supuestos del sistema SPY.
    - NO importes hábitos, intuiciones ni criterios de otros frameworks salvo que se indique explícitamente.
    - Evalúa únicamente con base en el dataset suministrado y en el framework de esta conversación.
    - No inventes datos faltantes.
    - Si el dataset no sostiene una operación válida, devuelve NO_TRADE según el esquema JSON requerido.

    A continuación está el JSON que gobierna esta conversación (aplica SOLO aquí):
    <FRAMEWORK_JSON>
    PASTE_JSON_HERE
    </FRAMEWORK_JSON>

    Objetivo de evaluación:
    - Identificar setups ejecutables bajo el framework activo.
    - Priorizar calidad de ejecución, claridad de invalidación y estructura riesgo/beneficio.
    - Ordenar escenarios por utilidad operativa esperada, de mayor a menor.
    - Devolver únicamente JSON válido conforme a la instrucción estricta de salida.
    """;*/

    private static readonly string ExecutionQuestion = """
    You are an intraday execution assistant operating under a high-volatility scalping framework.

    Analyze the supplied dataset and return exactly one execution card with up to 3 ranked trade scenarios.
    Evaluate both LONG and SHORT directions. Surface the best setups regardless of direction.
    Return JSON only. No explanation, no preamble, no markdown fences.

    ════════════════════════════════════════════
    1. OUTPUT SCHEMA
    ════════════════════════════════════════════

    Each scenario must include only these fields:
    direction         — "LONG" | "SHORT"
    entry_type        — see Section 3
    scenario_prob     — float 0–1: probability this setup triggers at all
    success_prob      — float 0–1: probability of reaching T1 if triggered
    entry_low         — decimal or null
    entry_high        — decimal or null
    stop_price        — decimal or null
    t1                — decimal or null
    t2                — decimal or null
    runner            — decimal or null
    grade             — "A" | "B" | "C" | "D" | "F"
    grade_rationale   — one sentence only

    Rules:
    - Do not force 3 scenarios. Return only those that are independently justified.
    - Do not invent values. Use null when a field cannot be derived from the dataset.
    - Rank scenarios highest to lowest expected usefulness.
    - If no valid setup exists, return: { "verdict": "NO_TRADE", "scenarios": [] }

    ════════════════════════════════════════════
    2. MANDATORY NO_TRADE CONDITIONS
    ════════════════════════════════════════════

    Return NO_TRADE immediately — do not score any scenario — if ANY of the following are true:
    • RVOL < 1.0x (no volume participation)
    • Spread is too wide to achieve minimum 1.5:1 R:R after entry cost
    • Market structure is pure chop — no directional bias, no defined levels
    • Time of day is late session (after 3:30 PM ET) AND volume is declining AND no catalyst
    • Active halt or news pending (unresolved binary event)

    ════════════════════════════════════════════
    3. ENTRY TYPE DEFINITIONS
    ════════════════════════════════════════════

    ── reclaim_hold ──
    Price reclaims a key level after a pullback and holds above it (LONG) or below it (SHORT)
    for at least 2 bars. Entry requires confirmation of hold, not just the cross.
    VP anchor: session_vp.val for LONG entry; session_vp.vah for SHORT entry.

    ── break_hold ──
    Price breaks a defined range or key level with expanding volume and holds.
    LONG on upside break; SHORT on downside break.
    A break without volume expansion is not break_hold — classify as reclaim_hold or do not use.
    VP anchor: session_vp.vah for LONG; session_vp.val for SHORT.

    ── fade_pop ──
    Counter-trend fade of an overextended spike INTO a known resistance or support zone.
    Entry is location-driven: must be at or within 0.3% of a defined VP or structural level.
    Do not use fade_pop if price is in open air with no overhead level to fade into.
    VP anchor: composite_vp.hvn or value area boundary.

    ── vwap_reclaim ──
    Price reclaims VWAP after a failed breakdown (LONG) or failed breakout (SHORT) and holds
    for at least 2 bars. The prior failure must be visible in bar structure (wick or close rejection).
    Targets: session_vp.poc and composite_vp.hvn.

    ── overextension_fade ──
    A four-phase reversal setup. ALL four phases must be confirmed before assigning this type.
    If any phase is absent, do not use overextension_fade — use a different type or NO_TRADE.

    PHASE 1 — EXTENSION (within last 15 bars):
        • Clear impulse leg of ≥3% above VWAP throughout
        • Stock currently extended ≥2.5% from VWAP OR ≥1.5 ATR from VWAP
        • Day gain ≥5% preferred; RVOL ≥2.5x required
        Without a prior impulse leg this is chop, not extension. STOP — do not proceed.

    PHASE 2 — EXHAUSTION (within last 8 bars):
        At least one of:
        • Upper wicks with wick-to-body ratio ≥0.7
        • Progressively smaller candle bodies near the high
        • Volume spike followed by price stall (≤0.2% net move on the spike bar)
        • Absorption: large avg_print_size that does not move price (if trade_count data present)
        The move must be visibly losing energy. Absence of exhaustion = STOP, not overextension_fade.

    PHASE 3 — FAILED CONTINUATION (within last 6 bars):
        • A new high attempt that reverses within 3 bars
        • ≤0.3% above prior high counts as failed, not a breakout
        • Double top or lower high both qualify
        DISQUALIFIER: if new highs are printing with expanding volume, Phase 3 is NOT present. STOP.

    PHASE 4 — STRUCTURE BREAK TRIGGER:
        Conservative entry: close below the last higher low
        Aggressive entry: rejection at a lower high within 1% of the high with visible stall or wick
        Trigger candle volume ≥1.2x recent average required for confirmation
        Maximum chase: 0.25% below trigger bar close

    ENTRY GUARDS — disqualify overextension_fade entirely if any of these are true:
        • New highs printing with expanding volume
        • Fresh momentum breakout in progress
        • No prior impulse leg (stock is extended sideways or on low RVOL)
        • RVOL < 2.5x
        • Day gain < 3%

    TARGETS:
        SHORT: entry near session_vp.vah or above; T1 = VWAP or session_vp.poc (closer one);
            T2 = session_vp.val or nearest HVN below; stop = signal high + 0.15% buffer
        LONG:  entry near session_vp.val or below; T1 = VWAP or session_vp.poc (closer one);
            T2 = session_vp.vah or nearest HVN above; stop = signal low + 0.15% buffer

    POST-ENTRY INVALIDATION (flag for re-eval or exit):
        • Fresh high printed after entry
        • VWAP reclaimed and held ≥3 bars
        • Higher high with expanding volume after entry
        • No follow-through in 4 bars

    ════════════════════════════════════════════
    4. GLOBAL ENTRY GUARDS (apply to ALL entry types)
    ════════════════════════════════════════════

    Downgrade to D or F if any of the following are present:
    • RVOL < 1.5x at time of signal
    • No defined structural level within 0.5% of entry zone
    • R:R at T1 is less than 1.5:1 after accounting for spread
    • Late session (after 3:00 PM ET) with declining volume — reduce scenario_prob by 0.15 minimum
    • Tape shows bid/ask chasing with no institutional print size (retail-only activity)

    For LONG entries only — additional disqualifiers:
    • No direct catalyst (earnings, FDA, product, offering) when day gain > 10%
        (fuel-less momentum: reversal risk is unmodelable, do not go LONG)
    • Price has already failed the key level twice intraday without recovery

    For SHORT entries — "no catalyst" is not a disqualifier.
    Absence of catalyst is often the short thesis itself.

    ════════════════════════════════════════════
    5. GRADING RULES
    ════════════════════════════════════════════

    Assign one grade per scenario. Base grade on the weakest factor present — one disqualifying
    condition outweighs multiple positive factors.

    ── A: High conviction ──
    • scenario_prob ≥ 0.65 AND success_prob ≥ 0.65
    • Entry anchored to a VP level (POC, VAH, VAL, or HVN)
    • RVOL ≥ 2.0x
    • Clean level structure — no overlapping S/R within 0.5% of entry
    • LONG additionally requires: catalyst strength ≥ 1 (direct or sector catalyst present)
    • SHORT: catalyst not required; tape exhaustion or failed breakout is sufficient
    • overextension_fade SHORT additionally requires: RVOL ≥ 2.5x, day gain ≥ 5%,
        all four phases confirmed, float ≤ 500M preferred

    ── B: Valid setup ──
    • scenario_prob ≥ 0.55 AND success_prob ≥ 0.60
    • Entry near (within 0.5%) a VP or structural level
    • RVOL ≥ 1.5x
    • At most one minor risk factor present (e.g. slightly wide spread, borderline time of day)

    ── C: Marginal — consider skipping ──
    • Valid setup structure present but at least one notable weakness:
        thin volume, borderline probabilities, unclear VP level, time-of-day risk,
        or catalyst uncertainty on a LONG
    • Only act on C-grade setups if the risk parameters are unusually tight

    ── D: Low confidence — likely skip ──
    • Setup structure is present but two or more risk factors are present
    • Probabilities do not meet B thresholds
    • Recommend passing unless position sizing is minimal

    ── F: Do not act ──
    • Any of the following:
        - RVOL < 1.0x
        - Late-session fade with no volume
        - Adverse tape (spread blowing out, no bids, erratic prints)
        - LONG with fuel-less momentum and no catalyst when day gain > 10%
        - overextension_fade with any entry guard triggered
    • F is a soft veto: still include the scenario in output, graded F, so the trader
        can see the setup was evaluated and rejected.

    grade_rationale: one sentence identifying the single most important factor that determined
    the grade. Be specific — cite the exact condition (e.g. "RVOL 1.2x below B threshold",
    "no VP anchor within 0.5% of entry", "Phase 3 absent — new highs with expanding volume").

    ════════════════════════════════════════════
    6. VOLUME PROFILE RULES
    ════════════════════════════════════════════

    Apply when session_vp and composite_vp are present in the dataset.
    If VP data is absent, fall back to VWAP and reference_levels.

    POC  — highest-volume price. Use composite_vp.poc as the runner magnet for both directions.
    VAH  — value area high. Price above VAH = imbalance/extension zone (short bias).
    VAL  — value area low. Price below VAL = imbalance/extension zone (long bias).
    HVN  — high-volume node. Use as T1/T2 targets. Do not use as entry zones.
    LVN  — low-volume node. Fast-move zone. Place stops beyond LVNs, not inside them.

    vp_context.migration:
        "migrating_higher" → supports long bias, raises scenario_prob for LONG setups
        "migrating_lower"  → supports short bias, raises scenario_prob for SHORT setups

    Entry anchoring by type (when VP is present):
        reclaim_hold  → session_vp.val (LONG) or session_vp.vah (SHORT)
        break_hold    → session_vp.vah (LONG) or session_vp.val (SHORT)
        fade_pop      → composite_vp.hvn or value area boundary
        vwap_reclaim  → VWAP cross; target session_vp.poc then composite_vp.hvn
        overextension_fade → see Section 3 targets above

    A scenario that ignores available VP data when anchoring entry or targets
    cannot be graded above C.

    ════════════════════════════════════════════
    7. TRADE FLOW INTERPRETATION
    ════════════════════════════════════════════

    Apply when trade_count and avg_print_size are present in intraday_bars.
    avg_print_size = volume / trade_count for a given bar.

    Low avg_print_size (many small prints)  → retail-driven; lower conviction on momentum continuation
    High avg_print_size (few large prints)  → institutional footprint; higher conviction

    Absorption: high volume + high avg_print_size + small price move (body < 0.2% range)
        → large participant taking the other side; momentum stalling
        → strongest confirmation for Phase 2 exhaustion in overextension_fade

    Climax: sudden spike in volume AND avg_print_size near a high/low,
            followed by next bar with significantly lower volume and avg_print_size
        → exhaustion confirmed; raises overextension_fade grade

    Continuation: expanding avg_print_size on breakout bar
        → institutional participation; raises conviction for break_hold and reclaim_hold

    If trade_count is null for a bar, rely on volume and bar structure only.

    NEWS CONTEXT RULES:
    • Use only news_context.items supplied in the dataset; do not claim access to any other current news.
    • Every article must have published_utc <= ts_asof_utc. Ignore any item that violates this.
    • active_halt=null or binary_event_pending=null means UNKNOWN, not false. Do not invent certainty.
    • If news_context.article_count=0, treat the catalyst as unconfirmed rather than manufacturing one.
    """;

    /*static void prepareFrameworkSystemPrompt()
    {
        var jsonPath = @"C:\Users\edgar\Documents\Phase4\Documents\stocks\Contexts\truth_2026_03_10.openai";
        if (!File.Exists(jsonPath))
        {
            Console.WriteLine($"Framework JSON file not found at path: {jsonPath}");
            Environment.Exit(1);
        }
        var jsonContent = File.ReadAllText(jsonPath);
        FrameworkSystemPrompt = FrameworkSystemPrompt.Replace("PASTE_JSON_HERE", jsonContent);

        jsonPath = @"C:\Users\edgar\Documents\Phase4\Documents\stocks\Contexts\json_schema_output.openai";
        if (!File.Exists(jsonPath))
        {
            Console.WriteLine($"Framework JSON file not found at path: {jsonPath}");
            Environment.Exit(1);
        }
        jsonContent = File.ReadAllText(jsonPath);
        FrameworkSystemPrompt = FrameworkSystemPrompt.Replace("<OUTPUT_JSON_SCHEMA>", jsonContent);
    }*/
    static string Env(string key)
        => Environment.GetEnvironmentVariable(key) ?? throw new Exception($"Missing env var: {key}");

    static DateTime LastClosedMinuteStartUtc(DateTime utcNow)
    {
        var floor = new DateTime(utcNow.Year, utcNow.Month, utcNow.Day, utcNow.Hour, utcNow.Minute, 0, DateTimeKind.Utc);
        return floor.AddMinutes(-1);
    }

    static string U(DateTime dt) => dt.ToUniversalTime().ToString("o");
    static string U(DateTime? dt) => dt.HasValue ? dt.Value.ToUniversalTime().ToString("o") : "<null>";

    static async Task<DateTime?> GetMinBarUtcAsync(SupabaseRestClient db, string ticker, CancellationToken ct)
    {
        var rows = await db.SelectAsync<MinuteBarRow>(
            "minute_bars",
            $"?select=ts_utc&ticker=eq.{Uri.EscapeDataString(ticker)}&order=ts_utc.asc&limit=1",
            ct);
        return rows.Count == 1 ? rows[0].TsUtc : null;
    }

    static async Task<DateTime?> GetMaxBarUtcAsync(SupabaseRestClient db, string ticker, CancellationToken ct)
    {
        var rows = await db.SelectAsync<MinuteBarRow>(
            "minute_bars",
            $"?select=ts_utc&ticker=eq.{Uri.EscapeDataString(ticker)}&order=ts_utc.desc&limit=1",
            ct);
        return rows.Count == 1 ? rows[0].TsUtc : null;
    }

    public static async Task Main(string[] args)
    {
        // Stage 2B.4 promoted structural-gate self-test. Pure in-memory; exits before
        // credentials, market data, DB, or LLM initialization.
        if (args.Any(a => string.Equals(a, "--stage2b4-gate-selftest", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = Stage2B4PromotionSelfTest.Run();
            return;
        }

        // Stage 2D quality-selection self-test. Pure in-memory; exits before
        // credentials, market data, DB, or LLM initialization.
        if (args.Any(a => string.Equals(a, "--stage2d-quality-selftest", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = Stage2DQualitySelectionSelfTest.Run();
            return;
        }

        // Local LLM integration smoke test. This path exits before any market-data,
        // Supabase, Polygon, or OpenAI initialization and cannot affect signal generation.
        if (args.Any(a => string.Equals(a, "--local-llm-smoke", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = await LocalLlmSmokeTest.RunAsync();
            return;
        }

        string? polygonKey  = Environment.GetEnvironmentVariable("AVA_MASSIVE_KEY");
        string? supabaseUrl = Environment.GetEnvironmentVariable("AVA_SUPABASE_URL");
        string? supabaseKey = Environment.GetEnvironmentVariable("AVA_SUPABASE_KEY");
        string? openAiKey   = Environment.GetEnvironmentVariable("AVA_OPENAI_KEY");
        string? anonKey     = Environment.GetEnvironmentVariable("AVA_ANON_KEY");
        string? model       = Environment.GetEnvironmentVariable("AVA_MODEL");

        // Stage 2B historical corpus/inventory. Read-only against Supabase; may call
        // Massive for future outcome bars, but never calls an LLM or writes production tables.
        if (args.Any(a => string.Equals(a, "--corpus-inventory", StringComparison.OrdinalIgnoreCase)) ||
            args.Any(a => string.Equals(a, "--corpus-build", StringComparison.OrdinalIgnoreCase)) ||
            args.Any(a => string.Equals(a, "--corpus-help", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = await HistoricalCorpusBuilder.RunAsync(
                args, polygonKey, supabaseUrl, supabaseKey);
            return;
        }

        // Historical dual-model replay exits before live symbol management, WebSockets,
        // scanner, trigger engine, and position re-evaluation are initialized.
        if (args.Any(a => string.Equals(a, "--historical-shadow", StringComparison.OrdinalIgnoreCase)) ||
            args.Any(a => string.Equals(a, "--historical-shadow-help", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = await HistoricalShadowRunner.RunAsync(
                args, polygonKey, supabaseUrl, supabaseKey, openAiKey, model, ExecutionQuestion);
            return;
        }

        // REPLAY MODE CONFIG
        var replayMode       = false;
        var stopOnFirstError = true;
        var stepDelayMs      = 200;
        var everyNMinutes    = 5;   // replay throttle (unchanged)

        // ── Card cadence (live mode) ──────────────────────────────────
        // Global 5-minute clock shared across all tickers.
        // Each ticker gets a staggered 1-bar offset so API calls never
        // fire at the same time. Override: always fires after you take a position.
        var cardIntervalMinutes = 5;   // <- change here to adjust cadence

        var replayEndUtc   = LastClosedMinuteStartUtc(DateTime.UtcNow).AddDays(-1).AddMinutes(50);
        var replayStartUtc = replayEndUtc.AddMinutes(-60);

        var usePolygonWebSocket = true;

        using var polygon = new PolygonClient(polygonKey);
        using var db      = new SupabaseRestClient(supabaseUrl, supabaseKey);

        //prepareFrameworkSystemPrompt();

        var ingest         = new PolygonIngestionService(polygon, db);
        var payloadBuilder = new PayloadBuilder(db, polygon);
        var worker         = new ProduceCardWorker(db, openAiKey, model, ExecutionQuestion, ingest);
        var reEvalWorker   = new ReEvalWorker(db, openAiKey, model);
        var qualityTracker = new CardQualityTracker();

        var pollSeconds = 2;

        // ── Symbol management ────────────────────────────────────────
        // Loads active tickers from DB (prompts if empty) and subscribes
        // to Realtime so adds/drops from symbols_ctl take effect live.
        await using var symbols = new SymbolsService(db, supabaseUrl, anonKey, supabaseKey);
        await symbols.InitAsync();

        // lastProcessed is now a ConcurrentDictionary so OnChanged can safely update it.
        var lastProcessed = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTime?>(
            symbols.ActiveTickers.ToDictionary(t => t, _ => (DateTime?)null),
            StringComparer.OrdinalIgnoreCase);

        // lastCardProduced[(ticker)] = UTC minute of last successful OpenAI call.
        // Used by the cadence gate to decide whether to call this bar.
        var lastCardProduced = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTime?>(
            symbols.ActiveTickers.ToDictionary(t => t, _ => (DateTime?)null),
            StringComparer.OrdinalIgnoreCase);

        // lastReEval[(ticker)] = UTC minute of last successful re-eval.
        var lastReEval = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTime?>(
            symbols.ActiveTickers.ToDictionary(t => t, _ => (DateTime?)null),
            StringComparer.OrdinalIgnoreCase);

        // forceReEval[(ticker)] = on-demand re-eval requested.
        var forceReEval = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(
            symbols.ActiveTickers.ToDictionary(t => t, _ => false),
            StringComparer.OrdinalIgnoreCase);

        // forceCardNext[(ticker)] = true means: produce a card on the very next
        // bar regardless of the cadence clock. Set when user takes a position.
        var forceCardNext = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(
            symbols.ActiveTickers.ToDictionary(t => t, _ => false),
            StringComparer.OrdinalIgnoreCase);

        // When a new ticker is added at runtime, start tracking it.
        // When a ticker is dropped, remove its tracking entry.
        symbols.OnChanged += (ticker, added) =>
        {
            if (added)
            {
                lastProcessed.TryAdd(ticker, null);
                lastCardProduced.TryAdd(ticker, null);
                forceCardNext.TryAdd(ticker, false);
                lastReEval.TryAdd(ticker, null);
                forceReEval.TryAdd(ticker, false);
                Console.WriteLine($"[main] Tracking new ticker: {ticker}");
            }
            else
            {
                lastProcessed.TryRemove(ticker, out _);
                lastCardProduced.TryRemove(ticker, out _);
                forceCardNext.TryRemove(ticker, out _);
                lastReEval.TryRemove(ticker, out _);
                forceReEval.TryRemove(ticker, out _);
                Console.WriteLine($"[main] Stopped tracking: {ticker}");
            }
        };

        // Listen for signal_actions 'taken' events via Supabase Realtime so we
        // can immediately set forceCardNext for the relevant ticker.
        // This wires into the same Realtime client used by SymbolsService.
        // NOTE: The taken action contains signal_id -> we resolve ticker from signal_events.
        symbols.OnPositionTaken += async (ticker) =>
        {
            forceCardNext[ticker.ToUpperInvariant()] = true;
            Console.WriteLine($"[main] Position taken on {ticker} — card forced on next bar");
            await Task.CompletedTask;
        };

        // ── WebSocket ingestion (live mode) ───────────────────────────
        CancellationTokenSource? liveCts    = null;
        Task?                    liveWsTask = null;

        // Helper to (re)start the WS ingester with the current active ticker list.
        // Called at startup and whenever the symbol set changes while in live mode.
        BarIngestionService? wsIngest = null;

        async Task StartWsIngestionAsync()
        {
            // Cancel any existing WS task first
            if (liveCts is not null)
            {
                await liveCts.CancelAsync();
                if (liveWsTask is not null)
                    await liveWsTask.ConfigureAwait(false);
            }

            var currentTickers = symbols.ActiveTickers.ToArray();
            if (currentTickers.Length == 0) return;

            liveCts = new CancellationTokenSource();
            var featureComputer = new RealtimeFeatureComputer();
            var wsClient        = new PolygonRealtimeClient(polygonKey);
            wsIngest            = new BarIngestionService(wsClient, db, featureComputer);

            await wsIngest.SeedStateFromDbAsync(currentTickers, DateTime.UtcNow, CancellationToken.None);

            liveWsTask = Task.Run(async () =>
            {
                try
                {
                    await wsIngest.RunAsync(
                        currentTickers.Append("SPY").ToArray(),
                        debugRaw: null,
                        ct: liveCts.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Console.WriteLine($"[bar-ingest] stopped: {ex}");
                }
            });

            Console.WriteLine($"[poly-ws] Started for: {string.Join(", ", currentTickers)}");
        }

        if (!replayMode && usePolygonWebSocket)
            await StartWsIngestionAsync();

        // Declared here so it's in scope for the OnChanged handler below
        ScannerService? scanner = null;
        if (!replayMode)
        {
            scanner = new ScannerService(polygonKey, forceCardNext, symbols.ActiveTickers);
            await scanner.StartAsync();

            // Wire BarIngestionService so scanner gets bar data via OnBarCommitted
            if (wsIngest != null)
            {
                scanner.SetBarIngestionService(wsIngest);
                await wsIngest.SeedScannerStateAsync(scanner, DateTime.UtcNow, CancellationToken.None);
            }

            // Register OnChanged AFTER scanner is wired so startup Realtime events
            // don't race with and cancel the initial RunAsync.
            // Debounce: wait 5s after last change, only restart if ticker list changed.
            if (!replayMode && usePolygonWebSocket)
            {
                DateTime lastChange  = DateTime.MinValue;
                int      lastCount   = symbols.ActiveTickers.Count();
                symbols.OnChanged += (_, _) =>
                {
                    lastChange = DateTime.UtcNow;
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(5000);
                        // Only restart if this is the most recent change
                        if ((DateTime.UtcNow - lastChange).TotalSeconds < 4) return;
                        var newCount = symbols.ActiveTickers.Count();
                        if (newCount == lastCount) return; // no actual change
                        lastCount = newCount;
                        Console.WriteLine($"[bar-ingest] ticker list changed ({newCount} tickers) — restarting WS");
                        await StartWsIngestionAsync();
                        if (scanner != null && wsIngest != null)
                        {
                            scanner.SetBarIngestionService(wsIngest);
                            await wsIngest.SeedScannerStateAsync(scanner, DateTime.UtcNow, CancellationToken.None);
                        }
                    });
                };
            }

            // Form-dependent wiring needs a delay for the WinForms STA thread to initialize
            _ = Task.Run(async () => {
                await Task.Delay(2000);
                AppLog.SetDisplay(scanner.Display);
                scanner.Display?.SetScanner(scanner);
            });

            // Keep scanner in sync when watchlist changes
            symbols.OnChanged += (ticker, added) =>
            {
                if (added) scanner.AddTicker(ticker);
                else       scanner.RemoveTicker(ticker);
            };

            Console.WriteLine($"[scanner] Pre-qualification scanner active (threshold: {ScannerConfig.TriggerPct*100:F0}% of available signal)");
        }

        // ── REPLAY MODE ───────────────────────────────────────────────
        if (replayMode)
        {
            var ct = CancellationToken.None;

            var bounds = new Dictionary<string, (DateTime min, DateTime max)>();
            foreach (var t in symbols.ActiveTickers)
            {
                var min = await GetMinBarUtcAsync(db, t, ct);
                var max = await GetMaxBarUtcAsync(db, t, ct);
                if (min is null || max is null)
                {
                    Console.WriteLine($"{DateTime.UtcNow:o} REPLAY {t} has no bars in DB. Skipping.");
                    continue;
                }
                bounds[t] = (min.Value, max.Value);
                Console.WriteLine($"{DateTime.UtcNow:o} REPLAY {t} range [{min.Value:o} .. {max.Value:o}]");
            }

            if (bounds.Count == 0)
                throw new Exception("ReplayMode: no tickers have bars in DB.");

            var cursor = bounds.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.min);

            while (true)
            {
                DateTime? nextCap    = null;
                string?   nextTicker = null;

                foreach (var (t, cur) in cursor)
                {
                    var max = bounds[t].max;
                    if (cur > max) continue;
                    if (nextCap is null || cur < nextCap.Value) { nextCap = cur; nextTicker = t; }
                }

                if (nextCap is null || nextTicker is null)
                {
                    Console.WriteLine($"{DateTime.UtcNow:o} REPLAY complete (all tickers exhausted).");
                    break;
                }

                var tkr    = nextTicker;
                var capUtc = nextCap.Value;

                try
                {
                    await ingest.IngestTodayAndEnsureFeaturesUpToAsync(tkr, capUtc, capUtc, ct);

                    var maxBar = await db.SelectAsync<MinuteBarRow>(
                        "minute_bars",
                        $"?select=ts_utc&ticker=eq.{tkr}&ts_utc=lte.{Uri.EscapeDataString(capUtc.ToString("o"))}&order=ts_utc.desc&limit=1",
                        ct);

                    var maxFeat = await db.SelectAsync<MinuteBarFeaturesRow>(
                        "minute_bar_features",
                        $"?select=ts_utc&ticker=eq.{tkr}&ts_utc=lte.{Uri.EscapeDataString(capUtc.ToString("o"))}&order=ts_utc.desc&limit=1",
                        ct);

                    if (maxBar.Count == 0 || maxFeat.Count == 0)
                    {
                        Console.WriteLine($"{DateTime.UtcNow:o} REPLAY {tkr} cap={capUtc:o} missing bar/feat -> skip");
                        cursor[tkr] = capUtc.AddMinutes(1);
                        continue;
                    }

                    if (maxBar[0].TsUtc != maxFeat[0].TsUtc)
                        throw new Exception($"Replay misaligned {tkr}: maxBar={maxBar[0].TsUtc:o} maxFeat={maxFeat[0].TsUtc:o} cap={capUtc:o}");

                    var datasetJson  = await payloadBuilder.BuildDatasetJsonUpToAsync(tkr, capUtc, capUtc, ct: ct, historicalAsOf: true);
                    var minuteIndex  = (int)((capUtc - bounds[tkr].min).TotalMinutes);

                    Console.WriteLine($"{DateTime.UtcNow:o} REPLAY {tkr} cap={capUtc:o} asof={maxBar[0].TsUtc:o} bytes={datasetJson.Length}");

                    if (minuteIndex % everyNMinutes == 0)
                    {
                        var sessionOpenUtc = MarketSession.GetSessionOpenUtcForDay(capUtc);
                        await worker.RunOnceAsync(tkr, capUtc, datasetJson, sessionOpenUtc, capUtc, ct);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{DateTime.UtcNow:o} REPLAY ERROR ticker={tkr} cap={capUtc:o} :: {ex}");
                    if (stopOnFirstError) throw;
                }
                finally
                {
                    cursor[tkr] = capUtc.AddMinutes(1);
                }

                if (stepDelayMs > 0)
                    await Task.Delay(stepDelayMs);
            }

            return;
        }

        // ── LIVE LOOP ─────────────────────────────────────────────────
        while (true)
        {
            var utcNow     = DateTime.UtcNow;
            var lastClosed = LastClosedMinuteStartUtc(utcNow);

            // Snapshot the active ticker list once per loop iteration.
            // Any add/drop that happened mid-loop takes effect on the next iteration.
            var activeTickers = symbols.ActiveTickers;

            foreach (var t in activeTickers)
            {
                // Skip if ticker was dropped mid-loop (Realtime fired during this iteration)
                if (!symbols.IsActive(t)) continue;

                var openUtc  = MarketSession.GetSessionOpenUtcForDay(utcNow.Date);
                var closeUtc = MarketSession.GetSessionCloseUtcForDay(utcNow.Date);
                if (lastClosed < openUtc || lastClosed > closeUtc)
                {
                    await Task.Delay(TimeSpan.FromSeconds(pollSeconds));
                    continue;
                }

                var lp = lastProcessed.GetValueOrDefault(t);
                var isForcedNow = forceCardNext.GetValueOrDefault(t, false);
                // Don't re-process the same bar unless the scanner has forced a re-evaluation.
                // Without the isForcedNow bypass, a scanner trigger that fires during the same
                // closed bar we already processed would be silently swallowed here.
                if (lp.HasValue && lp.Value >= lastClosed && !isForcedNow) continue;

                if (!usePolygonWebSocket)
                {
                    var lastTs = await ingest.IngestTodayAndEnsureFeaturesUpToAsync(t, utcNow, lastClosed, CancellationToken.None);
                    Console.WriteLine($"{U(DateTime.UtcNow)} {t} ingested_through={U(lastTs)} lastClosed={U(lastClosed)}");

                    if (lastTs is null)
                    {
                        lastProcessed[t] = lastClosed;
                        continue;
                    }
                }

                var maxBar = await db.SelectAsync<MinuteBarRow>(
                    "minute_bars",
                    $"?select=ts_utc&ticker=eq.{t}&order=ts_utc.desc&limit=1",
                    ct: CancellationToken.None);

                if (maxBar.Count < 1) continue;

                var capUtc = lastClosed;
                var barUtc = maxBar[0].TsUtc.ToUniversalTime();

                // Normal cadence: skip if no new bar since last processed.
                // Exception: if the scanner forced this ticker (A+++ score), use
                // the most recent available bar regardless — low-volume tickers
                // may not have a new bar every minute but still deserve evaluation.
                var forcedEarly = isForcedNow;
                if (barUtc < capUtc && !forcedEarly) continue;

                // For forced tickers with a stale bar, use the actual latest bar time
                if (forcedEarly && barUtc < capUtc)
                {
                    capUtc = barUtc;
                    Console.WriteLine($"{U(DateTime.UtcNow)} {t} FORCE_STALE_BAR using barUtc={U(barUtc)} (no new bar since lastClosed={U(lastClosed)})");
                }

                var maxFeat = await db.SelectAsync<MinuteBarFeaturesRow>(
                    "minute_bar_features",
                    $"?select=ts_utc&ticker=eq.{t}&order=ts_utc.desc&limit=1",
                    ct: CancellationToken.None);

                if (maxBar.Count == 0 || maxFeat.Count == 0)
                {
                    Console.WriteLine($"{U(DateTime.UtcNow)} {t} Missing bars/features (maxBar={maxBar.Count}, maxFeat={maxFeat.Count})");
                    if (!usePolygonWebSocket)
                        lastProcessed[t] = lastClosed;
                    continue;
                }

                if (maxBar.Count > 0 && maxFeat.Count > 0)
                    Console.WriteLine($"{U(DateTime.UtcNow)} {t} cap={U(lastClosed)} maxBar={U(maxBar[0].TsUtc)} maxFeat={U(maxFeat[0].TsUtc)}");

                if (maxFeat[0].TsUtc > maxBar[0].TsUtc)
                    Console.WriteLine($"{U(DateTime.UtcNow)} {t} WARNING: maxFeat.ts_utc > maxBar.ts_utc");

                if (maxBar[0].TsUtc != maxFeat[0].TsUtc)
                {
                    if (usePolygonWebSocket && maxFeat[0].TsUtc < maxBar[0].TsUtc)
                    {
                        if (!forcedEarly)
                        {
                            Console.WriteLine($"{U(DateTime.UtcNow)} {t} waiting for features catch-up (maxBar={U(maxBar[0].TsUtc)} maxFeat={U(maxFeat[0].TsUtc)})");
                            continue;
                        }
                        // Scanner-forced: use latest available features rather than skipping
                        capUtc = maxFeat[0].TsUtc.ToUniversalTime();
                        Console.WriteLine($"{U(DateTime.UtcNow)} {t} FORCE_STALE_FEATURES cap adjusted to {U(capUtc)}");
                    }
                    else if (!forcedEarly)
                        throw new Exception($"Misaligned bars/features for {t}: maxBar={U(maxBar[0].TsUtc)} maxFeat={U(maxFeat[0].TsUtc)}");
                }

                Console.WriteLine($"{U(DateTime.UtcNow)} {t} OK cap={U(lastClosed)} maxTs={U(maxBar[0].TsUtc)}");

                var datasetJson = await payloadBuilder.BuildDatasetJsonUpToAsync(t, utcNow, lastClosed, ct: CancellationToken.None);
                using var doc   = System.Text.Json.JsonDocument.Parse(datasetJson);

                var cap        = lastClosed.ToUniversalTime();
                var asof       = doc.RootElement.GetProperty("ts_asof_utc").GetDateTime().ToUniversalTime();
                var barsCount  = doc.RootElement.GetProperty("intraday_bars").GetArrayLength();

                Console.WriteLine($"{U(DateTime.UtcNow)} {t} PAYLOAD cap={cap:o} asof={asof:o} bars={barsCount} bytes={datasetJson.Length}");
                Console.WriteLine($"{U(DateTime.UtcNow)} {t} dataset_bytes={datasetJson.Length} cap={U(lastClosed)}");

                if (asof > cap)
                    throw new Exception($"{t} payload asof {asof:o} > cap {cap:o}");

                var sessionOpenUtc = MarketSession.GetSessionOpenUtcForDay(utcNow.Date);

                // ── Cadence gate ──────────────────────────────────────
                // Global 5-minute clock with per-ticker stagger.
                // Ticker slot = its index in the sorted active list, so ticker 0
                // fires at minutes 0,5,10... and ticker 1 fires at minutes 1,6,11...
                // This spreads API calls 1 minute apart regardless of how many tickers.
                // forceCardNext overrides the clock (set when a position is taken).
                var activeSorted  = symbols.ActiveTickers.ToList();  // stable sorted snapshot
                var tickerSlot    = activeSorted.IndexOf(t);
                if (tickerSlot < 0) tickerSlot = 0;         // safety: new ticker mid-loop

                var effectiveInterval = forcedEarly
                    ? CardQualityTracker.NormalIntervalMinutes  // scanner override — ignore backoff
                    : qualityTracker.GetEffectiveInterval(t);
                var minuteOfDay       = (int)lastClosed.TimeOfDay.TotalMinutes;
                var slotMinute        = (minuteOfDay - tickerSlot + 1440) % effectiveInterval;
                var isScheduledBar    = slotMinute == 0;

                if (effectiveInterval != CardQualityTracker.NormalIntervalMinutes && !forcedEarly)
                    Console.WriteLine($"{U(DateTime.UtcNow)} {t} QUALITY_BACKOFF interval={effectiveInterval}min " +
                        $"status={qualityTracker.GetStatusLine(t)}");

                var forced = forceCardNext.GetValueOrDefault(t, false);

                // ── Minimum bar guard ─────────────────────────────────────────
                // Don't call the LLM until we have enough session bars for meaningful
                // analysis. Low-volume tickers may have fewer than 8 bars even hours
                // into the session — use a hybrid gate: 8 bars OR 30 minutes since open.
                const int MinBarsToAnalyze = 8;
                var etNowCheck   = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
                                       TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));
                var sessionOpenCheck = etNowCheck.Date.AddHours(9).AddMinutes(30);
                var minsSinceOpenCheck = (etNowCheck - sessionOpenCheck).TotalMinutes;
                bool thinData = barsCount < MinBarsToAnalyze && minsSinceOpenCheck < 30;

                if (thinData && !forced)
                {
                    Console.WriteLine($"{U(DateTime.UtcNow)} {t} SKIP_THIN_DATA bars={barsCount} < {MinBarsToAnalyze} — waiting for more bars or 30min mark");
                    scanner?.Display?.UpdatePipeline(t,
                        scoreGate: ScoreGateState.ThinData,
                        scoreLabel: $"{barsCount}");
                    continue;
                }

                var shouldProduceCard = forced || isScheduledBar;

                // ── Event-driven triggers ─────────────────────────────────────
                // Detect meaningful market events from the already-built dataset.
                // These fire a card immediately regardless of the schedule clock.
                // Each event has a cooldown built into the detector to avoid
                // firing every bar during a sustained move.
                List<string> triggeredEvents = new();
                if (!shouldProduceCard)
                {
                    triggeredEvents = CardEventDetector.Detect(
                        doc,
                        lastCardProduced.GetValueOrDefault(t),
                        lastClosed);

                    if (triggeredEvents.Count > 0)
                        shouldProduceCard = true;
                }

                if (!shouldProduceCard)
                {
                    var lastCard = lastCardProduced.GetValueOrDefault(t);
                    Console.WriteLine($"{U(DateTime.UtcNow)} {t} SKIP_CARD cap={U(lastClosed)} " +
                        $"slot={tickerSlot} slotMinute={slotMinute} lastCard={U(lastCard)}");
                    lastProcessed[t] = lastClosed;
                    continue;
                }

                var cardReason = forced          ? (forcedEarly ? "scanner-A+++" : "forced-position-taken")
                               : isScheduledBar  ? $"schedule slot={tickerSlot}"
                               : triggeredEvents.Count > 0 ? $"event: {string.Join(", ", triggeredEvents)}"
                               : "unknown";

                Console.WriteLine($"{U(DateTime.UtcNow)} {t} PRODUCE_CARD cap={U(lastClosed)} reason={cardReason}");
                AppLog.Llm($"PRODUCE_CARD {t} reason={cardReason}");
                if (scanner != null) scanner.LlmCallCount++;
                scanner?.Display?.UpdatePipeline(t, llm: LlmState.InProgress);

                try
                {
                    var quality = await worker.RunOnceAsync(t, utcNow, datasetJson, sessionOpenUtc, lastClosed, CancellationToken.None);
                    lastCardProduced[t] = lastClosed;
                    forceCardNext[t]    = false;

                    qualityTracker.Record(t, quality);
                    Console.WriteLine($"{U(DateTime.UtcNow)} {t} CARD_QUALITY {qualityTracker.GetStatusLine(t)}");
                    AppLog.Info($"CARD_QUALITY {t} {qualityTracker.GetStatusLine(t)}");
                    // LLM result is now in DB — TriggerEngine will update CardGate and Signal
                    // For now mark LLM as complete; grade comes from TriggerEngine callback
                    var llmVerdict = quality?.Verdict == "NO_TRADE" ? LlmState.NoTrade : LlmState.Trade;
                    scanner?.Display?.UpdatePipeline(t, llm: llmVerdict, llmLabel: quality?.Verdict == "TRADE" ? $"s={quality.ScenarioCount}" : "");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"LLM failure for {t} @ {asof:o}: {ex.Message}");
                    AppLog.Error($"LLM failure {t}: {ex.Message}");
                    scanner?.Display?.UpdatePipeline(t, llm: LlmState.Error, llmLabel: "");
                }

                // ── Re-eval gate ──────────────────────────────────────────────
                // Fires every 5 bars for open positions, or immediately if forced.
                // Uses lastReEval timestamp (in-memory) so it survives restarts
                // via the reeval_at_utc column in trader_state.
                try
                {
                    const int ReEvalIntervalMinutes = 5;

                    // Load open positions for this ticker (cheap single-row query)
                    var posRows = await db.SelectAsync<TraderStateRow>(
                        "trader_state",
                        $"?ticker=eq.{Uri.EscapeDataString(t)}&position=in.(long,short)&select=*",
                        CancellationToken.None);

                    if (posRows.Count > 0)
                    {
                        var pos = posRows[0];
                        var lastRe = lastReEval.GetValueOrDefault(t);

                        // Seed from DB on first run (handles restarts)
                        if (lastRe is null && pos.ReevalAtUtc.HasValue)
                            lastRe = pos.ReevalAtUtc;

                        var force2   = forceReEval.GetValueOrDefault(t, false);
                        var elapsed  = lastRe.HasValue
                            ? (int)(lastClosed - lastRe.Value).TotalMinutes
                            : int.MaxValue;
                        var isDue    = elapsed >= ReEvalIntervalMinutes;

                        // Also consume any pending on-demand request from reeval_requests table
                        if (!force2)
                        {
                            var pending = await db.SelectAsync<ReEvalRequestRow>(
                                "reeval_requests",
                                $"?ticker=eq.{Uri.EscapeDataString(t)}&select=ticker",
                                CancellationToken.None);
                            if (pending.Count > 0)
                            {
                                force2 = true;
                                // Delete the request row
                                await db.DeleteAsync(
                                    "reeval_requests",
                                    $"?ticker=eq.{Uri.EscapeDataString(t)}",
                                    CancellationToken.None);
                                Console.WriteLine($"{U(DateTime.UtcNow)} {t} REEVAL on-demand request consumed");
                            }
                        }

                        if (force2 || isDue)
                        {
                            var reason = force2 ? "on-demand" : $"5-bar interval ({elapsed}min elapsed)";
                            Console.WriteLine($"{U(DateTime.UtcNow)} {t} REEVAL firing ({reason})");

                            var liveMarket = scanner?.GetReEvalMarketSnapshot(t);
                            var ok = await reEvalWorker.RunAsync(
                                t,
                                datasetJson,
                                pos,
                                lastClosed,
                                liveMarket,
                                CancellationToken.None);
                            if (ok)
                            {
                                lastReEval[t]  = lastClosed;
                                forceReEval[t] = false;
                            }
                        }
                        else
                        {
                            Console.WriteLine($"{U(DateTime.UtcNow)} {t} REEVAL skip ({elapsed}min < {ReEvalIntervalMinutes}min)");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{U(DateTime.UtcNow)} {t} REEVAL error: {ex.Message}");
                }

                await Task.Delay(400, CancellationToken.None);
                lastProcessed[t] = lastClosed;
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSeconds));
        }
    }
}
