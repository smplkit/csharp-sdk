using System.Net;
using System.Text;
using Smplkit.Errors;
using Smplkit.Logging;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Logging;

public class LoggingClientTests
{
    private const string LoggerId = "550e8400-e29b-41d4-a716-446655440099";
    private const string LoggerKey = "my-logger";
    private const string LoggerName = "My Logger";

    private const string LogGroupId = "550e8400-e29b-41d4-a716-446655440088";
    private const string LogGroupKey = "my-group";
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
                    "key": "{{LoggerKey}}",
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
                        "key": "{{LoggerKey}}",
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
                    "key": "{{LogGroupKey}}",
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
                        "key": "{{LogGroupKey}}",
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

        var logger = client.Logging.New(LoggerKey);
        Assert.Null(logger.Id);
        Assert.Equal(LoggerKey, logger.Key);

        await logger.SaveAsync();

        Assert.Equal(LoggerId, logger.Id);
        Assert.Equal(LoggerKey, logger.Key);
        Assert.Equal(LoggerName, logger.Name);
        Assert.Equal(LogLevel.Info, logger.Level);

        var postReq = handler.Requests.First(r => r.Method == HttpMethod.Post);
        Assert.Contains("logging.smplkit.com", postReq.RequestUri!.AbsoluteUri);
    }

    // ------------------------------------------------------------------
    // GetAsync by key (list with filter[key])
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetAsync_ByKey_ReturnsLogger()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonResponse(LoggerListJson())));

        var logger = await client.Logging.GetAsync(LoggerKey);

        Assert.Equal(LoggerId, logger.Id);
        Assert.Equal(LoggerKey, logger.Key);
        Assert.Equal(LoggerName, logger.Name);
        Assert.Equal(LogLevel.Info, logger.Level);
        Assert.NotNull(handler.LastRequest);
        Assert.Contains("logging.smplkit.com", handler.LastRequest.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task GetAsync_NotFound_ThrowsSmplNotFoundException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("""{"data":[]}""")));

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
        Assert.Equal(LoggerKey, loggers[0].Key);
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
    // DeleteAsync by key
    // ------------------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_ByKey_DeletesLogger()
    {
        int requestCount = 0;
        var (client, handler) = CreateClient(req =>
        {
            requestCount++;
            // First request: list to find the logger by key
            if (req.Method == HttpMethod.Get)
                return Task.FromResult(JsonResponse(LoggerListJson()));
            // Second request: delete by id
            if (req.Method == HttpMethod.Delete)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            return Task.FromResult(JsonResponse("{}"));
        });

        await client.Logging.DeleteAsync(LoggerKey);

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

        var group = client.Logging.NewGroup(LogGroupKey);
        Assert.Null(group.Id);
        Assert.Equal(LogGroupKey, group.Key);

        await group.SaveAsync();

        Assert.Equal(LogGroupId, group.Id);
        Assert.Equal(LogGroupKey, group.Key);
        Assert.Equal(LogGroupName, group.Name);
        Assert.Equal(LogLevel.Warn, group.Level);
    }

    // ------------------------------------------------------------------
    // GetGroupAsync by key (list all, filter locally)
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetGroupAsync_ByKey_ReturnsLogGroup()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(LogGroupListJson())));

        var group = await client.Logging.GetGroupAsync(LogGroupKey);

        Assert.Equal(LogGroupId, group.Id);
        Assert.Equal(LogGroupKey, group.Key);
        Assert.Equal(LogGroupName, group.Name);
        Assert.Equal(LogLevel.Warn, group.Level);
    }

    [Fact]
    public async Task GetGroupAsync_NotFound_ThrowsSmplNotFoundException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("""{"data":[]}""")));

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
        Assert.Equal(LogGroupKey, groups[0].Key);
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
    // DeleteGroupAsync by key
    // ------------------------------------------------------------------

    [Fact]
    public async Task DeleteGroupAsync_ByKey_DeletesLogGroup()
    {
        var (client, handler) = CreateClient(req =>
        {
            if (req.Method == HttpMethod.Get)
                return Task.FromResult(JsonResponse(LogGroupListJson()));
            if (req.Method == HttpMethod.Delete)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            return Task.FromResult(JsonResponse("{}"));
        });

        await client.Logging.DeleteGroupAsync(LogGroupKey);

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
}
