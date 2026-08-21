using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace get_assessment_no_graph.Llm;

public sealed class OllamaLlmClient : ILlmClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly int? _contextTokens;

    public string ProviderName => "ollama";

    public OllamaLlmClient(string baseUrl, TimeSpan timeout, HttpClient? httpClient = null, int? contextTokens = null)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("Ollama base URL is required.", nameof(baseUrl));

        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient();
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        _http.Timeout = timeout;
        _contextTokens = contextTokens is > 0 ? contextTokens : null;
    }

    public async Task<LlmStructuredResult> CompleteStructuredAsync(
        string model,
        string systemPrompt,
        string userPrompt,
        JsonElement jsonSchema,
        bool disableThinking,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(model));

        var request = new OllamaChatRequest
        {
            Model = model,
            Stream = false,
            Think = ResolveThinkSetting(model, disableThinking),
            Format = jsonSchema,
            KeepAlive = "5m",
            Messages =
            [
                new OllamaMessage { Role = "system", Content = systemPrompt },
                new OllamaMessage { Role = "user", Content = userPrompt }
            ],
            Options = BuildOptions()
        };

        var sw = Stopwatch.StartNew();
        using var response = await _http.PostAsJsonAsync("api/chat", request, JsonOptions, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        sw.Stop();

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Ollama returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). Body: {Truncate(raw, 1000)}");
        }

        OllamaChatResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<OllamaChatResponse>(raw, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Ollama returned invalid response JSON: {Truncate(raw, 1000)}", ex);
        }

        var content = parsed?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException($"Ollama returned no assistant content. Body: {Truncate(raw, 1000)}");

        return new LlmStructuredResult(
            ProviderName,
            parsed?.Model ?? model,
            content,
            sw.Elapsed,
            parsed?.TotalDuration,
            parsed?.LoadDuration,
            parsed?.PromptEvalCount,
            parsed?.EvalCount);
    }

    private static object? ResolveThinkSetting(string model, bool disableThinking)
    {
        // Ollama GPT-OSS does not support think=false. Its reasoning trace cannot
        // be fully disabled; low/medium/high are the supported controls. AVA's
        // disableThinking intent therefore maps to the lowest supported effort.
        if (model.StartsWith("gpt-oss", StringComparison.OrdinalIgnoreCase))
            return disableThinking ? "low" : null;

        return disableThinking ? false : null;
    }

    private Dictionary<string, object> BuildOptions()
    {
        var options = new Dictionary<string, object>
        {
            ["temperature"] = 0
        };
        if (_contextTokens.HasValue)
            options["num_ctx"] = _contextTokens.Value;
        return options;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "...";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private sealed class OllamaChatRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; init; } = "";

        [JsonPropertyName("messages")]
        public List<OllamaMessage> Messages { get; init; } = [];

        [JsonPropertyName("stream")]
        public bool Stream { get; init; }

        [JsonPropertyName("think")]
        public object? Think { get; init; }

        [JsonPropertyName("format")]
        public JsonElement Format { get; init; }

        [JsonPropertyName("keep_alive")]
        public string? KeepAlive { get; init; }

        [JsonPropertyName("options")]
        public Dictionary<string, object>? Options { get; init; }
    }

    private sealed class OllamaMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; init; } = "";

        [JsonPropertyName("content")]
        public string Content { get; init; } = "";
    }

    private sealed class OllamaChatResponse
    {
        [JsonPropertyName("model")]
        public string? Model { get; init; }

        [JsonPropertyName("message")]
        public OllamaResponseMessage? Message { get; init; }

        [JsonPropertyName("total_duration")]
        public long? TotalDuration { get; init; }

        [JsonPropertyName("load_duration")]
        public long? LoadDuration { get; init; }

        [JsonPropertyName("prompt_eval_count")]
        public int? PromptEvalCount { get; init; }

        [JsonPropertyName("eval_count")]
        public int? EvalCount { get; init; }
    }

    private sealed class OllamaResponseMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; init; }
    }
}
