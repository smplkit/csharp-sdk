using Xunit;

namespace Smplkit.Tests;

public class SmplClientOptionsTests
{
    [Fact]
    public void Timeout_Default_Is30Seconds()
    {
        var options = new SmplClientOptions { ApiKey = "sk_test", Environment = "test" };

        Assert.Equal(TimeSpan.FromSeconds(30), options.Timeout);
    }

    [Fact]
    public void Timeout_CanBeOverridden()
    {
        var options = new SmplClientOptions
        {
            ApiKey = "sk_test",
            Environment = "test",
            Timeout = TimeSpan.FromMinutes(2),
        };

        Assert.Equal(TimeSpan.FromMinutes(2), options.Timeout);
    }

    [Fact]
    public void ApiKey_IsSet()
    {
        var options = new SmplClientOptions { ApiKey = "sk_my_key", Environment = "test" };

        Assert.Equal("sk_my_key", options.ApiKey);
    }

    [Fact]
    public void Environment_IsSet()
    {
        var options = new SmplClientOptions { ApiKey = "sk_test", Environment = "production" };

        Assert.Equal("production", options.Environment);
    }

    [Fact]
    public void Service_IsSet()
    {
        var options = new SmplClientOptions
        {
            ApiKey = "sk_test",
            Environment = "test",
            Service = "my-service",
        };

        Assert.Equal("my-service", options.Service);
    }

    [Fact]
    public void Service_DefaultIsNull()
    {
        var options = new SmplClientOptions { ApiKey = "sk_test", Environment = "test" };

        Assert.Null(options.Service);
    }

    [Fact]
    public void DisableTelemetry_DefaultIsNull()
    {
        var options = new SmplClientOptions { ApiKey = "sk_test", Environment = "test" };

        Assert.Null(options.DisableTelemetry);
    }

    [Fact]
    public void DisableTelemetry_CanBeSetTrue()
    {
        var options = new SmplClientOptions
        {
            ApiKey = "sk_test",
            Environment = "test",
            DisableTelemetry = true,
        };

        Assert.True(options.DisableTelemetry);
    }

    [Fact]
    public void BaseDomain_DefaultIsNull()
    {
        var options = new SmplClientOptions { ApiKey = "sk_test", Environment = "test" };

        Assert.Null(options.BaseDomain);
    }

    [Fact]
    public void BaseDomain_CanBeOverridden()
    {
        var options = new SmplClientOptions
        {
            ApiKey = "sk_test",
            Environment = "test",
            BaseDomain = "internal.example.com",
        };

        Assert.Equal("internal.example.com", options.BaseDomain);
    }

    [Fact]
    public void Scheme_DefaultIsNull()
    {
        var options = new SmplClientOptions { ApiKey = "sk_test", Environment = "test" };

        Assert.Null(options.Scheme);
    }

    [Fact]
    public void Scheme_CanBeOverridden()
    {
        var options = new SmplClientOptions
        {
            ApiKey = "sk_test",
            Environment = "test",
            Scheme = "http",
        };

        Assert.Equal("http", options.Scheme);
    }

    [Fact]
    public void Profile_DefaultIsNull()
    {
        var options = new SmplClientOptions { ApiKey = "sk_test", Environment = "test" };

        Assert.Null(options.Profile);
    }

    [Fact]
    public void Profile_CanBeSet()
    {
        var options = new SmplClientOptions
        {
            ApiKey = "sk_test",
            Environment = "test",
            Profile = "staging",
        };

        Assert.Equal("staging", options.Profile);
    }

    [Fact]
    public void Debug_DefaultIsNull()
    {
        var options = new SmplClientOptions { ApiKey = "sk_test", Environment = "test" };

        Assert.Null(options.Debug);
    }

    [Fact]
    public void Debug_CanBeSetTrue()
    {
        var options = new SmplClientOptions
        {
            ApiKey = "sk_test",
            Environment = "test",
            Debug = true,
        };

        Assert.True(options.Debug);
    }
}
