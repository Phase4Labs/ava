# Stage 1 — OpenAI cost observability and output hardening

This version keeps GPT-5.2 as the only LLM. No local model or routing behavior is introduced.

## Implemented

- OpenAI Structured Outputs for execution cards and position re-evaluations.
- Token usage capture: input, cached input, output, reasoning, total.
- Estimated GPT-5.2 cost per successful call.
- Call metadata: ticker, call type, payload size, latency, attempts, response ID, service tier.
- JSONL usage log at `llm_usage.jsonl` by default.
- Removed `service_tier = "priority"` from routine position re-evaluation.
- Reduced execution-card retries from five to three; non-transient HTTP failures are not retried.
- Full prompt/payload logging is disabled by default.
- Optional bounded, gzip-compressed payload diagnostics.
- Timestamp-safe Massive ticker-news context in each dataset.
- News is filtered to `published_utc <= ts_asof_utc` to avoid look-ahead in replay.
- Massive news calls are cached because the reference-news feed is updated hourly.

## Optional environment variables

| Variable | Default | Purpose |
|---|---:|---|
| `LLM_USAGE_LOG_PATH` | `llm_usage.jsonl` | Usage JSONL output path |
| `LLM_DEBUG_PAYLOADS` | `false` | Set `true` only when full payload diagnostics are required |
| `LLM_DEBUG_DIRECTORY` | `llm_debug` | Directory for compressed diagnostic payloads |
| `LLM_DEBUG_MAX_FILES` | `20` | Maximum retained `.json.gz` payload files |
| `MASSIVE_NEWS_ENABLED` | `true` | Set `false` to omit the news block |
| `MASSIVE_NEWS_LOOKBACK_HOURS` | `72` | News lookback, limited to 1–168 hours |
| `MASSIVE_NEWS_LIMIT` | `6` | Maximum articles in the LLM payload, limited to 1–20 |
| `OPENAI_INPUT_USD_PER_1M` | GPT-5.2 default | Override input price for cost estimates |
| `OPENAI_CACHED_INPUT_USD_PER_1M` | GPT-5.2 default | Override cached-input price |
| `OPENAI_OUTPUT_USD_PER_1M` | GPT-5.2 default | Override output price |

## Usage report

After the program has produced calls, run in PowerShell:

```powershell
.\Summarize-LlmUsage.ps1
```

The report groups cost and token usage by day, call type, and ticker.

## Validation checklist

1. Build the project.
2. Run one execution-card call.
3. Confirm `llm_usage.jsonl` contains one `execution_card` row.
4. Open the generated execution card and confirm it parses normally.
5. Confirm the dataset contains `news_context` and no article is newer than `ts_asof_utc`.
6. With an open position, run a re-evaluation and confirm a `position_reeval` usage row.
7. Confirm the OpenAI response succeeds without requesting the priority service tier.
8. Run `Summarize-LlmUsage.ps1` and compare its daily estimate with the OpenAI dashboard.

## Important measurement note

The local estimate uses current GPT-5.2 standard token rates unless overridden. OpenAI billing remains the source of truth. The log is intended to identify which tickers, payloads, and call types consume the most tokens before Stage 2 compression.
