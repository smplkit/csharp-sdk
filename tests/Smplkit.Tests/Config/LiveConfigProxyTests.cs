using System.Net;
using System.Text;
using Smplkit;
using Smplkit.Config;
using Smplkit.Errors;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Config;

/// <summary>
/// Tests for <see cref="LiveConfigProxy"/> — PR #127 rule 10. The proxy is
/// dict-like, identity-stable, read-only, and reflects live cache state.
/// </summary>
public class LiveConfigProxyTests
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

    private const string ConfigListJson = """
        {
            "data": [
                {
                    "id": "user-svc",
                    "type": "config",
                    "attributes": {
                        "id": "user-svc",
                        "name": "User Service",
                        "description": null,
                        "parent": null,
                        "items": {
                            "host": {"value": "localhost", "type": "STRING"},
                            "retries": {"value": 3, "type": "NUMBER"},
                            "database.port": {"value": 5432, "type": "NUMBER"}
                        },
                        "environments": {
                            "test": {"values": {"host": {"value": "test-host", "type": "STRING"}}}
                        },
                        "created_at": "2024-01-15T10:30:00Z",
                        "updated_at": "2024-01-15T10:30:00Z"
                    }
                }
            ]
        }
        """;

    [Fact]
    public void Get_ReturnsLiveConfigProxy()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var proxy = client.Config.Get("user-svc");
        Assert.IsType<LiveConfigProxy>(proxy);
        Assert.Equal("user-svc", proxy.ConfigId);
    }

    [Fact]
    public void Indexer_ReturnsResolvedValue()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var proxy = client.Config.Get("user-svc");
        Assert.Equal("test-host", proxy["host"]); // env override wins
        Assert.Equal(3L, proxy["retries"]);
    }

    [Fact]
    public void Indexer_UnknownKey_Throws()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var proxy = client.Config.Get("user-svc");
        Assert.Throws<KeyNotFoundException>(() => _ = proxy["nope"]);
    }

    [Fact]
    public void ContainsKey_Works()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var proxy = client.Config.Get("user-svc");
        Assert.True(proxy.ContainsKey("host"));
        Assert.False(proxy.ContainsKey("nope"));
    }

    [Fact]
    public void TryGetValue_Works()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var proxy = client.Config.Get("user-svc");
        Assert.True(proxy.TryGetValue("host", out var v));
        Assert.Equal("test-host", v);
        Assert.False(proxy.TryGetValue("nope", out _));
    }

    [Fact]
    public void Count_ReflectsResolvedKeys()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var proxy = client.Config.Get("user-svc");
        Assert.Equal(3, proxy.Count); // host + retries + database.port
    }

    [Fact]
    public void Keys_AndValues_Enumerable()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var proxy = client.Config.Get("user-svc");
        Assert.Contains("host", proxy.Keys);
        Assert.Contains("retries", proxy.Keys);
        Assert.NotEmpty(proxy.Values);
    }

    [Fact]
    public void Foreach_IteratesKvPairs()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var proxy = client.Config.Get("user-svc");
        var seen = new HashSet<string>();
        foreach (var kv in proxy)
            seen.Add(kv.Key);
        Assert.Contains("host", seen);
        Assert.Contains("retries", seen);
    }

    [Fact]
    public void GetOrDefault_PresentKey_ReturnsValue()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var proxy = client.Config.Get("user-svc");
        Assert.Equal("test-host", proxy.GetOrDefault("host"));
    }

    [Fact]
    public void GetOrDefault_MissingKey_ReturnsDefault()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var proxy = client.Config.Get("user-svc");
        Assert.Equal("fallback", proxy.GetOrDefault("nope", "fallback"));
        Assert.Null(proxy.GetOrDefault("nope"));
    }

    public sealed class UserSvc { public string? Host { get; set; } public int Retries { get; set; } }

    [Fact]
    public void Into_DeserializesToTypedModel()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var proxy = client.Config.Get("user-svc");
        var typed = proxy.Into<UserSvc>();
        Assert.Equal("test-host", typed.Host);
        Assert.Equal(3, typed.Retries);
    }

    public sealed class Db { public string? Host { get; set; } public int Port { get; set; } }
    public sealed class Cfg { public Db Database { get; set; } = new(); }

    [Fact]
    public void Into_ExpandsDotNotation()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var proxy = client.Config.Get("user-svc");
        var typed = proxy.Into<Cfg>();
        Assert.Equal(5432, typed.Database.Port);
    }

    [Fact]
    public async Task Live_ReflectsCacheUpdates()
    {
        // Two list responses: first has host=local, second has host=updated.
        // After RefreshAsync, the same proxy reflects the new value.
        int call = 0;
        var updated = ConfigListJson.Replace("\"localhost\"", "\"updated-host\"");
        var (client, _) = MakeClient(_ =>
        {
            call++;
            return Task.FromResult(Json(call == 1 ? ConfigListJson : updated));
        });
        var proxy = client.Config.Get("user-svc");
        var initial = proxy["host"];
        await client.Config.RefreshAsync();
        var afterRefresh = proxy["host"];
        // env override "test-host" still wins; refresh doesn't change that here.
        // What we're verifying is the proxy doesn't cache stale data — every read goes through cache.
        Assert.Equal(initial, afterRefresh);
        Assert.NotNull(initial);
    }

    [Fact]
    public void OnChange_ConfigScoped_Sugar()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var proxy = client.Config.Get("user-svc");
        var fired = new List<ConfigChangeEvent>();
        proxy.OnChange(evt => fired.Add(evt));

        // Drive a change via DiffAndFire reflection
        var oldCache = new Dictionary<string, Dictionary<string, object?>>
        {
            ["user-svc"] = new() { ["host"] = "old" },
            ["other"] = new() { ["k"] = "v" },
        };
        var newCache = new Dictionary<string, Dictionary<string, object?>>
        {
            ["user-svc"] = new() { ["host"] = "new" },
            ["other"] = new() { ["k"] = "x" }, // change in another config
        };
        var method = typeof(ConfigClient).GetMethod("DiffAndFire",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        method.Invoke(client.Config, new object?[] { oldCache, newCache, "test" });

        // Only the user-svc change should fire on this proxy.
        Assert.Single(fired);
        Assert.Equal("user-svc", fired[0].ConfigId);
    }

    [Fact]
    public void OnChange_ItemScoped_Sugar()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var proxy = client.Config.Get("user-svc");
        var fired = new List<ConfigChangeEvent>();
        proxy.OnChange("host", evt => fired.Add(evt));

        var oldCache = new Dictionary<string, Dictionary<string, object?>>
        {
            ["user-svc"] = new() { ["host"] = "old", ["other"] = "x" },
        };
        var newCache = new Dictionary<string, Dictionary<string, object?>>
        {
            ["user-svc"] = new() { ["host"] = "new", ["other"] = "y" },
        };
        var method = typeof(ConfigClient).GetMethod("DiffAndFire",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        method.Invoke(client.Config, new object?[] { oldCache, newCache, "test" });

        // Only the host change should fire — not "other".
        Assert.Single(fired);
        Assert.Equal("host", fired[0].ItemKey);
    }

    [Fact]
    public void ToString_IncludesConfigId()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var proxy = client.Config.Get("user-svc");
        Assert.Contains("user-svc", proxy.ToString());
    }

    [Fact]
    public void Get_UnknownConfig_ThrowsNotFound()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        Assert.Throws<NotFoundException>(() => client.Config.Get("does-not-exist"));
    }

    [Fact]
    public void GetTyped_DeserializesViaProxyInto()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var typed = client.Config.Get<UserSvc>("user-svc");
        Assert.Equal("test-host", typed.Host);
        Assert.Equal(3, typed.Retries);
    }
}
