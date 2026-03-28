using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Smplkit.Config;

/// <summary>
/// Resolved, locally-cached configuration for a single config + environment.
///
/// All value-access methods (<see cref="Get"/>, <see cref="GetString"/>, etc.) are
/// <b>synchronous</b> — they read from an in-process dictionary and never touch the
/// network. The runtime is constructed by <see cref="ConfigClient.ConnectAsync"/>.
///
/// A background <see cref="Task"/> maintains a WebSocket connection to the config
/// service for real-time cache updates. If the WebSocket connection fails, the
/// runtime continues to serve cached values.
///
/// Implements <see cref="IAsyncDisposable"/>; use <c>await using</c> or call
/// <see cref="CloseAsync"/> explicitly.
/// </summary>
public sealed class ConfigRuntime : IAsyncDisposable
{
    private static readonly int[] BackoffSeconds = [1, 2, 4, 8, 16, 32, 60];
    private const string WsBasePath = "/api/ws/v1/configs";

    private readonly string _configKey;
    private readonly string _configId;
    private readonly string _environment;
    private readonly string _apiKey;
    private readonly Func<CancellationToken, Task<List<ConfigChainEntry>>>? _fetchChainFn;

    private List<ConfigChainEntry> _chain;
    private readonly object _cacheLock = new();
    private Dictionary<string, object?> _cache;
    private int _fetchCount;
    private string? _lastFetchAt;

    private readonly List<(Action<ConfigChangeEvent> Callback, string? Key)> _listeners = new();
    private volatile bool _closed;
    private volatile string _connectionStatus = "disconnected";

    private ClientWebSocket? _ws;
    private readonly CancellationTokenSource _wsCts = new();
    private readonly Task _wsTask;

    internal ConfigRuntime(
        string configKey,
        string configId,
        string environment,
        List<ConfigChainEntry> chain,
        string apiKey,
        Func<CancellationToken, Task<List<ConfigChainEntry>>>? fetchChainFn)
    {
        _configKey = configKey;
        _configId = configId;
        _environment = environment;
        _chain = chain;
        _apiKey = apiKey;
        _fetchChainFn = fetchChainFn;

        _cache = Resolver.Resolve(_chain, _environment);
        _fetchCount = chain.Count;
        _lastFetchAt = DateTimeOffset.UtcNow.ToString("o");

        _wsTask = Task.Run(() => RunWebSocketAsync(_wsCts.Token));
    }

    // ------------------------------------------------------------------
    // Value access — synchronous, thread-safe
    // ------------------------------------------------------------------

    /// <summary>
    /// Return the resolved value for <paramref name="key"/>, or <paramref name="default"/>
    /// if the key is absent.
    /// </summary>
    public object? Get(string key, object? @default = null)
    {
        lock (_cacheLock)
            return _cache.TryGetValue(key, out var v) ? v : @default;
    }

    /// <summary>
    /// Return the value for <paramref name="key"/> if it is a <see cref="string"/>,
    /// otherwise <paramref name="default"/>.
    /// </summary>
    public string? GetString(string key, string? @default = null)
    {
        object? val;
        lock (_cacheLock)
            val = _cache.TryGetValue(key, out var x) ? x : null;
        return val is string s ? s : @default;
    }

    /// <summary>
    /// Return the value for <paramref name="key"/> if it is numeric and integral,
    /// otherwise <paramref name="default"/>.
    /// </summary>
    public int? GetInt(string key, int? @default = null)
    {
        object? val;
        lock (_cacheLock)
            val = _cache.TryGetValue(key, out var x) ? x : null;
        return val switch
        {
            int i => i,
            long l => (int)l,
            double d when d == Math.Truncate(d) && d >= int.MinValue && d <= int.MaxValue
                => (int)d,
            _ => @default,
        };
    }

    /// <summary>
    /// Return the value for <paramref name="key"/> if it is a <see cref="bool"/>,
    /// otherwise <paramref name="default"/>.
    /// </summary>
    public bool? GetBool(string key, bool? @default = null)
    {
        object? val;
        lock (_cacheLock)
            val = _cache.TryGetValue(key, out var x) ? x : null;
        return val is bool b ? b : @default;
    }

    /// <summary>Return a shallow copy of the full resolved configuration.</summary>
    public Dictionary<string, object?> GetAll()
    {
        lock (_cacheLock)
            return new Dictionary<string, object?>(_cache);
    }

    /// <summary>Check whether <paramref name="key"/> is present in the resolved configuration.</summary>
    public bool Exists(string key)
    {
        lock (_cacheLock)
            return _cache.ContainsKey(key);
    }

    // ------------------------------------------------------------------
    // Listeners
    // ------------------------------------------------------------------

    /// <summary>
    /// Register a listener that fires when a config value changes.
    /// </summary>
    /// <param name="callback">Called with a <see cref="ConfigChangeEvent"/> on each change.</param>
    /// <param name="key">
    /// If provided, the listener fires only for changes to this specific key.
    /// If <c>null</c>, the listener fires for all changes.
    /// </param>
    public void OnChange(Action<ConfigChangeEvent> callback, string? key = null)
    {
        _listeners.Add((callback, key));
    }

    // ------------------------------------------------------------------
    // Diagnostics
    // ------------------------------------------------------------------

    /// <summary>Return diagnostic statistics for this runtime.</summary>
    public ConfigStats Stats() => new(_fetchCount, _lastFetchAt);

    /// <summary>
    /// Return the current WebSocket connection status: <c>"connected"</c>,
    /// <c>"connecting"</c>, or <c>"disconnected"</c>.
    /// </summary>
    public string ConnectionStatus() => _connectionStatus;

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    /// <summary>
    /// Force a manual refresh of the cached configuration. Fetches the full
    /// config chain via HTTP, re-resolves values, updates the local cache, and
    /// fires listeners for any changes with <c>source="manual"</c>.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (_fetchChainFn is null) return;

        var newChain = await _fetchChainFn(ct).ConfigureAwait(false);
        _chain = newChain;
        var newCache = Resolver.Resolve(newChain, _environment);
        _fetchCount += newChain.Count;
        _lastFetchAt = DateTimeOffset.UtcNow.ToString("o");

        Dictionary<string, object?> oldCache;
        lock (_cacheLock)
        {
            oldCache = _cache;
            _cache = newCache;
        }

        FireChangeListeners(oldCache, newCache, source: "manual");
    }

    /// <summary>
    /// Close the runtime connection. Cancels the WebSocket background task and
    /// waits up to two seconds for it to exit.
    /// </summary>
    public async Task CloseAsync(CancellationToken ct = default)
    {
        _closed = true;
        _connectionStatus = "disconnected";
        _wsCts.Cancel();

        var ws = _ws;
        if (ws is { State: WebSocketState.Open })
        {
            try
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing",
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* Ignore close errors */ }
        }

        try
        {
            await _wsTask.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
        catch { /* Ignore timeout or cancellation */ }

        ws?.Dispose();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);

    // ------------------------------------------------------------------
    // WebSocket background loop
    // ------------------------------------------------------------------

    private async Task RunWebSocketAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested && !_closed)
        {
            try
            {
                await ConnectAndSubscribeAsync(ct).ConfigureAwait(false);
                attempt = 0;
                await ReceiveLoopAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested || _closed)
            {
                break;
            }
            catch
            {
                if (ct.IsCancellationRequested || _closed) break;
                _connectionStatus = "connecting";
                int delay = BackoffSeconds[Math.Min(attempt, BackoffSeconds.Length - 1)];
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                attempt++;
                await ResyncCacheAsync(ct).ConfigureAwait(false);
            }
        }
    }

    private string BuildWebSocketUrl()
    {
        const string restBase = "https://config.smplkit.com";
        string wsBase = restBase.StartsWith("https://", StringComparison.Ordinal)
            ? "wss://" + restBase["https://".Length..]
            : restBase.StartsWith("http://", StringComparison.Ordinal)
                ? "ws://" + restBase["http://".Length..]
                : "wss://" + restBase;
        wsBase = wsBase.TrimEnd('/');
        return $"{wsBase}{WsBasePath}?api_key={Uri.EscapeDataString(_apiKey)}";
    }

    private async Task ConnectAndSubscribeAsync(CancellationToken ct)
    {
        _connectionStatus = "connecting";

        _ws?.Dispose();
        _ws = new ClientWebSocket();
        await _ws.ConnectAsync(new Uri(BuildWebSocketUrl()), ct).ConfigureAwait(false);

        var subscribeMsg = JsonSerializer.Serialize(new
        {
            type = "subscribe",
            config_id = _configId,
            environment = _environment,
        });
        await _ws.SendAsync(
            Encoding.UTF8.GetBytes(subscribeMsg),
            WebSocketMessageType.Text,
            endOfMessage: true,
            ct).ConfigureAwait(false);

        // Wait for confirmation
        var raw = await ReceiveRawAsync(ct).ConfigureAwait(false);
        if (raw is not null)
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "error")
            {
                var msg = doc.RootElement.TryGetProperty("message", out var m)
                    ? m.GetString() : "unknown error";
                throw new InvalidOperationException($"WebSocket subscription error: {msg}");
            }
        }

        _connectionStatus = "connected";
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!_closed && !ct.IsCancellationRequested)
        {
            var raw = await ReceiveRawAsync(ct).ConfigureAwait(false);
            if (raw is null) break; // connection closed

            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("type", out var typeProp)) continue;
            var msgType = typeProp.GetString();

            if (msgType == "config_changed")
                HandleConfigChanged(doc.RootElement);
            else if (msgType == "config_deleted")
                HandleConfigDeleted();
        }
    }

    private async Task<string?> ReceiveRawAsync(CancellationToken ct)
    {
        if (_ws is null || _ws.State != WebSocketState.Open) return null;

        var buffer = new byte[65536];
        var sb = new StringBuilder();
        WebSocketReceiveResult result;
        do
        {
            result = await _ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        }
        while (!result.EndOfMessage);

        return sb.ToString();
    }

    private void HandleConfigChanged(JsonElement root)
    {
        if (!root.TryGetProperty("config_id", out var cidProp)) return;
        var changedConfigId = cidProp.GetString();
        if (!root.TryGetProperty("changes", out var changesProp)) return;

        // Find the matching chain entry
        ConfigChainEntry? target = null;
        foreach (var entry in _chain)
        {
            if (entry.Id == changedConfigId)
            {
                target = entry;
                break;
            }
        }
        if (target is null) return;

        // Apply changes to base and env values in the chain entry
        var newBaseValues = new Dictionary<string, object?>(target.Values);

        if (!target.EnvValues.TryGetValue(_environment, out var existingEnvVals))
            existingEnvVals = new Dictionary<string, object?>();
        var newEnvVals = new Dictionary<string, object?>(existingEnvVals);

        foreach (var change in changesProp.EnumerateArray())
        {
            var key = change.GetProperty("key").GetString()!;
            var newVal = change.TryGetProperty("new_value", out var nv)
                ? Resolver.Normalize(nv) : null;
            var oldVal = change.TryGetProperty("old_value", out var ov)
                ? Resolver.Normalize(ov) : null;

            if (newVal is null && oldVal is not null)
            {
                newBaseValues.Remove(key);
                newEnvVals.Remove(key);
            }
            else
            {
                newBaseValues[key] = newVal;
                newEnvVals[key] = newVal;
            }
        }

        // Mutate chain entry in place
        target.Values = newBaseValues;
        var updatedEnvValues = new Dictionary<string, Dictionary<string, object?>>(target.EnvValues);
        updatedEnvValues[_environment] = newEnvVals;
        target.EnvValues = updatedEnvValues;

        var newCache = Resolver.Resolve(_chain, _environment);

        Dictionary<string, object?> oldCache;
        lock (_cacheLock)
        {
            oldCache = _cache;
            _cache = newCache;
        }

        FireChangeListeners(oldCache, newCache, source: "websocket");
    }

    private void HandleConfigDeleted()
    {
        _closed = true;
        _connectionStatus = "disconnected";
    }

    private async Task ResyncCacheAsync(CancellationToken ct)
    {
        if (_fetchChainFn is null) return;
        try
        {
            var newChain = await _fetchChainFn(ct).ConfigureAwait(false);
            _chain = newChain;
            var newCache = Resolver.Resolve(newChain, _environment);
            _fetchCount += newChain.Count;
            _lastFetchAt = DateTimeOffset.UtcNow.ToString("o");

            Dictionary<string, object?> oldCache;
            lock (_cacheLock)
            {
                oldCache = _cache;
                _cache = newCache;
            }
            FireChangeListeners(oldCache, newCache, source: "websocket");
        }
        catch { /* Ignore resync errors */ }
    }

    private void FireChangeListeners(
        Dictionary<string, object?> oldCache,
        Dictionary<string, object?> newCache,
        string source)
    {
        var allKeys = new HashSet<string>(oldCache.Keys);
        allKeys.UnionWith(newCache.Keys);

        foreach (var key in allKeys)
        {
            var oldVal = oldCache.TryGetValue(key, out var ov) ? ov : null;
            var newVal = newCache.TryGetValue(key, out var nv) ? nv : null;
            if (Equals(oldVal, newVal)) continue;

            var evt = new ConfigChangeEvent(key, oldVal, newVal, source);
            foreach (var (callback, keyFilter) in _listeners)
            {
                if (keyFilter is not null && keyFilter != key) continue;
                try { callback(evt); }
                catch { /* Ignore listener exceptions */ }
            }
        }
    }
}
