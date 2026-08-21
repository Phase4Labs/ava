# AVA Stage 2D — Quality-Aware Execution Selection

Stage 2D promotes one policy that survived temporal holdout validation in Stage 2C.8:

1. structurally executable scenarios only (Stage 2B.4 remains authoritative),
2. `PREFERRED` before `SECONDARY`,
3. original `scenario_rank` as the stable tie-break.

Stage 2D does **not** introduce a new quality rule. `PREFERRED`/`SECONDARY` are still
produced by the existing Stage 2B.4 quality profiler. Stage 2D only determines which
already-valid scenario gets first opportunity to execute when multiple scenarios are
present on the same evaluation.

## Explicit non-goals

Stage 2D does not:

- change the raw model card or DB audit rows,
- change scenario ranks,
- alter entry, stop, T1, T2, runner, probabilities, grade, or rationale,
- re-admit a structurally invalid scenario,
- use Stage 2C empirical-support bands to reorder scenarios,
- turn `NO_TRADE` into `TRADE`,
- change TriggerEngine probability/grade gates.

Stage 2C remains advisory/observability only. Stage 2C.8 showed that empirical-support
reordering improved training results but failed the temporal holdout, so it is explicitly
excluded from Stage 2D execution ordering.

## Runtime modes

`AVA_QUALITY_SELECTION_MODE`:

- `enforce` (default): Stage 2B.4 normalized executable scenarios are ordered
  `PREFERRED -> SECONDARY -> scenario_rank` before TriggerEngine.
- `shadow`: Stage 2D computes/logs the would-be order, while TriggerEngine receives the
  Stage 2B.4 structural order.

If the Stage 2B.4 structural gate is in `shadow`, Stage 2D never creates an executable
override. Stage 2D is layered on top of Stage 2B.4 enforcement, not around it.

## Fail-safe behavior

If Stage 2D ordering fails, AVA falls back to the already-normalized Stage 2B.4 executable
card. A Stage 2D bug therefore cannot re-admit structurally invalid raw scenarios.

## Audit behavior

Raw `execution_cards` and `execution_card_scenarios` remain unchanged. The selected
execution order is carried only in the in-memory executable card passed to TriggerEngine.
Original `scenario_rank` values are retained for signal persistence, deduplication, and audit.

Best-effort Stage 2D JSONL telemetry is written to `stage2d_quality_selection/` by default.
Disable with `AVA_STAGE2D_TELEMETRY=false`; override the directory with
`AVA_STAGE2D_TELEMETRY_DIR`.

## Self-test

```powershell
dotnet run -- --historical-shadow --stage2d-quality-selftest
```

The self-test is routed through the already-safe historical dispatcher and exits before any live-service initialization. It constructs rank 1 as a structurally valid `SECONDARY` scenario and rank 2
as a structurally valid `PREFERRED` scenario. Expected quality execution order is `2,1`,
while both the raw card and Stage 2B.4 normalized card retain their original `1,2` order.
