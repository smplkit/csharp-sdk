using System.Net;
using System.Text;
using Smplkit;
using Smplkit.Flags;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Flags;

/// <summary>
/// Pod-rebuild regression: flags-service is unavailable when the first
/// EnsureInitialized / start runs. Verifies that:
///   1. Pending declarations are NOT drained on a failed flush.
///   2. <c>_connected</c> stays <c>false</c> after the failure.
///   3. A retry after the backoff window flushes the still-pending queue
///      and connects successfully.
/// Mirrors <c>TestFlagsClientStartRetry</c> in the Python SDK.
/// </summary>
public class FlagsStartRetryTests
{
    private static (SmplClient client, MockHttpMessageHandler handler) MakeClient(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        var handler = new MockHttpMessageHandler(respond);
        var http = new HttpClient(handler);
        var client = new SmplClient(TestData.DefaultOptions(), http);
        return (client, handler);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/vnd.api+json") };

    private static HttpResponseMessage ServerError()
        => Json("""{"errors":[{"detail":"service unavailable"}]}""", HttpStatusCode.InternalServerError);

    private static HttpResponseMessage EmptyFlagList()
        => Json("""{"data":[]}""");

    // Matches only the flags bulk-register endpoint (POST .../flags/bulk),
    // not the context bulk-register endpoint (POST .../contexts/bulk_register).
    private static bool IsFlagsBulkPost(HttpRequestMessage req)
        => req.Method == HttpMethod.Post
        && req.RequestUri!.AbsolutePath.Contains("/flags/bulk");

    // ------------------------------------------------------------------
    // 1. Failed flush keeps queue and retries on next start
    // ------------------------------------------------------------------

    [Fact]
    public void FailedFlush_KeepsQueueAndRetries()
    {
        int bulkCallCount = 0;
        var (client, _) = MakeClient(req =>
        {
            if (IsFlagsBulkPost(req))
            {
                bulkCallCount++;
                return Task.FromResult(bulkCallCount == 1 ? ServerError() : EmptyFlagList());
            }
            return Task.FromResult(EmptyFlagList());
        });

        // Seed the discovery buffer WITHOUT connecting (Register is the
        // management/discovery path; it never opens the live connection). The
        // one-client refactor declares handle flags only after EnsureConnected,
        // so to exercise the connect-time flush failure the buffer must already
        // hold a declaration before the first live call.
        client.Flags.Register(new FlagDeclaration("product_alpha", "BOOLEAN", false));
        Assert.Equal(1, client.Flags.PendingFlagRegistrations);

        // First attempt: connect-time bulk-register 500s.
        // Queue must NOT be drained; client must remain not-connected.
        client.Flags.BooleanFlag("product_alpha", false).Get();
        Assert.False(client.Flags._connected);
        Assert.Equal(1, client.Flags.PendingFlagRegistrations);
        Assert.Equal(1, bulkCallCount);

        // Skip the backoff window.
        client.Flags._nextStartAttemptAt = 0L;

        // Second attempt: bulk-register 200s.
        // Queue drains, client connects.
        client.Flags.BooleanFlag("product_alpha", false).Get();
        Assert.True(client.Flags._connected);
        Assert.Equal(0, client.Flags.PendingFlagRegistrations);
        Assert.Equal(2, bulkCallCount);
    }

    // ------------------------------------------------------------------
    // 2. Repeated calls inside the backoff window are no-ops
    // ------------------------------------------------------------------

    [Fact]
    public void Backoff_SkipsRedundantAttempts()
    {
        int bulkCallCount = 0;
        var (client, _) = MakeClient(req =>
        {
            if (IsFlagsBulkPost(req))
            {
                bulkCallCount++;
                return Task.FromResult(ServerError());
            }
            return Task.FromResult(EmptyFlagList());
        });

        client.Flags.Register(new FlagDeclaration("f1", "BOOLEAN", true));
        var handle = client.Flags.BooleanFlag("f1", true);

        // First call fails and schedules a retry.
        handle.Get();
        var countAfterFirst = bulkCallCount;

        // Subsequent calls within the backoff window must not issue new HTTP requests.
        handle.Get();
        handle.Get();
        handle.Get();

        Assert.Equal(countAfterFirst, bulkCallCount);
        Assert.False(client.Flags._connected);
        Assert.Equal(1, client.Flags.PendingFlagRegistrations);
    }

    // ------------------------------------------------------------------
    // 3. Back-off delay doubles on each failure, capped at 60 s
    // ------------------------------------------------------------------

    [Fact]
    public void Backoff_DelayDoublesThenCaps()
    {
        var (client, _) = MakeClient(req =>
            Task.FromResult(IsFlagsBulkPost(req) ? ServerError() : EmptyFlagList()));

        // Seed the buffer without connecting so every connect attempt has a
        // declaration to flush and therefore fails (the bulk POST 500s).
        client.Flags.Register(new FlagDeclaration("f1", "BOOLEAN", true));

        var seenDelays = new List<double>();
        for (int i = 0; i < 10; i++)
        {
            // Reset the backoff gate so the attempt is allowed.
            client.Flags._nextStartAttemptAt = 0L;
            var before = client.Flags._startRetryDelayS;
            // Trigger EnsureConnected — fails each time.
            client.Flags.BooleanFlag("f1", true).Get();
            seenDelays.Add(before);
        }

        Assert.Equal(1.0, seenDelays[0]);
        Assert.Equal(2.0, seenDelays[1]);
        Assert.Equal(4.0, seenDelays[2]);
        Assert.Equal(FlagsClient.MaxStartRetryDelayS, seenDelays[^1]);
    }

    // ------------------------------------------------------------------
    // 4. Until start() succeeds, evaluation falls back to the handle default
    // ------------------------------------------------------------------

    [Fact]
    public void EvaluationFallsBackToDefault_WhileDisconnected()
    {
        var (client, _) = MakeClient(req =>
            Task.FromResult(IsFlagsBulkPost(req) ? ServerError() : EmptyFlagList()));

        // Seed the buffer first so the connect-time flush fails.
        client.Flags.Register(new FlagDeclaration("checkout_v2", "BOOLEAN", false));
        var flag = client.Flags.BooleanFlag("checkout_v2", false);

        // First evaluation triggers EnsureConnected which fails; must fall
        // back to the declared default rather than throwing.
        Assert.False(flag.Get());
        Assert.False(client.Flags._connected);
        Assert.Equal(1, client.Flags.PendingFlagRegistrations);
    }

    // ------------------------------------------------------------------
    // Timer and threshold flush: server errors are swallowed, buffer retained
    // ------------------------------------------------------------------

    [Fact]
    public void FlushTimerCallback_ServerError_BufferRetained()
    {
        var (client, _) = MakeClient(req =>
            Task.FromResult(IsFlagsBulkPost(req) ? ServerError() : EmptyFlagList()));

        // Declare a flag (adds to buffer) without triggering EnsureInitialized.
        client.Flags.BooleanFlag("f1", false);
        Assert.Equal(1, client.Flags.PendingFlagRegistrations);

        // Call FlushTimerCallback directly — SafeFlushFlagsAsync must swallow the 500.
        var method = typeof(FlagsClient).GetMethod("FlushTimerCallback",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        method.Invoke(client.Flags, Array.Empty<object>());

        // Error was swallowed; buffer is intact.
        Assert.Equal(1, client.Flags.PendingFlagRegistrations);
    }

    [Fact]
    public async Task ThresholdFlush_ServerError_BufferRetained()
    {
        var (client, _) = MakeClient(req =>
            Task.FromResult(IsFlagsBulkPost(req) ? ServerError() : EmptyFlagList()));

        // Declare 60 flags — this crosses the 50-flag threshold and fires SafeFlushFlagsAsync.
        for (int i = 0; i < 60; i++)
            client.Flags.BooleanFlag($"tf{i}", false);

        // Await the fire-and-forget task so the test is deterministic.
        var taskField = typeof(FlagsClient).GetField("_lastFlagBufferFlushTask",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        if (taskField.GetValue(client.Flags) is Task t) await t;

        // SafeFlushFlagsAsync swallowed the 500; declarations are still pending.
        Assert.True(client.Flags.PendingFlagRegistrations > 0);
    }

    // ------------------------------------------------------------------
    // 5. WebSocket handlers are registered only once across retry attempts
    // ------------------------------------------------------------------

    [Fact]
    public void WsSubscribed_OnlyOnceAcrossRetryAttempts()
    {
        int bulkCallCount = 0;
        var (client, _) = MakeClient(req =>
        {
            if (IsFlagsBulkPost(req))
            {
                bulkCallCount++;
                return Task.FromResult(bulkCallCount == 1 ? ServerError() : EmptyFlagList());
            }
            return Task.FromResult(EmptyFlagList());
        });

        // Seed the buffer first so the first connect-time flush fails.
        client.Flags.Register(new FlagDeclaration("f1", "BOOLEAN", true));
        var handle = client.Flags.BooleanFlag("f1", true);

        // First attempt fails — not yet subscribed.
        handle.Get();
        Assert.False(client.Flags._wsSubscribed);

        // Skip backoff, second attempt succeeds — subscribes once.
        client.Flags._nextStartAttemptAt = 0L;
        handle.Get();
        Assert.True(client.Flags._wsSubscribed);
        Assert.True(client.Flags._connected);

        // Third call — fast path, already connected, _wsSubscribed unchanged.
        handle.Get();
        Assert.True(client.Flags._wsSubscribed);
    }
}
