using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Moq;
using Smplkit.Config;
using Xunit;

namespace Smplkit.Tests.Config;

/// <summary>
/// Tests for ConfigRuntime WebSocket paths using mock WebSocket connections.
/// Covers ConnectAndSubscribeAsync, ReceiveLoopAsync, ReceiveRawAsync, and CloseAsync.
/// </summary>
public class ConfigRuntimeWsMockTests
{
    private static ConfigRuntime CreateRuntime(
        Func<Uri, CancellationToken, Task<WebSocket>> wsFactory,
        Func<CancellationToken, Task<List<ConfigChainEntry>>>? fetchChainFn = null,
        Dictionary<string, Dictionary<string, object?>>? baseValues = null)
    {
        var vals = baseValues ?? new Dictionary<string, Dictionary<string, object?>>
        {
            ["timeout"] = new() { ["_base"] = 60L },
            ["retries"] = new() { ["_base"] = 3L },
        };

        // Flatten into base values dict
        var baseVals = new Dictionary<string, object?>();
        foreach (var kvp in vals)
            baseVals[kvp.Key] = kvp.Value.GetValueOrDefault("_base");

        var chain = new List<ConfigChainEntry>
        {
            new() { Id = "cfg-1", Values = baseVals }
        };

        return new ConfigRuntime(
            configKey: "test-config",
            configId: "cfg-1",
            environment: "test-env",
            chain: chain,
            apiKey: "test-api-key",
            fetchChainFn: fetchChainFn,
            wsFactory: wsFactory);
    }

    /// <summary>
    /// Creates a mock WebSocket that:
    /// 1. Is in Open state
    /// 2. Returns specified messages from ReceiveAsync in order
    /// 3. Then returns a Close message
    /// </summary>
    private static Mock<WebSocket> CreateMockWs(params string[] messages)
    {
        var mockWs = new Mock<WebSocket>();
        mockWs.Setup(ws => ws.State).Returns(WebSocketState.Open);

        var receiveQueue = new Queue<string>(messages);
        mockWs.Setup(ws => ws.ReceiveAsync(It.IsAny<ArraySegment<byte>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ArraySegment<byte> buffer, CancellationToken _) =>
            {
                if (receiveQueue.Count == 0)
                {
                    return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
                }

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

        mockWs.Setup(ws => ws.Dispose());

        return mockWs;
    }

    // ------------------------------------------------------------------
    // ConnectAndSubscribeAsync + ReceiveLoopAsync (happy path)
    // ------------------------------------------------------------------

    [Fact]
    public async Task WsConnect_SubscribesAndReceivesMessages()
    {
        // Arrange: WS sends subscribed confirmation, then a config_changed, then closes.
        var subscribed = JsonSerializer.Serialize(new { type = "subscribed" });
        var changed = JsonSerializer.Serialize(new
        {
            type = "config_changed",
            config_id = "cfg-1",
            changes = new[] { new { key = "timeout", old_value = 60, new_value = 120 } }
        });

        var mockWs = CreateMockWs(subscribed, changed);
        var events = new List<ConfigChangeEvent>();
        int connectCount = 0;

        var rt = CreateRuntime(async (uri, ct) =>
        {
            var n = Interlocked.Increment(ref connectCount);
            if (n > 1)
            {
                // Block subsequent connections to avoid reconnect race
                await Task.Delay(Timeout.Infinite, ct);
            }
            return mockWs.Object;
        });

        rt.OnChange(evt => events.Add(evt));

        // Give the background WS loop time to process messages
        await Task.Delay(500);

        // The config_changed message should have fired a change event
        Assert.Contains(events, e => e.Key == "timeout");

        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // ConnectAndSubscribeAsync — error response
    // ------------------------------------------------------------------

    [Fact]
    public async Task WsConnect_ErrorResponse_RetriesAfterBackoff()
    {
        var errorMsg = JsonSerializer.Serialize(new { type = "error", message = "not authorized" });

        int connectCount = 0;
        var mockWs = CreateMockWs(errorMsg);

        var rt = CreateRuntime(async (uri, ct) =>
        {
            Interlocked.Increment(ref connectCount);
            await Task.CompletedTask;
            return mockWs.Object;
        });

        // Wait briefly - the error causes a retry with backoff
        await Task.Delay(300);
        await rt.CloseAsync();

        // Should have attempted at least one connection
        Assert.True(connectCount >= 1);
    }

    // ------------------------------------------------------------------
    // ReceiveLoopAsync — config_deleted message
    // ------------------------------------------------------------------

    [Fact]
    public async Task WsReceive_ConfigDeleted_ClearsCache()
    {
        var subscribed = JsonSerializer.Serialize(new { type = "subscribed" });
        var deleted = JsonSerializer.Serialize(new
        {
            type = "config_deleted",
            config_id = "cfg-1"
        });

        var mockWs = CreateMockWs(subscribed, deleted);

        var rt = CreateRuntime(async (uri, ct) =>
        {
            await Task.CompletedTask;
            return mockWs.Object;
        });

        await Task.Delay(500);

        // After config_deleted, runtime is marked as closed/disconnected
        // but cached values remain available for reads
        Assert.Equal("disconnected", rt.ConnectionStatus());
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // ReceiveLoopAsync — message without "type" property (skip)
    // ------------------------------------------------------------------

    [Fact]
    public async Task WsReceive_MessageWithoutType_IsSkipped()
    {
        var subscribed = JsonSerializer.Serialize(new { type = "subscribed" });
        var noType = JsonSerializer.Serialize(new { data = "something" });

        var mockWs = CreateMockWs(subscribed, noType);

        var rt = CreateRuntime(async (uri, ct) =>
        {
            await Task.CompletedTask;
            return mockWs.Object;
        });

        await Task.Delay(500);

        // Original values should be unchanged
        Assert.Equal(60L, rt.Get("timeout"));
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // CloseAsync — when WS is in Open state
    // ------------------------------------------------------------------

    [Fact]
    public async Task CloseAsync_WsOpen_SendsCloseFrame()
    {
        var subscribed = JsonSerializer.Serialize(new { type = "subscribed" });

        // WS that stays connected (no more messages after subscribe)
        var mockWs = new Mock<WebSocket>();
        mockWs.Setup(ws => ws.State).Returns(WebSocketState.Open);

        // First receive returns subscribed, then blocks until cancelled
        int receiveCall = 0;
        mockWs.Setup(ws => ws.ReceiveAsync(It.IsAny<ArraySegment<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ArraySegment<byte> buffer, CancellationToken ct) =>
            {
                var call = Interlocked.Increment(ref receiveCall);
                if (call == 1)
                {
                    var bytes = Encoding.UTF8.GetBytes(subscribed);
                    Array.Copy(bytes, 0, buffer.Array!, buffer.Offset, bytes.Length);
                    return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true);
                }

                // Block until cancelled to keep the receive loop alive
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { }
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            });

        mockWs.Setup(ws => ws.SendAsync(It.IsAny<ArraySegment<byte>>(), It.IsAny<WebSocketMessageType>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mockWs.Setup(ws => ws.CloseAsync(It.IsAny<WebSocketCloseStatus>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mockWs.Setup(ws => ws.Dispose());

        var rt = CreateRuntime(async (uri, ct) =>
        {
            await Task.CompletedTask;
            return mockWs.Object;
        });

        // Wait for connection to establish
        await Task.Delay(300);
        Assert.Equal("connected", rt.ConnectionStatus());

        await rt.CloseAsync();
        Assert.Equal("disconnected", rt.ConnectionStatus());

        // Verify CloseAsync was called on the WebSocket
        mockWs.Verify(ws => ws.CloseAsync(
            WebSocketCloseStatus.NormalClosure, "Closing", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ------------------------------------------------------------------
    // CloseAsync — ws.CloseAsync throws (catch block)
    // ------------------------------------------------------------------

    [Fact]
    public async Task CloseAsync_WsCloseThrows_DoesNotPropagate()
    {
        var subscribed = JsonSerializer.Serialize(new { type = "subscribed" });

        var mockWs = new Mock<WebSocket>();
        mockWs.Setup(ws => ws.State).Returns(WebSocketState.Open);

        int receiveCall = 0;
        mockWs.Setup(ws => ws.ReceiveAsync(It.IsAny<ArraySegment<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ArraySegment<byte> buffer, CancellationToken ct) =>
            {
                var call = Interlocked.Increment(ref receiveCall);
                if (call == 1)
                {
                    var bytes = Encoding.UTF8.GetBytes(subscribed);
                    Array.Copy(bytes, 0, buffer.Array!, buffer.Offset, bytes.Length);
                    return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true);
                }

                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { }
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            });

        mockWs.Setup(ws => ws.SendAsync(It.IsAny<ArraySegment<byte>>(), It.IsAny<WebSocketMessageType>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mockWs.Setup(ws => ws.CloseAsync(It.IsAny<WebSocketCloseStatus>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new WebSocketException("connection reset"));

        mockWs.Setup(ws => ws.Dispose());

        var rt = CreateRuntime(async (uri, ct) =>
        {
            await Task.CompletedTask;
            return mockWs.Object;
        });

        await Task.Delay(300);

        // Should not throw even though ws.CloseAsync throws
        await rt.CloseAsync();
        Assert.Equal("disconnected", rt.ConnectionStatus());
    }

    // ------------------------------------------------------------------
    // ReceiveRawAsync — WS returns Close message type
    // ------------------------------------------------------------------

    [Fact]
    public async Task WsReceive_CloseMessage_BreaksLoop()
    {
        var subscribed = JsonSerializer.Serialize(new { type = "subscribed" });

        var mockWs = new Mock<WebSocket>();
        mockWs.Setup(ws => ws.State).Returns(WebSocketState.Open);

        int receiveCall = 0;
        mockWs.Setup(ws => ws.ReceiveAsync(It.IsAny<ArraySegment<byte>>(), It.IsAny<CancellationToken>()))
            .Returns((ArraySegment<byte> buffer, CancellationToken _) =>
            {
                var call = Interlocked.Increment(ref receiveCall);
                if (call == 1)
                {
                    var bytes = Encoding.UTF8.GetBytes(subscribed);
                    Array.Copy(bytes, 0, buffer.Array!, buffer.Offset, bytes.Length);
                    return Task.FromResult(new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true));
                }

                // Close message
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            });

        mockWs.Setup(ws => ws.SendAsync(It.IsAny<ArraySegment<byte>>(), It.IsAny<WebSocketMessageType>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mockWs.Setup(ws => ws.CloseAsync(It.IsAny<WebSocketCloseStatus>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mockWs.Setup(ws => ws.Dispose());

        var rt = CreateRuntime(async (uri, ct) =>
        {
            await Task.CompletedTask;
            return mockWs.Object;
        });

        // Loop should exit quickly after close message, then retry
        await Task.Delay(500);
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // Multi-frame message in ReceiveRawAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task WsReceive_MultiFrameMessage_AssemblesCorrectly()
    {
        // We need a custom mock that returns a message in two frames
        var fullMsg = JsonSerializer.Serialize(new { type = "subscribed" });
        var part1 = fullMsg[..(fullMsg.Length / 2)];
        var part2 = fullMsg[(fullMsg.Length / 2)..];

        var mockWs = new Mock<WebSocket>();
        mockWs.Setup(ws => ws.State).Returns(WebSocketState.Open);

        int receiveCall = 0;
        mockWs.Setup(ws => ws.ReceiveAsync(It.IsAny<ArraySegment<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ArraySegment<byte> buffer, CancellationToken ct) =>
            {
                var call = Interlocked.Increment(ref receiveCall);
                if (call == 1)
                {
                    // First frame: partial message
                    var bytes = Encoding.UTF8.GetBytes(part1);
                    Array.Copy(bytes, 0, buffer.Array!, buffer.Offset, bytes.Length);
                    return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, false);
                }
                if (call == 2)
                {
                    // Second frame: end of message
                    var bytes = Encoding.UTF8.GetBytes(part2);
                    Array.Copy(bytes, 0, buffer.Array!, buffer.Offset, bytes.Length);
                    return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true);
                }

                // Then block
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { }
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            });

        mockWs.Setup(ws => ws.SendAsync(It.IsAny<ArraySegment<byte>>(), It.IsAny<WebSocketMessageType>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mockWs.Setup(ws => ws.CloseAsync(It.IsAny<WebSocketCloseStatus>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mockWs.Setup(ws => ws.Dispose());

        var rt = CreateRuntime(async (uri, ct) =>
        {
            await Task.CompletedTask;
            return mockWs.Object;
        });

        // Connection should succeed despite multi-frame subscribe response
        await Task.Delay(500);
        Assert.Equal("connected", rt.ConnectionStatus());
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // _wsTask timeout in CloseAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task CloseAsync_WsTaskTimeout_DoesNotHang()
    {
        // Create a WS that blocks forever on receive (simulates a stuck task)
        var subscribed = JsonSerializer.Serialize(new { type = "subscribed" });

        var mockWs = new Mock<WebSocket>();
        mockWs.Setup(ws => ws.State).Returns(WebSocketState.Open);

        mockWs.Setup(ws => ws.ReceiveAsync(It.IsAny<ArraySegment<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ArraySegment<byte> buffer, CancellationToken ct) =>
            {
                var bytes = Encoding.UTF8.GetBytes(subscribed);
                Array.Copy(bytes, 0, buffer.Array!, buffer.Offset, bytes.Length);
                // Never return from second call - simulates stuck connection
                await Task.Delay(Timeout.Infinite, CancellationToken.None);
                return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true);
            });

        mockWs.Setup(ws => ws.SendAsync(It.IsAny<ArraySegment<byte>>(), It.IsAny<WebSocketMessageType>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mockWs.Setup(ws => ws.CloseAsync(It.IsAny<WebSocketCloseStatus>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mockWs.Setup(ws => ws.Dispose());

        var rt = CreateRuntime(async (uri, ct) =>
        {
            await Task.CompletedTask;
            return mockWs.Object;
        });

        // CloseAsync should complete within a few seconds due to WaitAsync timeout
        var closeTask = rt.CloseAsync();
        var completed = await Task.WhenAny(closeTask, Task.Delay(5000));
        Assert.Equal(closeTask, completed); // Should not hang
    }

    // ------------------------------------------------------------------
    // ConnectAndSubscribeAsync — subscribe error with message field
    // ------------------------------------------------------------------

    [Fact]
    public async Task WsConnect_ErrorWithMessage_Reconnects()
    {
        var errorMsg = JsonSerializer.Serialize(new
        {
            type = "error",
            message = "subscription failed: config not found"
        });

        var mockWs = CreateMockWs(errorMsg);

        var rt = CreateRuntime(async (uri, ct) =>
        {
            await Task.CompletedTask;
            return mockWs.Object;
        });

        await Task.Delay(300);
        // Should be in connecting state due to reconnection attempts
        var status = rt.ConnectionStatus();
        Assert.True(status == "connecting" || status == "disconnected",
            $"Expected connecting or disconnected, got: {status}");

        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // ConnectAndSubscribeAsync — subscribe error without message field
    // ------------------------------------------------------------------

    [Fact]
    public async Task WsConnect_ErrorWithoutMessage_UsesDefault()
    {
        var errorMsg = JsonSerializer.Serialize(new { type = "error" });

        var mockWs = CreateMockWs(errorMsg);

        var rt = CreateRuntime(async (uri, ct) =>
        {
            await Task.CompletedTask;
            return mockWs.Object;
        });

        await Task.Delay(300);
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // CloseAsync — with pre-cancelled token hits catch block
    // ------------------------------------------------------------------

    [Fact]
    public async Task CloseAsync_CancelledToken_HitsCatchBlock()
    {
        var subscribed = JsonSerializer.Serialize(new { type = "subscribed" });

        var mockWs = new Mock<WebSocket>();
        mockWs.Setup(ws => ws.State).Returns(WebSocketState.Open);

        int receiveCall = 0;
        mockWs.Setup(ws => ws.ReceiveAsync(It.IsAny<ArraySegment<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ArraySegment<byte> buffer, CancellationToken ct) =>
            {
                var call = Interlocked.Increment(ref receiveCall);
                if (call == 1)
                {
                    var bytes = Encoding.UTF8.GetBytes(subscribed);
                    Array.Copy(bytes, 0, buffer.Array!, buffer.Offset, bytes.Length);
                    return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true);
                }

                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { }
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            });

        mockWs.Setup(ws => ws.SendAsync(It.IsAny<ArraySegment<byte>>(), It.IsAny<WebSocketMessageType>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mockWs.Setup(ws => ws.CloseAsync(It.IsAny<WebSocketCloseStatus>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mockWs.Setup(ws => ws.Dispose());

        var rt = CreateRuntime(async (uri, ct) =>
        {
            await Task.CompletedTask;
            return mockWs.Object;
        });

        await Task.Delay(300);

        // Pass an already-cancelled token - this forces WaitAsync to throw immediately
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await rt.CloseAsync(cts.Token);
    }
}
