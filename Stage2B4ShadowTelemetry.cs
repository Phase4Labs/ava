using System.Text.Json;

namespace get_assessment_no_graph;

/// <summary>
/// Best-effort local JSONL telemetry for the Stage 2B.4 decision layer.
/// It never writes production DB tables. The source field records shadow/enforce mode.
/// Disable telemetry with AVA_STAGE2B4_SHADOW=false.
/// </summary>
public static class Stage2B4ShadowTelemetry
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static bool Enabled
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("AVA_STAGE2B4_SHADOW");
            return string.IsNullOrWhiteSpace(raw) ||
                   !(string.Equals(raw, "0", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(raw, "off", StringComparison.OrdinalIgnoreCase));
        }
    }

    public static async Task TryWriteAsync(
        string source,
        string provider,
        string model,
        string ticker,
        DateTime asOfUtc,
        AvaScenarioDecisionResult decision,
        CancellationToken ct = default)
    {
        if (!Enabled) return;
        try
        {
            var dir = Environment.GetEnvironmentVariable("AVA_STAGE2B4_SHADOW_DIR");
            if (string.IsNullOrWhiteSpace(dir)) dir = "stage2b4_shadow";
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"ava_stage2b4_shadow_{DateTime.UtcNow:yyyyMMdd}.jsonl");
            var record = new
            {
                recorded_utc = DateTime.UtcNow,
                source,
                provider,
                model,
                ticker,
                asof_utc = asOfUtc,
                decision
            };

            var line = JsonSerializer.Serialize(record, JsonOptions);
            await Gate.WaitAsync(ct);
            try
            {
                await File.AppendAllTextAsync(path, line + Environment.NewLine, ct);
            }
            finally
            {
                Gate.Release();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{DateTime.UtcNow:o} STAGE2B4_SHADOW_LOG_FAILED {ticker} err={ex.Message}");
        }
    }
}
