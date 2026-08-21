using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
public static class Helpers {
    public static (JsonElement normalized, string sha256, string? ticker, DateTimeOffset? asofTs) NormalizeAndHashDataset(string datasetJson)
    {
        using var doc = JsonDocument.Parse(datasetJson);

        // Normalize: re-serialize with deterministic options.
        // (System.Text.Json keeps property order as inserted; if your datasetJson generation is stable, this is enough.)
        // If you want canonical ordering, you’d need a canonicalizer; start simple.

        var root = doc.RootElement;

        string? ticker = root.TryGetProperty("ticker", out var tEl) && tEl.ValueKind == JsonValueKind.String
            ? tEl.GetString()
            : null;

        DateTimeOffset? asofTs = null;
        if (root.TryGetProperty("ts_asof_utc", out var aEl) && aEl.ValueKind == JsonValueKind.String)
        {
            if (DateTimeOffset.TryParse(aEl.GetString(), out var dto))
                asofTs = dto;
        }

        // Deterministic JSON string
        var normalizedJson = JsonSerializer.Serialize(root, new JsonSerializerOptions
        {
            PropertyNamingPolicy = null, // preserve original
            WriteIndented = false
        });

        var sha = ComputeSha256Hex(normalizedJson);

        // Re-parse normalizedJson so we can store a clean JsonElement
        using var normDoc = JsonDocument.Parse(normalizedJson);
        var normalizedEl = normDoc.RootElement.Clone();

        return (normalizedEl, sha, ticker, asofTs);
    }

    public static string ComputeSha256Hex(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool TryParseModelOutputJson(string outputText, out JsonElement outputJson, out string? error)
    {
        outputJson = default;
        error = null;

        outputText = outputText.Trim();

        // If your governance ever allowed raw "NO TRADE", you’d special-case here.
        // After your governance fix, expect JSON always.
        try
        {
            using var doc = JsonDocument.Parse(outputText);
            outputJson = doc.RootElement.Clone();

            // Minimum schema checks (keep simple; expand later)
            if (!outputJson.TryGetProperty("verdict", out var vEl) || vEl.ValueKind != JsonValueKind.String)
            {
                error = "missing verdict";
                return false;
            }

            if (!outputJson.TryGetProperty("scenarios", out var sEl) || sEl.ValueKind != JsonValueKind.Array)
            {
                error = "missing scenarios array";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
    public static string ComputeFrameworkVersion(string _model, string _frameworkSystemPrompt, string _executionQuestion, string StrictJsonInstruction)
    {
        var s = $"{_model}\n---\n{_frameworkSystemPrompt}\n---\n{_executionQuestion}\n---\n{StrictJsonInstruction}";
        return ComputeSha256Hex(s);
    }
}