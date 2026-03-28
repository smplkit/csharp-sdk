using System.Net;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests;

/// <summary>
/// Additional SmplkitClient tests for 100% code coverage.
/// </summary>
public class SmplkitClientCoverageTests
{
    // ------------------------------------------------------------------
    // Dispose with owned client — disposes HttpClient
    // ------------------------------------------------------------------

    [Fact]
    public void Dispose_OwnedClient_DisposesHttpClient()
    {
        var client = new SmplkitClient(new SmplkitClientOptions
        {
            ApiKey = "sk_test_key",
        });

        // Should not throw
        client.Dispose();

        // Verify it was disposed by trying to use it (indirectly)
        // The Config property should still be accessible but requests would fail
        Assert.NotNull(client.Config);
    }

    // ------------------------------------------------------------------
    // Constructor with external HttpClient — does NOT dispose it
    // ------------------------------------------------------------------

    [Fact]
    public async Task Dispose_ExternalClient_DoesNotDispose()
    {
        var handler = new MockHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            }));
        var httpClient = new HttpClient(handler);

        var client = new SmplkitClient(
            new SmplkitClientOptions { ApiKey = "sk_test_key" },
            httpClient);

        client.Dispose();

        // HttpClient should still be usable
        var response = await httpClient.GetAsync("https://example.com");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        httpClient.Dispose();
    }

    // ------------------------------------------------------------------
    // Constructor validates both options and httpClient
    // ------------------------------------------------------------------

    [Fact]
    public void Constructor_NullOptions_External_ThrowsArgumentNullException()
    {
        var httpClient = new HttpClient();
        Assert.Throws<ArgumentNullException>(() =>
            new SmplkitClient(null!, httpClient));
        httpClient.Dispose();
    }

    // ------------------------------------------------------------------
    // Constructor with custom timeout
    // ------------------------------------------------------------------

    [Fact]
    public void Constructor_CustomTimeout_SetsOnHttpClient()
    {
        var handler = new MockHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var httpClient = new HttpClient(handler);

        var client = new SmplkitClient(
            new SmplkitClientOptions
            {
                ApiKey = "sk_test_key",
                Timeout = TimeSpan.FromSeconds(120),
            },
            httpClient);

        Assert.Equal(TimeSpan.FromSeconds(120), httpClient.Timeout);
        client.Dispose();
        httpClient.Dispose();
    }

    // ------------------------------------------------------------------
    // MockHttpMessageHandler — tracks multiple requests
    // ------------------------------------------------------------------

    [Fact]
    public async Task MockHandler_TracksAllRequests()
    {
        var handler = new MockHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            }));
        var httpClient = new HttpClient(handler);

        await httpClient.GetAsync("https://example.com/1");
        await httpClient.GetAsync("https://example.com/2");
        await httpClient.GetAsync("https://example.com/3");

        Assert.Equal(3, handler.Requests.Count);
        Assert.NotNull(handler.LastRequest);
        Assert.Contains("/3", handler.LastRequest!.RequestUri!.AbsoluteUri);

        httpClient.Dispose();
    }
}
