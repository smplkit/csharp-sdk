using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Smplkit.Internal;

/// <summary>
/// Manages a single Server-Sent Events (SSE) connection to the app service
/// event gateway (<c>GET /api/v1/events</c>). Shared across all product
/// modules (config, flags, logging) within one <see cref="SmplClient"/>.
/// </summary>
/// <remarks>
/// <para>The stream is plain HTTPS: a long-lived GET with
/// <c>Accept: text/event-stream</c>, authenticated via a
/// <c>Bearer</c> API key. Frames carry the event name in the SSE
/// <c>event:</c> field and a JSON payload in <c>data:</c>. The server emits a
/// comment frame (<c>: keepalive</c>) every 30 seconds when idle; a read
/// timeout of 45 seconds (two missed keepalives) triggers a reconnect.</para>
/// <para>Reconnect backoff is seeded from the server's <c>retry:</c> value
/// (1000 ms until one is received), doubles per failed attempt, is capped at
/// 60 s, and resets to base on every successful connect. On every successful
/// <i>re</i>connect (not the initial connect) the registered refetch callbacks
/// run so each product module can recover events missed while disconnected.</para>
/// </remarks>
internal sealed class EventStream
{
    private const string EventsPath = "/api/v1/events";
    private const int DefaultRetryMs = 1000;
    private const int MaxDelayMs = 60_000;

    private readonly string _apiKey;
    private readonly string _appBaseUrl;
    private readonly string _userAgent;
    private readonly ConcurrentDictionary<string, List<Action<Dictionary<string, object?>>>> _listeners = new();
    private readonly object _listenersLock = new();
    private readonly List<Action> _refetchCallbacks = new();
    private readonly object _refetchLock = new();
    private readonly MetricsReporter? _metrics;

    private volatile string _connectionStatus = "disconnected";
    private volatile bool _closed;
    private HttpClient? _ownedHttpClient;
    private readonly CancellationTokenSource _cts = new();
    private Task? _runTask;
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _sendAsync;
    private readonly TaskCompletionSource<bool> _initialConnect = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Reconnect state. _retryBaseMs tracks the server's retry: value; _delayMs
    // is the next reconnect delay (doubles per failure, resets to base on
    // success). Both live on the run-loop; internal for test observation.
    internal int _retryBaseMs = DefaultRetryMs;
    internal int _delayMs = DefaultRetryMs;
    private bool _hasConnectedBefore;

    // Liveness read timeout: two missed 30 s keepalives. Internal so tests can
    // shorten it; production always uses the default.
    internal TimeSpan ReadTimeout = TimeSpan.FromSeconds(45);

    // Backoff delay seam: production sleeps for real; tests substitute an
    // observer to record delays and return immediately.
    internal Func<int, CancellationToken, Task> DelayAsync = static (ms, ct) => Task.Delay(ms, ct);

    internal EventStream(
        string apiKey,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? sendAsync = null,
        MetricsReporter? metrics = null,
        string appBaseUrl = "https://app.smplkit.com",
        string? userAgent = null)
    {
        _apiKey = apiKey;
        _appBaseUrl = appBaseUrl;
        _sendAsync = sendAsync ?? DefaultSendAsync;
        _metrics = metrics;
        _userAgent = userAgent ?? SdkVersion.UserAgent;
    }

    /// <summary>The User-Agent stamped on the event stream request — the HTTP
    /// transport's effective value when wired by a client, else the SDK
    /// default. Exposed for tests.</summary>
    internal string RequestUserAgent => _userAgent;

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

    /// <summary>
    /// Register a refetch callback invoked on every successful <i>re</i>connect
    /// (never on the initial connect). Product modules register their bulk
    /// refresh here so state missed while disconnected is recovered.
    /// </summary>
    internal void OnReconnect(Action callback)
    {
        lock (_refetchLock)
        {
            _refetchCallbacks.Add(callback);
        }
    }

    /// <summary>Unregister a reconnect refetch callback.</summary>
    internal void OffReconnect(Action callback)
    {
        lock (_refetchLock)
        {
            _refetchCallbacks.Remove(callback);
        }
    }

    private void Dispatch(string eventName, Dictionary<string, object?> data)
    {
        List<Action<Dictionary<string, object?>>>? callbacks;
        lock (_listenersLock)
        {
            if (!_listeners.TryGetValue(eventName, out var list))
            {
                Debug.Log("events", $"no handler registered for event: \"{eventName}\"");
                return;
            }
            callbacks = new List<Action<Dictionary<string, object?>>>(list);
        }
        Debug.Log("events", $"routing \"{eventName}\" to {callbacks.Count} handler(s)");
        foreach (var cb in callbacks)
        {
            try { cb(data); }
            catch { /* Ignore listener exceptions */ }
        }
    }

    private void InvokeRefetchCallbacks()
    {
        List<Action> callbacks;
        lock (_refetchLock)
        {
            callbacks = new List<Action>(_refetchCallbacks);
        }
        Debug.Log("events", $"reconnected — invoking {callbacks.Count} refetch callback(s)");
        foreach (var cb in callbacks)
        {
            try { cb(); }
            catch { /* Ignore refetch exceptions */ }
        }
    }

    // ------------------------------------------------------------------
    // Connection status
    // ------------------------------------------------------------------

    /// <summary>Return the current connection status.</summary>
    internal string ConnectionStatus => _connectionStatus;

    /// <summary>
    /// Wait for the initial connect to succeed or fail.
    /// Resolves once the background task has completed its first connect attempt.
    /// </summary>
    internal Task WaitForInitialConnectAsync(CancellationToken ct = default)
    {
        return _initialConnect.Task.WaitAsync(ct);
    }

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    /// <summary>Start the background event stream task.</summary>
    internal void Start()
    {
        Debug.Log("events", "starting event stream connection");
        _closed = false;
        _runTask = Task.Run(() => RunEventStreamAsync(_cts.Token));
    }

    /// <summary>Stop the event stream and wait for cleanup.</summary>
    internal async Task StopAsync()
    {
        _closed = true;
        _connectionStatus = "disconnected";
        _metrics?.RecordGauge("platform.event_connections", 0, unit: "connections");
        _cts.Cancel();

        if (_runTask is not null)
        {
            try
            {
                await _runTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch { /* Ignore timeout or cancellation */ }
        }

        _ownedHttpClient?.Dispose();
    }

    // ------------------------------------------------------------------
    // Event stream background loop
    // ------------------------------------------------------------------

    private async Task RunEventStreamAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_closed)
        {
            HttpResponseMessage? response = null;
            try
            {
                response = await ConnectAsync(ct).ConfigureAwait(false);
                OnConnectSucceeded();
                await ReadLoopAsync(response, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested || _closed)
            {
                _initialConnect.TrySetResult(false);
                break;
            }
            catch
            {
                _initialConnect.TrySetResult(false);
            }
            finally
            {
                response?.Dispose();
            }

            if (ct.IsCancellationRequested || _closed) break;

            _connectionStatus = "reconnecting";
            _metrics?.RecordGauge("platform.event_connections", 0, unit: "connections");
            int delay = _delayMs;
            _delayMs = Math.Min(_delayMs * 2, MaxDelayMs);
            Debug.Log("events", $"stream disconnected; reconnecting in {delay}ms");
            try
            {
                await DelayAsync(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    [ExcludeFromCodeCoverage]
    private Task<HttpResponseMessage> DefaultSendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // The default HttpClient timeout (100 s) would sever an idle long-lived
        // stream; liveness is enforced by our own read timeout instead.
        _ownedHttpClient ??= new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        return _ownedHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private string BuildEventsUrl()
    {
        string baseUrl = _appBaseUrl.StartsWith("https://", StringComparison.Ordinal)
            || _appBaseUrl.StartsWith("http://", StringComparison.Ordinal)
            ? _appBaseUrl
            : "https://" + _appBaseUrl;
        return baseUrl.TrimEnd('/') + EventsPath;
    }

    private HttpRequestMessage BuildRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, BuildEventsUrl());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        // CloudFront's WAF blocks requests that omit a User-Agent header.
        // HttpClient doesn't set one by default (browsers do), so we inject the
        // transport's effective User-Agent — the caller's own value when one
        // was supplied, else the SDK default — to match the User-Agent the
        // HTTP transport sends.
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        return request;
    }

    private async Task<HttpResponseMessage> ConnectAsync(CancellationToken ct)
    {
        _connectionStatus = "connecting";
        var response = await _sendAsync(BuildRequest(), ct).ConfigureAwait(false);

        // A successful connect is an HTTP 200 with text/event-stream content.
        // Anything else (401 auth failure, proxy error page, ...) is a failed
        // attempt handled by the reconnect loop.
        var status = (int)response.StatusCode;
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (status != 200 || !string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            response.Dispose();
            throw new InvalidOperationException(
                $"Event stream connection failed: HTTP {status}, content-type \"{mediaType ?? "<none>"}\"");
        }
        return response;
    }

    private void OnConnectSucceeded()
    {
        _connectionStatus = "connected";
        _metrics?.RecordGauge("platform.event_connections", 1, unit: "connections");
        _delayMs = _retryBaseMs;
        bool isReconnect = _hasConnectedBefore;
        _hasConnectedBefore = true;
        _initialConnect.TrySetResult(true);
        Debug.Log("events", isReconnect ? "event stream reconnected" : "event stream connected");
        if (isReconnect)
            InvokeRefetchCallbacks();
    }

    private async Task ReadLoopAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        // StreamReader implements the SSE line semantics we need: \n, \r\n,
        // and \r all terminate a line, a leading UTF-8 BOM is stripped, and
        // values split across read-buffer boundaries are reassembled.
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        string? eventName = null;
        var dataLines = new List<string>();

        while (!_closed && !ct.IsCancellationRequested)
        {
            // Liveness: every line (including comment keepalives) re-arms the
            // read timeout; two missed keepalives cancel the read, and the run
            // loop reconnects.
            readCts.CancelAfter(ReadTimeout);
            var line = await reader.ReadLineAsync(readCts.Token).ConfigureAwait(false);
            if (line is null) break; // server closed the stream
            ProcessLine(line, ref eventName, dataLines);
        }
    }

    private void ProcessLine(string line, ref string? eventName, List<string> dataLines)
    {
        if (line.Length == 0)
        {
            // Blank line: dispatch the accumulated frame.
            DispatchPending(ref eventName, dataLines);
            return;
        }
        if (line[0] == ':')
            return; // comment frame (e.g. ": keepalive") — liveness only

        int idx = line.IndexOf(':');
        string field = idx < 0 ? line : line[..idx];
        string value = idx < 0 ? string.Empty : line[(idx + 1)..];
        if (value.StartsWith(' '))
            value = value[1..];

        switch (field)
        {
            case "event":
                eventName = value;
                break;
            case "data":
                dataLines.Add(value);
                break;
            case "retry":
                // Digits only per the SSE spec; anything else is ignored. The
                // server's value re-seeds the backoff base immediately.
                if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var retryMs))
                {
                    _retryBaseMs = retryMs;
                    _delayMs = retryMs;
                }
                break;
            default:
                break; // unknown field — ignored per the SSE spec
        }
    }

    private void DispatchPending(ref string? eventName, List<string> dataLines)
    {
        var name = eventName;
        eventName = null;
        if (dataLines.Count == 0)
            return; // nothing buffered (e.g. a retry:-only frame)
        var payload = string.Join("\n", dataLines);
        dataLines.Clear();
        if (string.IsNullOrEmpty(name))
            return; // the server always names its events; unnamed frames are dropped

        Dictionary<string, object?> data;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            data = new Dictionary<string, object?>();
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                    data[prop.Name] = Config.Resolver.Normalize(prop.Value);
            }
        }
        catch (JsonException)
        {
            Debug.Log("events", $"ignoring \"{name}\" event with malformed payload");
            return;
        }
        Dispatch(name, data);
    }
}
