using System.Net;
using System.Text;
using Smplkit;
using Smplkit.Errors;
using Smplkit.Flags;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Flags;

/// <summary>
/// Tests for the <see cref="Flag"/> active-record model: New(), per-environment
/// methods (rule 7), AddRule / AddValue / RemoveValue / ClearValues, ToString,
/// and the wire-level Save/Delete round-trip.
/// </summary>
public class FlagModelTests
{
    private static (SmplClient mgmt, MockHttpMessageHandler handler) Make(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        var handler = new MockHttpMessageHandler(respond);
        var http = new HttpClient(handler);
        var mgmt = new SmplClient(TestData.DefaultOptions(), http);
        return (mgmt, handler);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/vnd.api+json") };

    private const string FlagJson = """
        {
            "data": {
                "id": "checkout-v2",
                "type": "flag",
                "attributes": {
                    "id": "checkout-v2",
                    "name": "Checkout V2",
                    "type": "BOOLEAN",
                    "default": false,
                    "values": [{"name": "True", "value": true}, {"name": "False", "value": false}],
                    "description": "Rollout",
                    "environments": {
                        "production": {"enabled": false, "default": false, "rules": []},
                        "staging": {"enabled": true, "default": null, "rules": [
                            {"description": "rule1", "logic": {"==":[{"var":"user.plan"},"enterprise"]}, "value": true}
                        ]}
                    },
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;

    // ------------------------------------------------------------------
    // Per-environment methods (rule 7)
    // ------------------------------------------------------------------

    [Fact]
    public void EnableRules_AllEnvironments_TogglesAll()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var flag = mgmt.Flags.NewBooleanFlag("f", false);
        flag.SetEnvironmentEnabled("production", false);
        flag.SetEnvironmentEnabled("staging", false);
        flag.EnableRules();
        Assert.True((bool)flag.Environments["production"]["enabled"]!);
        Assert.True((bool)flag.Environments["staging"]["enabled"]!);
    }

    [Fact]
    public void EnableRules_SingleEnvironment()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var flag = mgmt.Flags.NewBooleanFlag("f", false);
        flag.EnableRules(environment: "production");
        Assert.True((bool)flag.Environments["production"]["enabled"]!);
        Assert.False(flag.Environments.ContainsKey("staging"));
    }

    [Fact]
    public void DisableRules_AllEnvironments()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var flag = mgmt.Flags.NewBooleanFlag("f", false);
        flag.SetEnvironmentEnabled("production", true);
        flag.SetEnvironmentEnabled("staging", true);
        flag.DisableRules();
        Assert.False((bool)flag.Environments["production"]["enabled"]!);
        Assert.False((bool)flag.Environments["staging"]["enabled"]!);
    }

    [Fact]
    public void DisableRules_SingleEnvironment()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var flag = mgmt.Flags.NewBooleanFlag("f", false);
        flag.DisableRules(environment: "production");
        Assert.False((bool)flag.Environments["production"]["enabled"]!);
    }

    [Fact]
    public void SetDefault_BaseLevel()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var flag = mgmt.Flags.NewBooleanFlag("f", false);
        flag.SetDefault(true);
        Assert.True((bool)flag.Default!);
    }

    [Fact]
    public void SetDefault_PerEnvironment_CreatesEntry()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var flag = mgmt.Flags.NewBooleanFlag("f", false);
        flag.SetDefault(true, environment: "production");
        Assert.Equal(true, flag.Environments["production"]["default"]);
    }

    [Fact]
    public void ClearDefault_PerEnvironment_NullsValue()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var flag = mgmt.Flags.NewBooleanFlag("f", false);
        flag.SetDefault(true, environment: "production");
        flag.ClearDefault("production");
        Assert.Null(flag.Environments["production"]["default"]);
    }

    [Fact]
    public void ClearDefault_NoOpIfMissing()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var flag = mgmt.Flags.NewBooleanFlag("f", false);
        // No exception, no environment created
        flag.ClearDefault("nonexistent");
        Assert.False(flag.Environments.ContainsKey("nonexistent"));
    }

    [Fact]
    public void ClearRules_AllEnvironments()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var flag = mgmt.Flags.NewBooleanFlag("f", false);
        flag.AddRule(new Rule("r1").Environment("production").Serve(true).Build());
        flag.AddRule(new Rule("r2").Environment("staging").Serve(true).Build());
        flag.ClearRules();
        Assert.Empty((List<object?>)flag.Environments["production"]["rules"]!);
        Assert.Empty((List<object?>)flag.Environments["staging"]["rules"]!);
    }

    [Fact]
    public void ClearRules_NoOpIfNoEnvs()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var flag = mgmt.Flags.NewBooleanFlag("f", false);
        flag.ClearRules(environment: "nonexistent");
        // No exception
        Assert.False(flag.Environments.ContainsKey("nonexistent"));
    }

    [Fact]
    public void AddRule_RequiresEnvironment()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var flag = mgmt.Flags.NewBooleanFlag("f", false);
        var rule = new Rule("no env").Serve(true).Build(); // missing .Environment(...)
        Assert.Throws<ArgumentException>(() => flag.AddRule(rule));
    }

    [Fact]
    public void AddRule_AppendsToEnvironment()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var flag = mgmt.Flags.NewBooleanFlag("f", false);
        flag.AddRule(new Rule("r1").Environment("prod")
            .When("user.plan", "==", "enterprise").Serve(true).Build());
        var rules = (List<object?>)flag.Environments["prod"]["rules"]!;
        Assert.Single(rules);
    }

    [Fact]
    public void AddValue_AppendsValue()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var flag = mgmt.Flags.NewStringFlag("f", "x");
        flag.AddValue("Red", "red").AddValue("Blue", "blue");
        Assert.Equal(2, flag.Values!.Count);
    }

    [Fact]
    public void RemoveValue_RemovesFirstMatch()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var flag = mgmt.Flags.NewStringFlag("f", "x",
            values: new[] { new FlagValue("A", "a"), new FlagValue("B", "b") });
        flag.RemoveValue("a");
        Assert.Single(flag.Values!);
        Assert.Equal("b", flag.Values![0]["value"]);
    }

    [Fact]
    public void RemoveValue_NoMatch_NoChange()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var flag = mgmt.Flags.NewStringFlag("f", "x",
            values: new[] { new FlagValue("A", "a") });
        flag.RemoveValue("nonexistent");
        Assert.Single(flag.Values!);
    }

    [Fact]
    public void RemoveValue_NullValuesList_NoOp()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var flag = mgmt.Flags.NewStringFlag("f", "x");
        flag.RemoveValue("anything");
        Assert.Null(flag.Values);
    }

    [Fact]
    public void ClearValues_SetsToNull()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var flag = mgmt.Flags.NewStringFlag("f", "x",
            values: new[] { new FlagValue("A", "a") });
        flag.ClearValues();
        Assert.Null(flag.Values);
    }

    [Fact]
    public void AddValue_NullValuesList_Initializes()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var flag = mgmt.Flags.NewStringFlag("f", "x");
        Assert.Null(flag.Values);
        flag.AddValue("A", "a");
        Assert.Single(flag.Values!);
    }

    [Fact]
    public void SetEnvironmentEnabled_MissingEnv_Creates()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var flag = mgmt.Flags.NewBooleanFlag("f", false);
        flag.SetEnvironmentEnabled("new", true);
        Assert.True((bool)flag.Environments["new"]["enabled"]!);
    }

    [Fact]
    public void SetEnvironmentDefault_MissingEnv_Creates()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var flag = mgmt.Flags.NewBooleanFlag("f", false);
        flag.SetEnvironmentDefault("new", true);
        Assert.Equal(true, flag.Environments["new"]["default"]);
    }

    [Fact]
    public void ToString_IncludesIdTypeDefault()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var flag = mgmt.Flags.NewBooleanFlag("checkout", false);
        var s = flag.ToString();
        Assert.Contains("checkout", s);
        Assert.Contains("BOOLEAN", s);
    }

    // ------------------------------------------------------------------
    // Save/delete wire round-trip
    // ------------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_NewFlag_SendsPost()
    {
        HttpRequestMessage? captured = null;
        var (mgmt, _) = Make(req =>
        {
            captured = req;
            return Task.FromResult(Json(FlagJson, HttpStatusCode.Created));
        });
        var flag = mgmt.Flags.NewBooleanFlag("checkout-v2", false, description: "Rollout");
        await flag.SaveAsync();
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.NotNull(flag.CreatedAt);
        Assert.NotNull(flag.UpdatedAt);
    }

    [Fact]
    public async Task SaveAsync_ExistingFlag_SendsPut()
    {
        HttpRequestMessage? captured = null;
        var (mgmt, _) = Make(req =>
        {
            if (req.Method == HttpMethod.Get) return Task.FromResult(Json(FlagJson));
            captured = req;
            return Task.FromResult(Json(FlagJson));
        });
        var flag = await mgmt.Flags.GetAsync("checkout-v2");
        flag.Description = "v2";
        await flag.SaveAsync();
        Assert.Equal(HttpMethod.Put, captured!.Method);
    }

    [Fact]
    public async Task SaveAsync_ParsesEnvironmentRules()
    {
        var (mgmt, _) = Make(req =>
            req.Method == HttpMethod.Get
                ? Task.FromResult(Json(FlagJson))
                : Task.FromResult(Json(FlagJson, HttpStatusCode.Created)));
        var flag = await mgmt.Flags.GetAsync("checkout-v2");
        Assert.True(flag.Environments.ContainsKey("staging"));
        var stagingRules = (List<object?>)flag.Environments["staging"]["rules"]!;
        Assert.Single(stagingRules);
    }

    [Fact]
    public async Task DeleteAsync_OnSavedFlag_SendsDelete()
    {
        HttpRequestMessage? captured = null;
        var (mgmt, _) = Make(req =>
        {
            if (req.Method == HttpMethod.Get) return Task.FromResult(Json(FlagJson));
            captured = req;
            return Task.FromResult(Json("{}", HttpStatusCode.NoContent));
        });
        var flag = await mgmt.Flags.GetAsync("checkout-v2");
        await flag.DeleteAsync();
        Assert.Equal(HttpMethod.Delete, captured!.Method);
    }

    [Fact]
    public async Task DeleteAsync_OnUnsavedFlag_Throws()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var flag = mgmt.Flags.NewBooleanFlag("nope", false);
        flag.Id = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => flag.DeleteAsync());
    }

    [Fact]
    public async Task SaveAsync_OnUnsavedWithoutId_Throws()
    {
        // BuildCreateFlagBody requires a caller-supplied id; the create
        // envelope's data.id is no longer optional.
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var flag = mgmt.Flags.NewBooleanFlag("placeholder", false);
        flag.Id = null;
        await Assert.ThrowsAsync<ValidationException>(() => flag.SaveAsync());
    }

    [Fact]
    public async Task ListAsync_EmptyData_ReturnsEmptyList()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("""{"data":null}""")));
        var flags = await mgmt.Flags.ListAsync();
        Assert.Empty(flags);
    }
}
