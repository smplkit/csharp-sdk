using System.Net;
using System.Text;
using Smplkit.Internal;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Internal;

/// <summary>
/// Tests for <see cref="EventStream"/>: SSE parsing (line terminators, BOM,
/// split reads, multiple data lines, comments, unknown fields/events), the
/// reconnect loop (backoff seeding/doubling/reset, refetch-on-reconnect,
/// liveness read timeout), request shape, and lifecycle (Start/StopAsync).
/// </summary>
public class EventStreamTests
{
    // Sentinel event appended to the pushed frames so tests can deterministically
    // wait for prior frames to be processed instead of using bare Task.Delay.
    // Dispatch is FIFO, so when the sentinel handler fires, every earlier frame
    // has already been dispatched and processed.
    private const string SyncEvent = "__test_sync__";
    private const string SyncFrame = "event: __test_sync__\ndata: {}\n\n";
    private static readonly TimeSpan SyncTimeout = TimeSpan.FromSeconds(5);

    private static HttpResponseMessage SseResponse(SsePushStream stream)
        => SseTestServer.CreateSseResponse(stream);

    /// <summary>Build an EventStream against the fake server with instant reconnect delays.</summary>
    private static EventStream Make(SseTestServer server, string apiKey = "sk_api_test")
    {
        var es = new EventStream(apiKey, server.SendAsync);
        es.DelayAsync = (_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        return es;
    }

    private static TaskCompletionSource RegisterSyncListener(EventStream es)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        es.On(SyncEvent, _ => tcs.TrySetResult());
        return tcs;
    }

    /// <summary>Run one always-open stream, push the given frames, await the sentinel, stop.</summary>
    private static async Task<List<Dictionary<string, object?>>> RunFramesAsync(
        string frames, string listenEvent)
    {
        var received = new List<Dictionary<string, object?>>();
        var stream = new SsePushStream();
        var server = new SseTestServer(_ => SseResponse(stream));
        var es = Make(server);
        es.On(listenEvent, data => received.Add(data));
        var synced = RegisterSyncListener(es);

        es.Start();
        stream.Push(frames);
        stream.Push(SyncFrame);
        await synced.Task.WaitAsync(SyncTimeout);
        await es.StopAsync();
        return received;
    }

    // ---------------------------------------------------------------
    // ConnectionStatus / RequestUserAgent
    // ---------------------------------------------------------------

    [Fact]
    public void ConnectionStatus_ReturnsDisconnected_Initially()
    {
        var es = new EventStream("test-key");
        Assert.Equal("disconnected", es.ConnectionStatus);
    }

    [Fact]
    public void RequestUserAgent_DefaultsToSdkUserAgent()
    {
        var es = new EventStream("test-key");
        Assert.Equal(SdkVersion.UserAgent, es.RequestUserAgent);
        Assert.StartsWith("smplkit-sdk-csharp/", es.RequestUserAgent, StringComparison.Ordinal);
    }

    [Fact]
    public void RequestUserAgent_UsesCallerSuppliedValue()
    {
        var es = new EventStream("test-key", userAgent: "caller-agent/7.7");
        Assert.Equal("caller-agent/7.7", es.RequestUserAgent);
    }

    // ---------------------------------------------------------------
    // Request shape
    // ---------------------------------------------------------------

    [Fact]
    public async Task Request_CarriesBearerAuthAcceptAndUserAgent_NeverLastEventId()
    {
        var stream = new SsePushStream();
        var server = new SseTestServer(_ => SseResponse(stream));
        var es = Make(server, apiKey: "sk_api_abc123");

        es.Start();
        await es.WaitForInitialConnectAsync().WaitAsync(SyncTimeout);
        await es.StopAsync();

        HttpRequestMessage request;
        lock (server.Requests) request = server.Requests[0];
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://app.smplkit.com/api/v1/events", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("sk_api_abc123", request.Headers.Authorization!.Parameter);
        Assert.Contains(request.Headers.Accept, a => a.MediaType == "text/event-stream");
        Assert.Equal(SdkVersion.UserAgent, string.Join(" ", request.Headers.GetValues("User-Agent")));
        // Resume is intentionally not implemented — Last-Event-ID must never be sent.
        Assert.False(request.Headers.Contains("Last-Event-ID"));
    }

    [Fact]
    public async Task BuildEventsUrl_UsesCustomAppBaseUrl_AndTrimsTrailingSlash()
    {
        var stream = new SsePushStream();
        var server = new SseTestServer(_ => SseResponse(stream));
        var es = new EventStream("test-key", server.SendAsync,
            appBaseUrl: "https://app.internal.example.com/");

        es.Start();
        await es.WaitForInitialConnectAsync().WaitAsync(SyncTimeout);
        await es.StopAsync();

        lock (server.Requests)
            Assert.Equal("https://app.internal.example.com/api/v1/events", server.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task BuildEventsUrl_HttpScheme_Preserved()
    {
        var stream = new SsePushStream();
        var server = new SseTestServer(_ => SseResponse(stream));
        var es = new EventStream("test-key", server.SendAsync,
            appBaseUrl: "http://app.localhost:8000");

        es.Start();
        await es.WaitForInitialConnectAsync().WaitAsync(SyncTimeout);
        await es.StopAsync();

        lock (server.Requests)
            Assert.Equal("http://app.localhost:8000/api/v1/events", server.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task BuildEventsUrl_NoScheme_DefaultsToHttps()
    {
        var stream = new SsePushStream();
        var server = new SseTestServer(_ => SseResponse(stream));
        var es = new EventStream("test-key", server.SendAsync,
            appBaseUrl: "app.internal.example.com");

        es.Start();
        await es.WaitForInitialConnectAsync().WaitAsync(SyncTimeout);
        await es.StopAsync();

        lock (server.Requests)
            Assert.Equal("https://app.internal.example.com/api/v1/events", server.Requests[0].RequestUri!.ToString());
    }

    // ---------------------------------------------------------------
    // Dispatch / On / Off
    // ---------------------------------------------------------------

    [Fact]
    public async Task Dispatch_EventFrame_FiresListeners()
    {
        var received = await RunFramesAsync(
            "event: flag_changed\ndata: {\"id\": \"test-flag\"}\n\n", "flag_changed");

        Assert.Single(received);
        Assert.Equal("test-flag", received[0]["id"]?.ToString());
    }

    [Fact]
    public async Task Dispatch_ConnectedEvent_HasNoHandler_Ignored()
    {
        // The server opens every stream with `event: connected` / `data: {}`;
        // no module subscribes to it and it must be silently ignored.
        var received = await RunFramesAsync(
            "retry: 1000\n\nevent: connected\ndata: {}\n\nevent: flag_changed\ndata: {\"id\": \"k\"}\n\n",
            "flag_changed");

        Assert.Single(received);
        Assert.Equal("k", received[0]["id"]?.ToString());
    }

    [Fact]
    public async Task Off_RemovesListener()
    {
        var events = new List<Dictionary<string, object?>>();
        void Handler(Dictionary<string, object?> data) => events.Add(data);

        var stream = new SsePushStream();
        var server = new SseTestServer(_ => SseResponse(stream));
        var es = Make(server);

        es.On("flag_changed", Handler);
        es.Off("flag_changed", Handler);
        var synced = RegisterSyncListener(es);

        es.Start();
        stream.Push("event: flag_changed\ndata: {\"id\": \"x\"}\n\n");
        stream.Push(SyncFrame);
        await synced.Task.WaitAsync(SyncTimeout);
        await es.StopAsync();

        Assert.Empty(events);
    }

    [Fact]
    public void Off_NonExistentEvent_DoesNotThrow()
    {
        var es = new EventStream("test-key");
        es.Off("nonexistent", _ => { });
        // No crash
    }

    [Fact]
    public async Task Dispatch_ListenerThrows_DoesNotPropagate()
    {
        var secondFired = false;
        var stream = new SsePushStream();
        var server = new SseTestServer(_ => SseResponse(stream));
        var es = Make(server);

        es.On("flag_changed", _ => throw new InvalidOperationException("boom"));
        es.On("flag_changed", _ => secondFired = true);
        var synced = RegisterSyncListener(es);

        es.Start();
        stream.Push("event: flag_changed\ndata: {\"id\": \"x\"}\n\n");
        stream.Push(SyncFrame);
        await synced.Task.WaitAsync(SyncTimeout);
        await es.StopAsync();

        Assert.True(secondFired);
    }

    [Fact]
    public async Task Dispatch_UnknownEventName_SilentlyIgnored()
    {
        var received = await RunFramesAsync(
            "event: mystery_event\ndata: {\"id\": \"x\"}\n\nevent: flag_changed\ndata: {\"id\": \"y\"}\n\n",
            "flag_changed");

        Assert.Single(received);
        Assert.Equal("y", received[0]["id"]?.ToString());
    }

    // ---------------------------------------------------------------
    // SSE parsing edge cases
    // ---------------------------------------------------------------

    [Fact]
    public async Task Parser_CrLfTerminators_Handled()
    {
        var received = await RunFramesAsync(
            "event: flag_changed\r\ndata: {\"id\": \"crlf\"}\r\n\r\n", "flag_changed");

        Assert.Single(received);
        Assert.Equal("crlf", received[0]["id"]?.ToString());
    }

    [Fact]
    public async Task Parser_BareCrTerminators_Handled()
    {
        var received = await RunFramesAsync(
            "event: flag_changed\rdata: {\"id\": \"cr\"}\r\r", "flag_changed");

        Assert.Single(received);
        Assert.Equal("cr", received[0]["id"]?.ToString());
    }

    [Fact]
    public async Task Parser_LeadingBom_Stripped()
    {
        var received = new List<Dictionary<string, object?>>();
        var stream = new SsePushStream();
        var server = new SseTestServer(_ => SseResponse(stream));
        var es = Make(server);
        es.On("flag_changed", data => received.Add(data));
        var synced = RegisterSyncListener(es);

        es.Start();
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var frame = Encoding.UTF8.GetBytes("event: flag_changed\ndata: {\"id\": \"bom\"}\n\n");
        stream.Push(bom.Concat(frame).ToArray());
        stream.Push(SyncFrame);
        await synced.Task.WaitAsync(SyncTimeout);
        await es.StopAsync();

        Assert.Single(received);
        Assert.Equal("bom", received[0]["id"]?.ToString());
    }

    [Fact]
    public async Task Parser_FieldValueSplitAcrossReads_Reassembled()
    {
        var received = new List<Dictionary<string, object?>>();
        var stream = new SsePushStream();
        var server = new SseTestServer(_ => SseResponse(stream));
        var es = Make(server);
        es.On("flag_changed", data => received.Add(data));
        var synced = RegisterSyncListener(es);

        es.Start();
        // The event name and the data value are each split mid-token across
        // separate pushes (separate reads on the wire).
        stream.Push("even");
        stream.Push("t: flag_chan");
        stream.Push("ged\ndata: {\"id\": \"spl");
        stream.Push("it\"}\n\n");
        stream.Push(SyncFrame);
        await synced.Task.WaitAsync(SyncTimeout);
        await es.StopAsync();

        Assert.Single(received);
        Assert.Equal("split", received[0]["id"]?.ToString());
    }

    [Fact]
    public async Task Parser_MultipleDataLines_JoinedWithNewline()
    {
        // The JSON payload is legal only if the two data: lines are joined
        // with "\n" per the SSE spec.
        var received = await RunFramesAsync(
            "event: flag_changed\ndata: {\"id\":\ndata: \"multi\"}\n\n", "flag_changed");

        Assert.Single(received);
        Assert.Equal("multi", received[0]["id"]?.ToString());
    }

    [Fact]
    public async Task Parser_CommentLines_Ignored()
    {
        var received = await RunFramesAsync(
            ": keepalive\nevent: flag_changed\n: comment between fields\ndata: {\"id\": \"c\"}\n\n: keepalive\n",
            "flag_changed");

        Assert.Single(received);
        Assert.Equal("c", received[0]["id"]?.ToString());
    }

    [Fact]
    public async Task Parser_UnknownFields_Ignored()
    {
        var received = await RunFramesAsync(
            "id: 42\nfoo: bar\nevent: flag_changed\ndata: {\"id\": \"u\"}\n\n", "flag_changed");

        Assert.Single(received);
        Assert.Equal("u", received[0]["id"]?.ToString());
    }

    [Fact]
    public async Task Parser_NoColonLine_TreatedAsFieldWithEmptyValue()
    {
        // "data" alone is a data field with an empty value (SSE spec); the
        // resulting empty payload is not valid JSON and the frame is dropped,
        // without disturbing subsequent frames.
        var received = await RunFramesAsync(
            "event: flag_changed\ndata\n\nevent: flag_changed\ndata: {\"id\": \"after\"}\n\n",
            "flag_changed");

        Assert.Single(received);
        Assert.Equal("after", received[0]["id"]?.ToString());
    }

    [Fact]
    public async Task Parser_NoSpaceAfterColon_Handled()
    {
        var received = await RunFramesAsync(
            "event:flag_changed\ndata:{\"id\": \"nospace\"}\n\n", "flag_changed");

        Assert.Single(received);
        Assert.Equal("nospace", received[0]["id"]?.ToString());
    }

    [Fact]
    public async Task Parser_MalformedJsonPayload_Dropped()
    {
        var received = await RunFramesAsync(
            "event: flag_changed\ndata: {not json\n\nevent: flag_changed\ndata: {\"id\": \"ok\"}\n\n",
            "flag_changed");

        Assert.Single(received);
        Assert.Equal("ok", received[0]["id"]?.ToString());
    }

    [Fact]
    public async Task Parser_NonObjectJsonPayload_DispatchesEmptyData()
    {
        var received = await RunFramesAsync(
            "event: flag_changed\ndata: 42\n\n", "flag_changed");

        Assert.Single(received);
        Assert.Empty(received[0]);
    }

    [Fact]
    public async Task Parser_EventFrameWithoutData_NotDispatched()
    {
        var received = await RunFramesAsync(
            "event: flag_changed\n\nevent: flag_changed\ndata: {\"id\": \"withdata\"}\n\n",
            "flag_changed");

        Assert.Single(received);
        Assert.Equal("withdata", received[0]["id"]?.ToString());
    }

    [Fact]
    public async Task Parser_DataWithoutEventName_Dropped()
    {
        var received = await RunFramesAsync(
            "data: {\"id\": \"orphan\"}\n\nevent: flag_changed\ndata: {\"id\": \"named\"}\n\n",
            "flag_changed");

        Assert.Single(received);
        Assert.Equal("named", received[0]["id"]?.ToString());
    }

    // ---------------------------------------------------------------
    // Backoff: seeding, doubling, reset on successful connect
    // ---------------------------------------------------------------

    [Fact]
    public async Task Backoff_ResetsToBase_AfterSuccessfulConnect()
    {
        // Attempts 1 and 2 fail; attempt 3 connects then the server closes the
        // stream; attempt 4 fails; attempt 5 connects and stays open. The
        // observed delays must double while failing and drop back to base
        // (1000 ms — no retry: frame is sent here) after each successful
        // connect.
        var delays = new List<int>();
        var statusAtDelay = new List<string>();
        SsePushStream? stream3 = null;
        var stream5 = new SsePushStream();
        var attempt3Served = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempt5Connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        HttpResponseMessage ServeAttempt3()
        {
            stream3 = new SsePushStream();
            var response = SseResponse(stream3);
            attempt3Served.TrySetResult();
            return response;
        }

        var server = new SseTestServer(n => n switch
        {
            1 or 2 or 4 => throw new HttpRequestException($"connect failure {n}"),
            3 => ServeAttempt3(),
            _ => SseResponse(stream5),
        });

        var es = new EventStream("test-key", server.SendAsync);
        es.DelayAsync = (ms, ct) =>
        {
            lock (delays)
            {
                delays.Add(ms);
                statusAtDelay.Add(es.ConnectionStatus);
            }
            return Task.CompletedTask;
        };
        es.On(SyncEvent, _ => attempt5Connected.TrySetResult());

        es.Start();
        // WaitForInitialConnectAsync resolves on the FIRST attempt (a failure
        // in this scenario), so wait for attempt 3 to be served instead, then
        // close its stream so the loop reconnects.
        await attempt3Served.Task.WaitAsync(SyncTimeout);
        stream3!.Complete();

        // Attempt 5: prove it is live by pushing the sentinel through it.
        stream5.Push(SyncFrame);
        await attempt5Connected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await es.StopAsync();

        List<int> observed;
        lock (delays) observed = new List<int>(delays);
        // fail, fail, success+close, fail, success:
        //   1000 (base), 2000 (doubled), 1000 (RESET after success), 2000 (doubled)
        Assert.Equal(new[] { 1000, 2000, 1000, 2000 }, observed.Take(4).ToArray());
        Assert.All(statusAtDelay, s => Assert.Equal("reconnecting", s));
    }

    [Fact]
    public async Task Backoff_SeededFromServerRetryValue()
    {
        // Attempt 1 sends `retry: 5` and closes; the reconnect delay must be
        // the server-seeded 5 ms. This test deliberately keeps the production
        // DelayAsync (a real Task.Delay) to prove the loop sleeps for real.
        var stream2 = new SsePushStream();
        var connected2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var stream1 = new SsePushStream();
        var server = new SseTestServer(n => n == 1 ? SseResponse(stream1) : SseResponse(stream2));

        var es = new EventStream("test-key", server.SendAsync);
        es.On(SyncEvent, _ => connected2.TrySetResult());

        es.Start();
        stream1.Push("retry: 5\n\n");
        stream1.Complete();

        stream2.Push(SyncFrame);
        await connected2.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await es.StopAsync();

        Assert.Equal(5, es._retryBaseMs);
        Assert.True(server.Attempts >= 2);
    }

    [Fact]
    public async Task Backoff_InvalidRetryValues_Ignored()
    {
        var stream = new SsePushStream();
        var server = new SseTestServer(_ => SseResponse(stream));
        var es = Make(server);
        var synced = RegisterSyncListener(es);

        es.Start();
        stream.Push("retry: abc\nretry: -5\nretry: 1 0\n\n");
        stream.Push(SyncFrame);
        await synced.Task.WaitAsync(SyncTimeout);
        await es.StopAsync();

        Assert.Equal(1000, es._retryBaseMs);
    }

    [Fact]
    public async Task Backoff_DoublingIsCappedAt60Seconds()
    {
        var delays = new List<int>();
        var enoughDelays = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new SseTestServer(_ => throw new HttpRequestException("always down"));
        var es = new EventStream("test-key", server.SendAsync);
        es.DelayAsync = (ms, ct) =>
        {
            lock (delays)
            {
                delays.Add(ms);
                if (delays.Count >= 9) enoughDelays.TrySetResult();
            }
            return Task.CompletedTask;
        };

        es.Start();
        await enoughDelays.Task.WaitAsync(SyncTimeout);
        await es.StopAsync();

        List<int> observed;
        lock (delays) observed = delays.Take(9).ToList();
        Assert.Equal(new[] { 1000, 2000, 4000, 8000, 16000, 32000, 60000, 60000, 60000 }, observed);
    }

    // ---------------------------------------------------------------
    // Refetch on reconnect
    // ---------------------------------------------------------------

    [Fact]
    public async Task Reconnect_TriggersRefetch_ButNotOnInitialConnect()
    {
        var refetchCount = 0;
        var refetched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var streams = new List<SsePushStream>();
        var server = new SseTestServer(_ =>
        {
            var s = new SsePushStream();
            lock (streams) streams.Add(s);
            return SseResponse(s);
        });
        var es = Make(server);
        es.OnReconnect(() =>
        {
            Interlocked.Increment(ref refetchCount);
            refetched.TrySetResult();
        });

        es.Start();
        await es.WaitForInitialConnectAsync().WaitAsync(SyncTimeout);
        // Initial connect must NOT refetch.
        Assert.Equal(0, Volatile.Read(ref refetchCount));

        // Drop the stream; the reconnect must refetch exactly once.
        lock (streams) streams[0].Complete();
        await refetched.Task.WaitAsync(SyncTimeout);
        await es.StopAsync();

        Assert.Equal(1, Volatile.Read(ref refetchCount));
    }

    [Fact]
    public async Task OffReconnect_UnregistersCallback()
    {
        var removedCount = 0;
        var keptFired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Removed() => Interlocked.Increment(ref removedCount);

        var streams = new List<SsePushStream>();
        var server = new SseTestServer(_ =>
        {
            var s = new SsePushStream();
            lock (streams) streams.Add(s);
            return SseResponse(s);
        });
        var es = Make(server);
        es.OnReconnect(Removed);
        es.OffReconnect(Removed);
        es.OnReconnect(() => keptFired.TrySetResult());

        es.Start();
        await es.WaitForInitialConnectAsync().WaitAsync(SyncTimeout);
        lock (streams) streams[0].Complete();
        await keptFired.Task.WaitAsync(SyncTimeout);
        await es.StopAsync();

        Assert.Equal(0, Volatile.Read(ref removedCount));
    }

    [Fact]
    public async Task Reconnect_RefetchCallbackThrows_DoesNotPropagate()
    {
        var secondFired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var streams = new List<SsePushStream>();
        var server = new SseTestServer(_ =>
        {
            var s = new SsePushStream();
            lock (streams) streams.Add(s);
            return SseResponse(s);
        });
        var es = Make(server);
        es.OnReconnect(() => throw new InvalidOperationException("refetch boom"));
        es.OnReconnect(() => secondFired.TrySetResult());

        es.Start();
        await es.WaitForInitialConnectAsync().WaitAsync(SyncTimeout);
        lock (streams) streams[0].Complete();
        await secondFired.Task.WaitAsync(SyncTimeout);
        await es.StopAsync();
    }

    // ---------------------------------------------------------------
    // Liveness read timeout
    // ---------------------------------------------------------------

    [Fact]
    public async Task Liveness_SilentStream_TimesOutAndReconnects()
    {
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new SseTestServer(n =>
        {
            if (n >= 2) reconnected.TrySetResult();
            return SseResponse(new SsePushStream()); // never pushes anything
        });
        var es = Make(server);
        es.ReadTimeout = TimeSpan.FromMilliseconds(100);

        es.Start();
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await es.StopAsync();

        Assert.True(server.Attempts >= 2);
    }

    [Fact]
    public async Task Liveness_CommentFrames_CountAsLiveness()
    {
        // Comments arrive every ~300 ms for well past the 1.5 s read timeout;
        // the connection must stay up (a single connect attempt) because every
        // comment line re-arms the timer. A real event afterwards proves the
        // original stream is still being read.
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new SsePushStream();
        var server = new SseTestServer(_ => SseResponse(stream));
        var es = Make(server);
        es.ReadTimeout = TimeSpan.FromMilliseconds(1500);
        es.On("flag_changed", _ => received.TrySetResult());

        es.Start();
        for (int i = 0; i < 8; i++)
        {
            stream.Push(": keepalive\n");
            await Task.Delay(300);
        }
        stream.Push("event: flag_changed\ndata: {}\n\n");
        await received.Task.WaitAsync(SyncTimeout);
        await es.StopAsync();

        Assert.Equal(1, server.Attempts);
    }

    // ---------------------------------------------------------------
    // Connect failures (HTTP status / content type)
    // ---------------------------------------------------------------

    [Fact]
    public async Task Connect_Http401_RetriesWithBackoff()
    {
        // Auth failure is a plain HTTP 401 — no handshake message, no close
        // codes. The stream treats it like any failed attempt and retries.
        var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new SseTestServer(n =>
        {
            if (n >= 2) secondAttempt.TrySetResult();
            return new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"errors":[{"status":"401"}]}""", Encoding.UTF8, "application/json"),
            };
        });
        var es = Make(server);

        es.Start();
        await es.WaitForInitialConnectAsync().WaitAsync(SyncTimeout);
        await secondAttempt.Task.WaitAsync(SyncTimeout);
        await es.StopAsync();

        Assert.NotEqual("connected", es.ConnectionStatus);
        Assert.True(server.Attempts >= 2);
    }

    [Fact]
    public async Task Connect_WrongContentType_RetriesWithBackoff()
    {
        var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new SseTestServer(n =>
        {
            if (n >= 2) secondAttempt.TrySetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html>proxy error</html>", Encoding.UTF8, "text/html"),
            };
        });
        var es = Make(server);

        es.Start();
        await secondAttempt.Task.WaitAsync(SyncTimeout);
        await es.StopAsync();

        Assert.True(server.Attempts >= 2);
    }

    [Fact]
    public async Task Connect_MissingContentType_RetriesWithBackoff()
    {
        var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new SseTestServer(n =>
        {
            if (n >= 2) secondAttempt.TrySetResult();
            var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
            resp.Content.Headers.ContentType = null;
            return resp;
        });
        var es = Make(server);

        es.Start();
        await secondAttempt.Task.WaitAsync(SyncTimeout);
        await es.StopAsync();

        Assert.True(server.Attempts >= 2);
    }

    // ---------------------------------------------------------------
    // Lifecycle: Start / StopAsync / WaitForInitialConnectAsync
    // ---------------------------------------------------------------

    [Fact]
    public async Task ConnectAsync_SetsStatusToConnected()
    {
        var stream = new SsePushStream();
        var server = new SseTestServer(_ => SseResponse(stream));
        var es = Make(server);

        es.Start();
        await es.WaitForInitialConnectAsync().WaitAsync(SyncTimeout);
        Assert.Equal("connected", es.ConnectionStatus);
        await es.StopAsync();
        Assert.Equal("disconnected", es.ConnectionStatus);
    }

    [Fact]
    public async Task StopAsync_NeverStarted_DoesNotThrow()
    {
        var es = new EventStream("test-key");
        await es.StopAsync();
        Assert.Equal("disconnected", es.ConnectionStatus);
    }

    [Fact]
    public async Task StopAsync_DuringBackoffDelay_BreaksLoop()
    {
        var delayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new SseTestServer(_ => throw new HttpRequestException("down"));
        var es = new EventStream("test-key", server.SendAsync);
        es.DelayAsync = async (ms, ct) =>
        {
            delayEntered.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(60), ct);
        };

        es.Start();
        await delayEntered.Task.WaitAsync(SyncTimeout);
        await es.StopAsync(); // must cancel the pending delay and return promptly

        Assert.Equal("disconnected", es.ConnectionStatus);
    }

    [Fact]
    public async Task StopAsync_RunTaskHangs_SwallowsTimeout()
    {
        // A send that never completes and ignores cancellation forces the
        // 2-second StopAsync wait to time out; StopAsync must not throw.
        var sendEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var es = new EventStream("test-key", (_, _) =>
        {
            sendEntered.TrySetResult();
            return new TaskCompletionSource<HttpResponseMessage>().Task;
        });

        es.Start();
        // Ensure the run task is actually stuck inside the send before
        // stopping — otherwise cancellation can win before the loop starts.
        await sendEntered.Task.WaitAsync(SyncTimeout);
        await es.StopAsync();
        Assert.Equal("disconnected", es.ConnectionStatus);
    }

    [Fact]
    public async Task WaitForInitialConnectAsync_SupportsCancellation()
    {
        var es = new EventStream("test-key", async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            throw new OperationCanceledException(ct);
        });

        es.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => es.WaitForInitialConnectAsync(cts.Token));

        await es.StopAsync();
    }

    [Fact]
    public async Task WaitForInitialConnectAsync_Resolves_WhenConnectionFails()
    {
        var server = new SseTestServer(_ => throw new HttpRequestException("no route"));
        var es = new EventStream("test-key", server.SendAsync);
        es.DelayAsync = (_, ct) => Task.Delay(1, ct);

        es.Start();
        // Resolves (false) after the first failed attempt instead of hanging.
        await es.WaitForInitialConnectAsync().WaitAsync(SyncTimeout);
        await es.StopAsync();

        Assert.True(server.Attempts >= 1);
    }

    [Fact]
    public async Task ServerClosesStream_Reconnects()
    {
        var streams = new List<SsePushStream>();
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new SseTestServer(n =>
        {
            if (n >= 2) reconnected.TrySetResult();
            var s = new SsePushStream();
            lock (streams) streams.Add(s);
            return SseResponse(s);
        });
        var es = Make(server);

        es.Start();
        await es.WaitForInitialConnectAsync().WaitAsync(SyncTimeout);
        lock (streams) streams[0].Complete();
        await reconnected.Task.WaitAsync(SyncTimeout);
        await es.StopAsync();

        Assert.True(server.Attempts >= 2);
    }
}
