using Smplkit.Internal;
using Xunit;

namespace Smplkit.Tests.Internal;

public class GeneratedClientFactoryTests
{
    [Fact]
    public void ExtraHeaders_ArePresentOnRequests()
    {
        using var httpClient = new HttpClient();
        var options = new SmplClientOptions
        {
            ApiKey = "sk_test_key",
            BaseDomain = "smplkit.com",
            Scheme = "https",
            Timeout = TimeSpan.FromSeconds(30),
            ExtraHeaders = new Dictionary<string, string> { ["X-Custom"] = "hello" },
        };

        _ = new GeneratedClientFactory(httpClient, options);

        Assert.True(httpClient.DefaultRequestHeaders.Contains("X-Custom"));
        Assert.Equal("hello", httpClient.DefaultRequestHeaders.GetValues("X-Custom").First());
    }

    [Fact]
    public void ExtraHeaders_SdkOwnedHeadersAreNotOverridden()
    {
        using var httpClient = new HttpClient();
        var options = new SmplClientOptions
        {
            ApiKey = "sk_test_key",
            BaseDomain = "smplkit.com",
            Scheme = "https",
            Timeout = TimeSpan.FromSeconds(30),
            ExtraHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer overridden",
                ["Accept"] = "text/plain",
                ["User-Agent"] = "rogue-agent",
                ["X-Passthrough"] = "yes",
            },
        };

        _ = new GeneratedClientFactory(httpClient, options);

        // SDK-owned headers are not overridden by extra headers
        Assert.Equal("Bearer sk_test_key", httpClient.DefaultRequestHeaders.Authorization?.ToString());
        Assert.Equal("application/vnd.api+json", httpClient.DefaultRequestHeaders.Accept.ToString());
        Assert.Equal("smplkit-dotnet-sdk/0.0.0", httpClient.DefaultRequestHeaders.UserAgent.ToString());

        // Non-SDK header passes through
        Assert.Equal("yes", httpClient.DefaultRequestHeaders.GetValues("X-Passthrough").First());
    }

    [Fact]
    public void ExtraHeaders_Null_NoHeadersAdded()
    {
        using var httpClient = new HttpClient();
        var options = new SmplClientOptions
        {
            ApiKey = "sk_test_key",
            BaseDomain = "smplkit.com",
            Scheme = "https",
            Timeout = TimeSpan.FromSeconds(30),
            ExtraHeaders = null,
        };

        _ = new GeneratedClientFactory(httpClient, options);

        // Only SDK headers present (Authorization, Accept, User-Agent)
        Assert.False(httpClient.DefaultRequestHeaders.Contains("X-Custom"));
    }
}
