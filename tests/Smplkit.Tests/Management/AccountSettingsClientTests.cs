using System.Net;
using System.Text;
using Smplkit;
using Smplkit.Errors;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Management;

public class AccountSettingsClientTests
{
    private static (SmplManagementClient mgmt, MockHttpMessageHandler handler) Make(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        var handler = new MockHttpMessageHandler(respond);
        var http = new HttpClient(handler);
        var mgmt = new SmplManagementClient(new SmplClientOptions { ApiKey = "k" }, http);
        return (mgmt, handler);
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
        var (mgmt, _) = Make(_ => Task.FromResult(Resp(body)));
        var settings = await mgmt.AccountSettings.GetAsync();
        Assert.Equal(2, settings.Raw.Count);
        Assert.Equal(3, settings.EnvironmentOrder.Count);
        Assert.Equal("production", settings.EnvironmentOrder[0]);
    }

    [Fact]
    public async Task GetAsync_EmptyBody_ReturnsEmptySettings()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Resp("")));
        var settings = await mgmt.AccountSettings.GetAsync();
        Assert.Empty(settings.Raw);
        Assert.Empty(settings.EnvironmentOrder);
    }

    [Fact]
    public async Task GetAsync_NonObjectBody_ReturnsEmpty()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Resp("[\"unexpected\"]")));
        var settings = await mgmt.AccountSettings.GetAsync();
        Assert.Empty(settings.Raw);
    }

    [Fact]
    public async Task GetAsync_HttpError_RaisesException()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(
            Resp("""{"errors":[{"detail":"forbidden"}]}""", HttpStatusCode.Forbidden)));
        await Assert.ThrowsAsync<SmplkitException>(() => mgmt.AccountSettings.GetAsync());
    }

    [Fact]
    public async Task GetAsync_NotFound_ThrowsNotFound()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(
            Resp("""{"errors":[{"detail":"not found"}]}""", HttpStatusCode.NotFound)));
        await Assert.ThrowsAsync<NotFoundException>(() => mgmt.AccountSettings.GetAsync());
    }

    [Fact]
    public async Task SaveAsync_SendsPut()
    {
        HttpRequestMessage? captured = null;
        var (mgmt, _) = Make(req =>
        {
            captured = req;
            return req.Method == HttpMethod.Get
                ? Task.FromResult(Resp("""{"environment_order":["a"]}"""))
                : Task.FromResult(Resp("""{"environment_order":["b"]}"""));
        });
        var settings = await mgmt.AccountSettings.GetAsync();
        settings.EnvironmentOrder = new List<string> { "b" };
        await settings.SaveAsync();
        Assert.Equal(HttpMethod.Put, captured!.Method);
        // After save, internal state should be updated from response
        Assert.Equal("b", settings.EnvironmentOrder[0]);
    }

    [Fact]
    public async Task EnvironmentOrder_NoneSet_ReturnsEmpty()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Resp("{}")));
        var settings = await mgmt.AccountSettings.GetAsync();
        Assert.Empty(settings.EnvironmentOrder);
    }

    [Fact]
    public async Task ToString_IncludesKeyCount()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Resp("""{"a":1,"b":2,"c":3}""")));
        var settings = await mgmt.AccountSettings.GetAsync();
        Assert.Contains("3", settings.ToString());
    }
}
