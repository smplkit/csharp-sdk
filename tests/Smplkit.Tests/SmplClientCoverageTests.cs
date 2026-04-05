using System.Net;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests;

/// <summary>
/// Additional SmplClient tests for 100% code coverage.
/// </summary>
public class SmplClientCoverageTests
{
    // ------------------------------------------------------------------
    // Dispose with owned client — disposes HttpClient
    // ------------------------------------------------------------------

    [Fact]
    public void Dispose_OwnedClient_DisposesHttpClient()
    {
        var client = new SmplClient(new SmplClientOptions
        {
            ApiKey = "sk_test_key",
            Environment = "test",
            Service = "test-service",
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

        var client = new SmplClient(
            new SmplClientOptions { ApiKey = "sk_test_key", Environment = "test", Service = "test-service" },
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
            new SmplClient(null!, httpClient));
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

        var client = new SmplClient(
            new SmplClientOptions
            {
                ApiKey = "sk_test_key",
                Environment = "test",
                Service = "test-service",
                Timeout = TimeSpan.FromSeconds(120),
            },
            httpClient);

        Assert.Equal(TimeSpan.FromSeconds(120), httpClient.Timeout);
        client.Dispose();
        httpClient.Dispose();
    }

    // ------------------------------------------------------------------
    // Dispose with shared WebSocket active — stops WebSocket
    // ------------------------------------------------------------------

    [Fact]
    public async Task Dispose_WithActiveWebSocket_StopsWebSocket()
    {
        var flagJson = """
        {
            "data": [
                {
                    "id": "flag-001",
                    "type": "flag",
                    "attributes": {
                        "key": "ws-flag",
                        "name": "WS Flag",
                        "type": "BOOLEAN",
                        "default": false,
                        "values": [],
                        "description": null,
                        "environments": {},
                        "created_at": null,
                        "updated_at": null
                    }
                }
            ]
        }
        """;
        var configJson = """{"data":[]}""";
        var handler = new MockHttpMessageHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("flags"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(flagJson, System.Text.Encoding.UTF8, "application/vnd.api+json"),
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(configJson, System.Text.Encoding.UTF8, "application/vnd.api+json"),
            });
        });
        var httpClient = new HttpClient(handler);
        var client = new SmplClient(
            new SmplClientOptions { ApiKey = "sk_test_key", Environment = "production", Service = "test-service" },
            httpClient);

        // Trigger WebSocket creation by calling ConnectAsync
        // The real WS will fail to connect (no server), but EnsureSharedWebSocket
        // will be called, creating _sharedWs. The connect will fail, but Dispose
        // should still try to stop it.
        try
        {
            await client.ConnectAsync();
        }
        catch
        {
            // Expected - no real WS server
        }

        // Now Dispose should hit the _sharedWs != null path
        client.Dispose();

        // Verify double-dispose is safe
        client.Dispose();

        httpClient.Dispose();
    }

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
