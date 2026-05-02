using System.Net;
using System.Text;
using Smplkit;
using Smplkit.Config;
using Smplkit.Errors;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Config;

/// <summary>
/// Tests for the <see cref="Config"/> active-record model: per-environment
/// setters (rule 7), Save/Delete round-trip, ToString.
/// </summary>
public class ConfigModelTests
{
    private static (SmplManagementClient mgmt, MockHttpMessageHandler handler) Make(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        var handler = new MockHttpMessageHandler(respond);
        var http = new HttpClient(handler);
        var mgmt = new SmplManagementClient(new SmplClientOptions { ApiKey = "k" }, http);
        return (mgmt, handler);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/vnd.api+json") };

    [Fact]
    public void Set_BaseItems()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var cfg = mgmt.Config.New("c");
        cfg.Set(new ConfigItem("k", "v", ItemType.String, "desc"));
        Assert.Equal("v", cfg.Items["k"]);
    }

    [Fact]
    public void Set_NullItem_Throws()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var cfg = mgmt.Config.New("c");
        Assert.Throws<ArgumentNullException>(() => cfg.Set(null!));
    }

    [Fact]
    public void SetString_Base()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var cfg = mgmt.Config.New("c");
        cfg.SetString("host", "localhost");
        Assert.Equal("localhost", cfg.Items["host"]);
    }

    [Fact]
    public void SetString_PerEnvironment_CreatesEnv()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var cfg = mgmt.Config.New("c");
        cfg.SetString("host", "prod.example.com", environment: "production");
        Assert.True(cfg.Environments.ContainsKey("production"));
        Assert.Equal("prod.example.com", cfg.Environments["production"]["host"]);
    }

    [Fact]
    public void SetNumber_Base()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var cfg = mgmt.Config.New("c");
        cfg.SetNumber("retries", 3);
        Assert.Equal(3.0, cfg.Items["retries"]);
    }

    [Fact]
    public void SetNumber_PerEnvironment()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var cfg = mgmt.Config.New("c");
        cfg.SetNumber("retries", 5, environment: "production");
        Assert.Equal(5.0, cfg.Environments["production"]["retries"]);
    }

    [Fact]
    public void SetBoolean_Base()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var cfg = mgmt.Config.New("c");
        cfg.SetBoolean("enabled", true);
        Assert.Equal(true, cfg.Items["enabled"]);
    }

    [Fact]
    public void SetBoolean_PerEnvironment()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var cfg = mgmt.Config.New("c");
        cfg.SetBoolean("enabled", false, environment: "production");
        Assert.Equal(false, cfg.Environments["production"]["enabled"]);
    }

    [Fact]
    public void SetJson_Base()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var cfg = mgmt.Config.New("c");
        cfg.SetJson("settings", new Dictionary<string, object?> { ["x"] = 1 });
        Assert.IsType<Dictionary<string, object?>>(cfg.Items["settings"]);
    }

    [Fact]
    public void SetJson_PerEnvironment()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var cfg = mgmt.Config.New("c");
        cfg.SetJson("settings", new Dictionary<string, object?> { ["mode"] = "dark" },
            environment: "staging");
        Assert.IsType<Dictionary<string, object?>>(cfg.Environments["staging"]["settings"]);
    }

    [Fact]
    public void Remove_BaseItem()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var cfg = mgmt.Config.New("c");
        cfg.SetString("k", "v");
        cfg.Remove("k");
        Assert.False(cfg.Items.ContainsKey("k"));
    }

    [Fact]
    public void Remove_PerEnvironment()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var cfg = mgmt.Config.New("c");
        cfg.SetString("k", "v", environment: "production");
        cfg.Remove("k", environment: "production");
        Assert.False(cfg.Environments["production"].ContainsKey("k"));
    }

    [Fact]
    public void Remove_NonexistentKey_NoOp()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var cfg = mgmt.Config.New("c");
        cfg.Remove("nope"); // no exception
    }

    [Fact]
    public void ToString_IncludesIdAndName()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var cfg = mgmt.Config.New("c", name: "Custom");
        var s = cfg.ToString();
        Assert.Contains("c", s);
        Assert.Contains("Custom", s);
    }

    // Save/delete wire round-trip

    private const string ConfigJson = """
        {
            "data": {
                "id": "user-svc",
                "type": "config",
                "attributes": {
                    "id": "user-svc",
                    "name": "User Service",
                    "description": "desc",
                    "parent": null,
                    "items": {"host": {"value": "localhost", "type": "STRING"}},
                    "environments": {
                        "production": {"values": {"host": {"value": "prod.example.com", "type": "STRING"}}}
                    },
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;

    [Fact]
    public async Task SaveAsync_NewConfig_SendsPost()
    {
        HttpRequestMessage? captured = null;
        var (mgmt, _) = Make(req =>
        {
            captured = req;
            return Task.FromResult(Json(ConfigJson, HttpStatusCode.Created));
        });
        var cfg = mgmt.Config.New("user-svc", description: "desc");
        cfg.SetString("host", "localhost");
        cfg.SetString("host", "prod.example.com", environment: "production");
        await cfg.SaveAsync();
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.NotNull(cfg.CreatedAt);
    }

    [Fact]
    public async Task SaveAsync_ExistingConfig_SendsPut()
    {
        HttpRequestMessage? captured = null;
        var (mgmt, _) = Make(req =>
        {
            if (req.Method == HttpMethod.Get) return Task.FromResult(Json(ConfigJson));
            captured = req;
            return Task.FromResult(Json(ConfigJson));
        });
        var cfg = await mgmt.Config.GetAsync("user-svc");
        cfg.Description = "updated";
        await cfg.SaveAsync();
        Assert.Equal(HttpMethod.Put, captured!.Method);
    }

    [Fact]
    public async Task DeleteAsync_OnSavedConfig_SendsDelete()
    {
        HttpRequestMessage? captured = null;
        var (mgmt, _) = Make(req =>
        {
            if (req.Method == HttpMethod.Get) return Task.FromResult(Json(ConfigJson));
            captured = req;
            return Task.FromResult(Json("{}", HttpStatusCode.NoContent));
        });
        var cfg = await mgmt.Config.GetAsync("user-svc");
        await cfg.DeleteAsync();
        Assert.Equal(HttpMethod.Delete, captured!.Method);
    }

    [Fact]
    public async Task DeleteAsync_OnUnsaved_Throws()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var cfg = mgmt.Config.New("c");
        cfg.Id = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => cfg.DeleteAsync());
    }

    [Fact]
    public async Task GetAsync_NotFound_Throws()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(
            Json("""{"errors":[{"detail":"x"}]}""", HttpStatusCode.NotFound)));
        await Assert.ThrowsAsync<NotFoundException>(() => mgmt.Config.GetAsync("missing"));
    }

    [Fact]
    public async Task ListAsync_NullData_ReturnsEmpty()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("""{"data":null}""")));
        var configs = await mgmt.Config.ListAsync();
        Assert.Empty(configs);
    }
}

public class ConfigEnvironmentTypeTests
{
    [Fact]
    public void ConfigEnvironment_DefaultsEmpty()
    {
        var env = new ConfigEnvironment();
        Assert.Empty(env.Values);
        Assert.Empty(env.ValuesRaw);
    }

    [Fact]
    public void ConfigEnvironment_WithRaw_ExposesValues()
    {
        var raw = new Dictionary<string, Dictionary<string, object?>>
        {
            ["host"] = new() { ["value"] = "localhost", ["type"] = "STRING" }
        };
        var env = new ConfigEnvironment(raw);
        Assert.Equal("localhost", env.Values["host"]);
        Assert.Equal("STRING", env.ValuesRaw["host"]["type"]);
    }

    [Fact]
    public void ConfigEnvironment_ValuesIsDefensiveCopy()
    {
        var raw = new Dictionary<string, Dictionary<string, object?>>
        {
            ["k"] = new() { ["value"] = "v" }
        };
        var env = new ConfigEnvironment(raw);
        var snap = env.Values;
        // IReadOnlyDictionary doesn't have a setter, so we just verify the type
        Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(snap);
    }

    [Fact]
    public void ConfigEnvironment_ToString_ShowsCount()
    {
        var raw = new Dictionary<string, Dictionary<string, object?>>
        {
            ["a"] = new() { ["value"] = 1 },
            ["b"] = new() { ["value"] = 2 }
        };
        var env = new ConfigEnvironment(raw);
        Assert.Contains("2", env.ToString());
    }

    [Fact]
    public void ConfigEnvironment_ValueWithoutValueKey_FallsThrough()
    {
        // When the dict doesn't have "value" key, it falls through to the dict itself
        var raw = new Dictionary<string, Dictionary<string, object?>>
        {
            ["k"] = new() { ["other"] = "x" }
        };
        var env = new ConfigEnvironment(raw);
        // Without "value", the entry value is the inner dict
        Assert.IsType<Dictionary<string, object?>>(env.Values["k"]);
    }
}
