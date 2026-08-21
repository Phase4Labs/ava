using System.Text.Json;

namespace get_assessment_no_graph.Llm;

public interface ILlmClient
{
    string ProviderName { get; }

    Task<LlmStructuredResult> CompleteStructuredAsync(
        string model,
        string systemPrompt,
        string userPrompt,
        JsonElement jsonSchema,
        bool disableThinking,
        CancellationToken ct);
}

public sealed record LlmStructuredResult(
    string Provider,
    string Model,
    string Content,
    TimeSpan Elapsed,
    long? TotalDurationNs,
    long? LoadDurationNs,
    int? PromptEvalCount,
    int? EvalCount);
