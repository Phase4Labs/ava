# AVA Stage 2B.4 — structural validity + quality profile

## Purpose

Stage 2B.4 separates two concerns that the earlier semantic gate mixed together:

1. **Structural validity** — whether Entry/Stop/T1 geometry is executable.
2. **Quality/calibration** — deterministic features that may raise or lower historical expectancy but do not make a trade intrinsically impossible.

The legacy `ScenarioSemanticValidator` remains in the project only so Stage 2B.1–2B.3 corpus results stay comparable. It is not the forward production gate.

## Structural hard invalidations

`ScenarioStructuralValidator` rejects a scenario only for defects that prevent reliable execution:

- invalid direction
- missing entry bounds
- reversed entry bounds
- missing stop
- missing T1
- stop on the wrong side of the entry window
- T1 on the wrong side of the entry window
- non-positive conservative risk/reward geometry

It does **not** hard reject on R:R preference, RVOL, grade, catalyst, anchor distance, or overextension policy.

## Safe target normalization

T2 and runner are optional later targets. Stage 2B.4 never invents replacement prices.

If T2 is not beyond T1 in the trade direction, normalized execution omits T2.
If runner is not beyond the furthest retained target, normalized execution omits runner.

The raw LLM card is never mutated.

## Quality profile

`ScenarioQualityProfiler` annotates structurally valid scenarios.

Current selection penalties:

- `rr_below_preferred`: conservative T1 R:R below 1.50. Stage 2B.3/full recent corpus showed this is the strongest selectivity feature, but the rejected cohort remains positive expectancy, so it is not structural invalidation.
- `rvol_below_actionable_reference`: RVOL below the prior 1.50x A/B reference. Current evidence supports a modest selection effect, not hard invalidation.

Other prior rules are retained as observations rather than blockers until more evidence supports a hard/quality role:

- entry far from anchor
- grade below trigger reference
- grade/probability mismatch
- catalyst availability
- overextension conditions
- rationale/news assertions
- near-duplicate scenario

The quality tier is deliberately simple:

- `PREFERRED`: structurally valid and no current selection penalty.
- `SECONDARY`: structurally valid with one or more current selection penalties.
- `STRUCTURALLY_INVALID`: cannot be executed reliably at Entry/Stop/T1.

No historical expectancy number is hard-coded. Stage 2C will attach empirical analogue counts, hit rates, and expectancy.

## Live safety / shadow mode

`ProduceCardWorker.RunOnceAsync` evaluates each GPT card through `AvaScenarioDecisionLayer` after calibration capture but **does not mutate**:

- `execution_cards`
- `execution_card_scenarios`
- TriggerEngine input
- signal decisions

It writes best-effort local JSONL telemetry under `stage2b4_shadow/`.

Disable shadow telemetry with:

```powershell
$env:AVA_STAGE2B4_SHADOW="false"
```

Change the local output folder with:

```powershell
$env:AVA_STAGE2B4_SHADOW_DIR="C:\path\to\shadow"
```

## Historical GPT/local comparison

Historical replay now applies the same Stage 2B.4 decision layer to:

- GPT full payload
- GPT compact payload
- local model compact payload

The replay result includes both the legacy semantic gate and the new Stage 2B.4 structural/quality result so behavior can be compared without changing the earlier research baseline.

## Corpus output

Corpus JSONL records now include `teacher.stage2b4`.
The corpus summary includes a top-level `stage2b4` block with:

- structural valid/invalid scenario counts
- safe later-target repair counts
- preferred/secondary counts
- hard issue counts
- repair counts
- selection penalty counts
- observation counts

## Recommended validation run

Use the full recent-framework cohort again:

```powershell
dotnet run -- --corpus-build `
  --model=gpt-5.2 `
  --from-et=2026-07-01 `
  --to-et=2026-08-07 `
  --limit=0 `
  --output-dir=historical_corpus_stage2b4
```

Then inspect:

```powershell
$summaryFile = Get-ChildItem .\historical_corpus_stage2b4\*_summary.json |
  Sort-Object LastWriteTime -Descending |
  Select-Object -First 1
$s = Get-Content $summaryFile.FullName -Raw | ConvertFrom-Json
$s.stage2b4 | ConvertTo-Json -Depth 20
```

## Promotion boundary

Stage 2B.4 does **not** yet replace the existing TriggerEngine grade/level-order gates. That is intentional. The next promotion step should occur only after the shadow/corpus output confirms the structural-normalization behavior on the recent-framework population.
