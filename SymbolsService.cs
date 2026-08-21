using System.Collections.Concurrent;
using System.Text.Json;
using Supabase.Realtime;
using Supabase.Realtime.PostgresChanges;
using Supabase.Realtime.Socket;

namespace get_assessment_no_graph;

/// <summary>
/// Manages the live set of active tickers.
///
/// Startup:
///   - Loads active_symbols WHERE is_active = true from Supabase REST.
///   - If the table is empty, prompts the user for a comma-separated list
///     and seeds the table.
///
/// Runtime:
///   - Subscribes to Supabase Realtime on active_symbols.
///   - INSERT or UPDATE with is_active=true  → adds ticker to active set.
///   - UPDATE with is_active=false           → DROP logic:
///       * Open position  → removes from active set immediately
///         (engine will finish current bar but not enter new positions).
///       * No open position → logs a warning and KEEPS the ticker active
///         (operator must resolve the issue or use symbols_ctl to retry).
///
/// Callers use ActiveTickers to get a snapshot, or subscribe to OnChanged.
/// </summary>
public sealed class SymbolsService : IAsyncDisposable
{
    private readonly SupabaseRestClient _db;
    private readonly string _supabaseUrl;
    private readonly string _anonKey;
    private readonly string _serviceRoleKey;

    private readonly ConcurrentDictionary<string, byte> _active = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Fired when the active set changes. Payload is (ticker, added: true/false).</summary>
    public event Action<string, bool>? OnChanged;

    /// <summary>
    /// Fired when a signal_actions 'taken' row is inserted (user clicked Taken in TrayApp).
    /// Payload is the ticker string (uppercase). Allows the main loop to force a fresh
    /// card on the next bar regardless of the cadence schedule.
    /// </summary>
    public event Func<string, Task>? OnPositionTaken;

    private Supabase.Realtime.Client? _realtimeClient;

    public SymbolsService(SupabaseRestClient db, string supabaseUrl, string anonKey, string serviceRoleKey)
    {
        _db             = db;
        _supabaseUrl    = supabaseUrl;
        _anonKey        = anonKey;
        _serviceRoleKey = serviceRoleKey;
    }

    /// <summary>
    /// Returns a point-in-time snapshot of active ticker symbols (uppercase).
    /// Safe to call from any thread.
    /// </summary>
    public IReadOnlyList<string> ActiveTickers => _active.Keys.OrderBy(x => x).ToList();

    /// <summary>Returns true if the ticker is currently active.</summary>
    public bool IsActive(string ticker) => _active.ContainsKey(ticker.ToUpperInvariant());

    // ── Initialise ────────────────────────────────────────────────

    /// <summary>
    /// Loads initial symbol set from DB and starts the Realtime subscription.
    /// Must be called once before the main loop.
    /// </summary>
    public async Task InitAsync(CancellationToken ct = default)
    {
        await LoadFromDbAsync(ct);

        if (_active.IsEmpty)
            await PromptAndSeedAsync(ct);

        await StartRealtimeAsync(ct);
    }

    private async Task LoadFromDbAsync(CancellationToken ct)
    {
        var rows = await _db.SelectAsync<ActiveSymbolRow>(
            "active_symbols",
            "?select=ticker&is_active=eq.true&order=ticker.asc",
            ct);

        foreach (var r in rows)
            _active[r.Ticker.ToUpperInvariant()] = 0;

        Console.WriteLine($"[symbols] Loaded {_active.Count} active ticker(s): " +
                          string.Join(", ", ActiveTickers));
    }

    private async Task PromptAndSeedAsync(CancellationToken ct)
    {
        Console.WriteLine("[symbols] No active symbols found in DB.");
        Console.Write("[symbols] Enter comma-separated tickers to start with (e.g. SOFI,MARA): ");
        var input = Console.ReadLine() ?? "";

        var tickers = input
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToUpperInvariant())
            .Where(t => t.Length > 0)
            .Distinct()
            .ToList();

        if (tickers.Count == 0)
            throw new Exception("[symbols] No tickers provided. Cannot start.");

        foreach (var ticker in tickers)
        {
            await _db.UpsertAsync("active_symbols", new[]
            {
                new
                {
                    ticker,
                    is_active      = true,
                    added_at       = DateTime.UtcNow,
                    deactivated_at = (DateTime?)null,
                }
            }, "ticker", ct);

            _active[ticker] = 0;
        }

        Console.WriteLine($"[symbols] Seeded {tickers.Count} ticker(s): {string.Join(", ", tickers)}");
    }

    // ── Realtime subscription ─────────────────────────────────────

    private async Task StartRealtimeAsync(CancellationToken ct)
    {
        var realtimeBaseUrl = _supabaseUrl
            .Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase)
            .Replace("http://",  "ws://",  StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/')
            + "/realtime/v1";

        var opts = new ClientOptions
        {
            Parameters = new SocketOptionsParameters
            {
                ApiKey = _anonKey,
                Token = _serviceRoleKey
            }
        };

        _realtimeClient = new Supabase.Realtime.Client(realtimeBaseUrl, opts);

        _realtimeClient.AddStateChangedHandler((state, previous) =>
            Console.WriteLine($"[symbols-realtime] {previous} → {state}"));

        // Parse postgres_changes frames from the debug handler
        // (same proven pattern already used in RelayService)
        _realtimeClient.AddDebugHandler((topic, payload, direction) =>
        {
            var s = payload?.ToString();
            if (string.IsNullOrWhiteSpace(s)) return;

            var i = s.IndexOf('{');
            if (i < 0) return;

            try
            {
                using var doc = JsonDocument.Parse(s[i..]);
                var root = doc.RootElement;

                if (!root.TryGetProperty("event", out var ev) ||
                    ev.GetString() != "postgres_changes") return;

                var data   = root.GetProperty("payload").GetProperty("data");
                var record = data.GetProperty("record");

                // Dispatch by table: active_symbols has is_active, signal_actions has action
                if (record.TryGetProperty("is_active", out var iaEl))
                {
                    // ── active_symbols change ─────────────────────
                    if (!record.TryGetProperty("ticker", out var tkEl)) return;
                    var ticker   = (tkEl.GetString() ?? "").ToUpperInvariant();
                    if (ticker.Length == 0) return;
                    var isActive = iaEl.ValueKind == JsonValueKind.True;
                    _ = Task.Run(() => HandleSymbolChangeAsync(ticker, isActive, ct));
                }
                else if (record.TryGetProperty("action", out var actionEl))
                {
                    // ── signal_actions change ─────────────────────
                    var action = actionEl.GetString() ?? "";
                    if (!string.Equals(action, "taken", StringComparison.OrdinalIgnoreCase))
                        return;

                    if (!record.TryGetProperty("signal_id", out var sigEl)) return;
                    var signalId = sigEl.GetString() ?? "";
                    if (signalId.Length == 0) return;

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var rows = await _db.SelectAsync<SignalEventTickerRow>(
                                "signal_events",
                                $"?select=ticker&id=eq.{Uri.EscapeDataString(signalId)}&limit=1",
                                ct);
                            if (rows.Count == 0) return;
                            var tkr = rows[0].Ticker.ToUpperInvariant();
                            Console.WriteLine($"[symbols] signal_actions taken: ticker={tkr}");
                            if (OnPositionTaken is not null)
                                await OnPositionTaken.Invoke(tkr);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[symbols] OnPositionTaken error: {ex.Message}");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[symbols-realtime] parse error: {ex.Message}");
            }
        });

        await _realtimeClient.ConnectAsync();
        _realtimeClient.SetAuth(_serviceRoleKey);

        var channel = _realtimeClient.Channel("public:active_symbols");
        channel.Register(new PostgresChangesOptions(schema: "public", table: "active_symbols"));
        await channel.Subscribe();

        // Also subscribe to signal_actions so we can detect 'taken' events
        // and fire OnPositionTaken to force a fresh card on the next bar.
        var actionsChannel = _realtimeClient.Channel("public:signal_actions");
        actionsChannel.Register(new PostgresChangesOptions(schema: "public", table: "signal_actions"));
        await actionsChannel.Subscribe();

        Console.WriteLine("[symbols-realtime] Subscribed to active_symbols + signal_actions.");
    }

    private async Task HandleSymbolChangeAsync(string ticker, bool isActive, CancellationToken ct)
    {
        if (isActive)
        {
            // ADD
            if (_active.TryAdd(ticker, 0))
            {
                Console.WriteLine($"[symbols] ADDED {ticker} (Realtime)");
                OnChanged?.Invoke(ticker, true);
            }
            return;
        }

        // DROP — check for open position first
        var states = await _db.SelectAsync<TraderStateRow>(
            "trader_state",
            $"?select=ticker,position&ticker=eq.{Uri.EscapeDataString(ticker)}",
            ct);

        var position = states.Count > 0 ? states[0].Position : "flat";
        var hasOpenPosition = !string.Equals(position, "flat", StringComparison.OrdinalIgnoreCase);

        if (hasOpenPosition)
        {
            // BLOCK the drop — can't remove while positioned
            Console.WriteLine(
                $"[symbols] WARNING: DROP blocked for {ticker} — open position ({position}). " +
                $"Close the position first, then drop.");
            // Do NOT remove from _active, do NOT fire OnChanged.
        }
        else
        {
            // No open position — safe to drop immediately
            _active.TryRemove(ticker, out _);
            Console.WriteLine(
                $"[symbols] DROPPED {ticker} (Realtime) — no open position, removed cleanly.");
            OnChanged?.Invoke(ticker, false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_realtimeClient is not null)
            _realtimeClient.Disconnect();
        await ValueTask.CompletedTask;
    }

    // ── DTOs ──────────────────────────────────────────────────────

    private sealed class ActiveSymbolRow
    {
        [System.Text.Json.Serialization.JsonPropertyName("ticker")]
        public string Ticker { get; set; } = "";
    }

    private sealed class TraderStateRow
    {
        [System.Text.Json.Serialization.JsonPropertyName("ticker")]
        public string Ticker { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("position")]
        public string Position { get; set; } = "flat";
    }

    private sealed class SignalEventTickerRow
    {
        [System.Text.Json.Serialization.JsonPropertyName("ticker")]
        public string Ticker { get; set; } = "";
    }
}