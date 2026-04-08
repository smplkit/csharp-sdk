using Smplkit.Logging;
using Xunit;

namespace Smplkit.Tests.Logging;

public class LoggerModelTests
{
    // We cannot directly instantiate Logger without the internal constructor
    // that requires a LoggingClient. We use the LoggingClient.New factory
    // via a SmplClient, which is the supported way to create Logger instances.

    private static Logger CreateTestLogger(SmplClient client)
    {
        return client.Logging.New("test-logger", name: "Test Logger");
    }

    private static SmplClient CreateSmplClient()
    {
        var handler = new Helpers.MockHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent("{}", System.Text.Encoding.UTF8, "application/vnd.api+json"),
            }));
        var httpClient = new HttpClient(handler);
        return new SmplClient(Helpers.TestData.DefaultOptions(), httpClient);
    }

    // ------------------------------------------------------------------
    // SetLevel / ClearLevel
    // ------------------------------------------------------------------

    [Fact]
    public void SetLevel_SetsLevel()
    {
        using var client = CreateSmplClient();
        var logger = CreateTestLogger(client);

        Assert.Null(logger.Level);

        logger.SetLevel(LogLevel.Error);
        Assert.Equal(LogLevel.Error, logger.Level);
    }

    [Fact]
    public void ClearLevel_ClearsLevel()
    {
        using var client = CreateSmplClient();
        var logger = CreateTestLogger(client);

        logger.SetLevel(LogLevel.Warn);
        Assert.Equal(LogLevel.Warn, logger.Level);

        logger.ClearLevel();
        Assert.Null(logger.Level);
    }

    // ------------------------------------------------------------------
    // SetEnvironmentLevel / ClearEnvironmentLevel / ClearAll
    // ------------------------------------------------------------------

    [Fact]
    public void SetEnvironmentLevel_SetsEnvLevel()
    {
        using var client = CreateSmplClient();
        var logger = CreateTestLogger(client);

        logger.SetEnvironmentLevel("production", LogLevel.Error);

        Assert.True(logger.Environments.ContainsKey("production"));
        Assert.Equal("ERROR", logger.Environments["production"]["level"]);
    }

    [Fact]
    public void ClearEnvironmentLevel_RemovesEnv()
    {
        using var client = CreateSmplClient();
        var logger = CreateTestLogger(client);

        logger.SetEnvironmentLevel("staging", LogLevel.Debug);
        Assert.True(logger.Environments.ContainsKey("staging"));

        logger.ClearEnvironmentLevel("staging");
        Assert.False(logger.Environments.ContainsKey("staging"));
    }

    [Fact]
    public void ClearAllEnvironmentLevels_ClearsAll()
    {
        using var client = CreateSmplClient();
        var logger = CreateTestLogger(client);

        logger.SetEnvironmentLevel("production", LogLevel.Error);
        logger.SetEnvironmentLevel("staging", LogLevel.Debug);
        Assert.Equal(2, logger.Environments.Count);

        logger.ClearAllEnvironmentLevels();
        Assert.Empty(logger.Environments);
    }

    // ------------------------------------------------------------------
    // ToString
    // ------------------------------------------------------------------

    [Fact]
    public void ToString_ReturnsExpectedFormat()
    {
        using var client = CreateSmplClient();
        var logger = CreateTestLogger(client);

        Assert.Equal("Logger(Key=test-logger, Level=)", logger.ToString());

        logger.SetLevel(LogLevel.Info);
        Assert.Equal("Logger(Key=test-logger, Level=Info)", logger.ToString());
    }

    // ------------------------------------------------------------------
    // Initial properties
    // ------------------------------------------------------------------

    [Fact]
    public void New_HasExpectedDefaults()
    {
        using var client = CreateSmplClient();
        var logger = CreateTestLogger(client);

        Assert.Null(logger.Id);
        Assert.Equal("test-logger", logger.Key);
        Assert.Equal("Test Logger", logger.Name);
        Assert.Null(logger.Level);
        Assert.Null(logger.Group);
        Assert.False(logger.Managed);
        Assert.Empty(logger.Sources);
        Assert.Empty(logger.Environments);
        Assert.Null(logger.CreatedAt);
        Assert.Null(logger.UpdatedAt);
    }

    [Fact]
    public void New_WithoutName_GeneratesNameFromKey()
    {
        using var client = CreateSmplClient();
        var logger = client.Logging.New("checkout-v2");

        Assert.Equal("Checkout V2", logger.Name);
    }
}
