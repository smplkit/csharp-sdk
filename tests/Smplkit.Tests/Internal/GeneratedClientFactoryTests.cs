using System.Net;
using System.Text;
using Smplkit.Internal;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Internal;

public class GeneratedClientFactoryTests
{
    private static SmplClientOptions Options(IDictionary<string, string>? extraHeaders = null) => new()
    {
        ApiKey = "sk_test_key",
        BaseDomain = "smplkit.com",
        Scheme = "https",
        Timeout = TimeSpan.FromSeconds(30),
        ExtraHeaders = extraHeaders,
    };

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
        var options = Options(new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer overridden",
            ["Accept"] = "text/plain",
            ["X-Passthrough"] = "yes",
        });

        _ = new GeneratedClientFactory(httpClient, options);

        // SDK-owned headers are not overridden by extra headers
        Assert.Equal("Bearer sk_test_key", httpClient.DefaultRequestHeaders.Authorization?.ToString());
        Assert.Equal("application/vnd.api+json", httpClient.DefaultRequestHeaders.Accept.ToString());

        // Non-SDK header passes through
        Assert.Equal("yes", httpClient.DefaultRequestHeaders.GetValues("X-Passthrough").First());
    }

    [Fact]
    public void UserAgent_DefaultApplied_WhenCallerSetsNone()
    {
        using var httpClient = new HttpClient();
        var factory = new GeneratedClientFactory(httpClient, Options());

        var userAgent = Assert.Single(httpClient.DefaultRequestHeaders.GetValues("User-Agent"));
        Assert.Equal(SdkVersion.UserAgent, userAgent);
        Assert.StartsWith("smplkit-sdk-csharp/", userAgent, StringComparison.Ordinal);
        Assert.Equal(SdkVersion.UserAgent, factory.EffectiveUserAgent);
    }

    [Fact]
    public async Task UserAgent_Default_TravelsOnRequests()
    {
        var handler = new MockHttpMessageHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/vnd.api+json"),
            }));
        using var httpClient = new HttpClient(handler);
        _ = new GeneratedClientFactory(httpClient, Options());

        // Any request through the shared HttpClient (the transport every
        // generated client uses) must carry the default User-Agent.
        _ = await httpClient.GetAsync("https://config.smplkit.com/api/v1/ping");

        var sent = handler.LastRequest!.Headers.GetValues("User-Agent").Single();
        Assert.Equal(SdkVersion.UserAgent, sent);
        Assert.StartsWith("smplkit-sdk-csharp/", sent, StringComparison.Ordinal);
        Assert.Equal(
            "smplkit-sdk-csharp/" + SdkVersion.Resolve(typeof(SmplClient).Assembly), sent);
    }

    [Fact]
    public void UserAgent_ExtraHeaders_OverridesSdkDefault()
    {
        using var httpClient = new HttpClient();
        var factory = new GeneratedClientFactory(httpClient, Options(
            new Dictionary<string, string> { ["User-Agent"] = "caller-agent/1.0" }));

        var userAgent = Assert.Single(httpClient.DefaultRequestHeaders.GetValues("User-Agent"));
        Assert.Equal("caller-agent/1.0", userAgent);
        Assert.Equal("caller-agent/1.0", factory.EffectiveUserAgent);
    }

    [Fact]
    public void UserAgent_ExtraHeaders_LowercaseKey_OverridesSdkDefault()
    {
        using var httpClient = new HttpClient();
        var factory = new GeneratedClientFactory(httpClient, Options(
            new Dictionary<string, string> { ["user-agent"] = "lower-agent/2.0" }));

        var userAgent = Assert.Single(httpClient.DefaultRequestHeaders.GetValues("User-Agent"));
        Assert.Equal("lower-agent/2.0", userAgent);
        Assert.Equal("lower-agent/2.0", factory.EffectiveUserAgent);
    }

    [Fact]
    public void UserAgent_CallerHttpClientValue_IsPreserved()
    {
        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "my-app/3.1");

        var factory = new GeneratedClientFactory(httpClient, Options());

        var userAgent = Assert.Single(httpClient.DefaultRequestHeaders.GetValues("User-Agent"));
        Assert.Equal("my-app/3.1", userAgent);
        Assert.Equal("my-app/3.1", factory.EffectiveUserAgent);
    }

    [Fact]
    public void UserAgent_CallerHttpClientValue_SurvivesSmplClientConstruction()
    {
        var handler = new MockHttpMessageHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/vnd.api+json"),
            }));
        using var httpClient = new HttpClient(handler);
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "caller-app/9.9");

        using var client = new SmplClient(TestData.DefaultOptions(), httpClient);

        var userAgent = Assert.Single(httpClient.DefaultRequestHeaders.GetValues("User-Agent"));
        Assert.Equal("caller-app/9.9", userAgent);
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
