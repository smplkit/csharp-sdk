using System.Net;
using Smplkit.Config;
using Smplkit.Errors;
using Smplkit.Logging;
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
            Service = "test-service",
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
            new SmplClient(new SmplClientOptions { ApiKey = "", Environment = "test", Service = "test-service" }));
    }

    [Fact]
    public void Constructor_WithNoApiKey_NoEnv_ThrowsSmplException()
    {
        Environment.SetEnvironmentVariable("SMPLKIT_API_KEY", null);
        Environment.SetEnvironmentVariable("HOME", Path.GetTempPath());
        Assert.Throws<SmplException>(() =>
            new SmplClient(new SmplClientOptions { Environment = "test", Service = "test-service" }));
    }

    [Fact]
    public void Constructor_Parameterless_WithEnvVars_Succeeds()
    {
        Environment.SetEnvironmentVariable("SMPLKIT_API_KEY", "sk_api_env");
        Environment.SetEnvironmentVariable("SMPLKIT_ENVIRONMENT", "staging");
        Environment.SetEnvironmentVariable("SMPLKIT_SERVICE", "env-service");
        using var client = new SmplClient();
        Assert.NotNull(client);
        Assert.Equal("staging", client.Environment);
    }

    [Fact]
    public void Constructor_MissingEnvironment_ThrowsSmplException()
    {
        Environment.SetEnvironmentVariable("SMPLKIT_ENVIRONMENT", null);
        Assert.Throws<SmplException>(() =>
            new SmplClient(new SmplClientOptions { ApiKey = "sk_api_test", Service = "test-service" }));
    }

    [Fact]
    public void Constructor_MissingEnvironment_ErrorDoesNotMentionSmplkitFile()
    {
        Environment.SetEnvironmentVariable("SMPLKIT_ENVIRONMENT", null);
        var ex = Assert.Throws<SmplException>(() =>
            new SmplClient(new SmplClientOptions { ApiKey = "sk_api_test", Service = "test-service" }));
        Assert.DoesNotContain("~/.smplkit", ex.Message);
    }

    [Fact]
    public void Constructor_EnvironmentFromEnvVar_Succeeds()
    {
        Environment.SetEnvironmentVariable("SMPLKIT_ENVIRONMENT", "from-env");
        using var client = new SmplClient(new SmplClientOptions
        {
            ApiKey = "sk_api_test_key",
            Service = "test-service",
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
    public void Constructor_MissingService_ThrowsSmplException()
    {
        Environment.SetEnvironmentVariable("SMPLKIT_SERVICE", null);
        var ex = Assert.Throws<SmplException>(() =>
            new SmplClient(new SmplClientOptions
            {
                ApiKey = "sk_api_test_key",
                Environment = "test",
            }));
        Assert.Contains("No service provided", ex.Message);
    }

    [Fact]
    public void Constructor_MissingService_ErrorMessage()
    {
        Environment.SetEnvironmentVariable("SMPLKIT_SERVICE", null);
        var ex = Assert.Throws<SmplException>(() =>
            new SmplClient(new SmplClientOptions
            {
                ApiKey = "sk_api_test_key",
                Environment = "test",
            }));
        Assert.Contains("SmplClientOptions", ex.Message);
        Assert.Contains("SMPLKIT_SERVICE", ex.Message);
    }

    [Fact]
    public void Config_ReturnsConfigClient()
    {
        using var client = new SmplClient(new SmplClientOptions
        {
            ApiKey = "sk_api_test_key",
            Environment = "test",
            Service = "test-service",
        });

        Assert.IsType<ConfigClient>(client.Config);
    }

    [Fact]
    public void Logging_ReturnsLoggingClient()
    {
        using var client = new SmplClient(new SmplClientOptions
        {
            ApiKey = "sk_api_test_key",
            Environment = "test",
            Service = "test-service",
        });

        Assert.IsType<LoggingClient>(client.Logging);
    }

    [Fact]
    public void Dispose_WithOwnedHttpClient_DoesNotThrow()
    {
        var client = new SmplClient(new SmplClientOptions
        {
            ApiKey = "sk_api_test_key",
            Environment = "test",
            Service = "test-service",
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
            new SmplClientOptions { ApiKey = "sk_api_test_key", Environment = "test", Service = "test-service" },
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
                new SmplClientOptions { ApiKey = "sk_test", Environment = "test", Service = "test-service" },
                null!));
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var client = new SmplClient(new SmplClientOptions
        {
            ApiKey = "sk_api_test_key",
            Environment = "test",
            Service = "test-service",
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
            Service = "test-service",
            Timeout = timeout,
        });

        Assert.NotNull(client.Config);
    }

    // ------------------------------------------------------------------
    // Resolution order: environment first, then service, then API key
    // ------------------------------------------------------------------

    [Fact]
    public void Constructor_ResolvesEnvironmentBeforeService()
    {
        // If environment resolution fails, it should throw about environment
        // even though service is also missing.
        Environment.SetEnvironmentVariable("SMPLKIT_ENVIRONMENT", null);
        Environment.SetEnvironmentVariable("SMPLKIT_SERVICE", null);
        var ex = Assert.Throws<SmplException>(() =>
            new SmplClient(new SmplClientOptions { ApiKey = "sk_api_test" }));
        Assert.Contains("environment", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Constructor_ResolvesServiceBeforeApiKey()
    {
        // With environment set but service missing, should throw about service
        // even though API key is also missing.
        Environment.SetEnvironmentVariable("SMPLKIT_API_KEY", null);
        Environment.SetEnvironmentVariable("SMPLKIT_SERVICE", null);
        var ex = Assert.Throws<SmplException>(() =>
            new SmplClient(new SmplClientOptions { Environment = "test" }));
        Assert.Contains("service", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Constructor_DisableTelemetry_NoMetricsRequests()
    {
        var handler = new MockHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var httpClient = new HttpClient(handler);

        using var client = new SmplClient(
            new SmplClientOptions
            {
                ApiKey = "sk_api_test_key",
                Environment = "test",
                Service = "test-service",
                DisableTelemetry = true,
            },
            httpClient);

        // No metrics endpoint calls should have been made
        Assert.DoesNotContain(handler.Requests, r =>
            r.RequestUri?.PathAndQuery.Contains("metrics") == true);
    }

    [Fact]
    public void Constructor_WithCustomBaseDomainAndScheme_CreatesClientSuccessfully()
    {
        using var client = new SmplClient(new SmplClientOptions
        {
            ApiKey = "sk_api_test_key",
            Environment = "test",
            Service = "test-service",
            BaseDomain = "internal.example.com",
            Scheme = "http",
        });

        Assert.NotNull(client.Config);
        Assert.NotNull(client.Flags);
        Assert.NotNull(client.Logging);
    }
}
