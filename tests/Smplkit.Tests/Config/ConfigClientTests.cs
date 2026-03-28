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

    // ---------------------------------------------------------------
    // GetAsync
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetAsync_ById_ReturnsConfig()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonResponse(TestData.SingleConfigJson())));

        var config = await client.Config.GetAsync(TestData.ConfigId);

        Assert.Equal(TestData.ConfigId, config.Id);
        Assert.Equal(TestData.ConfigKey, config.Key);
        Assert.Equal(TestData.ConfigName, config.Name);
        Assert.Equal("Test config", config.Description);
        Assert.Null(config.Parent);
        Assert.NotNull(config.Values);
        Assert.NotNull(config.Environments);

        // Verify correct URL
        Assert.NotNull(handler.LastRequest);
        Assert.Contains($"/api/v1/configs/{TestData.ConfigId}", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, handler.LastRequest.Method);
    }

    [Fact]
    public async Task GetAsync_SetsAuthorizationHeader()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonResponse(TestData.SingleConfigJson())));

        await client.Config.GetAsync(TestData.ConfigId);

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
            Task.FromResult(JsonResponse(TestData.SingleConfigJson())));

        await client.Config.GetAsync(TestData.ConfigId);

        Assert.NotNull(handler.LastRequest);
        var userAgent = handler.LastRequest.Headers.UserAgent.ToString();
        Assert.Contains("smplkit-dotnet-sdk", userAgent);
    }

    // ---------------------------------------------------------------
    // GetByKeyAsync
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetByKeyAsync_ReturnsConfig()
    {
        var listJson = $$"""
        {
            "data": [
                {
                    "id": "{{TestData.ConfigId}}",
                    "type": "config",
                    "attributes": {
                        "key": "{{TestData.ConfigKey}}",
                        "name": "{{TestData.ConfigName}}",
                        "description": "Test config",
                        "parent": null,
                        "values": {},
                        "environments": {},
                        "created_at": "2024-01-15T10:30:00Z",
                        "updated_at": "2024-01-15T10:30:00Z"
                    }
                }
            ]
        }
        """;

        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonResponse(listJson)));

        var config = await client.Config.GetByKeyAsync(TestData.ConfigKey);

        Assert.Equal(TestData.ConfigId, config.Id);
        Assert.Equal(TestData.ConfigKey, config.Key);

        // Verify filter[key] query param
        Assert.NotNull(handler.LastRequest);
        var url = handler.LastRequest.RequestUri!.ToString();
        Assert.Contains("filter[key]=user_service", url);
    }

    [Fact]
    public async Task GetByKeyAsync_WhenNotFound_ThrowsSmplNotFoundException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(TestData.EmptyListJson())));

        await Assert.ThrowsAsync<SmplNotFoundException>(
            () => client.Config.GetByKeyAsync("nonexistent_key"));
    }

    // ---------------------------------------------------------------
    // ListAsync
    // ---------------------------------------------------------------

    [Fact]
    public async Task ListAsync_ReturnsListOfConfigs()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonResponse(TestData.ConfigListJson())));

        var configs = await client.Config.ListAsync();

        Assert.Equal(2, configs.Count);
        Assert.Equal(TestData.ConfigKey, configs[0].Key);
        Assert.Equal("payment_service", configs[1].Key);

        Assert.NotNull(handler.LastRequest);
        Assert.Contains("/api/v1/configs", handler.LastRequest.RequestUri!.ToString());
        Assert.DoesNotContain("filter", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task ListAsync_WhenEmpty_ReturnsEmptyList()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(TestData.EmptyListJson())));

        var configs = await client.Config.ListAsync();

        Assert.Empty(configs);
    }

    // ---------------------------------------------------------------
    // CreateAsync
    // ---------------------------------------------------------------

    [Fact]
    public async Task CreateAsync_SendsCorrectBodyAndReturnsConfig()
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

        var config = await client.Config.CreateAsync(new CreateConfigOptions
        {
            Name = TestData.ConfigName,
            Key = TestData.ConfigKey,
            Description = "Test config",
        });

        Assert.Equal(TestData.ConfigId, config.Id);
        Assert.Equal(TestData.ConfigName, config.Name);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Contains("/api/v1/configs", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task CreateAsync_WithValues_IncludesValuesInBody()
    {
        var (client, handler) = CreateClient(async req =>
        {
            var body = await req.Content!.ReadAsStringAsync();
            Assert.Contains("\"timeout\"", body);
            return JsonResponse(TestData.SingleConfigJson(), HttpStatusCode.Created);
        });

        var config = await client.Config.CreateAsync(new CreateConfigOptions
        {
            Name = "Test",
            Values = new Dictionary<string, object?> { ["timeout"] = 30 },
        });

        Assert.NotNull(config);
    }

    // ---------------------------------------------------------------
    // DeleteAsync
    // ---------------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_SendsDeleteRequest()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));

        await client.Config.DeleteAsync(TestData.ConfigId);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Delete, handler.LastRequest.Method);
        Assert.Contains($"/api/v1/configs/{TestData.ConfigId}", handler.LastRequest.RequestUri!.ToString());
    }

    // ---------------------------------------------------------------
    // Error mapping
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetAsync_404_ThrowsSmplNotFoundException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Not found"}]}""",
                HttpStatusCode.NotFound)));

        var ex = await Assert.ThrowsAsync<SmplNotFoundException>(
            () => client.Config.GetAsync(TestData.ConfigId));
        Assert.Equal(404, ex.StatusCode);
        Assert.NotNull(ex.ResponseBody);
    }

    [Fact]
    public async Task DeleteAsync_409_ThrowsSmplConflictException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Has children"}]}""",
                HttpStatusCode.Conflict)));

        var ex = await Assert.ThrowsAsync<SmplConflictException>(
            () => client.Config.DeleteAsync(TestData.ConfigId));
        Assert.Equal(409, ex.StatusCode);
    }

    [Fact]
    public async Task CreateAsync_422_ThrowsSmplValidationException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Name is required"}]}""",
                (HttpStatusCode)422)));

        var ex = await Assert.ThrowsAsync<SmplValidationException>(
            () => client.Config.CreateAsync(new CreateConfigOptions { Name = "" }));
        Assert.Equal(422, ex.StatusCode);
    }

    [Fact]
    public async Task GetAsync_HttpRequestException_ThrowsSmplConnectionException()
    {
        var (client, _) = CreateClient(_ =>
            throw new HttpRequestException("Connection refused"));

        await Assert.ThrowsAsync<SmplConnectionException>(
            () => client.Config.GetAsync(TestData.ConfigId));
    }

    [Fact]
    public async Task GetAsync_TaskCanceledException_ThrowsSmplTimeoutException()
    {
        var (client, _) = CreateClient(_ =>
            throw new TaskCanceledException("The request timed out"));

        await Assert.ThrowsAsync<SmplTimeoutException>(
            () => client.Config.GetAsync(TestData.ConfigId));
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
            Task.FromResult(JsonResponse(TestData.SingleConfigJson())));

        // Cancelled token should throw OperationCanceledException or TaskCanceledException
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.Config.GetAsync(TestData.ConfigId, cts.Token));
    }

    // ---------------------------------------------------------------
    // Content-Type header
    // ---------------------------------------------------------------

    [Fact]
    public async Task CreateAsync_SetsJsonApiContentType()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonResponse(TestData.SingleConfigJson(), HttpStatusCode.Created)));

        await client.Config.CreateAsync(new CreateConfigOptions { Name = "Test" });

        Assert.NotNull(handler.LastRequest);
        var contentType = handler.LastRequest.Content!.Headers.ContentType!.MediaType;
        Assert.Equal("application/vnd.api+json", contentType);
    }

    // ---------------------------------------------------------------
    // Generic HTTP error (default case in HandleResponseAsync)
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetAsync_500_ThrowsSmplException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Internal server error"}]}""",
                HttpStatusCode.InternalServerError)));

        var ex = await Assert.ThrowsAsync<SmplException>(
            () => client.Config.GetAsync(TestData.ConfigId));
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
            () => client.Config.GetAsync(TestData.ConfigId));
        Assert.Equal(429, ex.StatusCode);
    }

    // ---------------------------------------------------------------
    // Post/Delete error paths (timeout, connection)
    // ---------------------------------------------------------------

    [Fact]
    public async Task CreateAsync_HttpRequestException_ThrowsSmplConnectionException()
    {
        var (client, _) = CreateClient(_ =>
            throw new HttpRequestException("Connection refused"));

        await Assert.ThrowsAsync<SmplConnectionException>(
            () => client.Config.CreateAsync(new CreateConfigOptions { Name = "Test" }));
    }

    [Fact]
    public async Task CreateAsync_TaskCanceledException_ThrowsSmplTimeoutException()
    {
        var (client, _) = CreateClient(_ =>
            throw new TaskCanceledException("The request timed out"));

        await Assert.ThrowsAsync<SmplTimeoutException>(
            () => client.Config.CreateAsync(new CreateConfigOptions { Name = "Test" }));
    }

    [Fact]
    public async Task DeleteAsync_HttpRequestException_ThrowsSmplConnectionException()
    {
        var (client, _) = CreateClient(_ =>
            throw new HttpRequestException("Connection refused"));

        await Assert.ThrowsAsync<SmplConnectionException>(
            () => client.Config.DeleteAsync(TestData.ConfigId));
    }

    [Fact]
    public async Task DeleteAsync_TaskCanceledException_ThrowsSmplTimeoutException()
    {
        var (client, _) = CreateClient(_ =>
            throw new TaskCanceledException("The request timed out"));

        await Assert.ThrowsAsync<SmplTimeoutException>(
            () => client.Config.DeleteAsync(TestData.ConfigId));
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
            () => client.Config.GetAsync(TestData.ConfigId));
    }

    [Fact]
    public async Task GetAsync_NullAttributes_ThrowsSmplNotFoundException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("""{"data": {"id": "abc", "type": "config", "attributes": null}}""")));

        await Assert.ThrowsAsync<SmplNotFoundException>(
            () => client.Config.GetAsync(TestData.ConfigId));
    }

    [Fact]
    public async Task GetByKeyAsync_NullData_ThrowsSmplNotFoundException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("""{"data": null}""")));

        await Assert.ThrowsAsync<SmplNotFoundException>(
            () => client.Config.GetByKeyAsync("some_key"));
    }

    [Fact]
    public async Task ListAsync_NullData_ReturnsEmptyList()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("""{"data": null}""")));

        var configs = await client.Config.ListAsync();

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
                    "id": "def",
                    "type": "config",
                    "attributes": {
                        "key": "valid_config",
                        "name": "Valid",
                        "description": null,
                        "parent": null,
                        "values": {},
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

        var configs = await client.Config.ListAsync();

        Assert.Single(configs);
        Assert.Equal("valid_config", configs[0].Key);
    }

    [Fact]
    public async Task CreateAsync_NullResponseData_ThrowsSmplValidationException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("""{"data": null}""", HttpStatusCode.Created)));

        await Assert.ThrowsAsync<SmplValidationException>(
            () => client.Config.CreateAsync(new CreateConfigOptions { Name = "Test" }));
    }

    // ---------------------------------------------------------------
    // Delete non-204 success (e.g., 200 OK)
    // ---------------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_200_Succeeds()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("{}", HttpStatusCode.OK)));

        // Should not throw — 200 is a success status
        await client.Config.DeleteAsync(TestData.ConfigId);
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
                    "key": null,
                    "name": null,
                    "description": null,
                    "parent": null,
                    "values": null,
                    "environments": null,
                    "created_at": null,
                    "updated_at": null
                }
            }
        }
        """;

        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(json)));

        var config = await client.Config.GetAsync("some-id");

        Assert.Equal(string.Empty, config.Id);
        Assert.Equal(string.Empty, config.Key);
        Assert.Equal(string.Empty, config.Name);
        Assert.Null(config.Description);
        Assert.Null(config.Parent);
        Assert.NotNull(config.Values);
        Assert.Empty(config.Values);
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
            Task.FromResult(JsonResponse(TestData.SingleConfigJson())));

        await client.Config.GetAsync(TestData.ConfigId);

        Assert.NotNull(handler.LastRequest);
        var accept = handler.LastRequest.Headers.Accept.ToString();
        Assert.Contains("application/vnd.api+json", accept);
    }

    // ---------------------------------------------------------------
    // CancellationToken for other methods
    // ---------------------------------------------------------------

    [Fact]
    public async Task CreateAsync_PassesCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(TestData.SingleConfigJson(), HttpStatusCode.Created)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.Config.CreateAsync(new CreateConfigOptions { Name = "Test" }, cts.Token));
    }

    [Fact]
    public async Task DeleteAsync_PassesCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var (client, _) = CreateClient(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.Config.DeleteAsync(TestData.ConfigId, cts.Token));
    }

    [Fact]
    public async Task ListAsync_PassesCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(TestData.ConfigListJson())));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.Config.ListAsync(cts.Token));
    }

    [Fact]
    public async Task GetByKeyAsync_PassesCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(TestData.EmptyListJson())));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.Config.GetByKeyAsync("key", cts.Token));
    }

    // ---------------------------------------------------------------
    // UpdateAsync
    // ---------------------------------------------------------------

    [Fact]
    public async Task UpdateAsync_SendsCorrectBodyAndReturnsConfig()
    {
        var (client, handler) = CreateClient(async req =>
        {
            var body = await req.Content!.ReadAsStringAsync();
            Assert.Contains("\"name\":", body);
            Assert.Contains("\"type\":\"config\"", body);
            return JsonResponse(TestData.SingleConfigJson());
        });

        var config = await client.Config.UpdateAsync(TestData.ConfigId, new CreateConfigOptions
        {
            Name = "Updated Service",
            Key = TestData.ConfigKey,
        });

        Assert.Equal(TestData.ConfigId, config.Id);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Put, handler.LastRequest.Method);
        Assert.Contains($"/api/v1/configs/{TestData.ConfigId}", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task UpdateAsync_SetsJsonApiContentType()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonResponse(TestData.SingleConfigJson())));

        await client.Config.UpdateAsync(TestData.ConfigId, new CreateConfigOptions { Name = "Test" });

        Assert.NotNull(handler.LastRequest);
        var contentType = handler.LastRequest.Content!.Headers.ContentType!.MediaType;
        Assert.Equal("application/vnd.api+json", contentType);
    }

    [Fact]
    public async Task UpdateAsync_404_ThrowsSmplNotFoundException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Not found"}]}""",
                HttpStatusCode.NotFound)));

        var ex = await Assert.ThrowsAsync<SmplNotFoundException>(
            () => client.Config.UpdateAsync("nonexistent", new CreateConfigOptions { Name = "Test" }));
        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public async Task UpdateAsync_422_ThrowsSmplValidationException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Name is required"}]}""",
                (HttpStatusCode)422)));

        var ex = await Assert.ThrowsAsync<SmplValidationException>(
            () => client.Config.UpdateAsync(TestData.ConfigId, new CreateConfigOptions { Name = "" }));
        Assert.Equal(422, ex.StatusCode);
    }

    [Fact]
    public async Task UpdateAsync_HttpRequestException_ThrowsSmplConnectionException()
    {
        var (client, _) = CreateClient(_ =>
            throw new HttpRequestException("Connection refused"));

        await Assert.ThrowsAsync<SmplConnectionException>(
            () => client.Config.UpdateAsync(TestData.ConfigId, new CreateConfigOptions { Name = "Test" }));
    }

    [Fact]
    public async Task UpdateAsync_TaskCanceledException_ThrowsSmplTimeoutException()
    {
        var (client, _) = CreateClient(_ =>
            throw new TaskCanceledException("The request timed out"));

        await Assert.ThrowsAsync<SmplTimeoutException>(
            () => client.Config.UpdateAsync(TestData.ConfigId, new CreateConfigOptions { Name = "Test" }));
    }

    [Fact]
    public async Task UpdateAsync_PassesCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(TestData.SingleConfigJson())));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.Config.UpdateAsync(TestData.ConfigId, new CreateConfigOptions { Name = "Test" }, cts.Token));
    }

    [Fact]
    public async Task UpdateAsync_NullResponseData_ThrowsSmplValidationException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("""{"data": null}""")));

        await Assert.ThrowsAsync<SmplValidationException>(
            () => client.Config.UpdateAsync(TestData.ConfigId, new CreateConfigOptions { Name = "Test" }));
    }

    // ---------------------------------------------------------------
    // UpdateAsync with all fields
    // ---------------------------------------------------------------

    [Fact]
    public async Task UpdateAsync_WithAllFields_IncludesAllInBody()
    {
        string? body = null;
        var (client, _) = CreateClient(async req =>
        {
            if (req.Method == HttpMethod.Put)
            {
                body = await req.Content!.ReadAsStringAsync();
            }
            return JsonResponse(TestData.SingleConfigJson());
        });

        await client.Config.UpdateAsync(TestData.ConfigId, new CreateConfigOptions
        {
            Name = "Updated",
            Key = "updated_key",
            Description = "Updated description",
            Parent = "parent-id",
            Values = new() { ["a"] = 1 },
            Environments = new() { ["prod"] = new Dictionary<string, object?> { ["b"] = 2 } },
        });

        Assert.NotNull(body);
        Assert.Contains("Updated", body);
        Assert.Contains("updated_key", body);
        Assert.Contains("Updated description", body);
        Assert.Contains("parent-id", body);
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
                "id": "cfg-1",
                "type": "config",
                "attributes": {
                    "key": "my_key",
                    "name": "My Config",
                    "description": null,
                    "parent": null,
                    "values": {"timeout": 30},
                    "environments": {
                        "production": {"values": {"timeout": 60}},
                        "staging": {"values": {"debug": true}}
                    },
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;

        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(json)));

        var config = await client.Config.GetAsync("cfg-1");

        Assert.Equal(2, config.Environments.Count);
        Assert.True(config.Environments.ContainsKey("production"));
        Assert.True(config.Environments.ContainsKey("staging"));
    }

    // ---------------------------------------------------------------
    // ListAsync — verifies all resources are mapped
    // ---------------------------------------------------------------

    [Fact]
    public async Task ListAsync_AllResourcesMapped_ReturnsList()
    {
        var json = """
        {
            "data": [
                {
                    "id": "1",
                    "type": "config",
                    "attributes": {
                        "key": "a",
                        "name": "A",
                        "description": null,
                        "parent": null,
                        "values": {},
                        "environments": {},
                        "created_at": null,
                        "updated_at": null
                    }
                },
                {
                    "id": "2",
                    "type": "config",
                    "attributes": {
                        "key": "b",
                        "name": "B",
                        "description": "desc",
                        "parent": "1",
                        "values": {"x": 1},
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

        var configs = await client.Config.ListAsync();

        Assert.Equal(2, configs.Count);
        Assert.Equal("a", configs[0].Key);
        Assert.Equal("b", configs[1].Key);
        Assert.Equal("1", configs[1].Parent);
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
            () => client.Config.GetAsync(TestData.ConfigId));
        Assert.Equal(503, ex.StatusCode);
    }

    // ---------------------------------------------------------------
    // Delete error — already caught SmplException rethrown
    // ---------------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_404_ThrowsSmplNotFoundException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Not found"}]}""",
                HttpStatusCode.NotFound)));

        var ex = await Assert.ThrowsAsync<SmplNotFoundException>(
            () => client.Config.DeleteAsync(TestData.ConfigId));
        Assert.Equal(404, ex.StatusCode);
    }

    // ---------------------------------------------------------------
    // Delete error — 422 Validation
    // ---------------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_422_ThrowsSmplValidationException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Invalid"}]}""",
                (HttpStatusCode)422)));

        var ex = await Assert.ThrowsAsync<SmplValidationException>(
            () => client.Config.DeleteAsync(TestData.ConfigId));
        Assert.Equal(422, ex.StatusCode);
    }

    // ---------------------------------------------------------------
    // Delete error — 500 generic
    // ---------------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_500_ThrowsSmplException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Server error"}]}""",
                HttpStatusCode.InternalServerError)));

        var ex = await Assert.ThrowsAsync<SmplException>(
            () => client.Config.DeleteAsync(TestData.ConfigId));
        Assert.Equal(500, ex.StatusCode);
    }

    // ---------------------------------------------------------------
    // UpdateAsync — 500 generic error
    // ---------------------------------------------------------------

    [Fact]
    public async Task UpdateAsync_500_ThrowsSmplException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Server error"}]}""",
                HttpStatusCode.InternalServerError)));

        var ex = await Assert.ThrowsAsync<SmplException>(
            () => client.Config.UpdateAsync(TestData.ConfigId, new CreateConfigOptions { Name = "Test" }));
        Assert.Equal(500, ex.StatusCode);
    }

    // ---------------------------------------------------------------
    // CreateAsync — 500 generic error
    // ---------------------------------------------------------------

    [Fact]
    public async Task CreateAsync_500_ThrowsSmplException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Server error"}]}""",
                HttpStatusCode.InternalServerError)));

        var ex = await Assert.ThrowsAsync<SmplException>(
            () => client.Config.CreateAsync(new CreateConfigOptions { Name = "Test" }));
        Assert.Equal(500, ex.StatusCode);
    }

    // ---------------------------------------------------------------
    // Multiple requests tracked by handler
    // ---------------------------------------------------------------

    [Fact]
    public async Task MultipleRequests_AreTrackedByHandler()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonResponse(TestData.SingleConfigJson())));

        await client.Config.GetAsync("id-1");
        await client.Config.GetAsync("id-2");

        Assert.Equal(2, handler.Requests.Count);
    }
}
