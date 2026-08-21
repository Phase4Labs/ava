# AVA Stage 2C — Historical Analogue Retrieval v1

Stage 2C adds a deterministic local historical-memory layer built from the Stage 2B corpus. It makes no embedding/API calls.

## Causal safety

Outcome-bearing analogue records are eligible only when their US/Eastern session date is strictly before the query session date. Same-day historical records are deliberately excluded during replay because those corpus rows contain outcomes evaluated through that day's close.

## Build the index

```powershell
$corpus = Get-ChildItem .\historical_corpus_full_recent\*.jsonl |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

dotnet run -- --historical-shadow --analogue-build `
  --corpus=$corpus.FullName `
  --output=.\historical_corpus_full_recent\ava_analogue_index.json
```

## Local historical replay with analogues

```powershell
dotnet run -- --historical-shadow `
  --stage2a `
  --no-cloud `
  --ticker=AAPL `
  --start-et=2026-08-07T10:00 `
  --analogue-index=.\historical_corpus_full_recent\ava_analogue_index.json `
  --analogue-top=24
```

The local payload receives `historical_analogue_context` with diversified nearest prior-session states, aggregate outcomes by direction + entry type, sample counts, trigger/T1/stop counts, realized-R summaries, Stage 2B.4 preferred/secondary counts, and a small auditable example set.

Historical price levels are not copied into current scenarios.

## Retrieval v1

Similarity uses standardized deterministic features including day/session change, distance to VWAP, 5/15-bar returns, volume acceleration, relative volume, position in the session range, minutes since open, premarket change, and regime. Results are diversified so one ticker/day cannot dominate the context.

## Scope

This is an empirical-context layer for local shadow analysis. It does not fine-tune a model, call embeddings, or alter production GPT signals.


## Stage 2C.1 local proposal contract

The local shadow model now uses a stricter schema than the cloud production model. A local TRADE scenario must include numeric `entry_low`, `entry_high`, `stop_price`, and `t1`, because these are required by the promoted Stage 2B.4 executable contract. If Qwen cannot provide complete geometry, it should return `NO_TRADE`.

Analogue indexes must be built from a corpus generated after Stage 2B.4 was introduced. Index building now fails explicitly when the corpus has no structurally valid Stage 2B.4 scenario metadata.

## Stage 2C.4 deterministic local-memory benchmark

The benchmark harness measures whether historical analogue context earns its added latency and complexity. It samples historical states deterministically from the Stage 2B.4-enriched corpus, balances stored GPT TRADE/NO_TRADE examples where possible, and diversifies TRADE examples across session bucket, direction, setup family, and realized outcome.

Each selected state is replayed twice through the same local model with local structural repair disabled:

1. compact market state only;
2. the same compact state plus the causal prior-session Stage 2C analogue context.

The stored GPT card is used as a teacher/reference only; the benchmark makes no OpenAI calls. Both local arms pass through Stage 2B.4. For the top executable local scenario, the harness also evaluates the historical trigger/T1/stop outcome through that session close and reports resolved R when the T1/initial-stop policy resolves.

The run is resumable. Completed per-state JSONL results are reused unless `--rerun` is specified. Progressive CSV/JSON summaries are rewritten after every state pair so a long laptop run can be interrupted without losing completed work.

Example:

```powershell
dotnet run -- --historical-shadow `
  --stage2c-benchmark `
  --corpus=".\historical_corpus_stage2c_source\ava_corpus_all_20260809_161635.jsonl" `
  --analogue-index=".\historical_corpus_stage2c_source\ava_analogue_index.json" `
  --local-model=gpt-oss:20b `
  --sample=10 `
  --seed=42 `
  --output-dir=".\stage2c_benchmark"
```

Primary comparison metrics include teacher raw/Stage 2B.4 structural agreement, structurally valid and preferred scenario counts, selected-scenario historical realized R, expectancy per triggered selection with unresolved outcomes scored as zero, and median local inference latency.

## Stage 2C.5 prompt-memory benchmark decision

The 10-state general benchmark and 10-state resolved-opportunity benchmark did not justify promoting prompt-injected analogue memory. In the resolved-opportunity sample, memory produced fewer direction mismatches and only PREFERRED valid scenarios, but realized -2R across two resolved selections while the no-memory arm realized +5R across one resolved selection. Memory also added roughly 20%+ median inference latency.

The key architectural finding was that prompt memory can change the local model's direction/setup selection. In one resolved benchmark state, the stored teacher setup was SHORT `overextension_fade` (+3R), while memory-assisted 20B chose SHORT `reclaim_hold` and lost -1R. That is not acceptable evidence for making analogue context a default prompt input.

Therefore `--analogue-index` is retained only as a legacy research comparison mode. It is not the Stage 2C.6 recommended path.

## Stage 2C.6 candidate-conditioned historical evidence

Stage 2C.6 moves historical evidence **after** model proposal and Stage 2B.4 structural validation:

```text
compact market state
        -> local model proposal
        -> Stage 2B.4 structural gate
        -> candidate-conditioned historical evidence sidecar
```

Historical evidence is never added to the LLM prompt in this mode and never changes the execution verdict. It evaluates only the already-proposed, structurally valid direction + entry-type combination.

Use:

```powershell
dotnet run -- --historical-shadow `
  --stage2a `
  --no-cloud `
  --ticker=ONDS `
  --start-et=2026-07-07T10:14 `
  --local-model=gpt-oss:20b `
  --no-local-repair `
  --candidate-evidence-index=".\historical_corpus_stage2c_source\ava_analogue_index.json" `
  --candidate-evidence-top=24 `
  --output=".\historical_shadow_results\ONDS_1014_candidate_evidence.jsonl"
```

For each normalized executable scenario, the sidecar returns causally eligible prior-session records that match the same direction and setup family. One matching scenario per historical record is retained before diversification so a single old execution card cannot overweight the evidence.

Reported evidence includes:

- eligible and returned matching historical records;
- average state-similarity distance;
- known trigger count/rate;
- T1-before-stop and stop-before-T1 counts;
- resolved-R sample count;
- positive/negative resolved counts;
- mean and median realized R;
- T1 rate among resolved T1/stop outcomes;
- expectancy R per triggered selection with unresolved triggered cases scored as zero;
- historical PREFERRED/SECONDARY counts;
- a small audit sample containing ticker/time/outcome/R only, never historical price levels.

Stage 2C.6 remains shadow-only. No evidence threshold, veto, promotion, or quality-tier change is justified until candidate-conditioned evidence is calibrated against realized outcomes.


## Stage 2C.6 offline evidence calibration

Candidate-conditioned evidence can be calibrated against the existing Stage 2B.4 corpus without any LLM or market-data calls. For each structurally valid scenario with a resolved R label, AVA reconstructs the compact decision state, queries only completed prior sessions for the same direction + setup family, and compares the sidecar evidence with the subsequently realized R.

```powershell
dotnet run -- --historical-shadow `
  --candidate-evidence-calibrate `
  --corpus=.\historical_corpus_stage2c_source\ava_corpus_all_....jsonl `
  --candidate-evidence-index=.\historical_corpus_stage2c_source\ava_analogue_index.json `
  --candidate-evidence-top=24 `
  --minimum-evidence-records=5 `
  --output-dir=.\stage2c6_candidate_evidence_calibration
```

Outputs include raw scenario-level calibration rows plus evidence-mean-R, evidence-expectancy, distance, and sample-size buckets. No decision threshold is promoted by this step; it is research-only evidence about whether the sidecar is actually predictive.


## Stage 2C.7 temporal holdout validation

Stage 2C.7 tests candidate-conditioned evidence out of sample before it can influence AVA quality tiers or execution. The default split is July 1-31 for training/calibration and August 1-7 for holdout.

The strong-support threshold is derived only from the training distribution of candidate evidence expectancy (`--strong-quantile=0.80` by default). Evidence bands are then frozen as:

- `INSUFFICIENT`: fewer than the configured minimum returned evidence records or no expectancy value;
- `NEGATIVE`: evidence expectancy below 0;
- `NEUTRAL`: evidence expectancy from 0 up to the frozen strong threshold;
- `STRONG`: evidence expectancy at or above the frozen training threshold.

For an even stricter holdout, every August row uses an evidence pool frozen before August 1. Earlier August outcomes are therefore not allowed to become evidence for later August decisions during this validation.

```powershell
dotnet run -- --historical-shadow `
  --candidate-evidence-holdout `
  --corpus=.\historical_corpus_stage2c_source\ava_corpus_all_....jsonl `
  --candidate-evidence-index=.\historical_corpus_stage2c_source\ava_analogue_index.json `
  --train-from-et=2026-07-01 --train-to-et=2026-07-31 `
  --holdout-from-et=2026-08-01 --holdout-to-et=2026-08-07 `
  --strong-quantile=0.80 --minimum-evidence-records=5 `
  --output-dir=.\stage2c7_temporal_holdout
```

No LLM or network calls are made. Stage 2C.7 remains research-only; no evidence band changes the candidate, Stage 2B.4 tier, or execution behavior.


## Stage 2C.8 empirical-support ranking simulation

Stage 2C.8 remains fully offline and does not change runtime scenario selection. It tests whether the frozen Stage 2C.7 empirical-support bands add value when used only as a tie-breaker inside the existing Stage 2B.4 quality tier.

Three policies are compared:

- `RAW_RANK`: lowest structurally-valid `scenario_rank` first (current executable-card ordering);
- `TIER_ONLY`: `PREFERRED` before `SECONDARY`, then scenario rank;
- `EVIDENCE_AWARE`: the same Stage 2B.4 tier ordering, then `STRONG > NEUTRAL > NEGATIVE > INSUFFICIENT`, then scenario rank.

The evidence-aware policy therefore never lets a `SECONDARY` scenario outrank a `PREFERRED` scenario. The empirical thresholds are frozen inputs from Stage 2C.7 (`NEGATIVE < 0`, `STRONG >= 0.9255 R/trigger` by default) and are not re-fit here. All holdout candidate evidence is again frozen before August 1 so no August outcome can affect another August selection.

```powershell
dotnet run -- --historical-shadow `
  --candidate-evidence-rank-sim `
  --corpus=.\historical_corpus_stage2c_source\ava_corpus_all_....jsonl `
  --candidate-evidence-index=.\historical_corpus_stage2c_source\ava_analogue_index.json `
  --train-from-et=2026-07-01 --train-to-et=2026-07-31 `
  --holdout-from-et=2026-08-01 --holdout-to-et=2026-08-07 `
  --negative-threshold=0 --strong-threshold=0.9255 `
  --minimum-evidence-records=5 --candidate-evidence-top=24 `
  --output-dir=.\stage2c8_evidence_ranking
```

Outputs report raw-rank vs tier-only selection effects separately from the incremental evidence-aware effect, including changed selections, paired resolved R, win rates, better/worse/equal changed decisions, direction/setup changes, and empirical-band composition. No LLM or network calls are made.
