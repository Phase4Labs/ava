# AVA Stage 2B.3 — Stratified Corpus, Rule Ablation, and Diagnostics

Stage 2B.3 remains research-only and read-only against Supabase. It does not change live AVA signal generation.

## New capabilities

### 1. Deterministic proportional stratified sampling

Use:

```powershell
dotnet run -- --corpus-build `
  --model=gpt-5.2 `
  --from-et=2026-07-01 `
  --to-et=2026-08-07 `
  --sample=stratified `
  --seed=42 `
  --limit=1000 `
  --output-dir=historical_corpus_stage2b3
```

The population is stratified by:

- ISO-like calendar week
- ET session bucket (premarket/open30/morning/midday/afternoon/close30)
- ticker
- raw GPT verdict
- top scenario direction
- top scenario entry type
- top scenario grade when available

Allocation is proportional to historical stratum size. A stable hash plus `--seed` makes selection repeatable.

`--sample=chronological` remains the default for backwards compatibility.

### 2. True one-rule ablation

The summary now contains:

```text
scenarios.validation_efficacy.rule_ablation
```

For each error-level semantic rule, AVA measures the scenarios that would become admissible if **only that rule** were removed. Scenarios failing multiple rules remain rejected in a one-rule ablation.

Each rule reports:

- newly admitted scenario count
- metrics for newly admitted scenarios
- metrics for the combined accepted population after removing that rule
- delta mean realized R vs the current validator
- delta profit factor
- delta expectancy R per triggered scenario

### 3. Better level-order / R:R diagnostics

The diagnostics CSV now includes `diagnostic_detail`.

`level_order` is broken into shapes such as:

- `stop_vs_entry`
- `entry_bounds_reversed`
- `entry_vs_t1`
- `t1_vs_t2`
- `t2_vs_runner`
- `entry_vs_runner`
- `stop_vs_t1`

`rr_unavailable` is broken into:

- `missing_stop`
- `missing_t1`
- `missing_entry`
- `nonpositive_risk`
- `nonpositive_reward`
- `nonpositive_risk_and_reward`
- `unknown_rr_unavailable`

Aggregate counts are also written to:

```text
scenarios.validation_efficacy.diagnostic_issue_shapes
```

### 4. R-metric coverage

Every efficacy metric now reports:

```text
resolved_r_coverage_if_triggered
```

This prevents categories such as `rr_unavailable` from looking statistically meaningful when only a small fraction of triggered scenarios can actually be expressed in R units.

## Recommended next run

Use the July 1–August 7 recent-framework window first:

```powershell
dotnet run -- --corpus-build `
  --model=gpt-5.2 `
  --from-et=2026-07-01 `
  --to-et=2026-08-07 `
  --sample=stratified `
  --seed=42 `
  --limit=1000 `
  --diagnostic-limit=20 `
  --output-dir=historical_corpus_stage2b3
```

Then inspect:

```powershell
$summaryFile = Get-ChildItem .\historical_corpus_stage2b3\*_summary.json |
  Sort-Object LastWriteTime -Descending |
  Select-Object -First 1

$s = Get-Content $summaryFile.FullName -Raw | ConvertFrom-Json

$s.sampling | ConvertTo-Json -Depth 10
$s.scenarios.validation_efficacy.accepted | ConvertTo-Json -Depth 10
$s.scenarios.validation_efficacy.rejected | ConvertTo-Json -Depth 10
$s.scenarios.validation_efficacy.rule_ablation | ConvertTo-Json -Depth 20
$s.scenarios.validation_efficacy.diagnostic_issue_shapes | ConvertTo-Json -Depth 20
```

Do not promote semantic rules to production based solely on this research path. Stage 2B.3 is designed to identify which rules improve or reduce realized expectancy before a separate production promotion step.
