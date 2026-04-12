using System.Net;
using System.Text;
using Smplkit.Config;
using Smplkit.Errors;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Config;

public class ConfigClientTests
{
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

    /// <summary>
    /// Helper that builds a single-resource JSON response wrapping one config resource.
    /// </summary>
    private static string SingleConfigJson(
        string id = TestData.ConfigId,
        string name = TestData.ConfigName,
        string? description = "Test config",
        string? parent = null,
        string itemsJson = """{ "timeout": {"value": 30, "type": "NUMBER"}, "retries": {"value": 3, "type": "NUMBER"} }""",
        string environmentsJson = "{}")
    {
        var descStr = description is null ? "null" : $"\"{description}\"";
        var parentStr = parent is null ? "null" : $"\"{parent}\"";
        return $$"""
        {
            "data": {
                "id": "{{id}}",
                "type": "config",
                "attributes": {
                    "id": "{{id}}",
                    "name": "{{name}}",
                    "description": {{descStr}},
                    "parent": {{parentStr}},
                    "items": {{itemsJson}},
                    "environments": {{environmentsJson}},
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;
    }

    // ---------------------------------------------------------------
    // GetAsync (by id — uses direct GET endpoint)
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetAsync_ById_ReturnsConfig()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonResponse(SingleConfigJson())));

        var config = await client.Config.Management.GetAsync(TestData.ConfigId);

        Assert.Equal(TestData.ConfigId, config.Id);
        Assert.Equal(TestData.ConfigName, config.Name);
        Assert.Equal("Test config", config.Description);
        Assert.Null(config.Parent);
        Assert.NotNull(config.Items);
        Assert.NotNull(config.Environments);

        // Verify direct GET endpoint URL
        Assert.NotNull(handler.LastRequest);
        var url = handler.LastRequest.RequestUri!.ToString();
        Assert.Contains($"/api/v1/configs/{TestData.ConfigId}", url);
    }

    [Fact]
    public async Task GetAsync_SetsAuthorizationHeader()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonResponse(SingleConfigJson())));

        await client.Config.Management.GetAsync(TestData.ConfigId);

        Assert.NotNull(handler.LastRequest);
        var authHeader = handler.LastRequest.Headers.Authorization;
        Assert.NotNull(authHeader);
        Assert.Equal("Bearer", authHeader.Scheme);
        Assert.Equal(TestData.ApiKey, authHeader.Parameter);
    }

    [Fact]
    public async Task GetAsync_SetsUserAgentHeader()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonResponse(SingleConfigJson())));

        await client.Config.Management.GetAsync(TestData.ConfigId);

        Assert.NotNull(handler.LastRequest);
        var userAgent = handler.LastRequest.Headers.UserAgent.ToString();
        Assert.Contains("smplkit-dotnet-sdk", userAgent);
    }

    [Fact]
    public async Task GetAsync_WhenNotFound_ThrowsSmplNotFoundException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Not found"}]}""",
                HttpStatusCode.NotFound)));

        await Assert.ThrowsAsync<SmplNotFoundException>(
            () => client.Config.Management.GetAsync("nonexistent_id"));
    }

    // ---------------------------------------------------------------
    // ListAsync
    // ---------------------------------------------------------------

    [Fact]
    public async Task ListAsync_ReturnsListOfConfigs()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonResponse(TestData.ConfigListJson())));

        var configs = await client.Config.Management.ListAsync();

        Assert.Equal(2, configs.Count);
        Assert.Equal(TestData.ConfigId, configs[0].Id);
        Assert.Equal("payment_service", configs[1].Id);

        Assert.NotNull(handler.LastRequest);
        Assert.Contains("/api/v1/configs", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task ListAsync_WhenEmpty_ReturnsEmptyList()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(TestData.EmptyListJson())));

        var configs = await client.Config.Management.ListAsync();

        Assert.Empty(configs);
    }

    // ---------------------------------------------------------------
    // New + SaveAsync (create)
    // ---------------------------------------------------------------

    [Fact]
    public async Task New_SaveAsync_SendsPostAndReturnsConfig()
    {
        var (client, handler) = CreateClient(async req =>
        {
            // Verify the request body
            var body = await req.Content!.ReadAsStringAsync();
            Assert.Contains("\"name\":", body);
            Assert.Contains("\"type\":\"config\"", body);

            return JsonResponse(
                TestData.SingleConfigJson(),
                HttpStatusCode.Created);
        });

        var config = client.Config.Management.New(TestData.ConfigId, TestData.ConfigName, "Test config");
        Assert.Equal(TestData.ConfigId, config.Id); // Id set by New()

        await config.SaveAsync();

        Assert.Equal(TestData.ConfigId, config.Id);
        Assert.Equal(TestData.ConfigName, config.Name);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Contains("/api/v1/configs", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task New_SaveAsync_WithItems_IncludesItemsInBody()
    {
        var (client, handler) = CreateClient(async req =>
        {
            var body = await req.Content!.ReadAsStringAsync();
            Assert.Contains("\"timeout\"", body);
            return JsonResponse(TestData.SingleConfigJson(), HttpStatusCode.Created);
        });

        var config = client.Config.Management.New("test_id", "Test");
        config.Items["timeout"] = 30;

        await config.SaveAsync();

        Assert.NotNull(config.Id);
    }

    // ---------------------------------------------------------------
    // SaveAsync (update — when Id is not null)
    // ---------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_Update_SendsPutAndReturnsConfig()
    {
        int requestCount = 0;
        var (client, handler) = CreateClient(async req =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                // GetAsync call
                return JsonResponse(SingleConfigJson());
            }
            // PUT for SaveAsync
            var body = await req.Content!.ReadAsStringAsync();
            Assert.Contains("\"name\":", body);
            Assert.Contains("\"type\":\"config\"", body);
            return JsonResponse(TestData.SingleConfigJson());
        });

        var config = await client.Config.Management.GetAsync(TestData.ConfigId);
        config.Name = "Updated Service";

        await config.SaveAsync();

        Assert.Equal(TestData.ConfigId, config.Id);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Put, handler.LastRequest.Method);
        Assert.Contains($"/api/v1/configs/{TestData.ConfigId}", handler.LastRequest.RequestUri!.ToString());
    }

    // ---------------------------------------------------------------
    // DeleteAsync (by id — calls Delete_configAsync directly)
    // ---------------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_SendsDeleteRequest()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));

        await client.Config.Management.DeleteAsync(TestData.ConfigId);

        Assert.Single(handler.Requests);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Delete, handler.LastRequest.Method);
        Assert.Contains($"/api/v1/configs/{TestData.ConfigId}", handler.LastRequest.RequestUri!.ToString());
    }

    // ---------------------------------------------------------------
    // Error mapping
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetAsync_NotFound_ThrowsSmplNotFoundException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Not found"}]}""",
                HttpStatusCode.NotFound)));

        await Assert.ThrowsAsync<SmplNotFoundException>(
            () => client.Config.Management.GetAsync("missing_id"));
    }

    [Fact]
    public async Task DeleteAsync_404_ThrowsSmplNotFoundException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Not found"}]}""",
                HttpStatusCode.NotFound)));

        await Assert.ThrowsAsync<SmplNotFoundException>(
            () => client.Config.Management.DeleteAsync("some_id"));
    }

    [Fact]
    public async Task DeleteAsync_409_ThrowsSmplConflictException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Has children"}]}""",
                HttpStatusCode.Conflict)));

        var ex = await Assert.ThrowsAsync<SmplConflictException>(
            () => client.Config.Management.DeleteAsync(TestData.ConfigId));
        Assert.Equal(409, ex.StatusCode);
    }

    [Fact]
    public async Task SaveAsync_Create_422_ThrowsSmplValidationException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Name is required"}]}""",
                (HttpStatusCode)422)));

        var config = client.Config.Management.New("id");
        var ex = await Assert.ThrowsAsync<SmplValidationException>(
            () => config.SaveAsync());
        Assert.Equal(422, ex.StatusCode);
    }

    [Fact]
    public async Task GetAsync_HttpRequestException_ThrowsSmplConnectionException()
    {
        var (client, _) = CreateClient(_ =>
            throw new HttpRequestException("Connection refused"));

        await Assert.ThrowsAsync<SmplConnectionException>(
            () => client.Config.Management.GetAsync("some_id"));
    }

    [Fact]
    public async Task GetAsync_TaskCanceledException_ThrowsSmplTimeoutException()
    {
        var (client, _) = CreateClient(_ =>
            throw new TaskCanceledException("The request timed out"));

        await Assert.ThrowsAsync<SmplTimeoutException>(
            () => client.Config.Management.GetAsync("some_id"));
    }

    // ---------------------------------------------------------------
    // CancellationToken support
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetAsync_PassesCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(SingleConfigJson())));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.Config.Management.GetAsync(TestData.ConfigId, cts.Token));
    }

    // ---------------------------------------------------------------
    // Content-Type header
    // ---------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_Create_SetsJsonApiContentType()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonResponse(TestData.SingleConfigJson(), HttpStatusCode.Created)));

        var config = client.Config.Management.New("test_id", "Test");
        await config.SaveAsync();

        Assert.NotNull(handler.LastRequest);
        var contentType = handler.LastRequest.Content!.Headers.ContentType!.MediaType;
        Assert.Equal("application/json", contentType);
    }

    // ---------------------------------------------------------------
    // Generic HTTP error (default case)
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetAsync_500_ThrowsSmplException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Internal server error"}]}""",
                HttpStatusCode.InternalServerError)));

        var ex = await Assert.ThrowsAsync<SmplException>(
            () => client.Config.Management.GetAsync("some_id"));
        Assert.Equal(500, ex.StatusCode);
        Assert.NotNull(ex.ResponseBody);
    }

    [Fact]
    public async Task GetAsync_429_ThrowsSmplException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Rate limited"}]}""",
                (HttpStatusCode)429)));

        var ex = await Assert.ThrowsAsync<SmplException>(
            () => client.Config.Management.GetAsync("some_id"));
        Assert.Equal(429, ex.StatusCode);
    }

    // ---------------------------------------------------------------
    // Post/Delete error paths (timeout, connection)
    // ---------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_Create_HttpRequestException_ThrowsSmplConnectionException()
    {
        var (client, _) = CreateClient(_ =>
            throw new HttpRequestException("Connection refused"));

        var config = client.Config.Management.New("test_id", "Test");
        await Assert.ThrowsAsync<SmplConnectionException>(
            () => config.SaveAsync());
    }

    [Fact]
    public async Task SaveAsync_Create_TaskCanceledException_ThrowsSmplTimeoutException()
    {
        var (client, _) = CreateClient(_ =>
            throw new TaskCanceledException("The request timed out"));

        var config = client.Config.Management.New("test_id", "Test");
        await Assert.ThrowsAsync<SmplTimeoutException>(
            () => config.SaveAsync());
    }

    [Fact]
    public async Task DeleteAsync_HttpRequestException_ThrowsSmplConnectionException()
    {
        var (client, _) = CreateClient(_ =>
            throw new HttpRequestException("Connection refused"));

        await Assert.ThrowsAsync<SmplConnectionException>(
            () => client.Config.Management.DeleteAsync("some_id"));
    }

    [Fact]
    public async Task DeleteAsync_TaskCanceledException_ThrowsSmplTimeoutException()
    {
        var (client, _) = CreateClient(_ =>
            throw new TaskCanceledException("The request timed out"));

        await Assert.ThrowsAsync<SmplTimeoutException>(
            () => client.Config.Management.DeleteAsync("some_id"));
    }

    // ---------------------------------------------------------------
    // Null/missing data edge cases
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetAsync_NullData_ThrowsSmplNotFoundException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("""{"data": null}""")));

        await Assert.ThrowsAsync<SmplNotFoundException>(
            () => client.Config.Management.GetAsync("some_id"));
    }

    [Fact]
    public async Task GetAsync_NullAttributesInResult_ThrowsSmplNotFoundException()
    {
        var json = """
        {
            "data": {"id": "some_id", "type": "config", "attributes": null}
        }
        """;

        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(json)));

        await Assert.ThrowsAsync<SmplNotFoundException>(
            () => client.Config.Management.GetAsync("some_id"));
    }

    [Fact]
    public async Task ListAsync_NullData_ReturnsEmptyList()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("""{"data": null}""")));

        var configs = await client.Config.Management.ListAsync();

        Assert.Empty(configs);
    }

    [Fact]
    public async Task ListAsync_SkipsResourcesWithNullAttributes()
    {
        var json = """
        {
            "data": [
                {"id": "abc", "type": "config", "attributes": null},
                {
                    "id": "valid_config",
                    "type": "config",
                    "attributes": {
                        "id": "valid_config",
                        "name": "Valid",
                        "description": null,
                        "parent": null,
                        "items": {},
                        "environments": {},
                        "created_at": "2024-01-15T10:30:00Z",
                        "updated_at": "2024-01-15T10:30:00Z"
                    }
                }
            ]
        }
        """;

        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(json)));

        var configs = await client.Config.Management.ListAsync();

        Assert.Single(configs);
        Assert.Equal("valid_config", configs[0].Id);
    }

    [Fact]
    public async Task SaveAsync_Create_NullResponseData_ThrowsSmplValidationException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("""{"data": null}""", HttpStatusCode.Created)));

        var config = client.Config.Management.New("test_id", "Test");
        await Assert.ThrowsAsync<SmplValidationException>(
            () => config.SaveAsync());
    }

    // ---------------------------------------------------------------
    // Delete non-204 success (e.g., 200 OK)
    // ---------------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_200_Succeeds()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("{}", HttpStatusCode.OK)));

        // Should not throw
        await client.Config.Management.DeleteAsync(TestData.ConfigId);
    }

    // ---------------------------------------------------------------
    // MapResource default values when fields are null
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetAsync_MapsNullFieldsToDefaults()
    {
        var json = """
        {
            "data": {
                "id": null,
                "type": "config",
                "attributes": {
                    "id": null,
                    "name": null,
                    "description": null,
                    "parent": null,
                    "items": null,
                    "environments": null,
                    "created_at": null,
                    "updated_at": null
                }
            }
        }
        """;

        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(json)));

        var config = await client.Config.Management.GetAsync("some_id");

        Assert.Equal(string.Empty, config.Id);
        Assert.Equal(string.Empty, config.Name);
        Assert.Null(config.Description);
        Assert.Null(config.Parent);
        Assert.NotNull(config.Items);
        Assert.Empty(config.Items);
        Assert.NotNull(config.Environments);
        Assert.Empty(config.Environments);
        Assert.Null(config.CreatedAt);
        Assert.Null(config.UpdatedAt);
    }

    // ---------------------------------------------------------------
    // Accept header
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetAsync_SetsAcceptHeader()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonResponse(SingleConfigJson())));

        await client.Config.Management.GetAsync(TestData.ConfigId);

        Assert.NotNull(handler.LastRequest);
        var accept = handler.LastRequest.Headers.Accept.ToString();
        Assert.Contains("application/vnd.api+json", accept);
    }

    // ---------------------------------------------------------------
    // CancellationToken for other methods
    // ---------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_Create_PassesCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(TestData.SingleConfigJson(), HttpStatusCode.Created)));

        var config = client.Config.Management.New("test_id", "Test");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => config.SaveAsync(cts.Token));
    }

    [Fact]
    public async Task DeleteAsync_PassesCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var (client, _) = CreateClient(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.Config.Management.DeleteAsync(TestData.ConfigId, cts.Token));
    }

    [Fact]
    public async Task ListAsync_PassesCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(TestData.ConfigListJson())));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.Config.Management.ListAsync(cts.Token));
    }

    // ---------------------------------------------------------------
    // SaveAsync (update) with all fields
    // ---------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_Update_WithAllFields_IncludesAllInBody()
    {
        string? putBody = null;
        int requestCount = 0;
        var (client, _) = CreateClient(async req =>
        {
            requestCount++;
            if (requestCount == 1)
                return JsonResponse(SingleConfigJson());
            if (req.Method == HttpMethod.Put)
                putBody = await req.Content!.ReadAsStringAsync();
            return JsonResponse(TestData.SingleConfigJson());
        });

        var config = await client.Config.Management.GetAsync(TestData.ConfigId);
        config.Name = "Updated";
        config.Description = "Updated description";
        config.Parent = "parent-id";
        config.Items = new() { ["a"] = 1 };
        config.Environments = new() { ["prod"] = new Dictionary<string, object?> { ["b"] = 2 } };

        await config.SaveAsync();

        Assert.NotNull(putBody);
        Assert.Contains("Updated", putBody);
        Assert.Contains("Updated description", putBody);
        Assert.Contains("parent-id", putBody);
    }

    // ---------------------------------------------------------------
    // GetAsync with environment data containing JsonElements
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetAsync_WithEnvironmentData_DeserializesCorrectly()
    {
        var json = """
        {
            "data": {
                "id": "my_id",
                "type": "config",
                "attributes": {
                    "id": "my_id",
                    "name": "My Config",
                    "description": null,
                    "parent": null,
                    "items": {"timeout": {"value": 30, "type": "NUMBER"}},
                    "environments": {
                        "production": {"timeout": {"value": 60}},
                        "staging": {"debug": {"value": true}}
                    },
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;

        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(json)));

        var config = await client.Config.Management.GetAsync("my_id");

        Assert.Equal(2, config.Environments.Count);
        Assert.True(config.Environments.ContainsKey("production"));
        Assert.True(config.Environments.ContainsKey("staging"));
    }

    // ---------------------------------------------------------------
    // ListAsync -- verifies all resources are mapped
    // ---------------------------------------------------------------

    [Fact]
    public async Task ListAsync_AllResourcesMapped_ReturnsList()
    {
        var json = """
        {
            "data": [
                {
                    "id": "a",
                    "type": "config",
                    "attributes": {
                        "id": "a",
                        "name": "A",
                        "description": null,
                        "parent": null,
                        "items": {},
                        "environments": {},
                        "created_at": null,
                        "updated_at": null
                    }
                },
                {
                    "id": "b",
                    "type": "config",
                    "attributes": {
                        "id": "b",
                        "name": "B",
                        "description": "desc",
                        "parent": "a",
                        "items": {"x": {"value": 1, "type": "NUMBER"}},
                        "environments": {},
                        "created_at": "2024-01-15T10:30:00Z",
                        "updated_at": "2024-01-15T10:30:00Z"
                    }
                }
            ]
        }
        """;

        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(json)));

        var configs = await client.Config.Management.ListAsync();

        Assert.Equal(2, configs.Count);
        Assert.Equal("a", configs[0].Id);
        Assert.Equal("b", configs[1].Id);
        Assert.Equal("a", configs[1].Parent);
        Assert.Equal("desc", configs[1].Description);
    }

    // ---------------------------------------------------------------
    // Generic HTTP 503 error
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetAsync_503_ThrowsSmplException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Service unavailable"}]}""",
                HttpStatusCode.ServiceUnavailable)));

        var ex = await Assert.ThrowsAsync<SmplException>(
            () => client.Config.Management.GetAsync("some_id"));
        Assert.Equal(503, ex.StatusCode);
    }

    // ---------------------------------------------------------------
    // Delete error paths
    // ---------------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_NotFound_ThrowsSmplNotFoundException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Not found"}]}""",
                HttpStatusCode.NotFound)));

        await Assert.ThrowsAsync<SmplNotFoundException>(
            () => client.Config.Management.DeleteAsync("nonexistent_id"));
    }

    [Fact]
    public async Task DeleteAsync_422_ThrowsSmplValidationException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Invalid"}]}""",
                (HttpStatusCode)422)));

        var ex = await Assert.ThrowsAsync<SmplValidationException>(
            () => client.Config.Management.DeleteAsync(TestData.ConfigId));
        Assert.Equal(422, ex.StatusCode);
    }

    [Fact]
    public async Task DeleteAsync_500_ThrowsSmplException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Server error"}]}""",
                HttpStatusCode.InternalServerError)));

        var ex = await Assert.ThrowsAsync<SmplException>(
            () => client.Config.Management.DeleteAsync(TestData.ConfigId));
        Assert.Equal(500, ex.StatusCode);
    }

    // ---------------------------------------------------------------
    // SaveAsync (update) error paths
    // ---------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_Update_404_ThrowsSmplNotFoundException()
    {
        int requestCount = 0;
        var (client, _) = CreateClient(_ =>
        {
            requestCount++;
            if (requestCount == 1)
                return Task.FromResult(JsonResponse(SingleConfigJson()));
            return Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Not found"}]}""",
                HttpStatusCode.NotFound));
        });

        var config = await client.Config.Management.GetAsync(TestData.ConfigId);
        config.Name = "Updated";

        var ex = await Assert.ThrowsAsync<SmplNotFoundException>(
            () => config.SaveAsync());
        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public async Task SaveAsync_Update_422_ThrowsSmplValidationException()
    {
        int requestCount = 0;
        var (client, _) = CreateClient(_ =>
        {
            requestCount++;
            if (requestCount == 1)
                return Task.FromResult(JsonResponse(SingleConfigJson()));
            return Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Name is required"}]}""",
                (HttpStatusCode)422));
        });

        var config = await client.Config.Management.GetAsync(TestData.ConfigId);
        config.Name = "";

        var ex = await Assert.ThrowsAsync<SmplValidationException>(
            () => config.SaveAsync());
        Assert.Equal(422, ex.StatusCode);
    }

    [Fact]
    public async Task SaveAsync_Update_HttpRequestException_ThrowsSmplConnectionException()
    {
        int requestCount = 0;
        var (client, _) = CreateClient(_ =>
        {
            requestCount++;
            if (requestCount == 1)
                return Task.FromResult(JsonResponse(SingleConfigJson()));
            throw new HttpRequestException("Connection refused");
        });

        var config = await client.Config.Management.GetAsync(TestData.ConfigId);
        await Assert.ThrowsAsync<SmplConnectionException>(
            () => config.SaveAsync());
    }

    [Fact]
    public async Task SaveAsync_Update_TaskCanceledException_ThrowsSmplTimeoutException()
    {
        int requestCount = 0;
        var (client, _) = CreateClient(_ =>
        {
            requestCount++;
            if (requestCount == 1)
                return Task.FromResult(JsonResponse(SingleConfigJson()));
            throw new TaskCanceledException("The request timed out");
        });

        var config = await client.Config.Management.GetAsync(TestData.ConfigId);
        await Assert.ThrowsAsync<SmplTimeoutException>(
            () => config.SaveAsync());
    }

    [Fact]
    public async Task SaveAsync_Update_PassesCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        int requestCount = 0;
        var (client, _) = CreateClient(_ =>
        {
            requestCount++;
            if (requestCount == 1)
                return Task.FromResult(JsonResponse(SingleConfigJson()));
            return Task.FromResult(JsonResponse(TestData.SingleConfigJson()));
        });

        var config = await client.Config.Management.GetAsync(TestData.ConfigId);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => config.SaveAsync(cts.Token));
    }

    [Fact]
    public async Task SaveAsync_Update_NullResponseData_ThrowsSmplValidationException()
    {
        int requestCount = 0;
        var (client, _) = CreateClient(_ =>
        {
            requestCount++;
            if (requestCount == 1)
                return Task.FromResult(JsonResponse(SingleConfigJson()));
            return Task.FromResult(JsonResponse("""{"data": null}"""));
        });

        var config = await client.Config.Management.GetAsync(TestData.ConfigId);
        config.Name = "Updated";

        await Assert.ThrowsAsync<SmplValidationException>(
            () => config.SaveAsync());
    }

    // ---------------------------------------------------------------
    // SaveAsync (create) — 500 generic error
    // ---------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_Create_500_ThrowsSmplException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Server error"}]}""",
                HttpStatusCode.InternalServerError)));

        var config = client.Config.Management.New("test_id", "Test");
        var ex = await Assert.ThrowsAsync<SmplException>(
            () => config.SaveAsync());
        Assert.Equal(500, ex.StatusCode);
    }

    // ---------------------------------------------------------------
    // SaveAsync (update) — 500 generic error
    // ---------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_Update_500_ThrowsSmplException()
    {
        int requestCount = 0;
        var (client, _) = CreateClient(_ =>
        {
            requestCount++;
            if (requestCount == 1)
                return Task.FromResult(JsonResponse(SingleConfigJson()));
            return Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Server error"}]}""",
                HttpStatusCode.InternalServerError));
        });

        var config = await client.Config.Management.GetAsync(TestData.ConfigId);
        config.Name = "Updated";

        var ex = await Assert.ThrowsAsync<SmplException>(
            () => config.SaveAsync());
        Assert.Equal(500, ex.StatusCode);
    }

    // ---------------------------------------------------------------
    // Multiple requests tracked by handler
    // ---------------------------------------------------------------

    [Fact]
    public async Task MultipleRequests_AreTrackedByHandler()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonResponse(SingleConfigJson())));

        await client.Config.Management.GetAsync("id_a");
        await client.Config.Management.GetAsync("id_b");

        Assert.Equal(2, handler.Requests.Count);
    }

    // ---------------------------------------------------------------
    // New() factory method
    // ---------------------------------------------------------------

    [Fact]
    public void New_ReturnsUnsavedConfigWithDefaults()
    {
        var handler = new MockHttpMessageHandler(_ => Task.FromResult(JsonResponse("{}")));
        var httpClient = new HttpClient(handler);
        var client = new SmplClient(TestData.DefaultOptions(), httpClient);

        var config = client.Config.Management.New("my_id");

        Assert.Equal("my_id", config.Id);
        Assert.Null(config.Description);
        Assert.Null(config.Parent);
        Assert.NotNull(config.Items);
        Assert.Empty(config.Items);
        Assert.NotNull(config.Environments);
        Assert.Empty(config.Environments);
        Assert.Null(config.CreatedAt);
        Assert.Null(config.UpdatedAt);
    }

    [Fact]
    public void New_WithAllParams_SetsAllProperties()
    {
        var handler = new MockHttpMessageHandler(_ => Task.FromResult(JsonResponse("{}")));
        var httpClient = new HttpClient(handler);
        var client = new SmplClient(TestData.DefaultOptions(), httpClient);

        var config = client.Config.Management.New("my_id", "My Name", "My Desc", "parent-uuid");

        Assert.Equal("my_id", config.Id);
        Assert.Equal("My Name", config.Name);
        Assert.Equal("My Desc", config.Description);
        Assert.Equal("parent-uuid", config.Parent);
    }

    // ---------------------------------------------------------------
    // SaveAsync (create) — 409 Conflict
    // ---------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_Create_409_ThrowsSmplConflictException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Already exists"}]}""",
                HttpStatusCode.Conflict)));

        var config = client.Config.Management.New("test_id", "Test");
        var ex = await Assert.ThrowsAsync<SmplConflictException>(
            () => config.SaveAsync());
        Assert.Equal(409, ex.StatusCode);
    }

    // ---------------------------------------------------------------
    // SaveAsync (update) — 409 Conflict
    // ---------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_Update_409_ThrowsSmplConflictException()
    {
        int requestCount = 0;
        var (client, _) = CreateClient(_ =>
        {
            requestCount++;
            if (requestCount == 1)
                return Task.FromResult(JsonResponse(SingleConfigJson()));
            return Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Conflict"}]}""",
                HttpStatusCode.Conflict));
        });

        var config = await client.Config.Management.GetAsync(TestData.ConfigId);
        config.Name = "Updated";

        var ex = await Assert.ThrowsAsync<SmplConflictException>(
            () => config.SaveAsync());
        Assert.Equal(409, ex.StatusCode);
    }
}
