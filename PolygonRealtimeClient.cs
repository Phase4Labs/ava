using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace get_assessment_no_graph;

/// <summary>
/// Polygon Stocks WebSocket client — single connection for AM.*, T.*, and Q.*.
///
/// Uses the official endpoint: wss://socket.polygon.io/stocks
/// Auth: {"action":"auth","params":"API_KEY"}
/// Subscribe: {"action":"subscribe","params":"AM.TICK,T.TICK,Q.TICK,..."}
///
/// One connection per API key per cluster — BarIngestionService owns this
/// and routes T/Q events to ScannerService via callbacks, eliminating the
/// PolicyViolation caused by ScannerService opening its own stocks WS.
/// </summary>
public sealed class PolygonRealtimeClient : IAsyncDisposable
{
    private readonly string _apiKey;
    private readonly Uri _uri = new("wss://socket.polygon.io/stocks");
    private ClientWebSocket? _ws;

    public PolygonRealtimeClient(string apiKey)
    {
        _apiKey = string.IsNullOrWhiteSpace(apiKey)
            ? throw new ArgumentException("POLYGON_API_KEY required")
            : apiKey;
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        // Always dispose and recreate the socket — handles reconnects cleanly
        Console.WriteLine("[bar-ingest] Connecting to Polygon WebSocket... URL=" + _uri);
        if (_ws is not null)
        {
            try
            {
                if (_ws.State == WebSocketState.Open)
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "reconnecting", ct);
            }
            catch { }
            _ws.Dispose();
            _ws = null;
        }

        _ws = new ClientWebSocket();
        await _ws.ConnectAsync(_uri, ct);

        // Send auth and wait for auth_success before returning
        await SendAsync(new { action = "auth", @params = _apiKey }, ct);

        using var authCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        authCts.CancelAfter(10_000);
        var buf = new byte[4096];
        try
        {
            while (!authCts.IsCancellationRequested)
            {
                var res = await _ws.ReceiveAsync(buf, authCts.Token);
                if (res.MessageType == WebSocketMessageType.Close)
                {
                    Console.WriteLine($"[bar-ingest] WebSocket closed during auth: status={_ws.CloseStatus} desc={_ws.CloseStatusDescription}");
                    return;
                }
                var msg = Encoding.UTF8.GetString(buf, 0, res.Count);
                if (msg.Contains("auth_success")) break;
                if (msg.Contains("auth_failed"))
                    throw new Exception($"Polygon auth failed: {msg}");
            }
        }
        catch (OperationCanceledException) { /* timeout — proceed anyway */ }
    }

    /// <summary>
    /// Subscribe to AM.*, T.*, and Q.* for all tickers in a single message.
    /// This keeps one connection handling everything the old two connections did.
    /// </summary>
    public Task SubscribeStocksAsync(IEnumerable<string> tickers, CancellationToken ct)
    {
        var upper  = tickers.Select(t => t.ToUpperInvariant()).ToArray();
        var chans  = string.Join(',',
            upper.Select(t => $"AM.{t}")
            .Concat(upper.Select(t => $"T.{t}"))
            .Concat(upper.Select(t => $"Q.{t}")));

        Console.WriteLine($"[bar-ingest] Subscribing AM+T+Q for {upper.Length} tickers");
        return SendAsync(new { action = "subscribe", @params = chans }, ct);
    }

    /// <summary>Legacy convenience — still usable if only AM is needed.</summary>
    public Task SubscribeMinuteAggregatesAsync(IEnumerable<string> tickers, CancellationToken ct)
    {
        var list  = tickers.Select(t => $"AM.{t.ToUpperInvariant()}");
        var chans = string.Join(',', list);
        return SendAsync(new { action = "subscribe", @params = chans }, ct);
    }

    public async Task RunAsync(
        Func<JsonElement, Task> onEvent,
        Action<string>? onRaw = null,
        CancellationToken ct  = default)
    {
        if (_ws is null) throw new InvalidOperationException("Call ConnectAsync first.");

        // Ping every 30 s to keep the connection alive between AM.* bursts
        using var pingTimer = new System.Timers.Timer(30_000);
        pingTimer.Elapsed += async (_, _) =>
        {
            try
            {
                if (_ws?.State == WebSocketState.Open)
                    await _ws.SendAsync(
                        Encoding.UTF8.GetBytes("{\"action\":\"ping\"}"),
                        WebSocketMessageType.Text, true, ct);
            }
            catch { }
        };
        pingTimer.Start();

        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 64);
        try
        {
            var sb = new StringBuilder();
            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                sb.Clear();

                WebSocketReceiveResult? res;
                do
                {
                    res = await _ws.ReceiveAsync(buffer, ct);
                    if (res.MessageType == WebSocketMessageType.Close)
                    {
                        Console.WriteLine($"[bar-ingest] WebSocket closed by server: status={_ws.CloseStatus} desc={_ws.CloseStatusDescription}");
                        return;
                    }
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, res.Count));
                } while (!res.EndOfMessage);

                var json = sb.ToString();
                onRaw?.Invoke(json);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (var ev in root.EnumerateArray())
                        await onEvent(ev);
                }
                else
                {
                    await onEvent(root);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task SendAsync(object payload, CancellationToken ct)
    {
        if (_ws is null) throw new InvalidOperationException("WebSocket not connected.");
        var json  = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken: ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_ws is not null)
        {
            try
            {
                if (_ws.State == WebSocketState.Open)
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch { /* ignore */ }
            _ws.Dispose();
        }
    }
}
