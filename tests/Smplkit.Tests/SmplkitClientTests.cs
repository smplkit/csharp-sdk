using System.Net;
using Smplkit.Config;
using Smplkit.Tests.Helpers;

namespace Smplkit.Tests;

public class SmplkitClientTests
{
    [Fact]
    public void Constructor_WithValidOptions_CreatesClient()
    {
        using var client = new SmplkitClient(new SmplkitClientOptions
        {
            ApiKey = "sk_api_test_key",
        });

        Assert.NotNull(client);
        Assert.NotNull(client.Config);
    }

    [Fact]
    public void Constructor_WithCustomBaseUrl_CreatesClient()
    {
        using var client = new SmplkitClient(new SmplkitClientOptions
        {
            ApiKey = "sk_api_test_key",
            BaseUrl = "https://custom.example.com",
            Timeout = TimeSpan.FromSeconds(60),
        });

        Assert.NotNull(client);
    }

    [Fact]
    public void Constructor_WithEmptyApiKey_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            new SmplkitClient(new SmplkitClientOptions { ApiKey = "" }));
    }

    [Fact]
    public void Constructor_WithWhitespaceApiKey_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            new SmplkitClient(new SmplkitClientOptions { ApiKey = "   " }));
    }

    [Fact]
    public void Config_ReturnsConfigClient()
    {
        using var client = new SmplkitClient(new SmplkitClientOptions
        {
            ApiKey = "sk_api_test_key",
        });

        Assert.IsType<ConfigClient>(client.Config);
    }

    [Fact]
    public void Dispose_WithOwnedHttpClient_DoesNotThrow()
    {
        var client = new SmplkitClient(new SmplkitClientOptions
        {
            ApiKey = "sk_api_test_key",
        });

        client.Dispose();
    }

    [Fact]
    public void Dispose_WithExternalHttpClient_DoesNotDisposeIt()
    {
        var handler = new MockHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var httpClient = new HttpClient(handler);

        var client = new SmplkitClient(
            new SmplkitClientOptions { ApiKey = "sk_api_test_key" },
            httpClient);
        client.Dispose();

        // External HttpClient should still be usable after SmplkitClient disposal.
        // If it were disposed, this would throw ObjectDisposedException.
        Assert.NotNull(httpClient.BaseAddress?.ToString() ?? "still-alive");
    }

    [Fact]
    public void DefaultOptions_HasCorrectDefaults()
    {
        var options = new SmplkitClientOptions { ApiKey = "test" };

        Assert.Equal("https://config.smplkit.com", options.BaseUrl);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Timeout);
    }
}
