using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using System.Text;
using System.Text.Json;
using System.Globalization;

namespace get_assessment_no_graph;
public sealed class SupabaseRestClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public SupabaseRestClient(string supabaseUrl, string serviceRoleKey, HttpClient? http = null)
    {
        _baseUrl = supabaseUrl.TrimEnd('/');
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        _http.DefaultRequestHeaders.Add("apikey", serviceRoleKey);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceRoleKey);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task UpsertAsync<T>(string table, IEnumerable<T> rows, string onConflict, CancellationToken ct = default)
    {
        var list = rows.ToList();
        if (list.Count == 0) return;

        var url = $"{_baseUrl}/rest/v1/{table}?on_conflict={Uri.EscapeDataString(onConflict)}";

        //var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var json = JsonSerializer.Serialize(list, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("Prefer", "resolution=merge-duplicates,return=minimal");
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Supabase upsert failed ({table}) HTTP {(int)resp.StatusCode}: {body}");
    }

    public async Task<List<T>> SelectAsync<T>(string table, string queryString, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/rest/v1/{table}{queryString}";
        using var resp = await _http.GetAsync(url, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Supabase select failed ({table}) HTTP {(int)resp.StatusCode}: {body}");

        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<List<T>>(body, opts) ?? new List<T>();
    }

    /// <summary>Returns an exact PostgREST row count for an optional filter query.</summary>
    public async Task<long> CountAsync(string table, string queryString = "", CancellationToken ct = default)
    {
        queryString ??= "";
        if (queryString.Length > 0 && queryString[0] != '?') queryString = "?" + queryString;
        var separator = queryString.Length == 0 ? "?" : "&";
        if (!queryString.Contains("limit=", StringComparison.OrdinalIgnoreCase))
            queryString += separator + "limit=1";

        var url = $"{_baseUrl}/rest/v1/{table}{queryString}";
        using var req = new HttpRequestMessage(HttpMethod.Head, url);
        req.Headers.TryAddWithoutValidation("Prefer", "count=exact");
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"CountAsync failed: {(int)resp.StatusCode} {resp.ReasonPhrase} table={table}", null, resp.StatusCode);

        // Content-Range is a content header in System.Net.Http. PostgREST returns
        // values such as "0-0/1234" when Prefer: count=exact is honored.
        // Some handlers/proxies may surface it under the general response headers,
        // so retain that as a compatibility fallback.
        string? cr = resp.Content.Headers.ContentRange?.ToString();
        if (string.IsNullOrWhiteSpace(cr) &&
            resp.Headers.TryGetValues("Content-Range", out var values))
        {
            cr = values.FirstOrDefault()?.Trim();
        }

        if (string.IsNullOrWhiteSpace(cr))
            throw new InvalidOperationException($"CountAsync response did not contain Content-Range for table={table}.");

        var slash = cr.LastIndexOf('/');
        if (slash < 0 || slash == cr.Length - 1)
            throw new InvalidOperationException($"CountAsync received malformed Content-Range '{cr}' for table={table}.");

        var totalPart = cr[(slash + 1)..].Trim();
        if (totalPart == "*")
            throw new InvalidOperationException($"CountAsync received an unknown total in Content-Range '{cr}' for table={table}.");

        return long.TryParse(totalPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var total)
            ? total
            : throw new InvalidOperationException($"CountAsync could not parse Content-Range '{cr}' for table={table}.");
    }

    public async Task DeleteAsync(string table, string queryString, CancellationToken ct = default)
    {
         var url = $"{_baseUrl}/rest/v1/{table}{queryString}";

        using var req = new HttpRequestMessage(HttpMethod.Delete, url);
        req.Headers.Add("Prefer", "return=minimal");

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Supabase delete failed ({table}) HTTP {(int)resp.StatusCode}: {body}");
    }
    public async Task PatchAsync(string table, string queryString, object patch, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/rest/v1/{table}{queryString}";
        // Use default (no renaming) so snake_case anonymous-type field names reach PostgREST unchanged.
        // e.g. new { t1_hit = true } must arrive as {"t1_hit":true}, not {"t1Hit":true}.
        var json = JsonSerializer.Serialize(patch, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

        using var req = new HttpRequestMessage(new HttpMethod("PATCH"), url);
        req.Headers.Add("Prefer", "return=minimal");
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Supabase patch failed ({table}) HTTP {(int)resp.StatusCode}: {body}");
    }

    /// <summary>
    /// Re-evaluation levels use explicit null to remove an optional target.
    /// Keep this separate from PatchAsync so no existing regular-flow caller
    /// changes its null-writing behavior.
    /// </summary>
    public async Task PatchIncludingNullsAsync(
        string table,
        string queryString,
        object patch,
        CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/rest/v1/{table}{queryString}";
        var json = JsonSerializer.Serialize(patch, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        });

        using var req = new HttpRequestMessage(new HttpMethod("PATCH"), url);
        req.Headers.Add("Prefer", "return=minimal");
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Supabase patch failed ({table}) HTTP {(int)resp.StatusCode}: {body}");
    }

    /*public async Task<bool> ExistsAsync(string table, string queryString, CancellationToken ct = default)
    {        
        try{
            Console.WriteLine($"Checking existence in table '{table}' with query '{queryString}'");
            var url = $"{_baseUrl}/rest/v1/{table}{queryString}";
            using var req = new HttpRequestMessage(HttpMethod.Head, url);

            Console.WriteLine($"Sending HEAD request to URL: {url}");
            using var resp = await _http.SendAsync(req, ct);

            Console.WriteLine($"Received response with status code: {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }*/

    /// <summary>
    /// Calls a Supabase/PostgREST RPC and deserializes its JSON response.
    /// Used by the pending-signal state machine so the state claim and signal insert
    /// can remain atomic inside PostgreSQL.
    /// </summary>
    public async Task<T> RpcAsync<T>(
        string functionName,
        object args,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(functionName))
            throw new ArgumentException("RPC function name is required.", nameof(functionName));

        var url = $"{_baseUrl}/rest/v1/rpc/{Uri.EscapeDataString(functionName)}";
        var json = JsonSerializer.Serialize(args, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("Prefer", "return=representation");
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new Exception(
                $"Supabase RPC failed ({functionName}) HTTP {(int)resp.StatusCode}: {body}");

        var result = JsonSerializer.Deserialize<T>(
            body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        return result
            ?? throw new InvalidOperationException(
                $"Supabase RPC ({functionName}) returned an empty or invalid JSON result.");
    }

    /// <summary>
    /// True if the filtered query matches at least 1 row.
    /// Uses HEAD + Prefer: count=exact and parses Content-Range.
    /// </summary>
    public async Task<bool> ExistsAsync(string table, string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(table))
            throw new ArgumentException("Table is required.", nameof(table));

        query ??= "";

        // Ensure query starts with '?'
        if (query.Length > 0 && query[0] != '?')
            query = "?" + query;

        // Ensure limit=1 is present (keeps it cheap; also makes Content-Range stable)
        query = EnsureLimitOne(query);

        // Build URL (adjust if your client stores different base path)
        // Example: $"{_baseRestUrl}/{table}{query}"
        var url = $"{_baseUrl}/rest/v1/{table}{query}";
        using var req = new HttpRequestMessage(HttpMethod.Head, url);

        // Critical: ask PostgREST to include total count in Content-Range
        req.Headers.TryAddWithoutValidation("Prefer", "count=exact");

        // Optional but recommended (some proxies behave better when Accept is explicit)
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var msg = $"ExistsAsync failed: {(int)resp.StatusCode} {resp.ReasonPhrase} " +
                      $"table={table} query={query}";
            throw new HttpRequestException(msg, null, resp.StatusCode);
        }

        // PostgREST returns Content-Range like:
        //  - "0-0/0"  (no rows)
        //  - "0-0/1"  (>= 1 row)
        // Sometimes can be "*/0" etc.
        if (!resp.Headers.TryGetValues("Content-Range", out var values))
            return false; // if header missing, safest is "not exists"

        var cr = values.FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(cr))
            return false;

        // Total is after the last '/'
        var slash = cr.LastIndexOf('/');
        if (slash < 0 || slash == cr.Length - 1)
            return false;

        var totalPart = cr[(slash + 1)..].Trim();

        // totalPart is usually an integer; may be "*" in some edge cases
        if (totalPart == "*" || totalPart.Length == 0)
            return false;

        if (!long.TryParse(totalPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var total))
            return false;

        return total > 0;
    }

    private static string EnsureLimitOne(string query)
    {
        // naive but effective: if "limit=" already present, leave it
        // (covers "&limit=1" and "?limit=1")
        if (query.Contains("limit=", StringComparison.OrdinalIgnoreCase))
            return query;

        // append with correct separator
        return query.Contains('?') ? (query + "&limit=1") : ("?limit=1" + query);
    }

    public async Task InsertAsync<T>(string table, IEnumerable<T> rows, CancellationToken ct = default)
    {
        var list = rows.ToList();
        if (list.Count == 0) return;

        var url = $"{_baseUrl}/rest/v1/{table}";

        var json = JsonSerializer.Serialize(list, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("Prefer", "return=minimal");
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Supabase insert failed ({table}) HTTP {(int)resp.StatusCode}: {body}");
    }

    public void Dispose() => _http.Dispose();
}
