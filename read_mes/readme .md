#1 — Pre-market data (PolygonClient, PayloadBuilder)
Added GetPreMarketBarsAsync fetching 4:00am–9:29am ET bars with extended_hours=true. Payload now includes premarket_high, premarket_low, premarket_open, premarket_close in reference_levels and a full premarket_bars array.
#2 — TriggerEngine full-session lookback (TriggerEngine)
Replaced limit=60 with a session-anchored query (ts_utc >= sessionOpen). Entry levels set early in the session are no longer invisible to the detector later in the day.
#3 — Volume context (PolygonClient, PayloadBuilder)
Added GetDaySnapshotVolumeAsync. Payload now includes a volume_context block with today_volume, prev_day_volume, avg_daily_volume, and rvol_vs_adv — today's running volume vs ADV scaled to session elapsed time.
#4 — Event-driven card cadence (CardEventDetector, Program.cs)
New CardEventDetector class detects volume spikes, VWAP crosses, new HOD/LOD, and key level approaches from the already-built dataset JSON. Cards fire immediately on meaningful events regardless of the 5-minute clock.
#5 — rel_volume baseline (SessionFeatureCalculator, RealtimeFeatureComputer)
Replaced the 5-bar rolling window with a hybrid baseline: cumulative session average (prior bars only) blended with the 5-bar window for the first 10 bars. rel_volume = 3.0 now genuinely means 3x the session's average pace.
#6 — Card quality feedback loop (CardQualityTracker, ProduceCardWorker, Program.cs)
New CardQualityTracker tracks recent card quality per ticker. Cold tickers (3 consecutive low-quality cards) get backed off to 15-minute cadence automatically. Event-driven triggers still fire immediately regardless of cold status.