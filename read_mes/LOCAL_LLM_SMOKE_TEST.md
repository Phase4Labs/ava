# AVA Local LLM C# Smoke Test

This change adds a local-provider integration test only. It does **not** alter the existing GPT-5.2 signal-generation or re-evaluation paths.

## Purpose

Prove this exact chain on the test laptop:

`AVA C# -> Ollama -> qwen3:8b -> JSON Schema -> C# deserialization`

## Defaults

- Ollama endpoint: `http://localhost:11434`
- Model: `qwen3:8b`
- Timeout: 180 seconds
- Qwen thinking: disabled for the smoke test

Optional environment variables:

```powershell
$env:LOCAL_LLM_BASE_URL = "http://localhost:11434"
$env:LOCAL_LLM_MODEL = "qwen3:8b"
$env:LOCAL_LLM_TIMEOUT_SECONDS = "180"
```

## Run

From the project directory:

```powershell
dotnet restore
dotnet build
dotnet run -- --local-llm-smoke
```

Expected final line:

```text
PASS: AVA C# -> Ollama -> qwen3:8b -> strict JSON -> C# object
```

The smoke-test command exits before the normal application initializes market-data, database, or OpenAI services.

## Troubleshooting

Check that Ollama is available:

```powershell
ollama list
ollama run qwen3:8b --think=false "Reply exactly: AVA LOCAL OK"
```

Then rerun:

```powershell
dotnet run -- --local-llm-smoke
```

If the C# call times out on the CPU-only test laptop, temporarily increase the timeout:

```powershell
$env:LOCAL_LLM_TIMEOUT_SECONDS = "300"
dotnet run -- --local-llm-smoke
```

Do not use `gpt-oss:20b` for this acceptance test. The objective is API and structured-output integration, not production-model performance.
