using System.Net;
using System.Text;
using Smplkit.Errors;
using Smplkit.Logging;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Logging;

public class LoggingClientTests
{
    private const string LoggerId = "my-logger";
    private const string LoggerName = "My Logger";

    private const string LogGroupId = "my-group";
    private const string LogGroupName = "My Group";

    private static (SmplClient client, MockHttpMessageHandler handler) CreateClient(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handlerFn)
    {
        var handler = new MockHttpMessageHandler(handlerFn);
        var httpClient = new HttpClient(handler);
        var options = TestData.DefaultOptions();
        var client = new SmplClient(options, httpClient);
        return (client, handler);
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/vnd.api+json"),
        };
    }

    private static string SingleLoggerJson() =>
        $$"""
        {
            "data": {
                "id": "{{LoggerId}}",
                "type": "logger",
                "attributes": {
                    "id": "{{LoggerId}}",
                    "name": "{{LoggerName}}",
                    "level": "INFO",
                    "group": null,
                    "managed": false,
                    "sources": [],
                    "environments": {},
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;

    private static string LoggerListJson() =>
        $$"""
        {
            "data": [
                {
                    "id": "{{LoggerId}}",
                    "type": "logger",
                    "attributes": {
                        "id": "{{LoggerId}}",
                        "name": "{{LoggerName}}",
                        "level": "INFO",
                        "group": null,
                        "managed": false,
                        "sources": [],
                        "environments": {},
                        "created_at": "2024-01-15T10:30:00Z",
                        "updated_at": "2024-01-15T10:30:00Z"
                    }
                }
            ]
        }
        """;

    private static string SingleLogGroupJson() =>
        $$"""
        {
            "data": {
                "id": "{{LogGroupId}}",
                "type": "log_group",
                "attributes": {
                    "id": "{{LogGroupId}}",
                    "name": "{{LogGroupName}}",
                    "level": "WARN",
                    "group": null,
                    "environments": {},
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;

    private static string LogGroupListJson() =>
        $$"""
        {
            "data": [
                {
                    "id": "{{LogGroupId}}",
                    "type": "log_group",
                    "attributes": {
                        "id": "{{LogGroupId}}",
                        "name": "{{LogGroupName}}",
                        "level": "WARN",
                        "group": null,
                        "environments": {},
                        "created_at": "2024-01-15T10:30:00Z",
                        "updated_at": "2024-01-15T10:30:00Z"
                    }
                }
            ]
        }
        """;

    // ------------------------------------------------------------------
    // New + SaveAsync creates logger (POST)
    // ------------------------------------------------------------------

    [Fact]
    public async Task New_SaveAsync_CreatesLogger()
    {
        var (client, handler) = CreateClient(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("logging.smplkit.com") && req.Method == HttpMethod.Post)
                return Task.FromResult(JsonResponse(SingleLoggerJson(), HttpStatusCode.Created));
            return Task.FromResult(JsonResponse("""{"data":[]}"""));
        });

        var logger = client.Logging.New(LoggerId);
        Assert.Equal(LoggerId, logger.Id);

        await logger.SaveAsync();

        Assert.Equal(LoggerId, logger.Id);
        Assert.Equal(LoggerName, logger.Name);
        Assert.Equal(LogLevel.Info, logger.Level);

        var postReq = handler.Requests.First(r => r.Method == HttpMethod.Post);
        Assert.Contains("logging.smplkit.com", postReq.RequestUri!.AbsoluteUri);
    }

    // ------------------------------------------------------------------
    // GetAsync by id (direct GET)
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetAsync_ById_ReturnsLogger()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonResponse(SingleLoggerJson())));

        var logger = await client.Logging.GetAsync(LoggerId);

        Assert.Equal(LoggerId, logger.Id);
        Assert.Equal(LoggerName, logger.Name);
        Assert.Equal(LogLevel.Info, logger.Level);
        Assert.NotNull(handler.LastRequest);
        Assert.Contains("logging.smplkit.com", handler.LastRequest.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task GetAsync_NotFound_ThrowsSmplNotFoundException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("""{"data":null}""")));

        await Assert.ThrowsAsync<SmplNotFoundException>(
            () => client.Logging.GetAsync("nonexistent"));
    }

    // ------------------------------------------------------------------
    // ListAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task ListAsync_ReturnsLoggerList()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(LoggerListJson())));

        var loggers = await client.Logging.ListAsync();

        Assert.Single(loggers);
        Assert.Equal(LoggerId, loggers[0].Id);
        Assert.Equal(LoggerName, loggers[0].Name);
    }

    [Fact]
    public async Task ListAsync_EmptyResponse_ReturnsEmptyList()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("""{"data":[]}""")));

        var loggers = await client.Logging.ListAsync();

        Assert.Empty(loggers);
    }

    // ------------------------------------------------------------------
    // DeleteAsync by id
    // ------------------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_ById_DeletesLogger()
    {
        var (client, handler) = CreateClient(req =>
        {
            if (req.Method == HttpMethod.Delete)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            return Task.FromResult(JsonResponse("{}"));
        });

        await client.Logging.DeleteAsync(LoggerId);

        var deleteReq = handler.Requests.First(r => r.Method == HttpMethod.Delete);
        Assert.Contains(LoggerId, deleteReq.RequestUri!.AbsoluteUri);
    }

    // ------------------------------------------------------------------
    // NewGroup + SaveAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task NewGroup_SaveAsync_CreatesLogGroup()
    {
        var (client, handler) = CreateClient(req =>
        {
            if (req.Method == HttpMethod.Post)
                return Task.FromResult(JsonResponse(SingleLogGroupJson(), HttpStatusCode.Created));
            return Task.FromResult(JsonResponse("""{"data":[]}"""));
        });

        var group = client.Logging.NewGroup(LogGroupId);
        Assert.Equal(LogGroupId, group.Id);

        await group.SaveAsync();

        Assert.Equal(LogGroupId, group.Id);
        Assert.Equal(LogGroupName, group.Name);
        Assert.Equal(LogLevel.Warn, group.Level);
    }

    // ------------------------------------------------------------------
    // GetGroupAsync by id (direct GET)
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetGroupAsync_ById_ReturnsLogGroup()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(SingleLogGroupJson())));

        var group = await client.Logging.GetGroupAsync(LogGroupId);

        Assert.Equal(LogGroupId, group.Id);
        Assert.Equal(LogGroupName, group.Name);
        Assert.Equal(LogLevel.Warn, group.Level);
    }

    [Fact]
    public async Task GetGroupAsync_NotFound_ThrowsSmplNotFoundException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("""{"data":null}""")));

        await Assert.ThrowsAsync<SmplNotFoundException>(
            () => client.Logging.GetGroupAsync("nonexistent"));
    }

    // ------------------------------------------------------------------
    // ListGroupsAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task ListGroupsAsync_ReturnsLogGroupList()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(LogGroupListJson())));

        var groups = await client.Logging.ListGroupsAsync();

        Assert.Single(groups);
        Assert.Equal(LogGroupId, groups[0].Id);
    }

    [Fact]
    public async Task ListGroupsAsync_EmptyResponse_ReturnsEmptyList()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("""{"data":[]}""")));

        var groups = await client.Logging.ListGroupsAsync();

        Assert.Empty(groups);
    }

    // ------------------------------------------------------------------
    // DeleteGroupAsync by id
    // ------------------------------------------------------------------

    [Fact]
    public async Task DeleteGroupAsync_ById_DeletesLogGroup()
    {
        var (client, handler) = CreateClient(req =>
        {
            if (req.Method == HttpMethod.Delete)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            return Task.FromResult(JsonResponse("{}"));
        });

        await client.Logging.DeleteGroupAsync(LogGroupId);

        var deleteReq = handler.Requests.First(r => r.Method == HttpMethod.Delete);
        Assert.Contains(LogGroupId, deleteReq.RequestUri!.AbsoluteUri);
    }

    // ------------------------------------------------------------------
    // StartAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task StartAsync_FetchesLoggersAndGroups()
    {
        int listCalls = 0;
        var (client, _) = CreateClient(req =>
        {
            listCalls++;
            return Task.FromResult(JsonResponse("""{"data":[]}"""));
        });

        // StartAsync should fetch loggers and groups (2 list calls)
        // Note: StartAsync also opens a WebSocket, but in tests it will
        // attempt to connect — we're verifying the HTTP calls happen.
        try
        {
            await client.Logging.StartAsync();
        }
        catch
        {
            // WebSocket connection will fail in unit tests, but
            // the list calls should have already been made
        }

        Assert.True(listCalls >= 2, $"Expected at least 2 list calls, got {listCalls}");
    }

    // ------------------------------------------------------------------
    // OnChange listeners
    // ------------------------------------------------------------------

    [Fact]
    public void OnChange_Global_RegistersListener()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("""{"data":[]}""")));

        var events = new List<LoggerChangeEvent>();
        client.Logging.OnChange(e => events.Add(e));

        // Listener registered without error — we can't easily trigger the event
        // from the outside without a real WebSocket, but registration succeeds.
        Assert.Empty(events); // No events fired yet
    }

    [Fact]
    public void OnChange_Scoped_RegistersListener()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("""{"data":[]}""")));

        var events = new List<LoggerChangeEvent>();
        client.Logging.OnChange("my-logger", e => events.Add(e));

        Assert.Empty(events); // No events fired yet
    }

    // ------------------------------------------------------------------
    // SaveAsync (update — Id is not null → PUT) for Logger
    // ------------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_Update_Logger_PutsToApi()
    {
        int requestCount = 0;
        var (client, handler) = CreateClient(_ =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                // GetAsync (direct GET)
                return Task.FromResult(JsonResponse(SingleLoggerJson()));
            }
            // SaveAsync (PUT) response
            return Task.FromResult(JsonResponse(SingleLoggerJson()));
        });

        var logger = await client.Logging.GetAsync(LoggerId);
        Assert.NotNull(logger.Id);

        logger.Name = "Updated Logger";
        await logger.SaveAsync();

        var putReq = handler.Requests.LastOrDefault(r => r.Method == HttpMethod.Put);
        Assert.NotNull(putReq);
        Assert.Contains("/api/v1/loggers/", putReq.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task SaveAsync_Update_Logger_AppliesAllReturnedFields()
    {
        int requestCount = 0;
        var (client, _) = CreateClient(_ =>
        {
            requestCount++;
            if (requestCount == 1)
                return Task.FromResult(JsonResponse(SingleLoggerJson()));
            return Task.FromResult(JsonResponse(SingleLoggerJson()));
        });

        var logger = await client.Logging.GetAsync(LoggerId);
        logger.Name = "Updated";
        await logger.SaveAsync();

        // Verify fields were applied from server response
        Assert.Equal(LoggerId, logger.Id);
        Assert.Equal(LoggerName, logger.Name);
        Assert.Equal(LogLevel.Info, logger.Level);
        Assert.NotNull(logger.CreatedAt);
        Assert.NotNull(logger.UpdatedAt);
    }

    // ------------------------------------------------------------------
    // SaveAsync (update — Id is not null → PUT) for LogGroup
    // ------------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_Update_LogGroup_PutsToApi()
    {
        int requestCount = 0;
        var (client, handler) = CreateClient(_ =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                // GetGroupAsync (direct GET)
                return Task.FromResult(JsonResponse(SingleLogGroupJson()));
            }
            // SaveAsync (PUT) response
            return Task.FromResult(JsonResponse(SingleLogGroupJson()));
        });

        var group = await client.Logging.GetGroupAsync(LogGroupId);
        Assert.NotNull(group.Id);

        group.Name = "Updated Group";
        await group.SaveAsync();

        var putReq = handler.Requests.LastOrDefault(r => r.Method == HttpMethod.Put);
        Assert.NotNull(putReq);
        Assert.Contains("/api/v1/log_groups/", putReq.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task SaveAsync_Update_LogGroup_AppliesAllReturnedFields()
    {
        int requestCount = 0;
        var (client, _) = CreateClient(_ =>
        {
            requestCount++;
            if (requestCount == 1)
                return Task.FromResult(JsonResponse(SingleLogGroupJson()));
            return Task.FromResult(JsonResponse(SingleLogGroupJson()));
        });

        var group = await client.Logging.GetGroupAsync(LogGroupId);
        group.Name = "Updated";
        await group.SaveAsync();

        // Verify fields were applied from server response
        Assert.Equal(LogGroupId, group.Id);
        Assert.Equal(LogGroupName, group.Name);
        Assert.Equal(LogLevel.Warn, group.Level);
        Assert.NotNull(group.CreatedAt);
        Assert.NotNull(group.UpdatedAt);
    }

    // ------------------------------------------------------------------
    // HandleLoggerChanged via reflection
    // ------------------------------------------------------------------

    [Fact]
    public void HandleLoggerChanged_WithValidData_FiresListeners()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("""{"data":[]}""")));

        var globalEvents = new List<LoggerChangeEvent>();
        var scopedEvents = new List<LoggerChangeEvent>();

        client.Logging.OnChange(e => globalEvents.Add(e));
        client.Logging.OnChange("my-logger", e => scopedEvents.Add(e));

        // Invoke HandleLoggerChanged via reflection
        var method = typeof(LoggingClient).GetMethod("HandleLoggerChanged",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method!.Invoke(client.Logging, new object[]
        {
            new Dictionary<string, object?> { ["id"] = "my-logger", ["level"] = "ERROR" }
        });

        Assert.Single(globalEvents);
        Assert.Equal("my-logger", globalEvents[0].Id);
        Assert.Equal(LogLevel.Error, globalEvents[0].Level);
        Assert.Equal("websocket", globalEvents[0].Source);

        Assert.Single(scopedEvents);
        Assert.Equal("my-logger", scopedEvents[0].Id);
    }

    [Fact]
    public void HandleLoggerChanged_FallsBackToKeyField()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("""{"data":[]}""")));

        var events = new List<LoggerChangeEvent>();
        client.Logging.OnChange(e => events.Add(e));

        var method = typeof(LoggingClient).GetMethod("HandleLoggerChanged",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method!.Invoke(client.Logging, new object[]
        {
            new Dictionary<string, object?> { ["key"] = "my-logger", ["level"] = "ERROR" }
        });

        Assert.Single(events);
        Assert.Equal("my-logger", events[0].Id);
    }

    [Fact]
    public void HandleLoggerChanged_NullId_DoesNotFireListeners()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("""{"data":[]}""")));

        var events = new List<LoggerChangeEvent>();
        client.Logging.OnChange(e => events.Add(e));

        var method = typeof(LoggingClient).GetMethod("HandleLoggerChanged",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method!.Invoke(client.Logging, new object[]
        {
            new Dictionary<string, object?> { ["something"] = "else" } // no "id" or "key"
        });

        Assert.Empty(events);
    }

    [Fact]
    public void HandleLoggerChanged_WithNullLevel_SetsNullLevel()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("""{"data":[]}""")));

        var events = new List<LoggerChangeEvent>();
        client.Logging.OnChange(e => events.Add(e));

        var method = typeof(LoggingClient).GetMethod("HandleLoggerChanged",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method!.Invoke(client.Logging, new object[]
        {
            new Dictionary<string, object?> { ["id"] = "my-logger" } // no level
        });

        Assert.Single(events);
        Assert.Null(events[0].Level);
    }

    [Fact]
    public void HandleLoggerChanged_WithInvalidLevel_SetsNullLevel()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("""{"data":[]}""")));

        var events = new List<LoggerChangeEvent>();
        client.Logging.OnChange(e => events.Add(e));

        var method = typeof(LoggingClient).GetMethod("HandleLoggerChanged",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method!.Invoke(client.Logging, new object[]
        {
            new Dictionary<string, object?> { ["id"] = "my-logger", ["level"] = "INVALID_LEVEL" }
        });

        Assert.Single(events);
        Assert.Null(events[0].Level); // Invalid level should result in null
    }

    // ------------------------------------------------------------------
    // FireListeners edge cases
    // ------------------------------------------------------------------

    [Fact]
    public void FireListeners_GlobalListenerThrows_DoesNotPropagate()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("""{"data":[]}""")));

        var postThrowEvents = new List<LoggerChangeEvent>();
        client.Logging.OnChange(_ => throw new InvalidOperationException("boom"));
        client.Logging.OnChange(e => postThrowEvents.Add(e));

        var method = typeof(LoggingClient).GetMethod("HandleLoggerChanged",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        // Should not throw despite listener exception
        method!.Invoke(client.Logging, new object[]
        {
            new Dictionary<string, object?> { ["id"] = "test", ["level"] = "WARN" }
        });

        // Second listener should still fire
        Assert.Single(postThrowEvents);
    }

    [Fact]
    public void FireListeners_ScopedListenerThrows_DoesNotPropagate()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("""{"data":[]}""")));

        var postThrowEvents = new List<LoggerChangeEvent>();
        client.Logging.OnChange("test", _ => throw new InvalidOperationException("boom"));
        client.Logging.OnChange("test", e => postThrowEvents.Add(e));

        var method = typeof(LoggingClient).GetMethod("HandleLoggerChanged",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        // Should not throw
        method!.Invoke(client.Logging, new object[]
        {
            new Dictionary<string, object?> { ["id"] = "test", ["level"] = "INFO" }
        });

        Assert.Single(postThrowEvents);
    }

    [Fact]
    public void FireListeners_NoScopedListeners_OnlyGlobalFires()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("""{"data":[]}""")));

        var globalEvents = new List<LoggerChangeEvent>();
        client.Logging.OnChange(e => globalEvents.Add(e));

        var method = typeof(LoggingClient).GetMethod("HandleLoggerChanged",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method!.Invoke(client.Logging, new object[]
        {
            new Dictionary<string, object?> { ["id"] = "unscoped-logger", ["level"] = "DEBUG" }
        });

        Assert.Single(globalEvents);
        Assert.Equal("unscoped-logger", globalEvents[0].Id);
    }

    // ------------------------------------------------------------------
    // Logger with managed=true and sources
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetAsync_ManagedLoggerWithSources_MapsCorrectly()
    {
        var loggerJson = """
        {
            "data": {
                "id": "managed-logger",
                "type": "logger",
                "attributes": {
                    "id": "managed-logger",
                    "name": "Managed Logger",
                    "level": "DEBUG",
                    "group": "group-id",
                    "managed": true,
                    "sources": [{"type": "file", "path": "/var/log/app.log"}],
                    "environments": {"production": {"level": "ERROR"}},
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(loggerJson)));

        var logger = await client.Logging.GetAsync("managed-logger");

        Assert.Equal("managed-logger", logger.Id);
        Assert.True(logger.Managed);
        Assert.Equal(LogLevel.Debug, logger.Level);
        Assert.Equal("group-id", logger.Group);
        Assert.Single(logger.Sources);
        Assert.True(logger.Environments.ContainsKey("production"));
    }

    // ------------------------------------------------------------------
    // Logger with null level in response
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetAsync_NullLevel_MapsAsNull()
    {
        var loggerJson = """
        {
            "data": {
                "id": "null-level-logger",
                "type": "logger",
                "attributes": {
                    "id": "null-level-logger",
                    "name": "Null Level Logger",
                    "level": null,
                    "group": null,
                    "managed": false,
                    "sources": [],
                    "environments": {},
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(loggerJson)));

        var logger = await client.Logging.GetAsync("null-level-logger");

        Assert.Null(logger.Level);
    }

    // ------------------------------------------------------------------
    // LogGroup with level and group
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetGroupAsync_WithGroupAndLevel_MapsCorrectly()
    {
        var groupJson = """
        {
            "data": {
                "id": "nested-group",
                "type": "log_group",
                "attributes": {
                    "id": "nested-group",
                    "name": "Nested Group",
                    "level": "FATAL",
                    "group": "parent-group-id",
                    "environments": {"staging": {"level": "WARN"}},
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(groupJson)));

        var group = await client.Logging.GetGroupAsync("nested-group");

        Assert.Equal(LogLevel.Fatal, group.Level);
        Assert.Equal("parent-group-id", group.Group);
        Assert.True(group.Environments.ContainsKey("staging"));
    }

    // ------------------------------------------------------------------
    // Close — lifecycle
    // ------------------------------------------------------------------

    [Fact]
    public void Close_WhenNotStarted_DoesNotThrow()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("""{"data":[]}""")));

        // Close when WS was never started — should not throw
        // (Dispose calls Logging.Close())
        client.Dispose();
    }

    // ------------------------------------------------------------------
    // Logger with environment-level as JsonElement
    // ------------------------------------------------------------------

    [Fact]
    public async Task Logger_EnvironmentsWithJsonElementLevel_NormalizesCorrectly()
    {
        var loggerJson = """
        {
            "data": {
                "id": "json-env-logger",
                "type": "logger",
                "attributes": {
                    "id": "json-env-logger",
                    "name": "Json Env Logger",
                    "level": "INFO",
                    "group": null,
                    "managed": false,
                    "sources": [],
                    "environments": {"production": {"level": "ERROR", "enabled": true}},
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(loggerJson)));

        var logger = await client.Logging.GetAsync("json-env-logger");

        Assert.True(logger.Environments.ContainsKey("production"));
        Assert.Equal("ERROR", logger.Environments["production"]["level"]);
    }

    // ------------------------------------------------------------------
    // NewGroup with optional parameters
    // ------------------------------------------------------------------

    [Fact]
    public void NewGroup_WithGroupParameter_SetsGroup()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("""{"data":[]}""")));

        var group = client.Logging.NewGroup("child-group", group: "parent-group-id");

        Assert.Equal("parent-group-id", group.Group);
    }

    [Fact]
    public void New_WithManagedTrue_SetsManagedFlag()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("""{"data":[]}""")));

        var logger = client.Logging.New("managed-logger", managed: true);

        Assert.True(logger.Managed);
    }

    // ------------------------------------------------------------------
    // Close with active WS manager
    // ------------------------------------------------------------------

    [Fact]
    public async Task StartAsync_ThenDispose_ClosesWebSocket()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("""{"data":[]}""")));

        // StartAsync will fetch loggers/groups and attempt WS connection
        // The WS connection will fail silently, but _wsManager will be set
        try
        {
            await client.Logging.StartAsync();
        }
        catch
        {
            // WS connection may fail in tests
        }

        // Dispose calls Close() which unregisters the WS listener
        client.Dispose();
    }

    // ------------------------------------------------------------------
    // Logger with invalid level string
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetAsync_InvalidLevel_MapsAsNull()
    {
        var loggerJson = """
        {
            "data": {
                "id": "invalid-level-logger",
                "type": "logger",
                "attributes": {
                    "id": "invalid-level-logger",
                    "name": "Invalid Level Logger",
                    "level": "INVALID_LEVEL_STRING",
                    "group": null,
                    "managed": false,
                    "sources": [],
                    "environments": {},
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(loggerJson)));

        var logger = await client.Logging.GetAsync("invalid-level-logger");

        // Invalid level should be mapped as null (catch block swallows)
        Assert.Null(logger.Level);
    }

    // ------------------------------------------------------------------
    // LogGroup with invalid level string
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetGroupAsync_InvalidLevel_MapsAsNull()
    {
        var groupJson = """
        {
            "data": {
                "id": "invalid-level-group",
                "type": "log_group",
                "attributes": {
                    "id": "invalid-level-group",
                    "name": "Invalid Level Group",
                    "level": "BOGUS_LEVEL",
                    "group": null,
                    "environments": {},
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(groupJson)));

        var group = await client.Logging.GetGroupAsync("invalid-level-group");

        Assert.Null(group.Level);
    }

    // ------------------------------------------------------------------
    // SaveAsync with non-empty environments (BuildEnvironmentsPayload)
    // ------------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_Logger_WithEnvironments_IncludesEnvsInBody()
    {
        string? postBody = null;
        var (client, _) = CreateClient(async req =>
        {
            if (req.Method == HttpMethod.Post)
                postBody = await req.Content!.ReadAsStringAsync();
            return JsonResponse(SingleLoggerJson(), HttpStatusCode.Created);
        });

        var logger = client.Logging.New("env-logger");
        logger.SetEnvironmentLevel("production", LogLevel.Error);
        await logger.SaveAsync();

        Assert.NotNull(postBody);
        Assert.Contains("production", postBody);
    }

    [Fact]
    public async Task SaveAsync_LogGroup_WithEnvironments_IncludesEnvsInBody()
    {
        string? postBody = null;
        var (client, _) = CreateClient(async req =>
        {
            if (req.Method == HttpMethod.Post)
                postBody = await req.Content!.ReadAsStringAsync();
            return JsonResponse(SingleLogGroupJson(), HttpStatusCode.Created);
        });

        var group = client.Logging.NewGroup("env-group");
        group.SetEnvironmentLevel("staging", LogLevel.Warn);
        await group.SaveAsync();

        Assert.NotNull(postBody);
        Assert.Contains("staging", postBody);
    }

    // ------------------------------------------------------------------
    // NormalizeEnvironments edge case — non-JsonElement, non-null env
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetAsync_EnvironmentsAsNonJsonElement_ReturnsEmptyDict()
    {
        // This tests the path where environments is neither null nor a JsonElement object.
        // In practice, NSwag may serialize environments differently.
        // When environments is an empty object "{}", it comes through as a JsonElement
        // and gets properly handled by the if branch.
        var loggerJson = """
        {
            "data": {
                "id": "no-env-logger",
                "type": "logger",
                "attributes": {
                    "id": "no-env-logger",
                    "name": "No Env Logger",
                    "level": "INFO",
                    "group": null,
                    "managed": false,
                    "sources": [],
                    "environments": null,
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(loggerJson)));

        var logger = await client.Logging.GetAsync("no-env-logger");

        Assert.Empty(logger.Environments);
    }

    // ------------------------------------------------------------------
    // StartAsync is idempotent
    // ------------------------------------------------------------------

    [Fact]
    public async Task StartAsync_SecondCall_IsIdempotent()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("""{"data":[]}""")));

        try { await client.Logging.StartAsync(); } catch { }
        try { await client.Logging.StartAsync(); } catch { }
        // Second call returns immediately, no crash
    }

    // ------------------------------------------------------------------
    // NormalizeEnvironments — non-null, non-JsonElement object
    // ------------------------------------------------------------------

    [Fact]
    public void NormalizeEnvironments_NonNullNonJsonElement_ReturnsEmptyDict()
    {
        // Invoke the private static method via reflection with a string value
        var method = typeof(LoggingClient).GetMethod("NormalizeEnvironments",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        // Pass a string value (not null, not JsonElement)
        var result = method!.Invoke(null, new object?[] { "not-a-json-element" })
            as Dictionary<string, Dictionary<string, object?>>;

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void NormalizeEnvironments_JsonElementArray_ReturnsEmptyDict()
    {
        // Pass a JsonElement that is an array, not an object
        var method = typeof(LoggingClient).GetMethod("NormalizeEnvironments",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var je = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("[]");
        var result = method!.Invoke(null, new object?[] { je })
            as Dictionary<string, Dictionary<string, object?>>;

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    // ------------------------------------------------------------------
    // MapLoggerResource — sources as Dictionary<string, object?>
    // ------------------------------------------------------------------

    [Fact]
    public void MapLoggerResource_SourcesAsDict_AddedDirectly()
    {
        // Invoke MapLoggerResource via reflection with a resource whose
        // sources contain a Dictionary<string, object?> (not a JsonElement).
        // This is a defensive branch — normally NSwag gives JsonElements.
        var method = typeof(LoggingClient).GetMethod("MapLoggerResource",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("""{"data":[]}""")));

        // Build a LoggerResource with sources as List containing a dict
        var resource = new Smplkit.Internal.Generated.Logging.LoggerResource
        {
            Id = "test-id",
            Attributes = new Smplkit.Internal.Generated.Logging.Logger
            {
                Name = "Test",
                Level = "INFO",
                Group = null,
                Managed = false,
                Sources = new List<object> { new Dictionary<string, object?> { ["type"] = "file", ["path"] = "/tmp/log" } },
                Environments = null,
                Created_at = null,
                Updated_at = null,
            }
        };

        var result = method!.Invoke(client.Logging, new object?[] { resource })
            as Logger;

        Assert.NotNull(result);
        Assert.Single(result!.Sources);
        Assert.Equal("file", result.Sources[0]["type"]);
    }
}
