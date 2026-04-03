using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Smplkit.Internal;

/// <summary>
/// Manages a single WebSocket connection to the app service event gateway.
/// Shared across all product modules (config, flags) within one <see cref="SmplClient"/>.
/// </summary>
internal sealed class SharedWebSocket
{
    private static readonly int[] BackoffSeconds = [1, 2, 4, 8, 16, 32, 60];
    private const string WsBasePath = "/api/ws/v1/events";
    private const string AppBaseUrl = "https://app.smplkit.com";

    private readonly string _apiKey;
    private readonly ConcurrentDictionary<string, List<Action<Dictionary<string, object?>>>> _listeners = new();
    private readonly object _listenersLock = new();

    private volatile string _connectionStatus = "disconnected";
    private volatile bool _closed;
    private WebSocket? _ws;
    private readonly CancellationTokenSource _wsCts = new();
    private Task? _wsTask;
    private readonly Func<Uri, CancellationToken, Task<WebSocket>> _wsFactory;

    internal SharedWebSocket(string apiKey, Func<Uri, CancellationToken, Task<WebSocket>>? wsFactory = null)
    {
        _apiKey = apiKey;
        _wsFactory = wsFactory ?? DefaultWsFactoryAsync;
    }

    // ------------------------------------------------------------------
    // Listener registration
    // ------------------------------------------------------------------

    /// <summary>Register a listener for a specific event type.</summary>
    internal void On(string eventName, Action<Dictionary<string, object?>> callback)
    {
        lock (_listenersLock)
        {
            if (!_listeners.TryGetValue(eventName, out var list))
            {
                list = new List<Action<Dictionary<string, object?>>>();
                _listeners[eventName] = list;
            }
            list.Add(callback);
        }
    }

    /// <summary>Unregister a listener for a specific event type.</summary>
    internal void Off(string eventName, Action<Dictionary<string, object?>> callback)
    {
        lock (_listenersLock)
        {
            if (_listeners.TryGetValue(eventName, out var list))
                list.Remove(callback);
        }
    }

    private void Dispatch(string eventName, Dictionary<string, object?> data)
    {
        List<Action<Dictionary<string, object?>>>? callbacks;
        lock (_listenersLock)
        {
            if (!_listeners.TryGetValue(eventName, out var list))
                return;
            callbacks = new List<Action<Dictionary<string, object?>>>(list);
        }
        foreach (var cb in callbacks)
        {
            try { cb(data); }
            catch { /* Ignore listener exceptions */ }
        }
    }

    // ------------------------------------------------------------------
    // Connection status
    // ------------------------------------------------------------------

    /// <summary>Return the current connection status.</summary>
    internal string ConnectionStatus => _connectionStatus;

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    /// <summary>Start the background WebSocket task.</summary>
    internal void Start()
    {
        _closed = false;
        _wsTask = Task.Run(() => RunWebSocketAsync(_wsCts.Token));
    }

    /// <summary>Stop the WebSocket connection and wait for cleanup.</summary>
    internal async Task StopAsync()
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

        if (_wsTask is not null)
        {
            try
            {
                await _wsTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch { /* Ignore timeout or cancellation */ }
        }

        ws?.Dispose();
    }

    // ------------------------------------------------------------------
    // WebSocket background loop
    // ------------------------------------------------------------------

    [ExcludeFromCodeCoverage]
    private async Task RunWebSocketAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested && !_closed)
        {
            try
            {
                await ConnectAsync(ct).ConfigureAwait(false);
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
                _connectionStatus = "reconnecting";
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
            }
        }
    }

    [ExcludeFromCodeCoverage]
    private static async Task<WebSocket> DefaultWsFactoryAsync(Uri uri, CancellationToken ct)
    {
        var cws = new ClientWebSocket();
        await cws.ConnectAsync(uri, ct).ConfigureAwait(false);
        return cws;
    }

    private string BuildWebSocketUrl()
    {
        string wsBase = AppBaseUrl.StartsWith("https://", StringComparison.Ordinal)
            ? "wss://" + AppBaseUrl["https://".Length..]
            : AppBaseUrl.StartsWith("http://", StringComparison.Ordinal)
                ? "ws://" + AppBaseUrl["http://".Length..]
                : "wss://" + AppBaseUrl;
        wsBase = wsBase.TrimEnd('/');
        return $"{wsBase}{WsBasePath}?api_key={Uri.EscapeDataString(_apiKey)}";
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        _connectionStatus = "connecting";

        _ws?.Dispose();
        var wsUri = new Uri(BuildWebSocketUrl());
        _ws = await _wsFactory(wsUri, ct).ConfigureAwait(false);

        // Wait for {"type": "connected"} confirmation
        var raw = await ReceiveRawAsync(ct).ConfigureAwait(false);
        if (raw is not null)
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "error")
            {
                var msg = doc.RootElement.TryGetProperty("message", out var m)
                    ? m.GetString() : "unknown error";
                throw new InvalidOperationException($"WebSocket connection error: {msg}");
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

            // Heartbeat: server sends "ping", we respond with "pong"
            if (raw == "ping")
            {
                if (_ws is { State: WebSocketState.Open })
                {
                    await _ws.SendAsync(
                        Encoding.UTF8.GetBytes("pong"),
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        ct).ConfigureAwait(false);
                }
                continue;
            }

            using var doc = JsonDocument.Parse(raw);

            // Check for event-style messages: {"event": "flag_changed", ...}
            if (doc.RootElement.TryGetProperty("event", out var eventProp))
            {
                var eventName = eventProp.GetString();
                if (eventName is not null)
                {
                    var data = new Dictionary<string, object?>();
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.Name == "event") continue;
                        data[prop.Name] = Config.Resolver.Normalize(prop.Value);
                    }
                    Dispatch(eventName, data);
                }
                continue;
            }

            // Config-style messages: {"type": "config_changed", ...}
            if (doc.RootElement.TryGetProperty("type", out var typeProp))
            {
                var msgType = typeProp.GetString();
                if (msgType is not null && msgType != "connected")
                {
                    var data = new Dictionary<string, object?>();
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        data[prop.Name] = Config.Resolver.Normalize(prop.Value);
                    }
                    Dispatch(msgType, data);
                }
            }
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
}
