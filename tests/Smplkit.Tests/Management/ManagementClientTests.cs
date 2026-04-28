using System.Net;
using System.Text;
using System.Text.Json;
using Smplkit.Errors;
using Smplkit.Management;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Management;

public class ManagementClientTests
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

    private static HttpResponseMessage JsonApiResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/vnd.api+json"),
        };

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    // ------------------------------------------------------------------
    // JSON fixtures
    // ------------------------------------------------------------------

    private static string SingleEnvironmentJson(string id = "production", string name = "Production",
        string classification = "STANDARD", string? color = null) =>
        $$"""
        {
            "data": {
                "id": "{{id}}",
                "type": "environment",
                "attributes": {
                    "name": "{{name}}",
                    "color": {{(color is null ? "null" : $"\"{color}\"")}},
                    "classification": "{{classification}}",
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;

    private static string EnvironmentListJson() =>
        """
        {
            "data": [
                {
                    "id": "production",
                    "type": "environment",
                    "attributes": {
                        "name": "Production",
                        "color": null,
                        "classification": "STANDARD",
                        "created_at": "2024-01-15T10:30:00Z",
                        "updated_at": "2024-01-15T10:30:00Z"
                    }
                },
                {
                    "id": "staging",
                    "type": "environment",
                    "attributes": {
                        "name": "Staging",
                        "color": "#ef4444",
                        "classification": "STANDARD",
                        "created_at": "2024-01-15T10:30:00Z",
                        "updated_at": "2024-01-15T10:30:00Z"
                    }
                }
            ]
        }
        """;

    private static string SingleContextTypeJson(string id = "user", string name = "User") =>
        $$$"""
        {
            "data": {
                "id": "{{{id}}}",
                "type": "context_type",
                "attributes": {
                    "id": "{{{id}}}",
                    "name": "{{{name}}}",
                    "attributes": {"plan": {}, "region": {}},
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;

    private static string ContextTypeListJson() =>
        """
        {
            "data": [
                {
                    "id": "user",
                    "type": "context_type",
                    "attributes": {
                        "id": "user",
                        "name": "User",
                        "attributes": {"plan": {}},
                        "created_at": "2024-01-15T10:30:00Z",
                        "updated_at": "2024-01-15T10:30:00Z"
                    }
                }
            ]
        }
        """;

    private static string SingleContextJson(string compositeId = "user:usr-1", string? name = "Alice") =>
        $$"""
        {
            "data": {
                "id": "{{compositeId}}",
                "type": "context",
                "attributes": {
                    "name": {{(name is null ? "null" : $"\"{name}\"")}},
                    "context_type": "user",
                    "attributes": {"plan": "pro"},
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;

    private static string ContextListJson() =>
        """
        {
            "data": [
                {
                    "id": "user:usr-1",
                    "type": "context",
                    "attributes": {
                        "name": "Alice",
                        "context_type": "user",
                        "attributes": {"plan": "pro"},
                        "created_at": "2024-01-15T10:30:00Z",
                        "updated_at": "2024-01-15T10:30:00Z"
                    }
                }
            ]
        }
        """;

    private static string ContextBatchJson() =>
        """{"registered":1}""";

    private static string AccountSettingsJson() =>
        """{"environment_order":["production","staging"]}""";

    // ==================================================================
    // EnvironmentsClient
    // ==================================================================

    [Fact]
    public async Task Environments_ListAsync_ReturnsList()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonApiResponse(EnvironmentListJson())));

        var envs = await client.Management.Environments.ListAsync();

        Assert.Equal(2, envs.Count);
        Assert.Equal("production", envs[0].Id);
        Assert.Equal("Production", envs[0].Name);
        Assert.Equal(EnvironmentClassification.Standard, envs[0].Classification);
        Assert.Equal("staging", envs[1].Id);
        Assert.Equal("#ef4444", envs[1].Color);
    }

    [Fact]
    public async Task Environments_ListAsync_Empty_ReturnsEmptyList()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonApiResponse("""{"data":[]}""")));

        var envs = await client.Management.Environments.ListAsync();

        Assert.Empty(envs);
    }

    [Fact]
    public async Task Environments_GetAsync_ReturnsEnvironment()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonApiResponse(SingleEnvironmentJson())));

        var env = await client.Management.Environments.GetAsync("production");

        Assert.Equal("production", env.Id);
        Assert.Equal("Production", env.Name);
        Assert.Equal(EnvironmentClassification.Standard, env.Classification);
        Assert.NotNull(handler.LastRequest);
        Assert.Contains("environments/production", handler.LastRequest!.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Environments_GetAsync_AdHocClassification_ParsedCorrectly()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonApiResponse(SingleEnvironmentJson("preview", "Preview", "AD_HOC", "#8b5cf6"))));

        var env = await client.Management.Environments.GetAsync("preview");

        Assert.Equal(EnvironmentClassification.AdHoc, env.Classification);
        Assert.Equal("#8b5cf6", env.Color);
    }

    [Fact]
    public async Task Environments_GetAsync_NotFound_ThrowsSmplNotFoundException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"errors":[{"status":"404","detail":"Not found"}]}""",
                    Encoding.UTF8, "application/vnd.api+json"),
            }));

        await Assert.ThrowsAsync<SmplNotFoundException>(
            () => client.Management.Environments.GetAsync("nonexistent"));
    }

    [Fact]
    public async Task Environments_DeleteAsync_CallsDeleteEndpoint()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));

        await client.Management.Environments.DeleteAsync("preview");

        var req = handler.Requests.Last();
        Assert.Equal(HttpMethod.Delete, req.Method);
        Assert.Contains("environments/preview", req.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public void Environments_New_CreatesUnsavedEnvironment()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonApiResponse("""{"data":{}}""")));

        var env = client.Management.Environments.New("preview",
            name: "Preview", color: "#8b5cf6",
            classification: EnvironmentClassification.AdHoc);

        Assert.Equal("preview", env.Id);
        Assert.Equal("Preview", env.Name);
        Assert.Equal("#8b5cf6", env.Color);
        Assert.Equal(EnvironmentClassification.AdHoc, env.Classification);
        Assert.Null(env.CreatedAt);
    }

    [Fact]
    public async Task Environments_New_SaveAsync_PostsToServer()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonApiResponse(SingleEnvironmentJson(), HttpStatusCode.Created)));

        var env = client.Management.Environments.New("production", name: "Production");
        await env.SaveAsync();

        var req = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Contains("environments", req.RequestUri!.AbsoluteUri);
        Assert.NotNull(env.CreatedAt);
    }

    [Fact]
    public async Task Environments_SaveAsync_Update_PutsToServer()
    {
        int callCount = 0;
        var (client, handler) = CreateClient(_ =>
        {
            callCount++;
            return Task.FromResult(JsonApiResponse(
                callCount == 1
                    ? SingleEnvironmentJson(color: "#ef4444")
                    : SingleEnvironmentJson(color: "#00ff00"),
                callCount == 1 ? HttpStatusCode.OK : HttpStatusCode.OK));
        });

        var env = await client.Management.Environments.GetAsync("production");
        env.Color = "#00ff00";
        await env.SaveAsync();

        var putReq = handler.Requests.Last();
        Assert.Equal(HttpMethod.Put, putReq.Method);
        Assert.Contains("environments/production", putReq.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Environments_SaveAsync_Update_AppliesServerResponse()
    {
        int callCount = 0;
        var (client, _) = CreateClient(_ =>
        {
            callCount++;
            return Task.FromResult(JsonApiResponse(
                SingleEnvironmentJson("production", "Production", color: callCount <= 1 ? null : "#ff0000")));
        });

        var env = await client.Management.Environments.GetAsync("production");
        env.Color = "#ff0000";
        await env.SaveAsync();

        Assert.Equal("#ff0000", env.Color);
        Assert.NotNull(env.UpdatedAt);
    }

    // ==================================================================
    // ContextTypesClient
    // ==================================================================

    [Fact]
    public async Task ContextTypes_ListAsync_ReturnsList()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonApiResponse(ContextTypeListJson())));

        var types = await client.Management.ContextTypes.ListAsync();

        Assert.Single(types);
        Assert.Equal("user", types[0].Id);
        Assert.Equal("User", types[0].Name);
        Assert.True(types[0].Attributes.ContainsKey("plan"));
    }

    [Fact]
    public async Task ContextTypes_GetAsync_ReturnsContextType()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonApiResponse(SingleContextTypeJson())));

        var ct = await client.Management.ContextTypes.GetAsync("user");

        Assert.Equal("user", ct.Id);
        Assert.Equal("User", ct.Name);
        Assert.True(ct.Attributes.ContainsKey("plan"));
        Assert.Contains("context_types/user", handler.LastRequest!.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task ContextTypes_GetAsync_NotFound_Throws()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"errors":[{"status":"404","detail":"Not found"}]}""",
                    Encoding.UTF8, "application/vnd.api+json"),
            }));

        await Assert.ThrowsAsync<SmplNotFoundException>(
            () => client.Management.ContextTypes.GetAsync("nonexistent"));
    }

    [Fact]
    public async Task ContextTypes_DeleteAsync_CallsDeleteEndpoint()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));

        await client.Management.ContextTypes.DeleteAsync("user");

        var req = handler.Requests.Last();
        Assert.Equal(HttpMethod.Delete, req.Method);
        Assert.Contains("context_types/user", req.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public void ContextTypes_New_CreatesUnsavedContextType()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonApiResponse("""{"data":{}}""")));

        var ct = client.Management.ContextTypes.New("account", name: "Account");

        Assert.Equal("account", ct.Id);
        Assert.Equal("Account", ct.Name);
        Assert.Empty(ct.Attributes);
        Assert.Null(ct.CreatedAt);
    }

    [Fact]
    public async Task ContextTypes_New_SaveAsync_PostsWithAttributes()
    {
        string? capturedBody = null;
        var (client, handler) = CreateClient(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return JsonApiResponse(SingleContextTypeJson(), HttpStatusCode.Created);
        });

        var ct = client.Management.ContextTypes.New("user", name: "User");
        ct.AddAttribute("plan");
        ct.AddAttribute("region");
        await ct.SaveAsync();

        Assert.Equal(HttpMethod.Post, handler.Requests.Single().Method);
        Assert.NotNull(capturedBody);
        Assert.Contains("plan", capturedBody);
        Assert.Contains("region", capturedBody);
    }

    [Fact]
    public async Task ContextTypes_SaveAsync_Update_PutsToServer()
    {
        int callCount = 0;
        var (client, handler) = CreateClient(_ =>
        {
            callCount++;
            // GET returns 200, PUT also returns 200
            return Task.FromResult(JsonApiResponse(SingleContextTypeJson()));
        });

        var ct = await client.Management.ContextTypes.GetAsync("user");
        ct.AddAttribute("lifetime_value");
        await ct.SaveAsync();

        var putReq = handler.Requests.Last();
        Assert.Equal(HttpMethod.Put, putReq.Method);
        Assert.Contains("context_types/user", putReq.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task ContextTypes_SaveAsync_Update_AppliesServerAttributes()
    {
        int callCount = 0;
        var (client, _) = CreateClient(_ =>
        {
            callCount++;
            return Task.FromResult(JsonApiResponse(SingleContextTypeJson()));
        });

        var ct = await client.Management.ContextTypes.GetAsync("user");
        ct.AddAttribute("lifetime_value");
        await ct.SaveAsync();

        Assert.True(ct.Attributes.ContainsKey("plan"));
        Assert.True(ct.Attributes.ContainsKey("region"));
    }

    [Fact]
    public async Task ContextTypes_New_WithDefaultName_UsesId()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonApiResponse(SingleContextTypeJson("device", "device"), HttpStatusCode.Created)));

        var ct = client.Management.ContextTypes.New("device");
        Assert.Equal("device", ct.Name);

        await ct.SaveAsync();
    }

    // ==================================================================
    // ContextsClient
    // ==================================================================

    [Fact]
    public async Task Contexts_RegisterAsync_Flush_CallsBulkEndpoint()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonApiResponse(ContextBatchJson())));

        await client.Management.Contexts.RegisterAsync(
            new Context("user", "usr-1", new Dictionary<string, object?> { ["plan"] = "pro" }),
            flush: true);

        var req = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Contains("contexts/bulk", req.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Contexts_RegisterAsync_NoFlush_DoesNotCallApi()
    {
        int callCount = 0;
        var (client, _) = CreateClient(_ =>
        {
            callCount++;
            return Task.FromResult(JsonApiResponse("{}"));
        });

        await client.Management.Contexts.RegisterAsync(
            new Context("user", "usr-1"), flush: false);

        Assert.Equal(0, callCount);
    }

    [Fact]
    public async Task Contexts_RegisterAsync_MultipleContexts_SendsAllInBulk()
    {
        string? capturedBody = null;
        var (client, _) = CreateClient(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return JsonApiResponse("""{"registered":3}""");
        });

        await client.Management.Contexts.RegisterAsync(new[]
        {
            new Context("user", "usr-1"),
            new Context("user", "usr-2"),
            new Context("account", "acct-1"),
        }, flush: true);

        Assert.NotNull(capturedBody);
        Assert.Contains("usr-1", capturedBody);
        Assert.Contains("usr-2", capturedBody);
        Assert.Contains("acct-1", capturedBody);
    }

    [Fact]
    public async Task Contexts_FlushAsync_SendsBufferedContexts()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonApiResponse(ContextBatchJson())));

        // Buffer without flushing
        await client.Management.Contexts.RegisterAsync(new Context("user", "usr-1"), flush: false);
        Assert.Empty(handler.Requests);

        // Now flush explicitly
        await client.Management.Contexts.FlushAsync();

        Assert.Single(handler.Requests);
        Assert.Contains("contexts/bulk", handler.Requests[0].RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Contexts_FlushAsync_EmptyBuffer_MakesNoRequest()
    {
        int callCount = 0;
        var (client, _) = CreateClient(_ =>
        {
            callCount++;
            return Task.FromResult(JsonApiResponse("{}"));
        });

        await client.Management.Contexts.FlushAsync();

        Assert.Equal(0, callCount);
    }

    [Fact]
    public async Task Contexts_ListAsync_ReturnsContexts()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonApiResponse(ContextListJson())));

        var contexts = await client.Management.Contexts.ListAsync("user");

        Assert.Single(contexts);
        Assert.Equal("user", contexts[0].Type);
        Assert.Equal("usr-1", contexts[0].Key);
        Assert.Equal("user:usr-1", contexts[0].Id);
        Assert.Equal("Alice", contexts[0].Name);
        Assert.Equal("pro", contexts[0].Attributes["plan"]?.ToString());
        // NSwag URL-encodes "filter[context_type]" → "filter%5Bcontext_type%5D"
        Assert.Contains("filter", handler.LastRequest!.RequestUri!.AbsoluteUri);
        Assert.Contains("context_type", handler.LastRequest.RequestUri!.AbsoluteUri);
        Assert.Contains("user", handler.LastRequest.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Contexts_ListAsync_Empty_ReturnsEmptyList()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonApiResponse("""{"data":[]}""")));

        var contexts = await client.Management.Contexts.ListAsync("device");

        Assert.Empty(contexts);
    }

    [Fact]
    public async Task Contexts_GetAsync_ByCompositeId_ReturnsContext()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonApiResponse(SingleContextJson())));

        var ctx = await client.Management.Contexts.GetAsync("user:usr-1");

        Assert.Equal("user", ctx.Type);
        Assert.Equal("usr-1", ctx.Key);
        Assert.Contains("contexts/", handler.LastRequest!.RequestUri!.AbsoluteUri);
        Assert.Contains("usr-1", handler.LastRequest.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Contexts_GetAsync_ByTypeAndKey_ReturnsContext()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonApiResponse(SingleContextJson())));

        var ctx = await client.Management.Contexts.GetAsync("user", "usr-1");

        Assert.Equal("user", ctx.Type);
        Assert.Equal("usr-1", ctx.Key);
        // Should have called the composite id path
        Assert.Contains("contexts", handler.LastRequest!.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Contexts_GetAsync_NotFound_Throws()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"errors":[{"status":"404","detail":"Not found"}]}""",
                    Encoding.UTF8, "application/vnd.api+json"),
            }));

        await Assert.ThrowsAsync<SmplNotFoundException>(
            () => client.Management.Contexts.GetAsync("user:nonexistent"));
    }

    [Fact]
    public async Task Contexts_DeleteAsync_ByCompositeId_CallsDeleteEndpoint()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));

        await client.Management.Contexts.DeleteAsync("user:usr-1");

        var req = handler.Requests.Last();
        Assert.Equal(HttpMethod.Delete, req.Method);
    }

    [Fact]
    public async Task Contexts_DeleteAsync_ByTypeAndKey_CallsDeleteEndpoint()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));

        await client.Management.Contexts.DeleteAsync("user", "usr-1");

        var req = handler.Requests.Last();
        Assert.Equal(HttpMethod.Delete, req.Method);
    }

    [Fact]
    public async Task Contexts_RegisterAsync_BulkPayload_HasCorrectStructure()
    {
        string? capturedBody = null;
        var (client, _) = CreateClient(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return JsonApiResponse(ContextBatchJson());
        });

        await client.Management.Contexts.RegisterAsync(
            new Context("account", "acct-1", new Dictionary<string, object?> { ["tier"] = "enterprise" }),
            flush: true);

        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        var contexts = doc.RootElement.GetProperty("contexts");
        Assert.Equal(JsonValueKind.Array, contexts.ValueKind);
        Assert.Equal(1, contexts.GetArrayLength());
        var first = contexts[0];
        Assert.Equal("account", first.GetProperty("type").GetString());
        Assert.Equal("acct-1", first.GetProperty("key").GetString());
    }

    // ==================================================================
    // AccountSettingsClient
    // ==================================================================

    [Fact]
    public async Task AccountSettings_GetAsync_ReturnsSettings()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonResponse(AccountSettingsJson())));

        var settings = await client.Management.AccountSettings.GetAsync();

        Assert.Equal(new[] { "production", "staging" }, settings.EnvironmentOrder);
        Assert.Contains("accounts/current/settings", handler.LastRequest!.RequestUri!.AbsoluteUri);
        Assert.Equal(HttpMethod.Get, handler.LastRequest.Method);
    }

    [Fact]
    public async Task AccountSettings_GetAsync_EmptyBody_ReturnsEmpty()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Array.Empty<byte>()),
            }));

        var settings = await client.Management.AccountSettings.GetAsync();

        Assert.Empty(settings.EnvironmentOrder);
        Assert.Empty(settings.Raw);
    }

    [Fact]
    public async Task AccountSettings_SaveAsync_PutsJsonBody()
    {
        string? capturedBody = null;
        var (client, handler) = CreateClient(async req =>
        {
            if (req.Method == HttpMethod.Put)
                capturedBody = await req.Content!.ReadAsStringAsync();
            return JsonResponse(AccountSettingsJson());
        });

        var settings = await client.Management.AccountSettings.GetAsync();
        settings.EnvironmentOrder = new List<string> { "production", "staging", "development" };
        await settings.SaveAsync();

        var putReq = handler.Requests.Last();
        Assert.Equal(HttpMethod.Put, putReq.Method);
        Assert.Contains("accounts/current/settings", putReq.RequestUri!.AbsoluteUri);
        Assert.NotNull(capturedBody);
        Assert.Contains("environment_order", capturedBody);
    }

    [Fact]
    public async Task AccountSettings_SaveAsync_AppliesServerResponse()
    {
        int callCount = 0;
        var (client, _) = CreateClient(_ =>
        {
            callCount++;
            return Task.FromResult(JsonResponse(
                callCount == 1
                    ? AccountSettingsJson()
                    : """{"environment_order":["production","staging","development"]}"""));
        });

        var settings = await client.Management.AccountSettings.GetAsync();
        settings.EnvironmentOrder = new List<string> { "production", "staging", "development" };
        await settings.SaveAsync();

        Assert.Equal(new[] { "production", "staging", "development" }, settings.EnvironmentOrder);
    }

    [Fact]
    public async Task AccountSettings_GetAsync_ServerError_ThrowsSmplException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("""{"errors":[{"detail":"oops"}]}""",
                    Encoding.UTF8, "application/json"),
            }));

        await Assert.ThrowsAsync<SmplException>(
            () => client.Management.AccountSettings.GetAsync());
    }

    [Fact]
    public async Task AccountSettings_SaveAsync_NotFound_ThrowsSmplNotFoundException()
    {
        int callCount = 0;
        var (client, _) = CreateClient(_ =>
        {
            callCount++;
            if (callCount == 1)
                return Task.FromResult(JsonResponse(AccountSettingsJson()));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"errors":[{"status":"404","detail":"Not found"}]}""",
                    Encoding.UTF8, "application/json"),
            });
        });

        var settings = await client.Management.AccountSettings.GetAsync();
        await Assert.ThrowsAsync<SmplNotFoundException>(() => settings.SaveAsync());
    }

    [Fact]
    public async Task AccountSettings_Raw_ContainsAllKeys()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("""{"environment_order":["production"],"some_flag":true}""")));

        var settings = await client.Management.AccountSettings.GetAsync();

        Assert.True(settings.Raw.ContainsKey("environment_order"));
        Assert.True(settings.Raw.ContainsKey("some_flag"));
    }

    // ==================================================================
    // ManagementClient — top-level property access
    // ==================================================================

    [Fact]
    public void ManagementClient_Exposes_AllSubClients()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonApiResponse("{}")));

        Assert.NotNull(client.Management.Environments);
        Assert.NotNull(client.Management.ContextTypes);
        Assert.NotNull(client.Management.Contexts);
        Assert.NotNull(client.Management.AccountSettings);
    }

    // ==================================================================
    // ContextsClient — shared buffer with FlagsClient
    // ==================================================================

    [Fact]
    public async Task Contexts_RegisterAsync_SharedBufferWithFlags_FlushedOnce()
    {
        // Both Flags evaluation and Management.Contexts.Register share the same
        // ContextRegistrationBuffer. When Management.Contexts.FlushAsync drains
        // the buffer, it should flush contexts added via either path.
        string? capturedBody = null;
        var (client, handler) = CreateClient(async req =>
        {
            if (req.RequestUri!.AbsoluteUri.Contains("contexts/bulk"))
            {
                capturedBody = await req.Content!.ReadAsStringAsync();
                return JsonApiResponse(ContextBatchJson());
            }
            return JsonApiResponse("""{"data":{}}""");
        });

        // Register via Management (no flush)
        await client.Management.Contexts.RegisterAsync(new Context("user", "u1"), flush: false);
        await client.Management.Contexts.RegisterAsync(new Context("user", "u2"), flush: false);

        // Flush should drain both
        await client.Management.Contexts.FlushAsync();

        Assert.NotNull(capturedBody);
        Assert.Contains("u1", capturedBody);
        Assert.Contains("u2", capturedBody);
        Assert.Single(handler.Requests.Where(r => r.RequestUri!.AbsoluteUri.Contains("contexts/bulk")).ToList());
    }

    // ==================================================================
    // Coverage gaps — ParseAttributeMap and ParseAttributeDict fallbacks
    // ==================================================================

    [Fact]
    public async Task ContextTypes_GetAsync_AttributesWithMetadata_ParsesInnerProperties()
    {
        // Covers ParseAttributeMap: the inner loop body (line 214) when attribute
        // metadata objects contain sub-properties.
        var jsonWithMeta = """
        {
            "data": {
                "id": "user",
                "type": "context_type",
                "attributes": {
                    "id": "user",
                    "name": "User",
                    "attributes": {"plan": {"label": "Plan", "type": "string"}, "region": {}},
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonApiResponse(jsonWithMeta)));

        var ct = await client.Management.ContextTypes.GetAsync("user");

        Assert.True(ct.Attributes.ContainsKey("plan"));
        Assert.True(ct.Attributes["plan"].ContainsKey("label"));
        Assert.Equal("Plan", ct.Attributes["plan"]["label"]);
    }

    [Fact]
    public async Task ContextTypes_GetAsync_NullAttributes_ReturnsEmpty()
    {
        // Covers ParseAttributeMap: the fallback return when raw is not a JsonElement Object.
        var jsonNullAttrs = """
        {
            "data": {
                "id": "device",
                "type": "context_type",
                "attributes": {
                    "id": "device",
                    "name": "Device",
                    "attributes": null,
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonApiResponse(jsonNullAttrs)));

        var ct = await client.Management.ContextTypes.GetAsync("device");

        Assert.Empty(ct.Attributes);
    }

    [Fact]
    public async Task Contexts_GetAsync_NullContextAttributes_ReturnsEmptyAttrs()
    {
        // Covers ParseAttributeDict: the fallback return when raw is not a JsonElement Object.
        var jsonNullAttrs = """
        {
            "data": {
                "id": "user:usr-1",
                "type": "context",
                "attributes": {
                    "name": "Alice",
                    "context_type": "user",
                    "attributes": null,
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonApiResponse(jsonNullAttrs)));

        var ctx = await client.Management.Contexts.GetAsync("user:usr-1");

        Assert.Empty(ctx.Attributes);
    }
}
