# AVA Stage 2A — Compact Market State + Semantic Gate

Stage 2A is evaluation-only. It does not replace the production GPT-5.2 signal path.

For each historical replay timestamp, `--stage2a` runs three analyses:

1. **GPT FULL** — GPT-5.2 receives the existing Stage-1 full payload. This is the quality baseline.
2. **GPT COMPACT** — GPT-5.2 receives `compact_v1` market state.
3. **LOCAL COMPACT** — the Ollama model receives the same compact payload used by GPT COMPACT.

Each parsed card then passes through `ScenarioSemanticValidator`.

## Compact payload

`CompactMarketStateBuilder` preserves:

- reference levels
- volume context
- timestamp-safe Massive news
- prior daily bars
- session/composite volume profile and VP context

It changes the bar representation:

- latest 60 regular-session bars remain at 1-minute resolution
- older regular-session bars are aggregated to 5-minute OHLCV
- premarket is aggregated to 15-minute OHLCV and a deterministic premarket summary
- a deterministic state block precomputes session/day change, VWAP distance, recent returns, volume acceleration, VWAP crosses, recent high/low, and regime

The full payload is still built first in Stage 2A so the experiment compares exactly the same causal source data. Production optimization can later build the compact state directly.

## Semantic gate

The deterministic gate currently checks:

- directional level ordering
- conservative T1 risk/reward >= 1.50:1 using the adverse edge of the entry window
- near-duplicate scenarios
- A/B grade probability requirements
- grade >= `TriggerConfig.MinimumGrade`
- A/B proximity to an applicable VP/structural anchor <= 0.50%
- RVOL grade/actionability rules
- overextension-fade RVOL/day-gain guards
- LONG >10% day-gain/no-news catalyst disqualifier
- warnings for catalyst claims without supplied news and for unverified divergence claims

`EffectiveVerdict` is `TRADE` only if at least one proposed scenario survives the deterministic gate.

## Regression command

Use the same AAPL timestamp that exposed the first local-model disagreement:

```powershell
dotnet run -- --historical-shadow `
  --stage2a `
  --ticker=AAPL `
  --start-et=2026-08-07T10:00
```

Expected console sections:

```text
FULL payload
COMPACT payload
GPT FULL
GPT COMPACT
LOCAL COMPACT
SEMANTIC GPT FULL
SEMANTIC GPT COMPACT
SEMANTIC LOCAL
QUALITY FULL→COMPACT GPT
COMPARE GPT COMPACT↔LOCAL
COMPARE AFTER GATE
```

## What determines success

Do not approve compact_v1 merely because it is smaller.

For a larger replay sample, compare:

- GPT FULL vs GPT COMPACT raw verdicts and scenario characteristics
- semantic effective verdicts
- accepted scenario counts
- entries/stops/targets
- realized outcomes in later historical bars
- GPT input-token reduction
- local prompt tokens and elapsed time

The compact representation is promoted only if cost/latency improves without a material degradation in signal quality.
