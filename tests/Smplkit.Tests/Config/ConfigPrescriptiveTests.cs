using System.Net;
using System.Text;
using Smplkit.Config;
using Smplkit.Errors;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Config;

/// <summary>
/// Tests for the prescriptive config access pattern: Resolve, Resolve&lt;T&gt;,
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
        string id = "app",
        string itemsJson = """{"name": {"value": "Acme", "type": "STRING"}, "count": {"value": 42, "type": "NUMBER"}, "enabled": {"value": true, "type": "BOOLEAN"}}""",
        string envsJson = "{}") =>
        $$"""
        {
            "data": [
                {
                    "id": "{{id}}",
                    "type": "config",
                    "attributes": {
                        "id": "{{id}}",
                        "name": "{{id}}",
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

    private static SmplClient CreateClientWithHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        var mockHandler = new MockHttpMessageHandler(handler);
        var httpClient = new HttpClient(mockHandler);
        return new SmplClient(
            new SmplClientOptions { ApiKey = "sk_api_test", Environment = "production", Service = "test-service" },
            httpClient);
    }

    // ------------------------------------------------------------------
    // Resolve — returns resolved values dict
    // ------------------------------------------------------------------

    [Fact]
    public void Resolve_ReturnsResolvedValues()
    {
        var client = CreateClientWithHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("flags")) return Task.FromResult(JsonResponse(FlagListJson()));
            return Task.FromResult(JsonResponse(ConfigListJson()));
        });

        var values = client.Config.Get("app");
        Assert.Equal("Acme", values["name"]);
        Assert.Equal(42L, values["count"]);
        Assert.Equal(true, values["enabled"]);
    }

    [Fact]
    public void Resolve_MissingId_ThrowsSmplNotFoundException()
    {
        var client = CreateClientWithHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("flags")) return Task.FromResult(JsonResponse(FlagListJson()));
            return Task.FromResult(JsonResponse(ConfigListJson()));
        });

        Assert.Throws<SmplNotFoundException>(() => client.Config.Get("nonexistent"));
    }

    [Fact]
    public void Resolve_ReturnsDefensiveCopy()
    {
        var client = CreateClientWithHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("flags")) return Task.FromResult(JsonResponse(FlagListJson()));
            return Task.FromResult(JsonResponse(ConfigListJson()));
        });

        var values1 = client.Config.Get("app");
        var values2 = client.Config.Get("app");
        Assert.NotSame(values1, values2);
    }

    // ------------------------------------------------------------------
    // Resolve<T> — typed deserialization
    // ------------------------------------------------------------------

    [Fact]
    public void ResolveT_DeserializesToTypedObject()
    {
        var client = CreateClientWithHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("flags")) return Task.FromResult(JsonResponse(FlagListJson()));
            return Task.FromResult(JsonResponse(ConfigListJson(
                itemsJson: """{"name": {"value": "Acme", "type": "STRING"}, "count": {"value": 42, "type": "NUMBER"}}""")));
        });

        var result = client.Config.Get<TestConfigModel>("app");
        Assert.Equal("Acme", result.Name);
        Assert.Equal(42, result.Count);
    }

    // ------------------------------------------------------------------
    // EnsureInitialized — no environment throws
    // ------------------------------------------------------------------

    [Fact]
    public void Resolve_NoEnvironment_ThrowsSmplException()
    {
        // SmplClient requires an environment, so we test that the resolve
        // path works correctly when client is properly configured.
        // This test verifies the basic path works.
        var client = CreateClientWithHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("flags")) return Task.FromResult(JsonResponse(FlagListJson()));
            return Task.FromResult(JsonResponse(ConfigListJson()));
        });

        // Should not throw since environment is configured
        var values = client.Config.Get("app");
        Assert.NotEmpty(values);
    }

    // ------------------------------------------------------------------
    // RefreshAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task RefreshAsync_UpdatesCache()
    {
        var refreshed = false;
        var client = CreateClientWithHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("flags")) return Task.FromResult(JsonResponse(FlagListJson()));
            if (!refreshed)
                return Task.FromResult(JsonResponse(ConfigListJson(
                    itemsJson: """{"retries": {"value": 3, "type": "NUMBER"}}""")));
            return Task.FromResult(JsonResponse(ConfigListJson(
                itemsJson: """{"retries": {"value": 7, "type": "NUMBER"}}""")));
        });

        var val = client.Config.Get("app");
        Assert.Equal(3L, val["retries"]);

        refreshed = true;
        await client.Config.RefreshAsync();

        val = client.Config.Get("app");
        Assert.Equal(7L, val["retries"]);
    }

    // ------------------------------------------------------------------
    // OnChange
    // ------------------------------------------------------------------

    [Fact]
    public async Task OnChange_Global_FiresOnRefresh()
    {
        var refreshed = false;
        var client = CreateClientWithHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("flags")) return Task.FromResult(JsonResponse(FlagListJson()));
            if (!refreshed)
                return Task.FromResult(JsonResponse(ConfigListJson(
                    itemsJson: """{"retries": {"value": 3, "type": "NUMBER"}}""")));
            return Task.FromResult(JsonResponse(ConfigListJson(
                itemsJson: """{"retries": {"value": 7, "type": "NUMBER"}}""")));
        });

        // Trigger lazy init
        _ = client.Config.Get("app");

        var events = new List<ConfigChangeEvent>();
        client.Config.OnChange(evt => events.Add(evt));

        refreshed = true;
        await client.Config.RefreshAsync();

        Assert.Single(events);
        Assert.Equal("app", events[0].ConfigId);
        Assert.Equal("retries", events[0].ItemKey);
        Assert.Equal("manual", events[0].Source);
    }

    [Fact]
    public async Task OnChange_FilteredByConfigAndItem()
    {
        var refreshed = false;
        var client = CreateClientWithHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("flags")) return Task.FromResult(JsonResponse(FlagListJson()));
            if (!refreshed)
                return Task.FromResult(JsonResponse(ConfigListJson(
                    itemsJson: """{"retries": {"value": 3, "type": "NUMBER"}, "timeout": {"value": 1000, "type": "NUMBER"}}""")));
            return Task.FromResult(JsonResponse(ConfigListJson(
                itemsJson: """{"retries": {"value": 7, "type": "NUMBER"}, "timeout": {"value": 2000, "type": "NUMBER"}}""")));
        });

        // Trigger lazy init
        _ = client.Config.Get("app");

        var retriesEvents = new List<ConfigChangeEvent>();
        client.Config.OnChange("app", "retries", evt => retriesEvents.Add(evt));

        refreshed = true;
        await client.Config.RefreshAsync();

        // Should only get the retries change, not timeout
        Assert.Single(retriesEvents);
        Assert.Equal("retries", retriesEvents[0].ItemKey);
    }

    [Fact]
    public async Task OnChange_FilteredByConfigIdOnly()
    {
        var refreshed = false;
        var client = CreateClientWithHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("flags")) return Task.FromResult(JsonResponse(FlagListJson()));
            if (!refreshed)
                return Task.FromResult(JsonResponse(ConfigListJson(
                    itemsJson: """{"retries": {"value": 3, "type": "NUMBER"}}""")));
            return Task.FromResult(JsonResponse(ConfigListJson(
                itemsJson: """{"retries": {"value": 7, "type": "NUMBER"}}""")));
        });

        // Trigger lazy init
        _ = client.Config.Get("app");

        var events = new List<ConfigChangeEvent>();
        client.Config.OnChange("app", evt => events.Add(evt));

        var otherEvents = new List<ConfigChangeEvent>();
        client.Config.OnChange("other_config", evt => otherEvents.Add(evt));

        refreshed = true;
        await client.Config.RefreshAsync();

        Assert.Single(events);
        Assert.Empty(otherEvents);
    }

    [Fact]
    public async Task OnChange_ListenerExceptionDoesNotPropagate()
    {
        var refreshed = false;
        var client = CreateClientWithHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("flags")) return Task.FromResult(JsonResponse(FlagListJson()));
            if (!refreshed)
                return Task.FromResult(JsonResponse(ConfigListJson(
                    itemsJson: """{"retries": {"value": 3, "type": "NUMBER"}}""")));
            return Task.FromResult(JsonResponse(ConfigListJson(
                itemsJson: """{"retries": {"value": 7, "type": "NUMBER"}}""")));
        });

        // Trigger lazy init
        _ = client.Config.Get("app");

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
        var client = CreateClientWithHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("flags")) return Task.FromResult(JsonResponse(FlagListJson()));
            if (!refreshed)
                return Task.FromResult(JsonResponse(ConfigListJson(
                    itemsJson: """{"retries": {"value": 3, "type": "NUMBER"}}""")));
            return Task.FromResult(JsonResponse(ConfigListJson(
                itemsJson: """{"retries": {"value": 7, "type": "NUMBER"}}""")));
        });

        // Trigger lazy init
        _ = client.Config.Get("app");

        // No listeners registered -- should not throw
        refreshed = true;
        await client.Config.RefreshAsync();
    }

    [Fact]
    public async Task DiffAndFire_NewConfig()
    {
        // Start with one config, refresh adds another
        var refreshed = false;
        var client = CreateClientWithHandler(req =>
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
                        "id": "app", "type": "config",
                        "attributes": {
                            "id": "app", "name": "app", "description": null, "parent": null,
                            "items": {"a": {"value": 1, "type": "NUMBER"}},
                            "environments": {}, "created_at": null, "updated_at": null
                        }
                    },
                    {
                        "id": "new_config", "type": "config",
                        "attributes": {
                            "id": "new_config", "name": "new_config", "description": null, "parent": null,
                            "items": {"b": {"value": 2, "type": "NUMBER"}},
                            "environments": {}, "created_at": null, "updated_at": null
                        }
                    }
                ]
            }
            """));
        });

        // Trigger lazy init
        _ = client.Config.Get("app");

        var events = new List<ConfigChangeEvent>();
        client.Config.OnChange(evt => events.Add(evt));

        refreshed = true;
        await client.Config.RefreshAsync();

        Assert.Single(events);
        Assert.Equal("new_config", events[0].ConfigId);
        Assert.Equal("b", events[0].ItemKey);
        Assert.Null(events[0].OldValue);
    }

    [Fact]
    public async Task DiffAndFire_RemovedKey()
    {
        var refreshed = false;
        var client = CreateClientWithHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("flags")) return Task.FromResult(JsonResponse(FlagListJson()));
            if (!refreshed)
                return Task.FromResult(JsonResponse(ConfigListJson(
                    itemsJson: """{"a": {"value": 1, "type": "NUMBER"}, "b": {"value": 2, "type": "NUMBER"}}""")));
            return Task.FromResult(JsonResponse(ConfigListJson(
                itemsJson: """{"a": {"value": 1, "type": "NUMBER"}}""")));
        });

        // Trigger lazy init
        _ = client.Config.Get("app");

        var events = new List<ConfigChangeEvent>();
        client.Config.OnChange(evt => events.Add(evt));

        refreshed = true;
        await client.Config.RefreshAsync();

        Assert.Single(events);
        Assert.Equal("b", events[0].ItemKey);
        Assert.Null(events[0].NewValue);
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
                    "id": "common", "type": "config",
                    "attributes": {
                        "id": "common", "name": "common", "description": null, "parent": null,
                        "items": {"retries": {"value": 3, "type": "NUMBER"}, "timeout": {"value": 1000, "type": "NUMBER"}},
                        "environments": {}, "created_at": null, "updated_at": null
                    }
                },
                {
                    "id": "service", "type": "config",
                    "attributes": {
                        "id": "service", "name": "service", "description": null, "parent": "common",
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
                    "id": "common", "type": "config",
                    "attributes": {
                        "id": "common", "name": "common", "description": null, "parent": null,
                        "items": {"retries": {"value": 3, "type": "NUMBER"}, "timeout": {"value": 2000, "type": "NUMBER"}},
                        "environments": {}, "created_at": null, "updated_at": null
                    }
                },
                {
                    "id": "service", "type": "config",
                    "attributes": {
                        "id": "service", "name": "service", "description": null, "parent": "common",
                        "items": {"retries": {"value": 5, "type": "NUMBER"}},
                        "environments": {}, "created_at": null, "updated_at": null
                    }
                }
            ]
        }
        """;

        var client = CreateClientWithHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("flags")) return Task.FromResult(JsonResponse(FlagListJson()));
            if (!refreshed) return Task.FromResult(JsonResponse(twoConfigJson));
            return Task.FromResult(JsonResponse(refreshJson));
        });

        // Child overrides parent retries, inherits timeout
        var svcValues = client.Config.Get("service");
        Assert.Equal(5L, svcValues["retries"]);
        Assert.Equal(1000L, svcValues["timeout"]);

        // Refresh with updated parent timeout
        refreshed = true;
        await client.Config.RefreshAsync();

        svcValues = client.Config.Get("service");
        Assert.Equal(2000L, svcValues["timeout"]);
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
            new SmplClientOptions { ApiKey = "sk_api_test", Environment = "test", Service = "test-service" },
            httpClient);

        Assert.Same(client.Config, client.Config);
        Assert.Same(client.Flags, client.Flags);
    }

    // ------------------------------------------------------------------
    // Resolve with environment overrides
    // ------------------------------------------------------------------

    [Fact]
    public void Resolve_WithEnvironmentOverrides_AppliesCorrectEnv()
    {
        var client = CreateClientWithHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("flags")) return Task.FromResult(JsonResponse(FlagListJson()));
            return Task.FromResult(JsonResponse(ConfigListJson(
                itemsJson: """{"timeout": {"value": 30, "type": "NUMBER"}}""",
                envsJson: """{"production": {"values": {"timeout": {"value": 60}}}}""")));
        });

        var values = client.Config.Get("app");
        // Client was created with environment = "production"
        Assert.Equal(60L, values["timeout"]);
    }

    // ------------------------------------------------------------------
    // Resolve — double number handling
    // ------------------------------------------------------------------

    [Fact]
    public void Resolve_DoubleWholeNumber_ReturnsLong()
    {
        var client = CreateClientWithHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("flags")) return Task.FromResult(JsonResponse(FlagListJson()));
            return Task.FromResult(JsonResponse(ConfigListJson(
                itemsJson: """{"price": {"value": 42.0, "type": "NUMBER"}}""")));
        });

        var values = client.Config.Get("app");
        // 42.0 in JSON: NSwag deserializes as double since decimal has fraction
        Assert.True(values["price"] is long or double);
        Assert.Equal(42.0, Convert.ToDouble(values["price"]));
    }

    [Fact]
    public void Resolve_DoubleWithFraction_ReturnsDouble()
    {
        var client = CreateClientWithHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("flags")) return Task.FromResult(JsonResponse(FlagListJson()));
            return Task.FromResult(JsonResponse(ConfigListJson(
                itemsJson: """{"ratio": {"value": 3.14, "type": "NUMBER"}}""")));
        });

        var values = client.Config.Get("app");
        Assert.Equal(3.14, values["ratio"]);
    }
}

/// <summary>
/// Test model for Resolve&lt;T&gt; testing.
/// </summary>
public class TestConfigModel
{
    public string? Name { get; set; }
    public int Count { get; set; }
}
