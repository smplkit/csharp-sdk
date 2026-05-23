using System.Net;
using System.Text;
using Smplkit;
using Smplkit.Errors;
using Smplkit.Logging;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Logging;

/// <summary>
/// Tests for the <see cref="Logger"/> and <see cref="LogGroup"/> active-record
/// models: SetLevel/ClearLevel (rule 7), Save/Delete round-trip.
/// </summary>
public class LoggerModelTests
{
    private static (SmplManagementClient mgmt, MockHttpMessageHandler handler) Make(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        var handler = new MockHttpMessageHandler(respond);
        var http = new HttpClient(handler);
        var mgmt = new SmplManagementClient(new SmplClientOptions { ApiKey = "k" }, http);
        return (mgmt, handler);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/vnd.api+json") };

    private const string LoggerJson = """
        {
            "data": {
                "id": "showcase",
                "type": "logger",
                "attributes": {
                    "name": "showcase",
                    "level": "INFO",
                    "group": null,
                    "managed": true,
                    "sources": [],
                    "environments": {
                        "production": {"level": "ERROR"},
                        "staging": {"level": "DEBUG"}
                    },
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;

    private const string LogGroupJson = """
        {
            "data": {
                "id": "billing",
                "type": "log_group",
                "attributes": {
                    "name": "Billing",
                    "level": "WARN",
                    "parent_id": null,
                    "environments": {
                        "production": {"level": "ERROR"}
                    },
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;

    // Logger SetLevel / ClearLevel

    [Fact]
    public void Logger_SetLevel_BaseLevel()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var logger = mgmt.Loggers.New("showcase");
        logger.SetLevel(LogLevel.Info);
        Assert.Equal(LogLevel.Info, logger.Level);
    }

    [Fact]
    public void Logger_SetLevel_PerEnvironment()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var logger = mgmt.Loggers.New("showcase");
        logger.SetLevel(LogLevel.Error, environment: "production");
        Assert.Equal("ERROR", logger.Environments["production"]["level"]);
    }

    [Fact]
    public void Logger_ClearLevel_Base()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var logger = mgmt.Loggers.New("showcase");
        logger.SetLevel(LogLevel.Info);
        logger.ClearLevel();
        Assert.Null(logger.Level);
    }

    [Fact]
    public void Logger_ClearLevel_PerEnvironment()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var logger = mgmt.Loggers.New("showcase");
        logger.SetLevel(LogLevel.Error, environment: "production");
        logger.ClearLevel(environment: "production");
        Assert.False(logger.Environments.ContainsKey("production"));
    }

    [Fact]
    public void Logger_ClearAllEnvironmentLevels()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var logger = mgmt.Loggers.New("showcase");
        logger.SetLevel(LogLevel.Error, environment: "production");
        logger.SetLevel(LogLevel.Debug, environment: "staging");
        logger.ClearAllEnvironmentLevels();
        Assert.Empty(logger.Environments);
    }

    [Fact]
    public async Task Logger_SaveAsync_SendsPut()
    {
        HttpRequestMessage? captured = null;
        var (mgmt, _) = Make(req =>
        {
            captured = req;
            return Task.FromResult(Json(LoggerJson));
        });
        var logger = mgmt.Loggers.New("showcase");
        logger.SetLevel(LogLevel.Info);
        await logger.SaveAsync();
        Assert.Equal(HttpMethod.Put, captured!.Method); // Loggers always use upsert (PUT)
        Assert.Equal(LogLevel.Info, logger.Level);
        Assert.True(logger.Environments.ContainsKey("production"));
    }

    [Fact]
    public async Task Logger_DeleteAsync_OnSaved_SendsDelete()
    {
        HttpRequestMessage? captured = null;
        var (mgmt, _) = Make(req =>
        {
            if (req.Method == HttpMethod.Get) return Task.FromResult(Json(LoggerJson));
            captured = req;
            return Task.FromResult(Json("{}", HttpStatusCode.NoContent));
        });
        var logger = await mgmt.Loggers.GetAsync("showcase");
        await logger.DeleteAsync();
        Assert.Equal(HttpMethod.Delete, captured!.Method);
    }

    [Fact]
    public async Task Logger_DeleteAsync_OnUnsaved_Throws()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var logger = mgmt.Loggers.New("nope");
        logger.Id = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => logger.DeleteAsync());
    }

    [Fact]
    public void Logger_ToString_IncludesIdAndLevel()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var logger = mgmt.Loggers.New("showcase");
        logger.SetLevel(LogLevel.Warn);
        var s = logger.ToString();
        Assert.Contains("showcase", s);
        Assert.Contains("Warn", s);
    }

    // LogGroup SetLevel / ClearLevel

    [Fact]
    public void LogGroup_SetLevel_BaseLevel()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var group = mgmt.LogGroups.New("billing");
        group.SetLevel(LogLevel.Warn);
        Assert.Equal(LogLevel.Warn, group.Level);
    }

    [Fact]
    public void LogGroup_SetLevel_PerEnvironment()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var group = mgmt.LogGroups.New("billing");
        group.SetLevel(LogLevel.Error, environment: "production");
        Assert.Equal("ERROR", group.Environments["production"]["level"]);
    }

    [Fact]
    public void LogGroup_ClearLevel_Base()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var group = mgmt.LogGroups.New("billing");
        group.SetLevel(LogLevel.Warn);
        group.ClearLevel();
        Assert.Null(group.Level);
    }

    [Fact]
    public void LogGroup_ClearLevel_PerEnvironment()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var group = mgmt.LogGroups.New("billing");
        group.SetLevel(LogLevel.Error, environment: "production");
        group.ClearLevel(environment: "production");
        Assert.False(group.Environments.ContainsKey("production"));
    }

    [Fact]
    public void LogGroup_ClearAllEnvironmentLevels()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var group = mgmt.LogGroups.New("billing");
        group.SetLevel(LogLevel.Error, environment: "production");
        group.ClearAllEnvironmentLevels();
        Assert.Empty(group.Environments);
    }

    [Fact]
    public async Task LogGroup_SaveAsync_NewGroup_SendsPost()
    {
        HttpRequestMessage? captured = null;
        var (mgmt, _) = Make(req =>
        {
            captured = req;
            return Task.FromResult(Json(LogGroupJson, HttpStatusCode.Created));
        });
        var group = mgmt.LogGroups.New("billing");
        group.SetLevel(LogLevel.Warn);
        await group.SaveAsync();
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.NotNull(group.CreatedAt);
    }

    [Fact]
    public async Task LogGroup_SaveAsync_ExistingGroup_SendsPut()
    {
        HttpRequestMessage? captured = null;
        var (mgmt, _) = Make(req =>
        {
            if (req.Method == HttpMethod.Get) return Task.FromResult(Json(LogGroupJson));
            captured = req;
            return Task.FromResult(Json(LogGroupJson));
        });
        var group = await mgmt.LogGroups.GetAsync("billing");
        group.SetLevel(LogLevel.Error);
        await group.SaveAsync();
        Assert.Equal(HttpMethod.Put, captured!.Method);
    }

    [Fact]
    public async Task LogGroup_DeleteAsync_OnSaved_SendsDelete()
    {
        HttpRequestMessage? captured = null;
        var (mgmt, _) = Make(req =>
        {
            if (req.Method == HttpMethod.Get) return Task.FromResult(Json(LogGroupJson));
            captured = req;
            return Task.FromResult(Json("{}", HttpStatusCode.NoContent));
        });
        var group = await mgmt.LogGroups.GetAsync("billing");
        await group.DeleteAsync();
        Assert.Equal(HttpMethod.Delete, captured!.Method);
    }

    [Fact]
    public async Task LogGroup_DeleteAsync_OnUnsaved_Throws()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var group = mgmt.LogGroups.New("nope");
        group.Id = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => group.DeleteAsync());
    }

    [Fact]
    public async Task LogGroup_SaveAsync_OnUnsavedWithoutId_Throws()
    {
        // BuildLogGroupCreateRequestBody requires a caller-supplied id; the create
        // envelope's data.id is no longer optional.
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var group = mgmt.LogGroups.New("placeholder");
        group.Id = null;
        await Assert.ThrowsAsync<ValidationException>(() => group.SaveAsync());
    }

    [Fact]
    public void LogGroup_ToString_IncludesIdAndLevel()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var group = mgmt.LogGroups.New("billing");
        group.SetLevel(LogLevel.Warn);
        var s = group.ToString();
        Assert.Contains("billing", s);
        Assert.Contains("Warn", s);
    }

    // List paths
    [Fact]
    public async Task Loggers_ListAsync_NullData_ReturnsEmpty()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("""{"data":null}""")));
        var loggers = await mgmt.Loggers.ListAsync();
        Assert.Empty(loggers);
    }

    [Fact]
    public async Task LogGroups_ListAsync_NullData_ReturnsEmpty()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("""{"data":null}""")));
        var groups = await mgmt.LogGroups.ListAsync();
        Assert.Empty(groups);
    }

    [Fact]
    public async Task LogGroup_GetAsync_NotFound_Throws()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(
            Json("""{"errors":[{"detail":"x"}]}""", HttpStatusCode.NotFound)));
        await Assert.ThrowsAsync<NotFoundException>(() => mgmt.LogGroups.GetAsync("missing"));
    }
}
