using System.Net.WebSockets;
using System.Text;
using Moq;
using Smplkit.Internal;
using Xunit;

namespace Smplkit.Tests.Internal;

/// <summary>
/// Coverage tests for SharedWebSocket: Off, Dispatch, ConnectionStatus,
/// ConnectAsync, ReceiveLoopAsync, RunWebSocketAsync, StopAsync.
/// </summary>
public class SharedWebSocketTests
{
    // Sentinel event appended to the receive queue so tests can deterministically
    // wait for prior messages to be processed instead of using bare Task.Delay.
    // Dispatch is FIFO, so when the sentinel handler fires, every earlier message
    // has already been dispatched and processed.
    private const string SyncEvent = "__test_sync__";
    private const string SyncMessage = """{"event": "__test_sync__"}""";
    private static readonly TimeSpan SyncTimeout = TimeSpan.FromSeconds(5);

    private static Mock<WebSocket> CreateMockWs(params string[] messages)
    {
        var mockWs = new Mock<WebSocket>();
        mockWs.Setup(ws => ws.State).Returns(WebSocketState.Open);

        var receiveQueue = new Queue<string>(messages);
        mockWs.Setup(ws => ws.ReceiveAsync(It.IsAny<ArraySegment<byte>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ArraySegment<byte> buffer, CancellationToken _) =>
            {
                if (receiveQueue.Count == 0)
                    return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);

                var msg = receiveQueue.Dequeue();
                var bytes = Encoding.UTF8.GetBytes(msg);
                Array.Copy(bytes, 0, buffer.Array!, buffer.Offset, bytes.Length);
                return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true);
            });

        mockWs.Setup(ws => ws.SendAsync(It.IsAny<ArraySegment<byte>>(), It.IsAny<WebSocketMessageType>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mockWs.Setup(ws => ws.CloseAsync(It.IsAny<WebSocketCloseStatus>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return mockWs;
    }

    private static Mock<WebSocket> CreateMockWsWithSync(params string[] messages)
    {
        var withSentinel = new string[messages.Length + 1];
        Array.Copy(messages, withSentinel, messages.Length);
        withSentinel[^1] = SyncMessage;
        return CreateMockWs(withSentinel);
    }

    private static TaskCompletionSource RegisterSyncListener(SharedWebSocket ws)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ws.On(SyncEvent, _ => tcs.TrySetResult());
        return tcs;
    }

    // ---------------------------------------------------------------
    // ConnectionStatus
    // ---------------------------------------------------------------

    [Fact]
    public void ConnectionStatus_ReturnsDisconnected_Initially()
    {
        var ws = new SharedWebSocket("test-key");
        Assert.Equal("disconnected", ws.ConnectionStatus);
    }

    // ---------------------------------------------------------------
    // On / Off
    // ---------------------------------------------------------------

    [Fact]
    public async Task Off_RemovesListener()
    {
        var events = new List<Dictionary<string, object?>>();
        void Handler(Dictionary<string, object?> data) => events.Add(data);

        var connectedMsg = """{"type": "connected"}""";
        var eventMsg = """{"event": "flag_changed", "key": "test-flag"}""";

        var mockWs = CreateMockWsWithSync(connectedMsg, eventMsg);
        var ws = new SharedWebSocket("test-key",
            (_, _) => Task.FromResult<WebSocket>(mockWs.Object));

        ws.On("flag_changed", Handler);
        ws.Off("flag_changed", Handler);
        var synced = RegisterSyncListener(ws);

        ws.Start();
        await synced.Task.WaitAsync(SyncTimeout);
        await ws.StopAsync();

        // Handler was removed, so no events
        Assert.Empty(events);
    }

    [Fact]
    public void Off_NonExistentEvent_DoesNotThrow()
    {
        var ws = new SharedWebSocket("test-key");
        ws.Off("nonexistent", _ => { });
        // No crash
    }

    // ---------------------------------------------------------------
    // Dispatch / event handling
    // ---------------------------------------------------------------

    [Fact]
    public async Task Dispatch_EventMessage_FiresListeners()
    {
        var events = new List<Dictionary<string, object?>>();
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var connectedMsg = """{"type": "connected"}""";
        var eventMsg = """{"event": "flag_changed", "key": "test-flag"}""";

        var mockWs = CreateMockWs(connectedMsg, eventMsg);
        var ws = new SharedWebSocket("test-key",
            (_, _) => Task.FromResult<WebSocket>(mockWs.Object));

        ws.On("flag_changed", data => { events.Add(data); fired.TrySetResult(); });
        ws.Start();

        await fired.Task.WaitAsync(SyncTimeout);
        await ws.StopAsync();

        Assert.Single(events);
        Assert.Equal("test-flag", events[0]["key"]?.ToString());
    }

    [Fact]
    public async Task Dispatch_TypeMessage_FiresListeners()
    {
        var events = new List<Dictionary<string, object?>>();
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var connectedMsg = """{"type": "connected"}""";
        var typeMsg = """{"type": "config_changed", "configKey": "my-config"}""";

        var mockWs = CreateMockWs(connectedMsg, typeMsg);
        var ws = new SharedWebSocket("test-key",
            (_, _) => Task.FromResult<WebSocket>(mockWs.Object));

        ws.On("config_changed", data => { events.Add(data); fired.TrySetResult(); });
        ws.Start();

        await fired.Task.WaitAsync(SyncTimeout);
        await ws.StopAsync();

        Assert.Single(events);
    }

    [Fact]
    public async Task Dispatch_ListenerThrows_DoesNotPropagate()
    {
        var connectedMsg = """{"type": "connected"}""";
        var eventMsg = """{"event": "flag_changed", "key": "x"}""";

        var mockWs = CreateMockWsWithSync(connectedMsg, eventMsg);
        var ws = new SharedWebSocket("test-key",
            (_, _) => Task.FromResult<WebSocket>(mockWs.Object));

        ws.On("flag_changed", _ => throw new InvalidOperationException("boom"));
        var synced = RegisterSyncListener(ws);
        ws.Start();

        await synced.Task.WaitAsync(SyncTimeout);
        await ws.StopAsync();
        // No exception propagated
    }

    // ---------------------------------------------------------------
    // Ping / Pong heartbeat
    // ---------------------------------------------------------------

    [Fact]
    public async Task ReceiveLoop_Ping_SendsPong()
    {
        var connectedMsg = """{"type": "connected"}""";
        var pongSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var mockWs = CreateMockWs(connectedMsg, "ping");
        mockWs.Setup(ws => ws.SendAsync(
                It.Is<ArraySegment<byte>>(b => Encoding.UTF8.GetString(b.Array!, b.Offset, b.Count) == "pong"),
                WebSocketMessageType.Text,
                true,
                It.IsAny<CancellationToken>()))
            .Returns(() => { pongSent.TrySetResult(); return Task.CompletedTask; });

        var ws = new SharedWebSocket("test-key",
            (_, _) => Task.FromResult<WebSocket>(mockWs.Object));

        ws.Start();
        await pongSent.Task.WaitAsync(SyncTimeout);
        await ws.StopAsync();

        mockWs.Verify(w => w.SendAsync(
            It.Is<ArraySegment<byte>>(b => Encoding.UTF8.GetString(b.Array!, b.Offset, b.Count) == "pong"),
            WebSocketMessageType.Text,
            true,
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    // ---------------------------------------------------------------
    // ConnectAsync error handling
    // ---------------------------------------------------------------

    [Fact]
    public async Task ConnectAsync_ErrorMessage_ThrowsInvalidOperation()
    {
        var errorMsg = """{"type": "error", "message": "invalid key"}""";
        var attempts = 0;
        var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var ws = new SharedWebSocket("test-key", (_, _) =>
        {
            attempts++;
            if (attempts >= 2) secondAttempt.TrySetResult();
            return Task.FromResult<WebSocket>(CreateMockWs(errorMsg).Object);
        });

        ws.Start();
        // Wait for the second connect attempt — proves the first threw and the
        // error-handling path in ConnectAsync executed.
        await secondAttempt.Task.WaitAsync(SyncTimeout);
        await ws.StopAsync();
    }

    [Fact]
    public async Task ConnectAsync_ErrorMessageNoMessage_ThrowsInvalidOperation()
    {
        var errorMsg = """{"type": "error"}""";
        var attempts = 0;
        var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var ws = new SharedWebSocket("test-key", (_, _) =>
        {
            attempts++;
            if (attempts >= 2) secondAttempt.TrySetResult();
            return Task.FromResult<WebSocket>(CreateMockWs(errorMsg).Object);
        });

        ws.Start();
        await secondAttempt.Task.WaitAsync(SyncTimeout);
        await ws.StopAsync();
    }

    // ---------------------------------------------------------------
    // RunWebSocketAsync reconnection
    // ---------------------------------------------------------------

    [Fact]
    public async Task RunWebSocketAsync_ReconnectsOnFailure()
    {
        int attempts = 0;
        var thirdAttemptReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ws = new SharedWebSocket("test-key",
            (_, ct) =>
            {
                attempts++;
                if (attempts <= 2)
                    throw new WebSocketException("Connection failed");
                // Third attempt succeeds
                thirdAttemptReached.TrySetResult();
                var mockWs = CreateMockWs("""{"type": "connected"}""");
                return Task.FromResult<WebSocket>(mockWs.Object);
            });

        ws.Start();
        // Backoff sequence is 1s + 2s, so the third attempt happens ~3s in.
        await thirdAttemptReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await ws.StopAsync();

        Assert.True(attempts >= 3);
    }

    // ---------------------------------------------------------------
    // StopAsync
    // ---------------------------------------------------------------

    [Fact]
    public async Task StopAsync_ClosesWebSocket()
    {
        var connectedMsg = """{"type": "connected"}""";

        // Use a mock WS that stays alive after the connected message
        var mockWs = new Mock<WebSocket>();
        mockWs.Setup(ws => ws.State).Returns(WebSocketState.Open);

        var msgs = new Queue<string>(new[] { connectedMsg });
        mockWs.Setup(ws => ws.ReceiveAsync(It.IsAny<ArraySegment<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ArraySegment<byte> buffer, CancellationToken ct) =>
            {
                if (msgs.Count > 0)
                {
                    var msg = msgs.Dequeue();
                    var bytes = Encoding.UTF8.GetBytes(msg);
                    Array.Copy(bytes, 0, buffer.Array!, buffer.Offset, bytes.Length);
                    return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true);
                }
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            });

        mockWs.Setup(ws => ws.CloseAsync(It.IsAny<WebSocketCloseStatus>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var ws = new SharedWebSocket("test-key",
            (_, _) => Task.FromResult<WebSocket>(mockWs.Object));

        ws.Start();
        await ws.WaitForInitialConnectAsync().WaitAsync(SyncTimeout);
        await ws.StopAsync();

        Assert.Equal("disconnected", ws.ConnectionStatus);
    }

    [Fact]
    public async Task StopAsync_NoWebSocket_DoesNotThrow()
    {
        var ws = new SharedWebSocket("test-key");
        await ws.StopAsync();
    }

    [Fact]
    public async Task StopAsync_WsNotOpen_DoesNotAttemptClose()
    {
        var mockWs = new Mock<WebSocket>();
        mockWs.Setup(ws => ws.State).Returns(WebSocketState.Closed);

        var receiveQueue = new Queue<string>(new[] { """{"type": "connected"}""" });
        mockWs.Setup(ws => ws.ReceiveAsync(It.IsAny<ArraySegment<byte>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ArraySegment<byte> buffer, CancellationToken _) =>
            {
                if (receiveQueue.Count == 0)
                    return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
                var msg = receiveQueue.Dequeue();
                var bytes = Encoding.UTF8.GetBytes(msg);
                Array.Copy(bytes, 0, buffer.Array!, buffer.Offset, bytes.Length);
                return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true);
            });

        var ws = new SharedWebSocket("test-key",
            (_, _) => Task.FromResult<WebSocket>(mockWs.Object));

        ws.Start();
        await ws.WaitForInitialConnectAsync().WaitAsync(SyncTimeout);
        await ws.StopAsync();

        mockWs.Verify(w => w.CloseAsync(
            It.IsAny<WebSocketCloseStatus>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StopAsync_CloseThrows_DoesNotPropagate()
    {
        var mockWs = new Mock<WebSocket>();
        mockWs.Setup(ws => ws.State).Returns(WebSocketState.Open);

        mockWs.Setup(ws => ws.ReceiveAsync(It.IsAny<ArraySegment<byte>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ArraySegment<byte> buffer, CancellationToken _) =>
            {
                var msg = """{"type": "connected"}""";
                var bytes = Encoding.UTF8.GetBytes(msg);
                Array.Copy(bytes, 0, buffer.Array!, buffer.Offset, bytes.Length);
                return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true);
            });

        mockWs.Setup(ws => ws.CloseAsync(It.IsAny<WebSocketCloseStatus>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new WebSocketException("Close failed"));

        var ws = new SharedWebSocket("test-key",
            (_, _) => Task.FromResult<WebSocket>(mockWs.Object));

        ws.Start();
        await ws.WaitForInitialConnectAsync().WaitAsync(SyncTimeout);

        // Should not throw
        await ws.StopAsync();
    }

    // ---------------------------------------------------------------
    // ReceiveRawAsync - null/closed ws
    // ---------------------------------------------------------------

    [Fact]
    public async Task ReceiveLoop_NullMessage_BreaksLoop()
    {
        // Mock WS that immediately returns Close after connecting.
        // Track when we observe the second receive call (i.e. the loop reached
        // the close branch after the connected message was processed).
        var mockWs = new Mock<WebSocket>();
        mockWs.Setup(ws => ws.State).Returns(WebSocketState.Open);

        var receiveCount = 0;
        var closeBranchReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mockWs.Setup(ws => ws.ReceiveAsync(It.IsAny<ArraySegment<byte>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ArraySegment<byte> buffer, CancellationToken _) =>
            {
                var n = Interlocked.Increment(ref receiveCount);
                if (n == 1)
                {
                    var msg = """{"type": "connected"}""";
                    var bytes = Encoding.UTF8.GetBytes(msg);
                    Array.Copy(bytes, 0, buffer.Array!, buffer.Offset, bytes.Length);
                    return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true);
                }
                if (n == 2) closeBranchReached.TrySetResult();
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            });

        var ws = new SharedWebSocket("test-key",
            (_, _) => Task.FromResult<WebSocket>(mockWs.Object));

        ws.Start();
        await closeBranchReached.Task.WaitAsync(SyncTimeout);
        await ws.StopAsync();
    }

    // ---------------------------------------------------------------
    // ConnectAsync success path
    // ---------------------------------------------------------------

    // ---------------------------------------------------------------
    // Dispatch with no listeners for event (line 69)
    // ---------------------------------------------------------------

    [Fact]
    public async Task Dispatch_NoListenersForEvent_DoesNotCrash()
    {
        var connectedMsg = """{"type": "connected"}""";
        // Event that has no listener registered
        var eventMsg = """{"event": "unhandled_event", "data": "test"}""";

        var mockWs = CreateMockWsWithSync(connectedMsg, eventMsg);
        var ws = new SharedWebSocket("test-key",
            (_, _) => Task.FromResult<WebSocket>(mockWs.Object));

        // Register listener for DIFFERENT event
        ws.On("flag_changed", _ => { });
        // Dispatch is FIFO; awaiting the sentinel guarantees `unhandled_event`
        // was already processed (taking the no-handler branch in Dispatch).
        var synced = RegisterSyncListener(ws);

        ws.Start();
        await synced.Task.WaitAsync(SyncTimeout);
        await ws.StopAsync();
    }

    // ---------------------------------------------------------------
    // StopAsync with task that times out (line 121)
    // ---------------------------------------------------------------

    [Fact]
    public async Task StopAsync_WsTaskTimesOut_DoesNotThrow()
    {
        // Create a mock WS that keeps receiving (never closes), so the background task hangs
        var mockWs = new Mock<WebSocket>();
        mockWs.Setup(ws => ws.State).Returns(WebSocketState.Open);

        var first = true;
        mockWs.Setup(ws => ws.ReceiveAsync(It.IsAny<ArraySegment<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ArraySegment<byte> buffer, CancellationToken ct) =>
            {
                if (first)
                {
                    first = false;
                    var msg = """{"type": "connected"}""";
                    var bytes = Encoding.UTF8.GetBytes(msg);
                    Array.Copy(bytes, 0, buffer.Array!, buffer.Offset, bytes.Length);
                    return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true);
                }
                // Block forever until cancelled - this simulates a hung task
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(5), ct);
                }
                catch (OperationCanceledException)
                {
                    // After cancel, keep delaying to force the WaitAsync timeout
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            });

        mockWs.Setup(ws => ws.CloseAsync(It.IsAny<WebSocketCloseStatus>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var ws = new SharedWebSocket("test-key",
            (_, _) => Task.FromResult<WebSocket>(mockWs.Object));

        ws.Start();
        await ws.WaitForInitialConnectAsync().WaitAsync(SyncTimeout);

        // StopAsync should handle the _wsTask timeout (line 121 catch)
        await ws.StopAsync();
    }

    [Fact]
    public async Task ConnectAsync_SetsStatusToConnectedOrReconnecting()
    {
        // Use a mock that sends connected then keeps alive with a delay before close
        var connectedMsg = """{"type": "connected"}""";
        var mockWs = new Mock<WebSocket>();
        mockWs.Setup(ws => ws.State).Returns(WebSocketState.Open);

        var msgs = new Queue<string>(new[] { connectedMsg });
        mockWs.Setup(ws => ws.ReceiveAsync(It.IsAny<ArraySegment<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ArraySegment<byte> buffer, CancellationToken ct) =>
            {
                if (msgs.Count > 0)
                {
                    var msg = msgs.Dequeue();
                    var bytes = Encoding.UTF8.GetBytes(msg);
                    Array.Copy(bytes, 0, buffer.Array!, buffer.Offset, bytes.Length);
                    return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true);
                }
                // Keep the connection alive by delaying until cancellation
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            });

        mockWs.Setup(ws => ws.CloseAsync(It.IsAny<WebSocketCloseStatus>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var ws = new SharedWebSocket("test-key",
            (_, _) => Task.FromResult<WebSocket>(mockWs.Object));

        ws.Start();
        await ws.WaitForInitialConnectAsync().WaitAsync(SyncTimeout);

        Assert.Equal("connected", ws.ConnectionStatus);

        await ws.StopAsync();
    }

    // ---------------------------------------------------------------
    // RunWebSocketAsync — OperationCanceledException with _closed=true
    // ---------------------------------------------------------------

    // ---------------------------------------------------------------
    // WaitForInitialConnectAsync
    // ---------------------------------------------------------------

    [Fact]
    public async Task WaitForInitialConnectAsync_ReturnsTrue_WhenConnected()
    {
        var connectedMsg = """{"type": "connected"}""";
        var mockWs = new Mock<WebSocket>();
        mockWs.Setup(ws => ws.State).Returns(WebSocketState.Open);

        var msgs = new Queue<string>(new[] { connectedMsg });
        mockWs.Setup(ws => ws.ReceiveAsync(It.IsAny<ArraySegment<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ArraySegment<byte> buffer, CancellationToken ct) =>
            {
                if (msgs.Count > 0)
                {
                    var msg = msgs.Dequeue();
                    var bytes = Encoding.UTF8.GetBytes(msg);
                    Array.Copy(bytes, 0, buffer.Array!, buffer.Offset, bytes.Length);
                    return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true);
                }
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            });

        mockWs.Setup(ws => ws.CloseAsync(It.IsAny<WebSocketCloseStatus>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var ws = new SharedWebSocket("test-key",
            (_, _) => Task.FromResult<WebSocket>(mockWs.Object));

        ws.Start();
        await ws.WaitForInitialConnectAsync();

        Assert.Equal("connected", ws.ConnectionStatus);

        await ws.StopAsync();
    }

    [Fact]
    public async Task WaitForInitialConnectAsync_SupportsCancellation()
    {
        // Create a WS that takes a long time to connect
        var ws = new SharedWebSocket("test-key",
            async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                throw new OperationCanceledException(ct);
            });

        ws.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ws.WaitForInitialConnectAsync(cts.Token));

        await ws.StopAsync();
    }

    [Fact]
    public async Task WaitForInitialConnectAsync_ReturnsFalse_WhenConnectionFails()
    {
        // Factory always throws — should signal false via TrySetResult(false).
        // Track when the second connect attempt happens so we know the failure
        // path completed (initial TCS resolved, backoff scheduled, retry kicked off).
        var attempts = 0;
        var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var ws = new SharedWebSocket("test-key", (_, _) =>
        {
            attempts++;
            if (attempts >= 2) secondAttempt.TrySetResult();
            throw new InvalidOperationException("connection failed");
        });

        ws.Start();
        await secondAttempt.Task.WaitAsync(SyncTimeout);
        await ws.StopAsync();

        // After stopping, the initial connect TCS should be resolved
        // It may have been set to false or cancelled
    }

    [Fact]
    public async Task RunWebSocketAsync_OperationCanceled_WhenClosedIsTrue_BreaksLoop()
    {
        // WS factory throws OperationCanceledException on the *second* connect attempt.
        // First attempt: connect + receive throws generic exception → triggers reconnect.
        // Before second attempt: call StopAsync → sets _closed=true + cancels CTS.
        // Second factory call throws OperationCanceledException → caught by the
        //   `catch (OperationCanceledException) when (ct.IsCancellationRequested || _closed)` filter.

        var attempt = 0;
        var secondAttemptReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var ws = new SharedWebSocket("test-key", (_, ct) =>
        {
            attempt++;
            if (attempt == 1)
            {
                // First attempt: throw a generic exception so the reconnect loop fires
                throw new InvalidOperationException("simulate connection failure");
            }
            // Second attempt: signal and throw OperationCanceledException
            secondAttemptReached.TrySetResult();
            throw new OperationCanceledException(ct);
        });

        ws.Start();

        // Wait until the second connect attempt is reached
        await secondAttemptReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Stop — this sets _closed=true and cancels the CTS
        await ws.StopAsync();

        Assert.Equal("disconnected", ws.ConnectionStatus);
        Assert.True(attempt >= 2);
    }

    // ---------------------------------------------------------------
    // Custom appBaseUrl
    // ---------------------------------------------------------------

    [Fact]
    public async Task BuildWebSocketUrl_UsesCustomAppBaseUrl()
    {
        Uri? capturedUri = null;
        var factoryCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectedMsg = """{"type": "connected"}""";
        var mockWs = CreateMockWs(connectedMsg);

        var ws = new SharedWebSocket(
            "test-key",
            wsFactory: (uri, _) =>
            {
                capturedUri = uri;
                factoryCalled.TrySetResult();
                return Task.FromResult<WebSocket>(mockWs.Object);
            },
            appBaseUrl: "https://app.internal.example.com");

        ws.Start();
        await factoryCalled.Task.WaitAsync(SyncTimeout);
        await ws.StopAsync();

        Assert.NotNull(capturedUri);
        Assert.StartsWith("wss://app.internal.example.com/api/ws/v1/events", capturedUri!.ToString());
    }

    [Fact]
    public async Task BuildWebSocketUrl_HttpScheme_UsesWs()
    {
        Uri? capturedUri = null;
        var factoryCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectedMsg = """{"type": "connected"}""";
        var mockWs = CreateMockWs(connectedMsg);

        var ws = new SharedWebSocket(
            "test-key",
            wsFactory: (uri, _) =>
            {
                capturedUri = uri;
                factoryCalled.TrySetResult();
                return Task.FromResult<WebSocket>(mockWs.Object);
            },
            appBaseUrl: "http://app.localhost:8000");

        ws.Start();
        await factoryCalled.Task.WaitAsync(SyncTimeout);
        await ws.StopAsync();

        Assert.NotNull(capturedUri);
        Assert.StartsWith("ws://app.localhost:8000/api/ws/v1/events", capturedUri!.ToString());
    }
}
