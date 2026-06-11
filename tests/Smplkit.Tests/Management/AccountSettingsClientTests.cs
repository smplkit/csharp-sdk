using System.Net;
using System.Text;
using Smplkit;
using Smplkit.Account;
using Smplkit.Errors;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Management;

public class AccountSettingsClientTests
{
    // The account-settings endpoint is not JSON:API, so SettingsClient opens a
    // short-lived HttpClient per call rather than going through a generated
    // client. Its internal ctor exposes an HttpMessageHandler seam so tests can
    // inject the mock; that is the only way to intercept its HTTP.
    private static (SettingsClient settings, MockHttpMessageHandler handler) Make(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        var handler = new MockHttpMessageHandler(respond);
        var settings = new SettingsClient(
            "https://app.smplkit.com", "sk_test_key", extraHeaders: null, handler: handler);
        return (settings, handler);
    }

    private static HttpResponseMessage Resp(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task GetAsync_ParsesSettings()
    {
        var body = """
            {
                "environment_order": ["production", "staging", "development"],
                "feature_x": true
            }
            """;
        var (settings, _) = Make(_ => Task.FromResult(Resp(body)));
        var result = await settings.GetAsync();
        Assert.Equal(2, result.Raw.Count);
        Assert.Equal(3, result.EnvironmentOrder.Count);
        Assert.Equal("production", result.EnvironmentOrder[0]);
    }

    [Fact]
    public async Task GetAsync_EmptyBody_ReturnsEmptySettings()
    {
        var (settings, _) = Make(_ => Task.FromResult(Resp("")));
        var result = await settings.GetAsync();
        Assert.Empty(result.Raw);
        Assert.Empty(result.EnvironmentOrder);
    }

    [Fact]
    public async Task GetAsync_NonObjectBody_ReturnsEmpty()
    {
        var (settings, _) = Make(_ => Task.FromResult(Resp("[\"unexpected\"]")));
        var result = await settings.GetAsync();
        Assert.Empty(result.Raw);
    }

    [Fact]
    public async Task GetAsync_HttpError_RaisesException()
    {
        var (settings, _) = Make(_ => Task.FromResult(
            Resp("""{"errors":[{"detail":"forbidden"}]}""", HttpStatusCode.Forbidden)));
        await Assert.ThrowsAsync<SmplkitException>(() => settings.GetAsync());
    }

    [Fact]
    public async Task GetAsync_NotFound_ThrowsNotFound()
    {
        var (settings, _) = Make(_ => Task.FromResult(
            Resp("""{"errors":[{"detail":"not found"}]}""", HttpStatusCode.NotFound)));
        await Assert.ThrowsAsync<NotFoundException>(() => settings.GetAsync());
    }

    [Fact]
    public async Task SaveAsync_SendsPut()
    {
        HttpRequestMessage? captured = null;
        var (settings, _) = Make(req =>
        {
            captured = req;
            return req.Method == HttpMethod.Get
                ? Task.FromResult(Resp("""{"environment_order":["a"]}"""))
                : Task.FromResult(Resp("""{"environment_order":["b"]}"""));
        });
        var result = await settings.GetAsync();
        result.EnvironmentOrder = new List<string> { "b" };
        await result.SaveAsync();
        Assert.Equal(HttpMethod.Put, captured!.Method);
        // After save, internal state should be updated from response
        Assert.Equal("b", result.EnvironmentOrder[0]);
    }

    [Fact]
    public async Task EnvironmentOrder_NoneSet_ReturnsEmpty()
    {
        var (settings, _) = Make(_ => Task.FromResult(Resp("{}")));
        var result = await settings.GetAsync();
        Assert.Empty(result.EnvironmentOrder);
    }

    [Fact]
    public async Task ToString_IncludesKeyCount()
    {
        var (settings, _) = Make(_ => Task.FromResult(Resp("""{"a":1,"b":2,"c":3}""")));
        var result = await settings.GetAsync();
        Assert.Contains("3", result.ToString());
    }

    [Fact]
    public async Task GetAsync_SendsBearerToken()
    {
        HttpRequestMessage? captured = null;
        var (settings, _) = Make(req => { captured = req; return Task.FromResult(Resp("{}")); });
        await settings.GetAsync();
        Assert.Equal("Bearer sk_test_key", captured!.Headers.GetValues("Authorization").Single());
        Assert.Contains("/api/v1/accounts/current/settings", captured.RequestUri!.ToString());
    }

    [Fact]
    public async Task ExtraHeaders_AreApplied()
    {
        HttpRequestMessage? captured = null;
        var handler = new MockHttpMessageHandler(req => { captured = req; return Task.FromResult(Resp("{}")); });
        var settings = new SettingsClient(
            "https://app.smplkit.com/", "sk_test_key",
            extraHeaders: new Dictionary<string, string> { ["X-Tenant"] = "acme" },
            handler: handler);
        await settings.GetAsync();
        Assert.Equal("acme", captured!.Headers.GetValues("X-Tenant").Single());
    }
}
