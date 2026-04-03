using System.Net;
using System.Text;
using Smplkit.Config;
using Smplkit.Errors;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Config;

/// <summary>
/// Tests for the prescriptive config access pattern: typed accessors,
/// RefreshAsync, OnChange, and DiffAndFire.
/// </summary>
public class ConfigPrescriptiveTests
{
    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/vnd.api+json"),
        };

    private static string FlagListJson() => """{"data":[]}""";

    private static string ConfigListJson(
        string id = "cfg-1",
        string key = "app",
        string itemsJson = """{"name": {"value": "Acme", "type": "STRING"}, "count": {"value": 42, "type": "NUMBER"}, "enabled": {"value": true, "type": "BOOLEAN"}}""",
        string envsJson = "{}") =>
        $$"""
        {
            "data": [
                {
                    "id": "{{id}}",
                    "type": "config",
                    "attributes": {
                        "key": "{{key}}",
                        "name": "{{key}}",
                        "description": null,
                        "parent": null,
                        "items": {{itemsJson}},
                        "environments": {{envsJson}},
                        "created_at": null,
                        "updated_at": null
                    }
                }
            ]
        }
        """;

    private static async Task<SmplClient> ConnectClient(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        var mockHandler = new MockHttpMessageHandler(handler);
        var httpClient = new HttpClient(mockHandler);
        var client = new SmplClient(
            new SmplClientOptions { ApiKey = "sk_api_test", Environment = "production" },
            httpClient);
        try { await client.ConnectAsync(); } catch { }
        return client;
    }

    // ------------------------------------------------------------------
    // GetString
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetString_ReturnsStringValue()
    {
        var client = await ConnectClient(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("flags")) return Task.FromResult(JsonResponse(FlagListJson()));
            return Task.FromResult(JsonResponse(ConfigListJson()));
        });

        var val = client.Config.GetString("app", "name");
        Assert.Equal("Acme", val);
    }

    [Fact]
    public async Task GetString_WrongType_ReturnsDefault()
    {
        var client = await ConnectClient(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("flags")) return Task.FromResult(JsonResponse(FlagListJson()));
            return Task.FromResult(JsonResponse(ConfigListJson()));
        });

        var val = client.Config.GetString("app", "count", "fallback");
        Assert.Equal("fallback", val);
    }

    [Fact]
    public async Task GetString_MissingKey_ReturnsNull()
    {
        var client = await ConnectClient(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("flags")) return Task.FromResult(JsonResponse(FlagListJson()));
            return Task.FromResult(JsonResponse(ConfigListJson()));
        });

        var val = client.Config.GetString("app", "missing");
        Assert.Null(val);
    }

    // ------------------------------------------------------------------
    // GetInt
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetInt_ReturnsIntValue()
    {
        var client = await ConnectClient(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("flags")) return Task.FromResult(JsonResponse(FlagListJson()));
            return Task.FromResult(JsonResponse(ConfigListJson()));
        });

        var val = client.Config.GetInt("app", "count");
        Assert.Equal(42, val);
    }

    [Fact]
    public async Task GetInt_WrongType_ReturnsDefault()
    {
        var client = await ConnectClient(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("flags")) return Task.FromResult(JsonResponse(FlagListJson()));
            return Task.FromResult(JsonResponse(ConfigListJson()));
        });

        var val = client.Config.GetInt("app", "name", 99);
        Assert.Equal(99, val);
    }

    [Fact]
    public async Task GetInt_MissingKey_ReturnsNull()
    {
        var client = await ConnectClient(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("flags")) return Task.FromResult(JsonResponse(FlagListJson()));
            return Task.FromResult(JsonResponse(ConfigListJson()));
        });

        var val = client.Config.GetInt("app", "missing");
        Assert.Null(val);
    }

    // ------------------------------------------------------------------
    // GetBool
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetBool_ReturnsBoolValue()
    {
        var client = await ConnectClient(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("flags")) return Task.FromResult(JsonResponse(FlagListJson()));
            return Task.FromResult(JsonResponse(ConfigListJson()));
        });

        var val = client.Config.GetBool("app", "enabled");
        Assert.True(val);
    }

    [Fact]
    public async Task GetBool_WrongType_ReturnsDefault()
    {
        var client = await ConnectClient(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("flags")) return Task.FromResult(JsonResponse(FlagListJson()));
            return Task.FromResult(JsonResponse(ConfigListJson()));
        });

        var val = client.Config.GetBool("app", "name", false);
        Assert.False(val);
    }

    // ------------------------------------------------------------------
    // NotConnected
    // ------------------------------------------------------------------

    [Fact]
    public void GetString_NotConnected_ThrowsSmplNotConnectedException()
    {
        var handler = new MockHttpMessageHandler(_ => Task.FromResult(JsonResponse("{}")));
        var httpClient = new HttpClient(handler);
        var client = new SmplClient(
            new SmplClientOptions { ApiKey = "sk_api_test", Environment = "test" },
            httpClient);

        Assert.Throws<SmplNotConnectedException>(() => client.Config.GetString("app", "name"));
    }

    [Fact]
    public void GetInt_NotConnected_ThrowsSmplNotConnectedException()
    {
        var handler = new MockHttpMessageHandler(_ => Task.FromResult(JsonResponse("{}")));
        var httpClient = new HttpClient(handler);
        var client = new SmplClient(
            new SmplClientOptions { ApiKey = "sk_api_test", Environment = "test" },
            httpClient);

        Assert.Throws<SmplNotConnectedException>(() => client.Config.GetInt("app", "port"));
    }

    [Fact]
    public void GetBool_NotConnected_ThrowsSmplNotConnectedException()
    {
        var handler = new MockHttpMessageHandler(_ => Task.FromResult(JsonResponse("{}")));
        var httpClient = new HttpClient(handler);
        var client = new SmplClient(
            new SmplClientOptions { ApiKey = "sk_api_test", Environment = "test" },
            httpClient);

        Assert.Throws<SmplNotConnectedException>(() => client.Config.GetBool("app", "flag"));
    }

    // ------------------------------------------------------------------
    // RefreshAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task RefreshAsync_UpdatesCache()
    {
        var refreshed = false;
        var client = await ConnectClient(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("flags")) return Task.FromResult(JsonResponse(FlagListJson()));
            if (!refreshed)
                return Task.FromResult(JsonResponse(ConfigListJson(
                    itemsJson: """{"retries": {"value": 3, "type": "NUMBER"}}""")));
            return Task.FromResult(JsonResponse(ConfigListJson(
                itemsJson: """{"retries": {"value": 7, "type": "NUMBER"}}""")));
        });

        var val = client.Config.GetInt("app", "retries");
        Assert.Equal(3, val);

        refreshed = true;
        await client.Config.RefreshAsync();

        val = client.Config.GetInt("app", "retries");
        Assert.Equal(7, val);
    }

    [Fact]
    public async Task RefreshAsync_NotConnected_ThrowsSmplNotConnectedException()
    {
        var handler = new MockHttpMessageHandler(_ => Task.FromResult(JsonResponse("{}")));
        var httpClient = new HttpClient(handler);
        var client = new SmplClient(
            new SmplClientOptions { ApiKey = "sk_api_test", Environment = "test" },
            httpClient);

        await Assert.ThrowsAsync<SmplNotConnectedException>(() => client.Config.RefreshAsync());
    }

    // ------------------------------------------------------------------
    // OnChange
    // ------------------------------------------------------------------

    [Fact]
    public async Task OnChange_FiresOnRefresh()
    {
        var refreshed = false;
        var client = await ConnectClient(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("flags")) return Task.FromResult(JsonResponse(FlagListJson()));
            if (!refreshed)
                return Task.FromResult(JsonResponse(ConfigListJson(
                    itemsJson: """{"retries": {"value": 3, "type": "NUMBER"}}""")));
            return Task.FromResult(JsonResponse(ConfigListJson(
                itemsJson: """{"retries": {"value": 7, "type": "NUMBER"}}""")));
        });

        var events = new List<ConfigChangeEvent>();
        client.Config.OnChange(evt => events.Add(evt));

        refreshed = true;
        await client.Config.RefreshAsync();

        Assert.Single(events);
        Assert.Equal("app", events[0].ConfigKey);
        Assert.Equal("retries", events[0].ItemKey);
        Assert.Equal("manual", events[0].Source);
    }

    [Fact]
    public async Task OnChange_FilteredByConfigAndItem()
    {
        var refreshed = false;
        var client = await ConnectClient(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("flags")) return Task.FromResult(JsonResponse(FlagListJson()));
            if (!refreshed)
                return Task.FromResult(JsonResponse(ConfigListJson(
                    itemsJson: """{"retries": {"value": 3, "type": "NUMBER"}, "timeout": {"value": 1000, "type": "NUMBER"}}""")));
            return Task.FromResult(JsonResponse(ConfigListJson(
                itemsJson: """{"retries": {"value": 7, "type": "NUMBER"}, "timeout": {"value": 2000, "type": "NUMBER"}}""")));
        });

        var retriesEvents = new List<ConfigChangeEvent>();
        client.Config.OnChange(
            evt => retriesEvents.Add(evt),
            configKey: "app",
            itemKey: "retries");

        refreshed = true;
        await client.Config.RefreshAsync();

        // Should only get the retries change, not timeout
        Assert.Single(retriesEvents);
        Assert.Equal("retries", retriesEvents[0].ItemKey);
    }

    [Fact]
    public async Task OnChange_FilteredByConfigKeyOnly()
    {
        var refreshed = false;
        var client = await ConnectClient(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("flags")) return Task.FromResult(JsonResponse(FlagListJson()));
            if (!refreshed)
                return Task.FromResult(JsonResponse(ConfigListJson(
                    itemsJson: """{"retries": {"value": 3, "type": "NUMBER"}}""")));
            return Task.FromResult(JsonResponse(ConfigListJson(
                itemsJson: """{"retries": {"value": 7, "type": "NUMBER"}}""")));
        });

        var events = new List<ConfigChangeEvent>();
        client.Config.OnChange(evt => events.Add(evt), configKey: "app");

        var otherEvents = new List<ConfigChangeEvent>();
        client.Config.OnChange(evt => otherEvents.Add(evt), configKey: "other_config");

        refreshed = true;
        await client.Config.RefreshAsync();

        Assert.Single(events);
        Assert.Empty(otherEvents);
    }

    [Fact]
    public async Task OnChange_ListenerExceptionDoesNotPropagate()
    {
        var refreshed = false;
        var client = await ConnectClient(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("flags")) return Task.FromResult(JsonResponse(FlagListJson()));
            if (!refreshed)
                return Task.FromResult(JsonResponse(ConfigListJson(
                    itemsJson: """{"retries": {"value": 3, "type": "NUMBER"}}""")));
            return Task.FromResult(JsonResponse(ConfigListJson(
                itemsJson: """{"retries": {"value": 7, "type": "NUMBER"}}""")));
        });

        var events = new List<ConfigChangeEvent>();
        // First listener throws
        client.Config.OnChange(_ => throw new InvalidOperationException("bad listener"));
        // Second listener should still fire
        client.Config.OnChange(evt => events.Add(evt));

        refreshed = true;
        await client.Config.RefreshAsync();

        Assert.Single(events);
    }

    // ------------------------------------------------------------------
    // DiffAndFire edge cases
    // ------------------------------------------------------------------

    [Fact]
    public async Task DiffAndFire_NoListeners_DoesNotThrow()
    {
        var refreshed = false;
        var client = await ConnectClient(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("flags")) return Task.FromResult(JsonResponse(FlagListJson()));
            if (!refreshed)
                return Task.FromResult(JsonResponse(ConfigListJson(
                    itemsJson: """{"retries": {"value": 3, "type": "NUMBER"}}""")));
            return Task.FromResult(JsonResponse(ConfigListJson(
                itemsJson: """{"retries": {"value": 7, "type": "NUMBER"}}""")));
        });

        // No listeners registered — should not throw
        refreshed = true;
        await client.Config.RefreshAsync();
    }

    [Fact]
    public async Task DiffAndFire_NewConfig()
    {
        // Start with one config, refresh adds another
        var refreshed = false;
        var client = await ConnectClient(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("flags")) return Task.FromResult(JsonResponse(FlagListJson()));
            if (!refreshed)
                return Task.FromResult(JsonResponse(ConfigListJson(
                    itemsJson: """{"a": {"value": 1, "type": "NUMBER"}}""")));
            // After refresh, return two configs
            return Task.FromResult(JsonResponse("""
            {
                "data": [
                    {
                        "id": "cfg-1", "type": "config",
                        "attributes": {
                            "key": "app", "name": "app", "description": null, "parent": null,
                            "items": {"a": {"value": 1, "type": "NUMBER"}},
                            "environments": {}, "created_at": null, "updated_at": null
                        }
                    },
                    {
                        "id": "cfg-2", "type": "config",
                        "attributes": {
                            "key": "new_config", "name": "new_config", "description": null, "parent": null,
                            "items": {"b": {"value": 2, "type": "NUMBER"}},
                            "environments": {}, "created_at": null, "updated_at": null
                        }
                    }
                ]
            }
            """));
        });

        var events = new List<ConfigChangeEvent>();
        client.Config.OnChange(evt => events.Add(evt));

        refreshed = true;
        await client.Config.RefreshAsync();

        Assert.Single(events);
        Assert.Equal("new_config", events[0].ConfigKey);
        Assert.Equal("b", events[0].ItemKey);
        Assert.Null(events[0].OldValue);
    }

    [Fact]
    public async Task DiffAndFire_RemovedKey()
    {
        var refreshed = false;
        var client = await ConnectClient(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("flags")) return Task.FromResult(JsonResponse(FlagListJson()));
            if (!refreshed)
                return Task.FromResult(JsonResponse(ConfigListJson(
                    itemsJson: """{"a": {"value": 1, "type": "NUMBER"}, "b": {"value": 2, "type": "NUMBER"}}""")));
            return Task.FromResult(JsonResponse(ConfigListJson(
                itemsJson: """{"a": {"value": 1, "type": "NUMBER"}}""")));
        });

        var events = new List<ConfigChangeEvent>();
        client.Config.OnChange(evt => events.Add(evt));

        refreshed = true;
        await client.Config.RefreshAsync();

        Assert.Single(events);
        Assert.Equal("b", events[0].ItemKey);
        Assert.Null(events[0].NewValue);
    }

    // ------------------------------------------------------------------
    // GetInt — double and long edge cases
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetInt_DoubleWholeNumber_ReturnsInt()
    {
        // JSON 42.0 may come through as double after normalization
        var client = await ConnectClient(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("flags")) return Task.FromResult(JsonResponse(FlagListJson()));
            return Task.FromResult(JsonResponse(ConfigListJson(
                itemsJson: """{"price": {"value": 42.0, "type": "NUMBER"}}""")));
        });

        var val = client.Config.GetInt("app", "price");
        // 42.0 in JSON: if TryGetInt64 succeeds → long → (int); if not → double → (int)
        Assert.Equal(42, val);
    }

    [Fact]
    public async Task GetInt_DoubleWithFraction_ReturnsDefault()
    {
        var client = await ConnectClient(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("flags")) return Task.FromResult(JsonResponse(FlagListJson()));
            return Task.FromResult(JsonResponse(ConfigListJson(
                itemsJson: """{"ratio": {"value": 3.14, "type": "NUMBER"}}""")));
        });

        var val = client.Config.GetInt("app", "ratio", 0);
        Assert.Equal(0, val);
    }

    // ------------------------------------------------------------------
    // RefreshAsync — parent chain walking
    // ------------------------------------------------------------------

    [Fact]
    public async Task RefreshAsync_ParentChain_ResolvesInheritance()
    {
        var refreshed = false;
        var twoConfigJson = """
        {
            "data": [
                {
                    "id": "cfg-parent", "type": "config",
                    "attributes": {
                        "key": "common", "name": "common", "description": null, "parent": null,
                        "items": {"retries": {"value": 3, "type": "NUMBER"}, "timeout": {"value": 1000, "type": "NUMBER"}},
                        "environments": {}, "created_at": null, "updated_at": null
                    }
                },
                {
                    "id": "cfg-child", "type": "config",
                    "attributes": {
                        "key": "service", "name": "service", "description": null, "parent": "cfg-parent",
                        "items": {"retries": {"value": 5, "type": "NUMBER"}},
                        "environments": {}, "created_at": null, "updated_at": null
                    }
                }
            ]
        }
        """;
        var refreshJson = """
        {
            "data": [
                {
                    "id": "cfg-parent", "type": "config",
                    "attributes": {
                        "key": "common", "name": "common", "description": null, "parent": null,
                        "items": {"retries": {"value": 3, "type": "NUMBER"}, "timeout": {"value": 2000, "type": "NUMBER"}},
                        "environments": {}, "created_at": null, "updated_at": null
                    }
                },
                {
                    "id": "cfg-child", "type": "config",
                    "attributes": {
                        "key": "service", "name": "service", "description": null, "parent": "cfg-parent",
                        "items": {"retries": {"value": 5, "type": "NUMBER"}},
                        "environments": {}, "created_at": null, "updated_at": null
                    }
                }
            ]
        }
        """;

        var client = await ConnectClient(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("flags")) return Task.FromResult(JsonResponse(FlagListJson()));
            if (!refreshed) return Task.FromResult(JsonResponse(twoConfigJson));
            return Task.FromResult(JsonResponse(refreshJson));
        });

        // Child overrides parent retries, inherits timeout
        var retries = client.Config.GetInt("service", "retries");
        Assert.Equal(5, retries);
        var timeout = client.Config.GetInt("service", "timeout");
        Assert.Equal(1000, timeout);

        // Refresh with updated parent timeout
        refreshed = true;
        await client.Config.RefreshAsync();

        timeout = client.Config.GetInt("service", "timeout");
        Assert.Equal(2000, timeout);
    }

    // ------------------------------------------------------------------
    // Singleton accessor identity
    // ------------------------------------------------------------------

    [Fact]
    public void SingletonAccessor_Config_ReturnsSameInstance()
    {
        var handler = new MockHttpMessageHandler(_ => Task.FromResult(JsonResponse("{}")));
        var httpClient = new HttpClient(handler);
        var client = new SmplClient(
            new SmplClientOptions { ApiKey = "sk_api_test", Environment = "test" },
            httpClient);

        Assert.Same(client.Config, client.Config);
        Assert.Same(client.Flags, client.Flags);
    }
}
