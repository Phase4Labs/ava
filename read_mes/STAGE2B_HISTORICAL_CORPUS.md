# AVA Stage 2B — Historical GPT Corpus

Stage 2B converts the existing `eval_examples` history into a causal dataset for AVA evaluation, retrieval, and later fine-tuning.

## Why `eval_examples`

Each row already preserves:

- ticker
- decision timestamp
- exact model input JSON
- normalized GPT output
- input hash
- model/framework version
- OpenAI response id

Older rows did not include scenario `grade` or `grade_rationale` inside `model_output_json`. Stage 2B enriches those fields from `execution_card_scenarios` when an exact ticker/timestamp/rank match exists. `CalibrationCollector` is also updated so new rows retain those fields directly.

## No look-ahead rule

The corpus has an explicit boundary:

- model input = stored `input_json`, containing information available at/before `ts_asof_utc`
- outcome label = Massive minute bars strictly after `ts_asof_utc`

Future bars are never merged into either the full or compact model input.

## Realized outcome method

For each stored scenario:

1. Reconstruct AVA session features from the stored intraday bars plus later Massive bars.
2. Reuse the existing `ScenarioDetectors` to determine whether the setup actually presents after the card.
3. The trigger bar itself is not used to score stop/T1 because the detector requires that completed bar.
4. Starting with the next minute, classify T1-before-stop, stop-before-T1, ambiguous same-minute stop/T1, T2, runner, not-triggered, or open-at-close.
5. Compute MFE/MAE in R using the direction-aware conservative edge of the entry window.

This is a scenario-path label, not a claim about actual brokerage P&L or manual fills.

## Step 1 — inventory

```powershell
dotnet run -- --corpus-inventory
```

This is read-only and reports counts for the major historical tables plus the GPT-5.2 eval-example date range.

## Step 2 — build a small sample first

```powershell
dotnet run -- --corpus-build --model=gpt-5.2 --limit=100
```

Recommended first filtered run:

```powershell
dotnet run -- --corpus-build `
  --ticker=AAPL `
  --model=gpt-5.2 `
  --limit=100
```

Outputs under `historical_corpus`:

- `*.jsonl` — one causal teacher record per GPT example; compact_v1 is always included
- `*_scenarios.csv` — scenario-level outcome table
- `*_summary.json` — aggregate metrics by direction, entry type, and grade

Use `--include-full-input` only when you need the original large input JSON embedded in the export. By default the source remains in Supabase and the corpus stores compact_v1 to keep local files manageable.

## Important interpretation

GPT-5.2 is the teacher, not ground truth. The corpus intentionally keeps three independent dimensions:

1. what GPT proposed
2. whether the semantic validator accepts it
3. what the market did afterward

This allows later AVA training to prefer strong GPT examples without blindly reproducing GPT mistakes.

## Stage 2B.2 — expectancy calibration and validator diagnostics

Stage 2B.2 adds an explicit R-based expectancy layer. It does not change production signal logic.

The fixed comparison policy is `resolved_t1_or_initial_stop`:

- if T1 is reached before the initial stop, the scenario earns its direction-aware conservative T1 R:R
- if the initial stop is reached before T1, the scenario loses `-1R`
- not-triggered, ambiguous, invalid, and open-at-close cases are excluded from resolved expectancy
- `expectancy_r_per_triggered_zero_unresolved` is also reported as a conservative secondary metric that counts unresolved triggered scenarios as `0R`

The summary now reports, for accepted scenarios, rejected scenarios, every rejection reason, and every observed combination of rejection reasons:

- resolved R sample count
- mean and median realized R
- profit factor
- average winning R
- average losing R magnitude
- break-even win rate implied by average winning R
- R expectancy per triggered scenario with unresolved cases treated as zero
- MFE/MAE and the existing trigger/T1/stop statistics

The scenario CSV also adds `resolved_t1_or_stop_r` and `semantic_error_codes`.

A diagnostics CSV is produced for the two suspicious rules that require direct inspection before calibration:

- `level_order`
- `rr_unavailable`

By default, up to 20 examples per reason are exported with full scenario levels, semantic errors, trigger timestamps, outcomes, MFE/MAE, and realized R. Change that with:

```powershell
--diagnostic-limit=50
```

or disable the diagnostic sample with:

```powershell
--diagnostic-limit=0
```

These expectancy metrics are research labels only. They assume a full exit at T1 or the initial stop and therefore are not brokerage P&L, slippage-adjusted returns, or the final AVA execution policy.
