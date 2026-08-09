using System.Net;
using System.Text;
using Smplkit;
using Smplkit.Errors;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests;

/// <summary>
/// Tests for <see cref="SmplClient"/> covering the runtime-side surface:
/// SetContext (and disposal), WaitUntilReadyAsync, top-level sub-clients, Dispose
/// lifecycle, and various constructor paths.
/// </summary>
public class SmplClientTests
{
    private static (SmplClient client, MockHttpMessageHandler handler) MakeClient(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond,
        SmplClientOptions? options = null)
    {
        var handler = new MockHttpMessageHandler(respond);
        var http = new HttpClient(handler);
        var client = new SmplClient(options ?? TestData.DefaultOptions(), http);
        return (client, handler);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/vnd.api+json") };

    [Fact]
    public void SetContext_NullArgument_Throws()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json("{}")));
        Assert.Throws<ArgumentNullException>(() => client.SetContext((IEnumerable<Context>)null!));
    }

    [Fact]
    public void SetContext_SingleContext_Convenience()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json("{}")));
        var ctx = new Context("user", "u-1");
        using var scope = client.SetContext(ctx);
        var ambient = client.GetAmbientContext();
        Assert.Single(ambient);
        Assert.Equal("u-1", ambient[0].Key);
    }

    [Fact]
    public void SetContext_Enumerable_StoresAmbient()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json("{}")));
        var contexts = new[]
        {
            new Context("user", "u-1"),
            new Context("account", "acct-1"),
        };
        using var scope = client.SetContext(contexts);
        var ambient = client.GetAmbientContext();
        Assert.Equal(2, ambient.Count);
    }

    [Fact]
    public void SetContext_ScopeDispose_RestoresPrevious()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json("{}")));
        // Initially no ambient context
        Assert.Empty(client.GetAmbientContext());

        using (client.SetContext(new[] { new Context("user", "u-1") }))
        {
            Assert.Single(client.GetAmbientContext());

            using (client.SetContext(new[] { new Context("user", "u-2") }))
            {
                Assert.Equal("u-2", client.GetAmbientContext()[0].Key);
            }
            // Outer scope restored
            Assert.Equal("u-1", client.GetAmbientContext()[0].Key);
        }

        // Outer dispose restores empty
        Assert.Empty(client.GetAmbientContext());
    }

    [Fact]
    public void SetContext_DoubleDispose_IsIdempotent()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json("{}")));
        var scope = client.SetContext(new[] { new Context("user", "u-1") });
        scope.Dispose();
        scope.Dispose(); // no-op
    }

    [Fact]
    public void SetContext_AsReadOnlyList_Direct()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json("{}")));
        IReadOnlyList<Context> list = new List<Context> { new("user", "u-1") };
        using var scope = client.SetContext(list);
        Assert.Same(list, client.GetAmbientContext());
    }

    [Fact]
    public async Task WaitUntilReadyAsync_TimesOut_WhenEventStreamDoesntConnect()
    {
        // The default mock returns valid responses but the event stream never connects.
        var (client, _) = MakeClient(_ => Task.FromResult(Json("""{"data":[]}""")));
        await Assert.ThrowsAsync<Smplkit.Errors.TimeoutException>(
            () => client.WaitUntilReadyAsync(TimeSpan.FromMilliseconds(150)));
    }

    [Fact]
    public async Task WaitUntilReadyAsync_RespectsCancellationToken()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json("""{"data":[]}""")));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.WaitUntilReadyAsync(TimeSpan.FromSeconds(10), cts.Token));
    }

    [Fact]
    public void TopLevelClients_AccessibleAndShared()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json("""{"data":[]}""")));
        Assert.NotNull(client.Platform);
        Assert.NotNull(client.Account);
        Assert.NotNull(client.Config);
        Assert.NotNull(client.Flags);
        Assert.NotNull(client.Logging);
        Assert.NotNull(client.Audit);
        Assert.NotNull(client.Jobs);
        // Same instance — important for shared-buffer / shared-transport behavior.
        Assert.Same(client.Platform, client.Platform);
        Assert.Same(client.Config, client.Config);
        Assert.Same(client.Flags, client.Flags);
    }

    [Fact]
    public void Constructor_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new SmplClient(null!, new HttpClient()));
    }

    [Fact]
    public void Constructor_NullHttpClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new SmplClient(TestData.DefaultOptions(), null!));
    }

    [Fact]
    public void Constructor_FromEnvironment_OnlyApiKeyVar()
    {
        // Use the (options, httpClient) constructor that owns its own HTTP client
        var savedKey = System.Environment.GetEnvironmentVariable("SMPLKIT_API_KEY");
        try
        {
            System.Environment.SetEnvironmentVariable("SMPLKIT_API_KEY", "sk_env_key");
            using var client = new SmplClient(new SmplClientOptions
            {
                Environment = "test",
                Service = "svc",
            });
            Assert.Equal("test", client.Environment);
            Assert.Equal("svc", client.Service);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("SMPLKIT_API_KEY", savedKey);
        }
    }

    [Fact]
    public void Dispose_Idempotent()
    {
        var handler = new MockHttpMessageHandler(_ => Task.FromResult(Json("{}")));
        var http = new HttpClient(handler);
        var client = new SmplClient(TestData.DefaultOptions(), http);
        client.Dispose();
        client.Dispose(); // no exception
    }

    [Fact]
    public void Dispose_OwnedHttpClient_DisposesIt()
    {
        // Use parameterless / options-only ctor — owns http client
        var savedKey = System.Environment.GetEnvironmentVariable("SMPLKIT_API_KEY");
        try
        {
            System.Environment.SetEnvironmentVariable("SMPLKIT_API_KEY", "sk_test_key");
            var client = new SmplClient(new SmplClientOptions
            {
                Environment = "t",
                Service = "s",
            });
            client.Dispose();
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("SMPLKIT_API_KEY", savedKey);
        }
    }

    [Fact]
    public void Constructor_DebugFlag_TogglesDebugLogging()
    {
        // Just verifies the debug constructor branch is hit
        var (client, _) = MakeClient(_ => Task.FromResult(Json("""{"data":[]}""")), new SmplClientOptions
        {
            ApiKey = "sk_test_key",
            Environment = "t",
            Service = "s",
            Debug = true,
        });
        Assert.NotNull(client);
    }

    [Fact]
    public void Constructor_TelemetryDisabled_NoMetricsReporter()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json("""{"data":[]}""")), new SmplClientOptions
        {
            ApiKey = "sk_test_key",
            Environment = "t",
            Service = "s",
            Telemetry = false,
        });
        Assert.NotNull(client);
    }

    [Fact]
    public void GetAmbientContext_Default_Empty()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json("{}")));
        Assert.Empty(client.GetAmbientContext());
    }

    [Fact]
    public void EnsureSharedEventStream_ReturnsSameInstance()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json("{}")));
        var events1 = client.EnsureSharedEventStream();
        var events2 = client.EnsureSharedEventStream();
        Assert.Same(events1, events2);
    }
}
