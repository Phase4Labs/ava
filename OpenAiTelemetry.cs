using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace get_assessment_no_graph;

public sealed record OpenAiUsage(
    int InputTokens,
    int CachedInputTokens,
    int OutputTokens,
    int ReasoningTokens,
    int TotalTokens);

public sealed record OpenAiCallResult(
    string? ResponseId,
    string OutputText,
    OpenAiUsage Usage,
    string? ServiceTier,
    long LatencyMs,
    int AttemptCount,
    int RequestBytes);

public sealed record ExecutionCardAnalysisResult(
    string Provider,
    string Model,
    string? ResponseId,
    string RawJson,
    ExecutionCardJsonV1? Card,
    bool ParseSuccess,
    string? ParseError,
    long ElapsedMs,
    int? PromptTokens,
    int? OutputTokens,
    int? CachedInputTokens = null,
    int? ReasoningTokens = null);

public sealed class LlmUsageLogRow
{
    [JsonPropertyName("timestamp_utc")]       public DateTime TimestampUtc { get; init; }
    [JsonPropertyName("asof_utc")]            public DateTime AsOfUtc { get; init; }
    [JsonPropertyName("provider")]            public string Provider { get; init; } = "openai";
    [JsonPropertyName("call_type")]           public string CallType { get; init; } = "";
    [JsonPropertyName("ticker")]              public string Ticker { get; init; } = "";
    [JsonPropertyName("model")]               public string Model { get; init; } = "";
    [JsonPropertyName("response_id")]         public string? ResponseId { get; init; }
    [JsonPropertyName("service_tier")]        public string? ServiceTier { get; init; }
    [JsonPropertyName("reasoning_effort")]    public string? ReasoningEffort { get; init; }
    [JsonPropertyName("dataset_chars")]       public int DatasetChars { get; init; }
    [JsonPropertyName("request_bytes")]       public int RequestBytes { get; init; }
    [JsonPropertyName("response_chars")]      public int ResponseChars { get; init; }
    [JsonPropertyName("input_tokens")]        public int InputTokens { get; init; }
    [JsonPropertyName("cached_input_tokens")] public int CachedInputTokens { get; init; }
    [JsonPropertyName("output_tokens")]       public int OutputTokens { get; init; }
    [JsonPropertyName("reasoning_tokens")]    public int ReasoningTokens { get; init; }
    [JsonPropertyName("total_tokens")]        public int TotalTokens { get; init; }
    [JsonPropertyName("attempt_count")]       public int AttemptCount { get; init; }
    [JsonPropertyName("latency_ms")]          public long LatencyMs { get; init; }
    [JsonPropertyName("estimated_cost_usd")]  public decimal? EstimatedCostUsd { get; init; }
}

public static class OpenAiTelemetry
{
    private static readonly object FileGate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static OpenAiUsage ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return new OpenAiUsage(0, 0, 0, 0, 0);

        var input = ReadInt(usage, "input_tokens");
        var output = ReadInt(usage, "output_tokens");
        var total = ReadInt(usage, "total_tokens");

        var cached = 0;
        if (usage.TryGetProperty("input_tokens_details", out var inputDetails) &&
            inputDetails.ValueKind == JsonValueKind.Object)
        {
            cached = ReadInt(inputDetails, "cached_tokens");
        }

        var reasoning = 0;
        if (usage.TryGetProperty("output_tokens_details", out var outputDetails) &&
            outputDetails.ValueKind == JsonValueKind.Object)
        {
            reasoning = ReadInt(outputDetails, "reasoning_tokens");
        }

        return new OpenAiUsage(input, cached, output, reasoning, total);
    }

    public static string? ReadServiceTier(JsonElement root)
        => root.TryGetProperty("service_tier", out var tier) && tier.ValueKind == JsonValueKind.String
            ? tier.GetString()
            : null;

    public static void Record(LlmUsageLogRow row)
    {
        try
        {
            var enriched = WithEstimatedCost(row, CalculateEstimatedCost(row.Model, row.InputTokens,
                row.CachedInputTokens, row.OutputTokens));

            var path = Environment.GetEnvironmentVariable("LLM_USAGE_LOG_PATH");
            if (string.IsNullOrWhiteSpace(path)) path = "llm_usage.jsonl";

            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            var line = JsonSerializer.Serialize(enriched, JsonOptions) + Environment.NewLine;
            lock (FileGate)
            {
                File.AppendAllText(fullPath, line, Encoding.UTF8);
            }

            var costText = enriched.EstimatedCostUsd.HasValue
                ? $" est=${enriched.EstimatedCostUsd.Value:F5}"
                : "";
            AppLog.Llm($"USAGE {enriched.CallType} {enriched.Ticker} " +
                       $"in={enriched.InputTokens} cached={enriched.CachedInputTokens} " +
                       $"out={enriched.OutputTokens} reasoning={enriched.ReasoningTokens}" +
                       $"{costText} latency={enriched.LatencyMs}ms");
        }
        catch (Exception ex)
        {
            // Telemetry must never break live signal production.
            AppLog.Error($"LLM usage logging failed: {ex.Message}");
        }
    }

    public static void WriteDebugPayloadIfEnabled(
        string callType,
        string ticker,
        DateTime asOfUtc,
        string payload)
    {
        var enabled = Environment.GetEnvironmentVariable("LLM_DEBUG_PAYLOADS");
        if (!string.Equals(enabled, "1", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            var directory = Environment.GetEnvironmentVariable("LLM_DEBUG_DIRECTORY");
            if (string.IsNullOrWhiteSpace(directory)) directory = "llm_debug";
            Directory.CreateDirectory(directory);

            var safeTicker = string.Concat(ticker.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_'));
            var name = $"{asOfUtc:yyyyMMdd_HHmmss}_{safeTicker}_{callType}.json.gz";
            var path = Path.Combine(directory, name);

            using (var file = File.Create(path))
            using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
            using (var writer = new StreamWriter(gzip, Encoding.UTF8))
                writer.Write(payload);

            var maxFiles = ReadPositiveIntEnv("LLM_DEBUG_MAX_FILES", 20, 1, 500);
            var files = new DirectoryInfo(directory)
                .GetFiles("*.json.gz")
                .OrderByDescending(f => f.CreationTimeUtc)
                .Skip(maxFiles)
                .ToArray();
            foreach (var old in files)
                old.Delete();
        }
        catch (Exception ex)
        {
            AppLog.Error($"LLM debug payload write failed: {ex.Message}");
        }
    }

    private static LlmUsageLogRow WithEstimatedCost(LlmUsageLogRow row, decimal? cost)
        => new()
        {
            TimestampUtc = row.TimestampUtc,
            AsOfUtc = row.AsOfUtc,
            Provider = row.Provider,
            CallType = row.CallType,
            Ticker = row.Ticker,
            Model = row.Model,
            ResponseId = row.ResponseId,
            ServiceTier = row.ServiceTier,
            ReasoningEffort = row.ReasoningEffort,
            DatasetChars = row.DatasetChars,
            RequestBytes = row.RequestBytes,
            ResponseChars = row.ResponseChars,
            InputTokens = row.InputTokens,
            CachedInputTokens = row.CachedInputTokens,
            OutputTokens = row.OutputTokens,
            ReasoningTokens = row.ReasoningTokens,
            TotalTokens = row.TotalTokens,
            AttemptCount = row.AttemptCount,
            LatencyMs = row.LatencyMs,
            EstimatedCostUsd = cost
        };

    private static decimal? CalculateEstimatedCost(
        string model,
        int inputTokens,
        int cachedInputTokens,
        int outputTokens)
    {
        var inputRate = ReadDecimalEnv("OPENAI_INPUT_USD_PER_1M");
        var cachedRate = ReadDecimalEnv("OPENAI_CACHED_INPUT_USD_PER_1M");
        var outputRate = ReadDecimalEnv("OPENAI_OUTPUT_USD_PER_1M");

        if (!inputRate.HasValue || !cachedRate.HasValue || !outputRate.HasValue)
        {
            if (model.StartsWith("gpt-5.2", StringComparison.OrdinalIgnoreCase))
            {
                inputRate ??= 1.75m;
                cachedRate ??= 0.175m;
                outputRate ??= 14.00m;
            }
            else
            {
                return null;
            }
        }

        var cached = Math.Clamp(cachedInputTokens, 0, inputTokens);
        var uncached = Math.Max(0, inputTokens - cached);

        return Math.Round(
            (uncached / 1_000_000m * inputRate.Value) +
            (cached / 1_000_000m * cachedRate.Value) +
            (outputTokens / 1_000_000m * outputRate.Value),
            8);
    }

    private static int ReadInt(JsonElement obj, string property)
        => obj.TryGetProperty(property, out var value) && value.TryGetInt32(out var parsed)
            ? parsed
            : 0;

    private static decimal? ReadDecimalEnv(string key)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static int ReadPositiveIntEnv(string key, int fallback, int min, int max)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        return int.TryParse(raw, out var value) ? Math.Clamp(value, min, max) : fallback;
    }
}
