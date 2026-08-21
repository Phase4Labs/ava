# AVA Historical Dual-Model Shadow Replay

This mode lets AVA run on a closed-market day using Massive historical data while keeping GPT-5.2 as the cloud benchmark and Qwen3 as a local shadow model.

## Safety

`--historical-shadow` exits before live WebSockets, scanner, TriggerEngine, position re-evaluation, and normal signal production are started.

The mode may upsert historical `minute_bars` and `minute_bar_features` into the configured shadow Supabase project so the existing Stage-1 `PayloadBuilder` can be reused. It does **not** write `analysis_jobs`, `execution_cards`, `execution_card_scenarios`, triggers, or signals.

## First recommended test

Use one early-session snapshot so the full Stage-1 payload remains reasonable for the CPU-only Qwen test machine:

```powershell
dotnet run -- --historical-shadow --ticker=AAPL --start-et=2026-08-07T10:00
```

`10:00 ET` is the analysis clock time. AVA uses only fully closed bars, so the newest regular-session bar in that request is `09:59 ET`.

The default run performs one GPT-5.2 call and one `qwen3:8b` call.

## Advance the historical clock

After the one-snapshot test passes:

```powershell
dotnet run -- --historical-shadow --ticker=AAPL --start-et=2026-08-07T10:00 --steps=6 --step-minutes=5
```

This evaluates at:

- 10:00 ET (bars through 09:59)
- 10:05 ET (bars through 10:04)
- 10:10 ET (bars through 10:09)
- 10:15 ET (bars through 10:14)
- 10:20 ET (bars through 10:19)
- 10:25 ET (bars through 10:24)

You can also use an explicit end:

```powershell
dotnet run -- --historical-shadow --ticker=AAPL --start-et=2026-08-07T10:00 --end-et=2026-08-07T11:00 --step-minutes=5
```

## Useful switches

Local-only (no OpenAI cost):

```powershell
dotnet run -- --historical-shadow --ticker=AAPL --start-et=2026-08-07T10:00 --no-cloud
```

GPT-only:

```powershell
dotnet run -- --historical-shadow --ticker=AAPL --start-et=2026-08-07T10:00 --no-local
```

Help:

```powershell
dotnet run -- --historical-shadow-help
```

## Local context protection

The runner sets Ollama `num_ctx` to 32,768 tokens by default and estimates prompt size before each local call. If the estimated input exceeds 85% of the configured context, AVA skips the local call instead of risking silent context truncation.

Override if the machine can support a larger context:

```powershell
dotnet run -- --historical-shadow --ticker=AAPL --start-et=2026-08-07T10:00 --local-context-tokens=65536
```

For this Intel CPU-only test laptop, do not increase context merely to force late-session full-payload tests. Stage 2 payload compression is intended to solve that problem properly.

## Historical look-ahead protection

Historical mode uses:

- regular-session minute bars capped at the replay time;
- features calculated causally from bars up to each timestamp;
- prior-day daily bars only;
- premarket data from that same historical trading date;
- news filtered to `published_utc <= payload as-of`;
- running session volume calculated from historical bars already visible at the replay time.

It does **not** use Massive's current snapshot volume when replaying history.

## Results

By default results are written under:

```text
historical_shadow_results\<TICKER>_<START>_shadow.jsonl
```

Each line contains:

- analysis timestamp and bar cap;
- payload size;
- GPT-5.2 card and token usage;
- local Qwen card and local token counts;
- model latency;
- verdict agreement;
- top direction / entry-type / grade agreement;
- probability and price-level deltas.

The JSONL file is analysis evidence only. It is not consumed by live signal generation.
