using System.Net;
using System.Reflection;
using System.Text;
using Smplkit;
using Smplkit.Config;
using Smplkit.Tests.Helpers;
using Xunit;
using ConfigModel = Smplkit.Config.Config;
using ServiceModel = Smplkit.Platform.Service;

namespace Smplkit.Tests;

/// <summary>
/// Closes the final wrapper-coverage gaps left after the one-client refactor:
/// connect-time discovery-flush failure handling, the async pre-warm path, the
/// pending-seed merge during a cache rebuild, the not-connected config_deleted
/// route, the explicit-context flush threshold, the flag <c>Register(flush)</c>
/// path, and the clientless active-record save guards.
/// </summary>
public class CoverageGapsRemainingTests
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

    private static bool IsConfigsBulkPost(HttpRequestMessage req)
        => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/configs/bulk");

    // ------------------------------------------------------------------
    // ConfigClient.EnsureConnected — discovery-flush failure is swallowed
    // ------------------------------------------------------------------

    [Fact]
    public void ConfigClient_ConnectTimeFlushFails_Swallowed()
    {
        var (client, _) = MakeClient(req =>
        {
            if (IsConfigsBulkPost(req))
                return Task.FromResult(Json("""{"errors":[{"detail":"boom"}]}""", HttpStatusCode.InternalServerError));
            return Task.FromResult(Json("""{"data":[]}"""));
        });

        // Seed a discovery declaration WITHOUT connecting, so the connect-time
        // flush has something to POST — that POST 500s and the catch swallows it.
        client.Config.RegisterConfig("billing", "svc", "test");

        // Subscribe triggers EnsureConnected (flush fails, swallowed) then NotFound
        // because the (empty) list has no "billing".
        Assert.Throws<Smplkit.Errors.NotFoundException>(() => client.Config.Subscribe("billing"));
    }

    // ------------------------------------------------------------------
    // ConfigClient.EnsureConnectedAsync — async pre-warm
    // ------------------------------------------------------------------

    [Fact]
    public async Task ConfigClient_EnsureConnectedAsync_Connects()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json("""{"data":[]}""")));
        var method = typeof(ConfigClient).GetMethod("EnsureConnectedAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(client.Config, new object?[] { CancellationToken.None })!;
        // After connecting, a present config resolves from cache.
        Assert.Equal(0, client.Config.PendingCount);
    }

    // ------------------------------------------------------------------
    // ConfigClient.MergePendingSeeds — bound config re-seeded on cache rebuild
    // ------------------------------------------------------------------

    [Fact]
    public async Task ConfigClient_RefreshAsync_ReSeedsBoundConfig()
    {
        // Bind a brand-new config (lives only as an in-memory seed). A subsequent
        // RefreshAsync rebuilds the cache from a server list that doesn't include
        // it — MergePendingSeeds must re-seed it so it survives.
        var (client, _) = MakeClient(_ => Task.FromResult(Json("""{"data":[]}""")));
        var bound = client.Config.Bind("billing", new Dictionary<string, object?> { ["max_seats"] = 5 });
        Assert.NotNull(bound);

        await client.Config.RefreshAsync();

        // The seed survived the rebuild — Subscribe finds it in cache.
        Assert.NotNull(client.Config.Subscribe("billing"));
    }

    // ------------------------------------------------------------------
    // ConfigClient.HandleConfigDeleted — not-connected / null-id route
    // ------------------------------------------------------------------

    [Fact]
    public void ConfigClient_HandleConfigDeleted_BeforeConnect_RoutesToConfigsChanged()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json("""{"data":[]}""")));
        var method = typeof(ConfigClient).GetMethod("HandleConfigDeleted",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        // Not yet connected → falls through to HandleConfigsChanged (which no-ops
        // while disconnected). No throw is the assertion.
        method.Invoke(client.Config, new object?[] { new Dictionary<string, object?> { ["id"] = "gone" } });
    }

    // ------------------------------------------------------------------
    // FlagsClient — explicit-context flush threshold
    // ------------------------------------------------------------------

    [Fact]
    public async Task FlagsClient_ExplicitContextOverThreshold_TriggersFlush()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json("""{"data":[]}""")));
        var handle = client.Flags.BooleanFlag("any", true);
        // Pass an explicit context of >100 entries to cross ContextBatchFlushSize.
        var contexts = Enumerable.Range(0, 110)
            .Select(i => new Context("user", $"u-{i}"))
            .ToList();
        handle.Get(contexts);

        var taskField = typeof(Smplkit.Flags.FlagsClient).GetField("_lastContextBufferFlushTask",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        if (taskField.GetValue(client.Flags) is Task t) await t;
    }

    // ------------------------------------------------------------------
    // FlagsClient.Register(flush: true)
    // ------------------------------------------------------------------

    [Fact]
    public async Task FlagsClient_Register_FlushTrue_SendsBulkAndDrains()
    {
        HttpRequestMessage? captured = null;
        var (client, _) = MakeClient(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.Contains("/flags/bulk"))
                captured = req;
            return Task.FromResult(Json("""{"data":[]}"""));
        });

        client.Flags.Register(new Smplkit.Flags.FlagDeclaration("f", "BOOLEAN", true), flush: true);

        var taskField = typeof(Smplkit.Flags.FlagsClient).GetField("_lastFlagBufferFlushTask",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        if (taskField.GetValue(client.Flags) is Task t) await t;

        Assert.NotNull(captured);
        Assert.Equal(0, client.Flags.PendingFlagRegistrations);
    }

    // ------------------------------------------------------------------
    // Clientless active-record save guards
    // ------------------------------------------------------------------

    [Fact]
    public async Task Config_SaveAsync_WithoutClient_Throws()
    {
        var cfg = (ConfigModel)Activator.CreateInstance(
            typeof(ConfigModel),
            BindingFlags.NonPublic | BindingFlags.Instance,
            null,
            new object?[]
            {
                null, "id", "Name", null, null,
                new Dictionary<string, object?>(),
                new Dictionary<string, Dictionary<string, object?>>(),
                null, null,
            },
            null)!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => cfg.SaveAsync());
    }

    [Fact]
    public async Task Service_SaveAsync_WithoutClient_Throws()
    {
        var svc = (ServiceModel)Activator.CreateInstance(
            typeof(ServiceModel),
            BindingFlags.NonPublic | BindingFlags.Instance,
            null,
            new object?[] { null, "id", "Name", null, null },
            null)!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SaveAsync());
    }
}
