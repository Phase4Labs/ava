namespace get_assessment_no_graph;

public sealed class TriggerEngine
{
    // Must match MinBarsToAnalyze in Program.cs — same gate applied consistently
    private const int MinBars = 8;
    private readonly SupabaseRestClient       _db;
    private readonly PolygonIngestionService? _ingest;

    public TriggerEngine(SupabaseRestClient db, PolygonIngestionService? ingest = null)
    {
        _db     = db;
        _ingest = ingest;
    }

    // DTO for execution_card_scenarios
    private sealed class ExecutionCardScenarioRow
    {
        public string ticker { get; set; } = "";
        public DateTime asof_ts_utc { get; set; }
        public int rank { get; set; }
        public string direction { get; set; } = "";   // long|short
        public string entry_type { get; set; } = "";  // reclaim_hold|break_hold|fade_pop|vwap_reclaim|overextension_fade

        public decimal? entry_low { get; set; }
        public decimal? entry_high { get; set; }
        public decimal? stop { get; set; }
        public decimal? t1 { get; set; }
        public decimal? t2 { get; set; }
        public decimal? runner { get; set; }
        public decimal? scenario_prob { get; set; }
        public decimal? success_prob { get; set; }
        public string? setup { get; set; }
        public string? grade { get; set; }
        public string? grade_rationale { get; set; }
    }

    public async Task EvaluateAndEmitAsync(
        string ticker,
        DateTime asofTsUtc,
        string cardText,
        ExecutionCardJsonV1? executableCardOverride = null,
        CancellationToken ct = default)
    {
        ticker = ticker.ToUpperInvariant();
        var keyAsOf = EnsureUtc(asofTsUtc);

        Console.WriteLine($"{DateTime.UtcNow:o} TRIG_START {ticker} keyAsOf={keyAsOf:o} cardLen={(cardText?.Length ?? 0)}");


        // 1) Load trader state
        var stateRows = await _db.SelectAsync<TraderStateRow>(
            "trader_state",
            $"?select=ticker,position,entry_price,stop_price,opened_at_utc,t1,t2,runner,t1_hit,t2_hit,runner_hit,last_signal_id,entry_type,reeval_stop,reeval_t1,reeval_t2,reeval_runner,reeval_at_utc" +
            $"&ticker=eq.{Uri.EscapeDataString(ticker)}&limit=1",
            ct);

        var st = stateRows.Count == 1 ? stateRows[0] : new TraderStateRow { Ticker = ticker, Position = "flat" };
        if (string.Equals(st.Position, "pending", StringComparison.OrdinalIgnoreCase)) {
            Console.WriteLine($"{DateTime.UtcNow:o} TRIG {ticker} SKIP_PENDING keyAsOf={keyAsOf:o} position=pending");
            return;
        }

        // 2) Load full session bars up to keyAsOf (from session open, not a fixed lookback).
        //    Using limit=60 previously meant entry levels set early in the session (e.g. prior
        //    day close reclaim at 9:45) were invisible by 11:30. Full session fixes this.
        var sessionOpenUtcForTrigger = MarketSession.GetSessionOpenUtcForDay(keyAsOf);
        var bars = await _db.SelectAsync<MinuteBarRow>(
            "minute_bars",
            $"?select=ticker,ts_utc,o,h,l,c,v" +
            $"&ticker=eq.{Uri.EscapeDataString(ticker)}" +
            $"&ts_utc=gte.{Uri.EscapeDataString(sessionOpenUtcForTrigger.ToString("o"))}" +
            $"&ts_utc=lte.{Uri.EscapeDataString(keyAsOf.ToString("o"))}" +
            $"&order=ts_utc.asc",
            ct);

        // No reverse needed — already ascending
        if (bars.Count < MinBars)
        {
            // If we have an ingestion service, try a REST pull to get current bars.
            // This handles the timing race where the scanner fires mid-bar and the
            // most recent bar hasn't been written to minute_bars by the WS ingestion yet.
            if (_ingest != null && bars.Count == 0)
            {
                AppLog.Info($"INSUFFICIENT_BARS {ticker} — REST ingest");
                Console.WriteLine($"{DateTime.UtcNow:o} TRIG {ticker} INSUFFICIENT_BARS keyAsOf={keyAsOf:o} bars={bars.Count} — attempting REST ingestion");
                try
                {
                    await _ingest.IngestTodayAndEnsureFeaturesUpToAsync(ticker, keyAsOf, keyAsOf, ct);
                    // Re-query after ingestion
                    bars = await _db.SelectAsync<MinuteBarRow>(
                        "minute_bars",
                        $"?select=ticker,ts_utc,o,h,l,c,v" +
                        $"&ticker=eq.{Uri.EscapeDataString(ticker)}" +
                        $"&ts_utc=gte.{Uri.EscapeDataString(sessionOpenUtcForTrigger.ToString("o"))}" +
                        $"&ts_utc=lte.{Uri.EscapeDataString(keyAsOf.ToString("o"))}" +
                        $"&order=ts_utc.asc",
                        ct);
                    AppLog.Info($"POST_INGEST {ticker} bars={bars.Count}");
                    Console.WriteLine($"{DateTime.UtcNow:o} TRIG {ticker} POST_INGEST bars={bars.Count}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{DateTime.UtcNow:o} TRIG {ticker} REST_INGEST_FAILED {ex.Message}");
                }
            }

            if (bars.Count < MinBars)
            {
                AppLog.Error($"INSUFFICIENT_BARS {ticker} bars={bars.Count} — skipped");
                Console.WriteLine($"{DateTime.UtcNow:o} TRIG {ticker} INSUFFICIENT_BARS keyAsOf={keyAsOf:o} bars={bars.Count} — skipping");
                AppLog.UpdatePipeline(ticker, cardGate: CardGateState.NoBars, cardLabel: $"bars={bars.Count}");
                return;
            }
        }

        var tsMin = EnsureUtc(bars.First().TsUtc);
        var tsMax = EnsureUtc(bars.Last().TsUtc); // should be <= keyAsOf

        // Defensive: should not happen given lte filter, but keep safe.
        if (tsMax > keyAsOf)
        {
            Console.WriteLine($"{DateTime.UtcNow:o} TRIG {ticker} ASOF_DRIFT_FUTURE keyAsOf={keyAsOf:o} tsMax={tsMax:o} (clamping)");
            tsMax = keyAsOf;
        }

        // Important: do NOT return if tsMax < keyAsOf; continue evaluating with last available bar (tsMax).
        if (tsMax < keyAsOf)
        {
            Console.WriteLine($"{DateTime.UtcNow:o} TRIG {ticker} ASOF_GAP keyAsOf={keyAsOf:o} tsMax={tsMax:o} (continuing with tsMax)");
        }

        // 3) Load features within the same bar window [tsMin..tsMax]
        var feats = await _db.SelectAsync<MinuteBarFeaturesRow>(
            "minute_bar_features",
            $"?select=ticker,ts_utc,vwap,dist_to_vwap" +
            $"&ticker=eq.{Uri.EscapeDataString(ticker)}" +
            $"&ts_utc=gte.{Uri.EscapeDataString(tsMin.ToString("o"))}" +
            $"&ts_utc=lte.{Uri.EscapeDataString(tsMax.ToString("o"))}" +
            $"&order=ts_utc.asc&limit=50000",
            ct);

        var featByTs = feats.ToDictionary(f => EnsureUtc(f.TsUtc), f => f);

        var series = new List<BarWithFeat>(bars.Count);
        int miss = 0;

        Console.WriteLine($"{DateTime.UtcNow:o} TRIG {ticker} LOAD_SERIES keyAsOf={keyAsOf:o} tsMin={tsMin:o} tsMax={tsMax:o} bars={bars.Count} feats={feats.Count}");
        foreach (var b in bars)
        {
            var bts = EnsureUtc(b.TsUtc);

            // Skip bars outside [tsMin..tsMax] defensively (shouldn't happen).
            if (bts < tsMin || bts > tsMax) {
                continue;
            }

            if (!featByTs.TryGetValue(bts, out var f)) { miss++; continue; }

            series.Add(new BarWithFeat(
                bts,
                b.O, b.H, b.L, b.C,
                b.V,
                f.Vwap,
                f.DistToVwap
            ));
        }

        if (series.Count < MinBars)
        {
            Console.WriteLine($"{DateTime.UtcNow:o} TRIG {ticker} INSUFFICIENT_SERIES keyAsOf={keyAsOf:o} tsMax={tsMax:o} series={series.Count} missFeat={miss}");
            return;
        }

        // 4) Scenario source. In Stage 2B.4 enforce mode ProduceCardWorker passes the
        // normalized executable card directly. This keeps execution_card_scenarios as the
        // raw model/audit record while preventing structurally invalid scenarios from
        // reaching entry/exit scenario evaluation. A non-null override is authoritative
        // even when it contains zero scenarios (effective NO_TRADE).
        List<ParsedScenario> scenarios;
        if (executableCardOverride is not null)
        {
            // Preserve the authoritative executable order supplied by ProduceCardWorker.
            // Stage 2B.4 structural-only order is scenario_rank order; Stage 2D enforce mode
            // supplies PREFERRED -> SECONDARY -> scenario_rank. Original ScenarioRank values
            // are retained for audit/dedup/signal persistence.
            scenarios = executableCardOverride.Scenarios
                .Take(3)
                .Select(ToParsedScenario)
                .ToList();

            Console.WriteLine(
                $"{DateTime.UtcNow:o} TRIG {ticker} EXECUTABLE_OVERRIDE " +
                $"keyAsOf={keyAsOf:o} verdict={executableCardOverride.Verdict} scenarios={scenarios.Count} " +
                $"order=[{string.Join(",", scenarios.Select(s => s.Rank))}]");
        }
        else
        {
            var scenarioRows = await _db.SelectAsync<ExecutionCardScenarioRow>(
                "execution_card_scenarios",
                $"?select=ticker,asof_ts_utc,rank,direction,entry_type,entry_low,entry_high,stop,t1,t2,runner,scenario_prob,success_prob,setup,grade,grade_rationale" +
                $"&ticker=eq.{Uri.EscapeDataString(ticker)}" +
                $"&asof_ts_utc=eq.{Uri.EscapeDataString(keyAsOf.ToString("o"))}" +
                $"&order=rank.asc&limit=3",
                ct);

            if (scenarioRows.Count == 0)
            {
                Console.WriteLine($"{DateTime.UtcNow:o} TRIG {ticker} NO_SCENARIOS_DB keyAsOf={keyAsOf:o} tsMax={tsMax:o} cardLen={(cardText?.Length ?? 0)}");
            }

            if (scenarioRows.Count > 0)
            {
                scenarios = scenarioRows
                    .OrderBy(r => r.rank)
                    .Take(3)
                    .Select(r => new ParsedScenario(
                        r.rank,
                        (r.direction ?? "").ToLowerInvariant(),
                        r.entry_low,
                        r.entry_high,
                        FromDbEntryType(r.entry_type ?? ""),
                        r.stop,
                        r.t1,
                        r.t2,
                        r.runner,
                        r.scenario_prob,
                        r.success_prob,
                        r.setup ?? "",
                        r.grade,
                        r.grade_rationale
                    ))
                    .ToList();
            }
            else
            {
                // Legacy fallback only when no authoritative Stage 2B.4 override was supplied.
                scenarios = ExecutionCardParser.ParseTop3(cardText);
            }
        }

        // -------------------------
        // EXIT LOGIC
        // -------------------------
        if (st.Position == "long" || st.Position == "short")
        {
            // A) Hard stop
            if (ExitDetectors.IsStopHit(series, st, out var stopReason))
            {
                Console.WriteLine($"{DateTime.UtcNow:o} TRIG {ticker} EXIT_STOP_HIT keyAsOf={keyAsOf:o} reason={stopReason}");
                await EmitExitSignalAsync(ticker, keyAsOf, st, scenarioRank: 0, stopReason, ct);
                return;
            }

            // B) Targets
            if (ExitDetectors.IsTargetHit(series, st, out var tgtName, out var _tgtPrice, out var tgtReason))
            {
                await EmitExitSignalAsync(ticker, keyAsOf, st, scenarioRank: 0, tgtReason, ct);

                object? patch = tgtName switch
                {
                    "T1_HIT" => new { t1_hit = true },
                    "T2_HIT" => new { t2_hit = true },
                    "RUNNER_HIT" => new { runner_hit = true },
                    _ => null
                };

                if (patch is not null)
                    await _db.PatchAsync("trader_state", $"?ticker=eq.{Uri.EscapeDataString(ticker)}", patch, ct);

                Console.WriteLine($"{DateTime.UtcNow:o} TRIG {ticker} EXIT_TARGET_HIT keyAsOf={keyAsOf:o} target={tgtName} reason={tgtReason}");
                return;
            }

            // C) Exit reminder — fires every bar when past last defined target with no manual exit
            if (ExitDetectors.ShouldEmitExitReminder(st, out var reminderReason))
            {
                Console.WriteLine($"{DateTime.UtcNow:o} TRIG {ticker} EXIT_REMINDER keyAsOf={keyAsOf:o} reason={reminderReason}");
                await EmitExitSignalAsync(ticker, keyAsOf, st, scenarioRank: 0, reminderReason, ct);
                // Do NOT return — fall through so opposite scenario is also evaluated this bar
            }

            // D) Opposite scenario confirmed (best matching opposite in top3)
            var opposite = PickBestOppositeScenario(scenarios, st.Position);
            if (opposite is not null && ExitDetectors.IsOppositeScenarioConfirmed(series, opposite, out var oppReason))
            {
                Console.WriteLine($"{DateTime.UtcNow:o} TRIG {ticker} EXIT_OPPOSITE_SCENARIO keyAsOf={keyAsOf:o} scenarioRank={opposite.Rank} reason={oppReason}");
                await EmitExitSignalAsync(ticker, keyAsOf, st, scenarioRank: opposite.Rank, oppReason, ct);
                return;
            }

            Console.WriteLine($"{DateTime.UtcNow:o} TRIG {ticker} RETURNING SINCE 1 BAR PROCESSED keyAsOf={keyAsOf:o} position={st.Position}");
            return;
        }

        // If not flat, do nothing
        if (!string.Equals(st.Position, "flat", StringComparison.OrdinalIgnoreCase)) {
            Console.WriteLine($"{DateTime.UtcNow:o} TRIG {ticker} RETURNING SINCE NON-FLAT POSITION keyAsOf={keyAsOf:o} position={st.Position}");
            return;
        }

        Console.WriteLine($"{DateTime.UtcNow:o} TRIG {ticker} EVALUATE_ENTRIES keyAsOf={keyAsOf:o} scenarios={scenarios.Count}");
        // -------------------------
        // ENTRY LOGIC
        // -------------------------
        foreach (var s in scenarios)
        {
            if ((s.ScenarioProb ?? 0m) < 0.35m) {
                Console.WriteLine($"{DateTime.UtcNow:o} TRIG {ticker} SKIP_LOW_SCENARIO_PROB keyAsOf={keyAsOf:o} scenarioRank={s.Rank} scenarioProb={s.ScenarioProb}");
                continue;
            }
            if ((s.SuccessProb ?? 0m) < 0.55m) {
                Console.WriteLine($"{DateTime.UtcNow:o} TRIG {ticker} SKIP_LOW_SUCCESS_PROB keyAsOf={keyAsOf:o} scenarioRank={s.Rank} successProb={s.SuccessProb}");
                continue;
            }

            // Grade filter — skip scenarios below the configured minimum grade.
            if (!ScenarioValidator.MeetsMinGrade(s.Grade, TriggerConfig.MinimumGrade)) {
                Console.WriteLine($"{DateTime.UtcNow:o} TRIG {ticker} SKIP_GRADE_BELOW_MIN keyAsOf={keyAsOf:o} scenarioRank={s.Rank} grade={s.Grade ?? "null"} minGrade={TriggerConfig.MinimumGrade}");
                continue;
            }

            // Level-order sanity check — reject hallucinated or incoherent LLM price levels.
            if (!ScenarioValidator.IsLevelOrderValid(s, out var levelReason)) {
                Console.WriteLine($"{DateTime.UtcNow:o} TRIG {ticker} SKIP_INVALID_LEVELS keyAsOf={keyAsOf:o} scenarioRank={s.Rank} reason={levelReason}");
                continue;
            }

            var exists = await _db.ExistsAsync(
                "signal_events",
                $"?ticker=eq.{Uri.EscapeDataString(ticker)}" +
                $"&asof_ts_utc=eq.{Uri.EscapeDataString(keyAsOf.ToString("o"))}" +
                $"&scenario_rank=eq.{s.Rank}" +
                $"&event_type=eq.entry",
                ct);
            if (exists) {
                Console.WriteLine($"{DateTime.UtcNow:o} TRIG {ticker} SKIP_ALREADY_TRIGGERED keyAsOf={keyAsOf:o} scenarioRank={s.Rank}");
                continue;
            }

            bool presented;
            string reason = "";

            presented = s.EntryType switch
            {
                EntryType.ReclaimHold       => ScenarioDetectors.IsReclaimHoldPresented(series, s, out reason),
                EntryType.BreakHold         => ScenarioDetectors.IsBreakHoldPresented(series, s, out reason),
                EntryType.FadePop           => ScenarioDetectors.IsFadePopPresented(series, s, out reason),
                EntryType.VwapReclaim       => ScenarioDetectors.IsVwapReclaimPresented(series, s, out reason),
                EntryType.OverextensionFade => ScenarioDetectors.IsOverextensionFadePresented(series, s, out reason),
                _ => (reason = "Unknown entry type", false).Item2
            };

            if (!presented) {
                Console.WriteLine($"{DateTime.UtcNow:o} TRIG {ticker} SKIP_ENTRY_NOT_PRESENTED keyAsOf={keyAsOf:o} scenarioRank={s.Rank} entryType={s.EntryType} reason={reason}");
                AppLog.UpdatePipeline(ticker, cardGate: CardGateState.Passed, signal: SignalState.Pending);
                continue;
            }

            var row = new SignalEventRow
            {
                Ticker = ticker,
                AsofTsUtc = keyAsOf,
                ScenarioRank = s.Rank,
                Direction = s.Direction,
                EntryLow = s.EntryLow,
                EntryHigh = s.EntryHigh,
                EntryType = ToDbEntryType(s.EntryType),
                StopPrice = s.Stop,
                T1 = s.T1,
                T2 = s.T2,
                Runner = s.Runner,
                ScenarioProb = s.ScenarioProb,
                SuccessProb = s.SuccessProb,
                Grade = s.Grade,
                GradeRationale = s.GradeRationale,
                TriggerReason = reason,
                Triggered = true,
                EventType = "entry"
            };

            var claim = await _db.RpcAsync<PendingClaimResult>(
                "ava_claim_entry_signal",
                new
                {
                    p_signal = new
                    {
                        id              = row.Id,
                        ticker         = row.Ticker,
                        asof_ts_utc     = row.AsofTsUtc,
                        scenario_rank  = row.ScenarioRank,
                        direction      = row.Direction,
                        entry_low      = row.EntryLow,
                        entry_high     = row.EntryHigh,
                        entry_type     = row.EntryType,
                        stop_price     = row.StopPrice,
                        t1             = row.T1,
                        t2             = row.T2,
                        runner         = row.Runner,
                        scenario_prob  = row.ScenarioProb,
                        success_prob   = row.SuccessProb,
                        grade          = row.Grade,
                        grade_rationale = row.GradeRationale,
                        trigger_reason = row.TriggerReason
                    }
                },
                ct);

            if (!claim.Claimed)
            {
                Console.WriteLine($"{DateTime.UtcNow:o} TRIG {ticker} ENTRY_CLAIM_REJECTED " +
                                  $"keyAsOf={keyAsOf:o} scenarioRank={s.Rank} reason={claim.Reason}");
                return;
            }

            AppLog.Trigger($"ENTRY_EMITTED {ticker} rank={s.Rank} {s.Direction} {s.EntryType} reason={reason}");
            Console.WriteLine($"{DateTime.UtcNow:o} TRIG {ticker} ENTRY_EMITTED keyAsOf={keyAsOf:o} scenarioRank={s.Rank} reason={reason}");
            AppLog.UpdatePipeline(ticker, cardGate: CardGateState.Passed, signal: SignalState.Emitted);

            AppLog.Trigger($"ENTRY_TRIGGERED {ticker} rank={s.Rank} {s.Direction} {s.EntryType}");
            Console.WriteLine($"{DateTime.UtcNow:o} TRIG {ticker} ENTRY_TRIGGERED keyAsOf={keyAsOf:o} scenarioRank={s.Rank} reason={reason}");
            return; // one entry per asof
        }
    }

    private static ParsedScenario? PickBestOppositeScenario(List<ParsedScenario> scenarios, string position)
    {
        var want = position == "long" ? "short" : "long";
        return scenarios
            .Where(s => s.Direction == want)
            .OrderByDescending(s => (s.SuccessProb ?? 0m) * (s.ScenarioProb ?? 0m))
            .FirstOrDefault();
    }

    private async Task EmitExitSignalAsync(string ticker, DateTime asofTsUtc, TraderStateRow st, int scenarioRank, string reason, CancellationToken ct)
    {
        asofTsUtc = EnsureUtc(asofTsUtc);

        // Dedup by reason: allow multiple exit rows per bar (stop, target, reminder, opposite
        // can all fire on the same minute) but never write the exact same reason twice.
        var exists = await _db.ExistsAsync(
            "signal_events",
            $"?ticker=eq.{Uri.EscapeDataString(ticker)}" +
            $"&asof_ts_utc=eq.{Uri.EscapeDataString(asofTsUtc.ToString("o"))}" +
            $"&event_type=eq.exit" +
            $"&trigger_reason=eq.{Uri.EscapeDataString(reason)}",
            ct);
        if (exists) return;

        var row = new SignalEventRow
        {
            Ticker = ticker,
            AsofTsUtc = asofTsUtc,
            ScenarioRank = 0,
            Direction = st.Position,
            EntryType = "exit",
            EntryLow = null,
            EntryHigh = null,
            StopPrice = st.EffectiveStop,
            T1 = st.EffectiveT1,
            T2 = st.EffectiveT2,
            Runner = st.EffectiveRunner,
            ScenarioProb = null,
            SuccessProb = null,
            TriggerReason = reason,
            Triggered = true,
            EventType = "exit"
        };

        Console.WriteLine($"{DateTime.UtcNow:o} TRIG {ticker} EXIT_EMITTED keyAsOf={asofTsUtc:o} reason={reason}");

        await _db.InsertAsync("signal_events", new[] { row }, ct);
    }

    private static string ToDbEntryType(EntryType et) => et switch
    {
        EntryType.ReclaimHold      => "reclaim_hold",
        EntryType.BreakHold        => "break_hold",
        EntryType.FadePop          => "fade_pop",
        EntryType.VwapReclaim      => "vwap_reclaim",
        EntryType.OverextensionFade => "overextension_fade",
        _                          => "fade_pop"
    };

    private static ParsedScenario ToParsedScenario(ExecutionScenarioJsonV1 s) => new(
        s.ScenarioRank,
        (s.Direction ?? "").ToLowerInvariant(),
        s.EntryLow,
        s.EntryHigh,
        FromDbEntryType(s.EntryType ?? ""),
        s.StopPrice,
        s.T1,
        s.T2,
        s.Runner,
        s.ScenarioProb,
        s.SuccessProb,
        "stage2b4_normalized",
        s.Grade,
        s.GradeRationale);

    private static EntryType FromDbEntryType(string s) => s switch
    {
        "reclaim_hold"       => EntryType.ReclaimHold,
        "break_hold"         => EntryType.BreakHold,
        "fade_pop"           => EntryType.FadePop,
        "vwap_reclaim"       => EntryType.VwapReclaim,
        "overextension_fade" => EntryType.OverextensionFade,
        _                    => EntryType.FadePop
    };

    private static DateTime EnsureUtc(DateTime dt)
    {
        if (dt.Kind == DateTimeKind.Utc) return dt;
        if (dt.Kind == DateTimeKind.Local) return dt.ToUniversalTime();
        // Unspecified: assume it's already UTC coming from DB (common with some serializers)
        return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
    }
}
