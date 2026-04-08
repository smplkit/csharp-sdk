using System.Net;
using System.Text;
using Smplkit.Config;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Config;

public class ModelsTests
{
    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/vnd.api+json"),
        };

    private static SmplClient CreateSmplClient()
    {
        var handler = new MockHttpMessageHandler(_ => Task.FromResult(JsonResponse("{}")));
        var httpClient = new HttpClient(handler);
        return new SmplClient(TestData.DefaultOptions(), httpClient);
    }

    // ------------------------------------------------------------------
    // Config class — created via ConfigClient.New
    // ------------------------------------------------------------------

    [Fact]
    public void Config_New_HasCorrectProperties()
    {
        var smplClient = CreateSmplClient();

        var config = smplClient.Config.New(
            key: "my_key",
            name: "My Config",
            description: "A description",
            parent: "parent-id");

        Assert.Null(config.Id);
        Assert.Equal("my_key", config.Key);
        Assert.Equal("My Config", config.Name);
        Assert.Equal("A description", config.Description);
        Assert.Equal("parent-id", config.Parent);
        Assert.NotNull(config.Items);
        Assert.Empty(config.Items);
        Assert.NotNull(config.Environments);
        Assert.Empty(config.Environments);
        Assert.Null(config.CreatedAt);
        Assert.Null(config.UpdatedAt);
    }

    [Fact]
    public void Config_New_NullableFieldsCanBeNull()
    {
        var smplClient = CreateSmplClient();

        var config = smplClient.Config.New("key");

        Assert.Null(config.Id);
        Assert.Null(config.Description);
        Assert.Null(config.Parent);
        Assert.Null(config.CreatedAt);
        Assert.Null(config.UpdatedAt);
    }

    [Fact]
    public void Config_Properties_AreMutable()
    {
        var smplClient = CreateSmplClient();

        var config = smplClient.Config.New("key", "Name");
        config.Name = "Updated Name";
        config.Description = "Updated Description";
        config.Parent = "new-parent";
        config.Items = new() { ["timeout"] = 30 };
        config.Environments = new()
        {
            ["production"] = new() { ["timeout"] = 60 },
        };

        Assert.Equal("Updated Name", config.Name);
        Assert.Equal("Updated Description", config.Description);
        Assert.Equal("new-parent", config.Parent);
        Assert.Equal(30, config.Items["timeout"]);
        Assert.True(config.Environments.ContainsKey("production"));
    }

    [Fact]
    public void Config_Items_CanBeMutatedDirectly()
    {
        var smplClient = CreateSmplClient();

        var config = smplClient.Config.New("key");
        config.Items["a"] = 1;
        config.Items["b"] = "two";
        config.Items["c"] = true;

        Assert.Equal(3, config.Items.Count);
        Assert.Equal(1, config.Items["a"]);
        Assert.Equal("two", config.Items["b"]);
        Assert.Equal(true, config.Items["c"]);
    }

    [Fact]
    public void Config_Environments_CanBeMutatedDirectly()
    {
        var smplClient = CreateSmplClient();

        var config = smplClient.Config.New("key");
        config.Environments["prod"] = new() { ["timeout"] = 60 };
        config.Environments["staging"] = new() { ["debug"] = true };

        Assert.Equal(2, config.Environments.Count);
        Assert.Equal(60, config.Environments["prod"]["timeout"]);
        Assert.Equal(true, config.Environments["staging"]["debug"]);
    }

    [Fact]
    public void Config_ToString_ReturnsFormattedString()
    {
        var smplClient = CreateSmplClient();
        var config = smplClient.Config.New("my_key", "My Config");

        Assert.Equal("Config(Key=my_key, Name=My Config)", config.ToString());
    }

    // ------------------------------------------------------------------
    // Config populated via GetAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task Config_FromGetAsync_HasAllFieldsPopulated()
    {
        var listJson = """
        {
            "data": [
                {
                    "id": "id-1",
                    "type": "config",
                    "attributes": {
                        "key": "my_key",
                        "name": "My Config",
                        "description": "A description",
                        "parent": "parent-id",
                        "items": {"timeout": {"value": 30, "type": "NUMBER"}},
                        "environments": {
                            "production": {"timeout": {"value": 60}}
                        },
                        "created_at": "2024-01-15T00:00:00Z",
                        "updated_at": "2024-01-16T00:00:00Z"
                    }
                }
            ]
        }
        """;

        var handler = new MockHttpMessageHandler(_ =>
            Task.FromResult(JsonResponse(listJson)));
        var httpClient = new HttpClient(handler);
        var smplClient = new SmplClient(TestData.DefaultOptions(), httpClient);

        var config = await smplClient.Config.GetAsync("my_key");

        Assert.Equal("id-1", config.Id);
        Assert.Equal("my_key", config.Key);
        Assert.Equal("My Config", config.Name);
        Assert.Equal("A description", config.Description);
        Assert.Equal("parent-id", config.Parent);
        Assert.Equal(30L, config.Items["timeout"]);
        Assert.True(config.Environments.ContainsKey("production"));
        Assert.NotNull(config.CreatedAt);
        Assert.NotNull(config.UpdatedAt);
    }

    // ------------------------------------------------------------------
    // ConfigChangeEvent record
    // ------------------------------------------------------------------

    [Fact]
    public void ConfigChangeEvent_HasCorrectProperties()
    {
        var evt = new ConfigChangeEvent("my_config", "timeout", 30, 60, "websocket");

        Assert.Equal("my_config", evt.ConfigKey);
        Assert.Equal("timeout", evt.ItemKey);
        Assert.Equal(30, evt.OldValue);
        Assert.Equal(60, evt.NewValue);
        Assert.Equal("websocket", evt.Source);
    }

    [Fact]
    public void ConfigChangeEvent_WithNullValues()
    {
        var evt = new ConfigChangeEvent("cfg", "key", null, null, "manual");

        Assert.Equal("cfg", evt.ConfigKey);
        Assert.Equal("key", evt.ItemKey);
        Assert.Null(evt.OldValue);
        Assert.Null(evt.NewValue);
        Assert.Equal("manual", evt.Source);
    }
}
