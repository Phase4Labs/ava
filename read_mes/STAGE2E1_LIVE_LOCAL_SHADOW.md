# AVA Stage 2E.1 — Live Local Shadow Capture

Stage 2E.1 runs the validated local candidate beside the live GPT-5.2 execution-card path without giving the local model any production authority.

## Production safety

- `AVA_LOCAL_SHADOW_ENABLED` defaults to `false`.
- GPT-5.2 remains the only source of the production execution card.
- Stage 2B.4 and Stage 2D remain authoritative for the production card.
- The local model never writes execution cards, scenarios, triggers, signals, positions, calibration rows, or analysis jobs.
- The local model never calls `TriggerEngine`.
- Stage 2C historical evidence is not injected into the prompt and does not affect the local decision.
- Local inference starts only after the GPT card has completed production DB persistence and TriggerEngine evaluation.
- Only one local inference can be active process-wide. If it is busy, the next live evaluation is logged as `busy_skipped` instead of being queued.
- All local exceptions and telemetry failures are contained inside the detached shadow task.

## Local path

For each accepted live GPT evaluation:

1. transform the already-built causal full dataset with `CompactMarketStateBuilder.Build`;
2. call Ollama using `gpt-oss:20b` by default;
3. parse the stricter local executable-card schema;
4. evaluate the local card with Stage 2B.4;
5. order its executable scenarios with Stage 2D;
6. write a JSONL comparison sidecar only.

No validator-guided repair is performed in Stage 2E.1. The goal is to measure the independent local model continuously before introducing another inference pass.

## Configuration

Safe default:

```text
AVA_LOCAL_SHADOW_ENABLED=false
```

Enable intentionally:

```powershell
$env:AVA_LOCAL_SHADOW_ENABLED="true"
$env:AVA_LOCAL_LLM_MODEL="gpt-oss:20b"
$env:AVA_LOCAL_LLM_BASE_URL="http://localhost:11434"
$env:AVA_LOCAL_LLM_TIMEOUT_SECONDS="600"
$env:AVA_LOCAL_LLM_CONTEXT_TOKENS="32768"
```

Optional telemetry settings:

```powershell
$env:AVA_LOCAL_SHADOW_TELEMETRY="true"
$env:AVA_LOCAL_SHADOW_TELEMETRY_DIR="stage2e_local_shadow"
```

Then start AVA normally. Do not use a historical-shadow command.

## Console events

Expected events include:

```text
STAGE2E1_LOCAL_SHADOW_QUEUED
STAGE2E1_LOCAL_SHADOW_COMPLETE
STAGE2E1_LOCAL_SHADOW_BUSY_SKIP
STAGE2E1_LOCAL_SHADOW_CONTEXT_SKIP
STAGE2E1_LOCAL_SHADOW_ERROR
```

A busy skip is expected on slower hardware when a new GPT card is produced before the previous local inference completes. It is preferable to accumulating stale local work.

## Telemetry

The default directory is `stage2e_local_shadow/`. One JSONL file is written per UTC date.

Completed rows include:

- GPT raw verdict and scenario count;
- GPT Stage 2B.4 structural verdict and valid-scenario count;
- GPT preferred/secondary counts;
- GPT Stage 2D selected rank, direction, setup, and tier;
- local raw verdict and scenario count;
- local Stage 2B.4 structural verdict and valid-scenario count;
- local preferred/secondary counts;
- local Stage 2D selected rank, direction, setup, and tier;
- raw/structural/direction/setup/tier agreement;
- compact-payload size reduction;
- local prompt/output token counts from Ollama;
- local inference latency;
- parse or decision errors;
- the local raw execution-card JSON.

The full market dataset is not written into Stage 2E.1 telemetry.
