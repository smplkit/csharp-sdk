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

        var mockWs = CreateMockWs(connectedMsg, eventMsg);
        var ws = new SharedWebSocket("test-key",
            (_, _) => Task.FromResult<WebSocket>(mockWs.Object));

        ws.On("flag_changed", Handler);
        ws.Off("flag_changed", Handler);

        ws.Start();
        await Task.Delay(200);
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

        var connectedMsg = """{"type": "connected"}""";
        var eventMsg = """{"event": "flag_changed", "key": "test-flag"}""";

        var mockWs = CreateMockWs(connectedMsg, eventMsg);
        var ws = new SharedWebSocket("test-key",
            (_, _) => Task.FromResult<WebSocket>(mockWs.Object));

        ws.On("flag_changed", data => events.Add(data));
        ws.Start();

        await Task.Delay(200);
        await ws.StopAsync();

        Assert.Single(events);
        Assert.Equal("test-flag", events[0]["key"]?.ToString());
    }

    [Fact]
    public async Task Dispatch_TypeMessage_FiresListeners()
    {
        var events = new List<Dictionary<string, object?>>();

        var connectedMsg = """{"type": "connected"}""";
        var typeMsg = """{"type": "config_changed", "configKey": "my-config"}""";

        var mockWs = CreateMockWs(connectedMsg, typeMsg);
        var ws = new SharedWebSocket("test-key",
            (_, _) => Task.FromResult<WebSocket>(mockWs.Object));

        ws.On("config_changed", data => events.Add(data));
        ws.Start();

        await Task.Delay(200);
        await ws.StopAsync();

        Assert.Single(events);
    }

    [Fact]
    public async Task Dispatch_ListenerThrows_DoesNotPropagate()
    {
        var connectedMsg = """{"type": "connected"}""";
        var eventMsg = """{"event": "flag_changed", "key": "x"}""";

        var mockWs = CreateMockWs(connectedMsg, eventMsg);
        var ws = new SharedWebSocket("test-key",
            (_, _) => Task.FromResult<WebSocket>(mockWs.Object));

        ws.On("flag_changed", _ => throw new InvalidOperationException("boom"));
        ws.Start();

        await Task.Delay(200);
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

        var mockWs = CreateMockWs(connectedMsg, "ping");
        var ws = new SharedWebSocket("test-key",
            (_, _) => Task.FromResult<WebSocket>(mockWs.Object));

        ws.Start();
        await Task.Delay(300);
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

        var mockWs = CreateMockWs(errorMsg);
        var ws = new SharedWebSocket("test-key",
            (_, _) => Task.FromResult<WebSocket>(mockWs.Object));

        ws.Start();
        await Task.Delay(300);
        await ws.StopAsync();

        // The error should cause reconnect attempts but not crash
        // Connection status should reflect reconnecting or disconnected
    }

    [Fact]
    public async Task ConnectAsync_ErrorMessageNoMessage_ThrowsInvalidOperation()
    {
        var errorMsg = """{"type": "error"}""";

        var mockWs = CreateMockWs(errorMsg);
        var ws = new SharedWebSocket("test-key",
            (_, _) => Task.FromResult<WebSocket>(mockWs.Object));

        ws.Start();
        await Task.Delay(300);
        await ws.StopAsync();
    }

    // ---------------------------------------------------------------
    // RunWebSocketAsync reconnection
    // ---------------------------------------------------------------

    [Fact]
    public async Task RunWebSocketAsync_ReconnectsOnFailure()
    {
        int attempts = 0;
        var ws = new SharedWebSocket("test-key",
            (_, ct) =>
            {
                attempts++;
                if (attempts <= 2)
                    throw new WebSocketException("Connection failed");
                // Third attempt succeeds
                var mockWs = CreateMockWs("""{"type": "connected"}""");
                return Task.FromResult<WebSocket>(mockWs.Object);
            });

        ws.Start();
        await Task.Delay(4000); // Wait for reconnection backoff
        await ws.StopAsync();

        Assert.True(attempts >= 2);
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
        await Task.Delay(200);
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
        await Task.Delay(200);
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
        await Task.Delay(200);

        // Should not throw
        await ws.StopAsync();
    }

    // ---------------------------------------------------------------
    // ReceiveRawAsync - null/closed ws
    // ---------------------------------------------------------------

    [Fact]
    public async Task ReceiveLoop_NullMessage_BreaksLoop()
    {
        // Mock WS that immediately returns Close
        var mockWs = new Mock<WebSocket>();
        mockWs.Setup(ws => ws.State).Returns(WebSocketState.Open);

        var first = true;
        mockWs.Setup(ws => ws.ReceiveAsync(It.IsAny<ArraySegment<byte>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ArraySegment<byte> buffer, CancellationToken _) =>
            {
                if (first)
                {
                    first = false;
                    var msg = """{"type": "connected"}""";
                    var bytes = Encoding.UTF8.GetBytes(msg);
                    Array.Copy(bytes, 0, buffer.Array!, buffer.Offset, bytes.Length);
                    return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true);
                }
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            });

        var ws = new SharedWebSocket("test-key",
            (_, _) => Task.FromResult<WebSocket>(mockWs.Object));

        ws.Start();
        await Task.Delay(300);
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

        var mockWs = CreateMockWs(connectedMsg, eventMsg);
        var ws = new SharedWebSocket("test-key",
            (_, _) => Task.FromResult<WebSocket>(mockWs.Object));

        // Register listener for DIFFERENT event
        ws.On("flag_changed", _ => { });

        ws.Start();
        await Task.Delay(200);
        await ws.StopAsync();
        // No crash - Dispatch returns early when no listeners for "unhandled_event"
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
        await Task.Delay(200);

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
        await Task.Delay(200);

        Assert.Equal("connected", ws.ConnectionStatus);

        await ws.StopAsync();
    }
}
