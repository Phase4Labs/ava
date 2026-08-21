using System.Text.Json;
using System.Text.Json.Serialization;
using get_assessment_no_graph.Llm;

namespace get_assessment_no_graph;

public static class LocalLlmSmokeTest
{
    public static async Task<int> RunAsync(CancellationToken ct = default)
    {
        var baseUrl = Environment.GetEnvironmentVariable("LOCAL_LLM_BASE_URL")
                      ?? "http://localhost:11434";
        var model = Environment.GetEnvironmentVariable("LOCAL_LLM_MODEL")
                    ?? "qwen3:8b";
        var timeoutSeconds = ParsePositiveInt(
            Environment.GetEnvironmentVariable("LOCAL_LLM_TIMEOUT_SECONDS"),
            fallback: 180);

        Console.WriteLine("AVA local LLM smoke test");
        Console.WriteLine($"Provider : Ollama");
        Console.WriteLine($"Endpoint : {baseUrl}");
        Console.WriteLine($"Model    : {model}");
        Console.WriteLine($"Timeout  : {timeoutSeconds}s");
        Console.WriteLine();

        using var client = new OllamaLlmClient(baseUrl, TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            using var schemaDoc = JsonDocument.Parse(SmokeSchemaJson);
            var result = await client.CompleteStructuredAsync(
                model,
                systemPrompt: "You are an AVA integration-test endpoint. Follow the supplied JSON schema exactly. Do not add prose or markdown.",
                userPrompt: "Return an integration-test result. Set direction to NO_TRADE, confidence to 0, and reason to integration test.",
                jsonSchema: schemaDoc.RootElement.Clone(),
                disableThinking: true,
                ct);

            Console.WriteLine("Raw structured response:");
            Console.WriteLine(result.Content);
            Console.WriteLine();

            SmokeResponse? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<SmokeResponse>(result.Content, JsonOptions);
            }
            catch (JsonException ex)
            {
                Console.Error.WriteLine($"FAIL: model content was not deserializable as the expected C# type: {ex.Message}");
                return 2;
            }

            if (parsed is null)
            {
                Console.Error.WriteLine("FAIL: deserialization returned null.");
                return 2;
            }

            if (!string.Equals(parsed.Direction, "NO_TRADE", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"FAIL: expected direction=NO_TRADE, got '{parsed.Direction}'.");
                return 3;
            }

            if (parsed.Confidence is < 0 or > 1)
            {
                Console.Error.WriteLine($"FAIL: confidence must be between 0 and 1, got {parsed.Confidence}.");
                return 3;
            }

            if (string.IsNullOrWhiteSpace(parsed.Reason))
            {
                Console.Error.WriteLine("FAIL: reason was empty.");
                return 3;
            }

            Console.WriteLine("PASS: AVA C# -> Ollama -> qwen3:8b -> strict JSON -> C# object");
            Console.WriteLine($"Parsed   : direction={parsed.Direction}, confidence={parsed.Confidence:0.###}, reason={parsed.Reason}");
            Console.WriteLine($"Elapsed  : {result.Elapsed.TotalSeconds:0.00}s");
            if (result.PromptEvalCount.HasValue || result.EvalCount.HasValue)
                Console.WriteLine($"Tokens   : prompt={result.PromptEvalCount?.ToString() ?? "?"}, output={result.EvalCount?.ToString() ?? "?"}");

            return 0;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            Console.Error.WriteLine($"FAIL: local LLM call exceeded {timeoutSeconds} seconds.");
            return 4;
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"FAIL: could not call Ollama: {ex.Message}");
            return 5;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: {ex.GetType().Name}: {ex.Message}");
            return 6;
        }
    }

    private static int ParsePositiveInt(string? raw, int fallback)
        => int.TryParse(raw, out var parsed) && parsed > 0 ? parsed : fallback;

    private sealed class SmokeResponse
    {
        [JsonPropertyName("direction")]
        public string Direction { get; init; } = "";

        [JsonPropertyName("confidence")]
        public double Confidence { get; init; }

        [JsonPropertyName("reason")]
        public string Reason { get; init; } = "";
    }

    private const string SmokeSchemaJson = """
    {
      "type": "object",
      "properties": {
        "direction": {
          "type": "string",
          "enum": ["NO_TRADE"]
        },
        "confidence": {
          "type": "number",
          "minimum": 0,
          "maximum": 1
        },
        "reason": {
          "type": "string"
        }
      },
      "required": ["direction", "confidence", "reason"],
      "additionalProperties": false
    }
    """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
}
