using System.Net;
using System.Reflection;
using System.Text;
using Smplkit;
using Smplkit.Internal;
using Smplkit.Tests.Helpers;
using Xunit;
using ConfigClient = Smplkit.Config.ConfigClient;

namespace Smplkit.Tests.Config;

/// <summary>
/// Tests for the stateless config mode (<c>streaming: false</c>): the first live
/// call fetches and resolves once with no WebSocket, discovery threshold flushes
/// run inline, and RefreshAsync re-fetches on demand.
/// </summary>
public class ConfigStatelessTests
{
    private static readonly Func<SharedWebSocket> ThrowingWs =
        () => throw new InvalidOperationException("stateless mode must not create a WebSocket");

    private static (ConfigClient config, MockHttpMessageHandler handler) MakeStateless(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        var handler = new MockHttpMessageHandler(respond);
        var http = new HttpClient(handler);
        var factory = new GeneratedClientFactory(http, new SmplClientOptions
        {
            ApiKey = TestData.ApiKey,
            BaseDomain = "example.test",
        });
        var config = new ConfigClient(factory, ThrowingWs, parent: null, metrics: null, streaming: false);
        return (config, handler);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/vnd.api+json") };

    private static bool IsConfigsBulkPost(HttpRequestMessage req)
        => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/configs/bulk");

    private const string ConfigListJson = """
        {
            "data": [
                {
                    "id": "billing",
                    "type": "config",
                    "attributes": {
                        "id": "billing",
                        "name": "Billing",
                        "description": null,
                        "parent": null,
                        "items": {
                            "max_seats": {"value": 50, "type": "NUMBER"}
                        },
                        "environments": {},
                        "created_at": "2024-01-15T10:30:00Z",
                        "updated_at": "2024-01-15T10:30:00Z"
                    }
                }
            ]
        }
        """;

    [Fact]
    public void StatelessConnect_NoWebSocket_SubscribeAndGetValueWork()
    {
        var (config, _) = MakeStateless(_ => Task.FromResult(Json(ConfigListJson)));

        var proxy = config.Subscribe("billing");
        Assert.Equal(50L, proxy["max_seats"]);
        Assert.Equal(50L, config.GetValue("billing", "max_seats"));

        // No WebSocket was opened or subscribed.
        Assert.Null(typeof(ConfigClient).GetField("_wsManager",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(config));
    }

    [Fact]
    public void RegisterConfig_Threshold_Stateless_FlushesInline()
    {
        var (config, handler) = MakeStateless(_ => Task.FromResult(Json(TestData.EmptyListJson())));

        for (int i = 0; i < 50; i++)
            config.RegisterConfig($"cfg-{i}", "svc", "prod");

        // The threshold flush ran inline — drained synchronously, no polling.
        Assert.Contains(handler.Requests, IsConfigsBulkPost);
        Assert.Equal(0, config.PendingCount);
    }

    [Fact]
    public void RegisterConfigItem_Threshold_Stateless_FlushesInline()
    {
        var (config, handler) = MakeStateless(_ => Task.FromResult(Json(TestData.EmptyListJson())));

        // Fill to the threshold once (drains inline), then bring the buffer
        // back to one-below so the item declaration itself tips it over.
        for (int i = 0; i < 50; i++)
            config.RegisterConfig($"cfg-{i}", "svc", "prod");
        for (int i = 50; i < 99; i++)
            config.RegisterConfig($"cfg-{i}", "svc", "prod");
        var postsBefore = handler.Requests.Count(IsConfigsBulkPost);

        // cfg-0 was drained, so its item re-creates a pending entry — the 50th.
        config.RegisterConfigItem("cfg-0", "k", "STRING", "v", null);

        Assert.True(handler.Requests.Count(IsConfigsBulkPost) > postsBefore);
        Assert.Equal(0, config.PendingCount);
    }

    [Fact]
    public void RegisterConfig_Threshold_Stateless_SwallowsFailure()
    {
        var (config, handler) = MakeStateless(_ => Task.FromResult(Json(
            "{\"errors\":[{\"status\":\"500\",\"title\":\"boom\"}]}", HttpStatusCode.InternalServerError)));

        for (int i = 0; i < 50; i++)
            config.RegisterConfig($"cfg-{i}", "svc", "prod");

        // Discovery is best-effort: the failed inline flush never reaches the
        // caller. Drained entries are not requeued.
        Assert.Contains(handler.Requests, IsConfigsBulkPost);
        Assert.Equal(0, config.PendingCount);
    }

    [Fact]
    public async Task RefreshAsync_Stateless_RefetchesAndFiresListeners()
    {
        var (config, _) = MakeStateless(_ => Task.FromResult(Json(ConfigListJson)));

        var fired = new List<string>();
        config.OnChange(evt => fired.Add($"{evt.ConfigId}.{evt.ItemKey}"));

        await config.RefreshAsync();

        // Values are unchanged between fetches, so no spurious change events.
        Assert.Empty(fired);
        Assert.Equal(50L, config.GetValue("billing", "max_seats"));
    }
}
