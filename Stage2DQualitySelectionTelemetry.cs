using System.Text.Json;

namespace get_assessment_no_graph;

/// <summary>
/// Best-effort JSONL audit telemetry for Stage 2D ordering.
/// No production DB tables are changed. Disable with AVA_STAGE2D_TELEMETRY=false.
/// </summary>
public static class Stage2DQualitySelectionTelemetry
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static bool Enabled
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("AVA_STAGE2D_TELEMETRY");
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
        Stage2DQualitySelectionResult selection,
        CancellationToken ct = default)
    {
        if (!Enabled) return;
        try
        {
            var dir = Environment.GetEnvironmentVariable("AVA_STAGE2D_TELEMETRY_DIR");
            if (string.IsNullOrWhiteSpace(dir)) dir = "stage2d_quality_selection";
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"ava_stage2d_quality_selection_{DateTime.UtcNow:yyyyMMdd}.jsonl");
            var record = new
            {
                recorded_utc = DateTime.UtcNow,
                source,
                provider,
                model,
                ticker,
                asof_utc = asOfUtc,
                structural_gate_mode = Stage2B4GateConfig.Label,
                quality_selection_mode = Stage2DQualitySelectionConfig.Label,
                selection
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
            Console.WriteLine($"{DateTime.UtcNow:o} STAGE2D_TELEMETRY_FAILED {ticker} err={ex.Message}");
        }
    }
}
