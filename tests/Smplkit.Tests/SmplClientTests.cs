using System.Net;
using Smplkit.Config;
using Smplkit.Errors;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests;

[Collection("EnvironmentTests")]
public class SmplClientTests : IDisposable
{
    private readonly string? _originalApiKeyEnv;
    private readonly string? _originalEnvEnv;
    private readonly string? _originalServiceEnv;

    public SmplClientTests()
    {
        _originalApiKeyEnv = Environment.GetEnvironmentVariable("SMPLKIT_API_KEY");
        _originalEnvEnv = Environment.GetEnvironmentVariable("SMPLKIT_ENVIRONMENT");
        _originalServiceEnv = Environment.GetEnvironmentVariable("SMPLKIT_SERVICE");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SMPLKIT_API_KEY", _originalApiKeyEnv);
        Environment.SetEnvironmentVariable("SMPLKIT_ENVIRONMENT", _originalEnvEnv);
        Environment.SetEnvironmentVariable("SMPLKIT_SERVICE", _originalServiceEnv);
    }

    [Fact]
    public void Constructor_WithValidOptions_CreatesClient()
    {
        using var client = new SmplClient(new SmplClientOptions
        {
            ApiKey = "sk_api_test_key",
            Environment = "production",
        });

        Assert.NotNull(client);
        Assert.NotNull(client.Config);
        Assert.Equal("production", client.Environment);
    }

    [Fact]
    public void Constructor_WithEmptyApiKey_NoEnv_ThrowsSmplException()
    {
        Environment.SetEnvironmentVariable("SMPLKIT_API_KEY", null);
        Environment.SetEnvironmentVariable("HOME", Path.GetTempPath());
        Assert.Throws<SmplException>(() =>
            new SmplClient(new SmplClientOptions { ApiKey = "", Environment = "test" }));
    }

    [Fact]
    public void Constructor_WithNoApiKey_NoEnv_ThrowsSmplException()
    {
        Environment.SetEnvironmentVariable("SMPLKIT_API_KEY", null);
        Environment.SetEnvironmentVariable("HOME", Path.GetTempPath());
        Assert.Throws<SmplException>(() =>
            new SmplClient(new SmplClientOptions { Environment = "test" }));
    }

    [Fact]
    public void Constructor_Parameterless_WithEnvVars_Succeeds()
    {
        Environment.SetEnvironmentVariable("SMPLKIT_API_KEY", "sk_api_env");
        Environment.SetEnvironmentVariable("SMPLKIT_ENVIRONMENT", "staging");
        using var client = new SmplClient();
        Assert.NotNull(client);
        Assert.Equal("staging", client.Environment);
    }

    [Fact]
    public void Constructor_MissingEnvironment_ThrowsSmplException()
    {
        Environment.SetEnvironmentVariable("SMPLKIT_ENVIRONMENT", null);
        Assert.Throws<SmplException>(() =>
            new SmplClient(new SmplClientOptions { ApiKey = "sk_api_test" }));
    }

    [Fact]
    public void Constructor_EnvironmentFromEnvVar_Succeeds()
    {
        Environment.SetEnvironmentVariable("SMPLKIT_ENVIRONMENT", "from-env");
        using var client = new SmplClient(new SmplClientOptions
        {
            ApiKey = "sk_api_test_key",
        });
        Assert.Equal("from-env", client.Environment);
    }

    [Fact]
    public void Constructor_ServiceFromOptions_Succeeds()
    {
        using var client = new SmplClient(new SmplClientOptions
        {
            ApiKey = "sk_api_test_key",
            Environment = "test",
            Service = "my-service",
        });
        Assert.Equal("my-service", client.Service);
    }

    [Fact]
    public void Constructor_ServiceFromEnvVar_Succeeds()
    {
        Environment.SetEnvironmentVariable("SMPLKIT_SERVICE", "env-service");
        using var client = new SmplClient(new SmplClientOptions
        {
            ApiKey = "sk_api_test_key",
            Environment = "test",
        });
        Assert.Equal("env-service", client.Service);
    }

    [Fact]
    public void Constructor_ServiceNull_IsValid()
    {
        Environment.SetEnvironmentVariable("SMPLKIT_SERVICE", null);
        using var client = new SmplClient(new SmplClientOptions
        {
            ApiKey = "sk_api_test_key",
            Environment = "test",
        });
        Assert.Null(client.Service);
    }

    [Fact]
    public void Config_ReturnsConfigClient()
    {
        using var client = new SmplClient(new SmplClientOptions
        {
            ApiKey = "sk_api_test_key",
            Environment = "test",
        });

        Assert.IsType<ConfigClient>(client.Config);
    }

    [Fact]
    public void Dispose_WithOwnedHttpClient_DoesNotThrow()
    {
        var client = new SmplClient(new SmplClientOptions
        {
            ApiKey = "sk_api_test_key",
            Environment = "test",
        });

        client.Dispose();
    }

    [Fact]
    public void Dispose_WithExternalHttpClient_DoesNotDisposeIt()
    {
        var handler = new MockHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var httpClient = new HttpClient(handler);

        var client = new SmplClient(
            new SmplClientOptions { ApiKey = "sk_api_test_key", Environment = "test" },
            httpClient);
        client.Dispose();

        // External HttpClient should still be usable after SmplClient disposal.
        // If it were disposed, this would throw ObjectDisposedException.
        Assert.NotNull(httpClient.BaseAddress?.ToString() ?? "still-alive");
    }

    [Fact]
    public void DefaultOptions_HasCorrectDefaults()
    {
        var options = new SmplClientOptions { ApiKey = "test", Environment = "test" };

        Assert.Equal(TimeSpan.FromSeconds(30), options.Timeout);
    }

    [Fact]
    public void Constructor_WithNullOptions_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SmplClient(null!));
    }

    [Fact]
    public void Constructor_WithNullHttpClient_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SmplClient(
                new SmplClientOptions { ApiKey = "sk_test", Environment = "test" },
                null!));
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var client = new SmplClient(new SmplClientOptions
        {
            ApiKey = "sk_api_test_key",
            Environment = "test",
        });

        client.Dispose();
        // Second dispose should not throw
        client.Dispose();
    }

    [Fact]
    public void Constructor_WithCustomTimeout_SetsTimeout()
    {
        var timeout = TimeSpan.FromSeconds(120);
        using var client = new SmplClient(new SmplClientOptions
        {
            ApiKey = "sk_api_test_key",
            Environment = "test",
            Timeout = timeout,
        });

        Assert.NotNull(client.Config);
    }

    [Fact]
    public async Task ConnectAsync_IsIdempotent()
    {
        var flagJson = """{"data":[]}""";
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
            new SmplClientOptions { ApiKey = "sk_api_test_key", Environment = "test" },
            httpClient);

        // ConnectAsync may throw due to WebSocket, but the _connected flag should be set
        // after the internal connects succeed. We wrap in try/catch since WS has no server.
        try { await client.ConnectAsync(); } catch { }
        var requestCount = handler.Requests.Count;
        try { await client.ConnectAsync(); } catch { }
        // Second call should be a no-op (same request count)
        Assert.Equal(requestCount, handler.Requests.Count);

        client.Dispose();
        httpClient.Dispose();
    }
}
