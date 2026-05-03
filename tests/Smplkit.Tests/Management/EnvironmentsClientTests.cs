using System.Net;
using System.Text;
using Smplkit;
using Smplkit.Errors;
using Smplkit.Management;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Management;

public class EnvironmentsClientTests
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

    private const string SingleEnvJson = """
        {
            "data": {
                "id": "production",
                "type": "environment",
                "attributes": {
                    "name": "Production",
                    "color": "#ef4444",
                    "classification": "STANDARD",
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;

    private const string EnvListJson = """
        {
            "data": [
                {
                    "id": "production",
                    "type": "environment",
                    "attributes": {
                        "name": "Production",
                        "color": "#ef4444",
                        "classification": "STANDARD",
                        "created_at": "2024-01-15T10:30:00Z",
                        "updated_at": "2024-01-15T10:30:00Z"
                    }
                },
                {
                    "id": "preview-feature-x",
                    "type": "environment",
                    "attributes": {
                        "name": "Preview x",
                        "color": null,
                        "classification": "AD_HOC"
                    }
                }
            ]
        }
        """;

    [Fact]
    public void New_CreatesUnsavedEnvironment()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var env = mgmt.Environments.New("dev", "Development",
            new Color("#abcdef"), EnvironmentClassification.Standard);
        Assert.Equal("dev", env.Id);
        Assert.Equal("Development", env.Name);
        Assert.Equal("#abcdef", env.Color!.Hex);
        Assert.Null(env.CreatedAt);
    }

    [Fact]
    public void New_StringColorOverload_Validates()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var env = mgmt.Environments.New("dev", "Dev", color: "#000");
        Assert.Equal("#000", env.Color!.Hex);
    }

    [Fact]
    public void New_StringColorOverload_InvalidHex_Throws()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        Assert.Throws<ArgumentException>(() =>
            mgmt.Environments.New("dev", "Dev", color: "not-a-color"));
    }

    [Fact]
    public void New_NoColor_NullColor()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var env = mgmt.Environments.New("dev", "Development");
        Assert.Null(env.Color);
    }

    [Fact]
    public async Task ListAsync_ParsesAll()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json(EnvListJson)));
        var envs = await mgmt.Environments.ListAsync();
        Assert.Equal(2, envs.Count);
        Assert.Equal("production", envs[0].Id);
        Assert.Equal("#ef4444", envs[0].Color!.Hex);
        Assert.Equal(EnvironmentClassification.Standard, envs[0].Classification);
        Assert.Null(envs[1].Color);
        Assert.Equal(EnvironmentClassification.AdHoc, envs[1].Classification);
    }

    [Fact]
    public async Task GetAsync_ParsesResponse()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json(SingleEnvJson)));
        var env = await mgmt.Environments.GetAsync("production");
        Assert.Equal("production", env.Id);
        Assert.Equal("Production", env.Name);
        Assert.Equal("#ef4444", env.Color!.Hex);
        Assert.NotNull(env.CreatedAt);
        Assert.NotNull(env.UpdatedAt);
    }

    [Fact]
    public async Task DeleteAsync_SendsDelete()
    {
        HttpRequestMessage? captured = null;
        var (mgmt, _) = Make(req =>
        {
            captured = req;
            return Task.FromResult(Json("{}", HttpStatusCode.NoContent));
        });
        await mgmt.Environments.DeleteAsync("preview-x");
        Assert.Equal(HttpMethod.Delete, captured!.Method);
        Assert.Contains("preview-x", captured.RequestUri!.ToString());
    }

    [Fact]
    public async Task SaveAsync_NewEnvironment_SendsPost()
    {
        HttpRequestMessage? captured = null;
        var (mgmt, _) = Make(req =>
        {
            captured = req;
            return Task.FromResult(Json(SingleEnvJson, HttpStatusCode.Created));
        });
        var env = mgmt.Environments.New("production", "Production", new Color("#ef4444"));
        await env.SaveAsync();
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.NotNull(env.CreatedAt);
    }

    [Fact]
    public async Task SaveAsync_ExistingEnvironment_SendsPut()
    {
        HttpRequestMessage? captured = null;
        var (mgmt, _) = Make(req =>
        {
            // First request (GET) returns the env so CreatedAt is non-null
            if (req.Method == HttpMethod.Get) return Task.FromResult(Json(SingleEnvJson));
            captured = req;
            return Task.FromResult(Json(SingleEnvJson));
        });
        var env = await mgmt.Environments.GetAsync("production");
        env.Name = "Production v2";
        await env.SaveAsync();
        Assert.Equal(HttpMethod.Put, captured!.Method);
    }

    [Fact]
    public async Task DeleteAsync_OnUnsavedEnvironment_Throws()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var env = mgmt.Environments.New("nope", "Nope");
        env.Id = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => env.DeleteAsync());
    }

    [Fact]
    public async Task DeleteAsync_OnSavedEnvironment_SendsDelete()
    {
        HttpRequestMessage? captured = null;
        var (mgmt, _) = Make(req =>
        {
            if (req.Method == HttpMethod.Get) return Task.FromResult(Json(SingleEnvJson));
            captured = req;
            return Task.FromResult(Json("{}", HttpStatusCode.NoContent));
        });
        var env = await mgmt.Environments.GetAsync("production");
        await env.DeleteAsync();
        Assert.Equal(HttpMethod.Delete, captured!.Method);
    }

    [Fact]
    public void Environment_ToString_IncludesIdAndName()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var env = mgmt.Environments.New("staging", "Staging");
        var s = env.ToString();
        Assert.Contains("staging", s);
        Assert.Contains("Staging", s);
    }
}
