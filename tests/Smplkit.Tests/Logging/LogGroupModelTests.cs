using Smplkit.Logging;
using Xunit;

namespace Smplkit.Tests.Logging;

public class LogGroupModelTests
{
    private static LogGroup CreateTestLogGroup(SmplClient client)
    {
        return client.Logging.NewGroup("test-group", name: "Test Group");
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
        var group = CreateTestLogGroup(client);

        Assert.Null(group.Level);

        group.SetLevel(LogLevel.Error);
        Assert.Equal(LogLevel.Error, group.Level);
    }

    [Fact]
    public void ClearLevel_ClearsLevel()
    {
        using var client = CreateSmplClient();
        var group = CreateTestLogGroup(client);

        group.SetLevel(LogLevel.Warn);
        Assert.Equal(LogLevel.Warn, group.Level);

        group.ClearLevel();
        Assert.Null(group.Level);
    }

    // ------------------------------------------------------------------
    // SetEnvironmentLevel / ClearEnvironmentLevel / ClearAll
    // ------------------------------------------------------------------

    [Fact]
    public void SetEnvironmentLevel_SetsEnvLevel()
    {
        using var client = CreateSmplClient();
        var group = CreateTestLogGroup(client);

        group.SetEnvironmentLevel("production", LogLevel.Fatal);

        Assert.True(group.Environments.ContainsKey("production"));
        Assert.Equal("FATAL", group.Environments["production"]["level"]);
    }

    [Fact]
    public void ClearEnvironmentLevel_RemovesEnv()
    {
        using var client = CreateSmplClient();
        var group = CreateTestLogGroup(client);

        group.SetEnvironmentLevel("staging", LogLevel.Debug);
        Assert.True(group.Environments.ContainsKey("staging"));

        group.ClearEnvironmentLevel("staging");
        Assert.False(group.Environments.ContainsKey("staging"));
    }

    [Fact]
    public void ClearAllEnvironmentLevels_ClearsAll()
    {
        using var client = CreateSmplClient();
        var group = CreateTestLogGroup(client);

        group.SetEnvironmentLevel("production", LogLevel.Error);
        group.SetEnvironmentLevel("staging", LogLevel.Debug);
        Assert.Equal(2, group.Environments.Count);

        group.ClearAllEnvironmentLevels();
        Assert.Empty(group.Environments);
    }

    // ------------------------------------------------------------------
    // ToString
    // ------------------------------------------------------------------

    [Fact]
    public void ToString_ReturnsExpectedFormat()
    {
        using var client = CreateSmplClient();
        var group = CreateTestLogGroup(client);

        Assert.Equal("LogGroup(Key=test-group, Level=)", group.ToString());

        group.SetLevel(LogLevel.Warn);
        Assert.Equal("LogGroup(Key=test-group, Level=Warn)", group.ToString());
    }

    // ------------------------------------------------------------------
    // Initial properties
    // ------------------------------------------------------------------

    [Fact]
    public void NewGroup_HasExpectedDefaults()
    {
        using var client = CreateSmplClient();
        var group = CreateTestLogGroup(client);

        Assert.Null(group.Id);
        Assert.Equal("test-group", group.Key);
        Assert.Equal("Test Group", group.Name);
        Assert.Null(group.Level);
        Assert.Null(group.Group);
        Assert.Empty(group.Environments);
        Assert.Null(group.CreatedAt);
        Assert.Null(group.UpdatedAt);
    }

    [Fact]
    public void NewGroup_WithoutName_GeneratesNameFromKey()
    {
        using var client = CreateSmplClient();
        var group = client.Logging.NewGroup("com.acme.payments");

        Assert.Equal("Com Acme Payments", group.Name);
    }
}
