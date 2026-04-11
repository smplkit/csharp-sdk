using System.Net;
using System.Text;
using Smplkit.Config;
using Smplkit.Errors;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Config;

/// <summary>
/// Tests for direct Items/Environments mutation + SaveAsync pattern,
/// which replaces the old SetValuesAsync/SetValueAsync API.
/// </summary>
public class ConfigClientSetValuesTests
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

    private static string SingleConfigJson(
        string id = "my_id",
        string name = "My Config",
        string? parent = null,
        string valuesJson = "{}",
        string environmentsJson = "{}")
    {
        var parentStr = parent is null ? "null" : $"\"{parent}\"";
        return $$"""
        {
            "data": {
                "id": "{{id}}",
                "type": "config",
                "attributes": {
                    "id": "{{id}}",
                    "name": "{{name}}",
                    "description": null,
                    "parent": {{parentStr}},
                    "items": {{valuesJson}},
                    "environments": {{environmentsJson}},
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;
    }

    // ------------------------------------------------------------------
    // Items mutation — add new key, then SaveAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task Items_AddNewKey_SaveAsync_IncludesInPutBody()
    {
        string? putBody = null;
        int requestCount = 0;

        var (client, handler) = CreateClient(async req =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                return JsonResponse(SingleConfigJson(
                    valuesJson: """{"timeout": {"value": 30, "type": "NUMBER"}, "retries": {"value": 3, "type": "NUMBER"}}""",
                    environmentsJson: """{"production": {"timeout": {"value": 60}}}"""));
            }
            if (req.Method == HttpMethod.Put)
            {
                putBody = await req.Content!.ReadAsStringAsync();
                return JsonResponse(SingleConfigJson(
                    valuesJson: """{"new_key": {"value": "new_value", "type": "STRING"}}"""));
            }
            return JsonResponse("{}", HttpStatusCode.InternalServerError);
        });

        var config = await client.Config.GetAsync("my_id");
        config.Items["new_key"] = "new_value";
        await config.SaveAsync();

        Assert.Equal(2, requestCount);
        Assert.NotNull(putBody);
        Assert.Contains("new_key", putBody);
        Assert.Contains("new_value", putBody);
    }

    [Fact]
    public async Task Items_AddNewKey_SaveAsync_PreservesEnvironments()
    {
        string? putBody = null;
        int requestCount = 0;

        var (client, _) = CreateClient(async req =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                return JsonResponse(SingleConfigJson(
                    environmentsJson: """{"staging": {"debug": {"value": true}}}"""));
            }
            if (req.Method == HttpMethod.Put)
            {
                putBody = await req.Content!.ReadAsStringAsync();
                return JsonResponse(SingleConfigJson());
            }
            return JsonResponse("{}", HttpStatusCode.InternalServerError);
        });

        var config = await client.Config.GetAsync("my_id");
        config.Items["key"] = "val";
        await config.SaveAsync();

        Assert.NotNull(putBody);
        Assert.Contains("staging", putBody);
    }

    // ------------------------------------------------------------------
    // Environments mutation — set env-specific values, then SaveAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task Environments_SetValues_SaveAsync_IncludesInPutBody()
    {
        string? putBody = null;
        int requestCount = 0;

        var (client, _) = CreateClient(async req =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                return JsonResponse(SingleConfigJson(
                    valuesJson: """{"timeout": {"value": 30, "type": "NUMBER"}}""",
                    environmentsJson: """{"production": {"timeout": {"value": 60}}}"""));
            }
            if (req.Method == HttpMethod.Put)
            {
                putBody = await req.Content!.ReadAsStringAsync();
                return JsonResponse(SingleConfigJson());
            }
            return JsonResponse("{}", HttpStatusCode.InternalServerError);
        });

        var config = await client.Config.GetAsync("my_id");
        config.Environments["production"] = new Dictionary<string, object?> { ["timeout"] = 120 };
        await config.SaveAsync();

        Assert.NotNull(putBody);
        Assert.Contains("production", putBody);
    }

    [Fact]
    public async Task Environments_AddNewEnvironment_SaveAsync()
    {
        string? putBody = null;
        int requestCount = 0;

        var (client, _) = CreateClient(async req =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                return JsonResponse(SingleConfigJson(
                    valuesJson: """{"timeout": {"value": 30, "type": "NUMBER"}}"""));
            }
            if (req.Method == HttpMethod.Put)
            {
                putBody = await req.Content!.ReadAsStringAsync();
                return JsonResponse(SingleConfigJson());
            }
            return JsonResponse("{}", HttpStatusCode.InternalServerError);
        });

        var config = await client.Config.GetAsync("my_id");
        config.Environments["staging"] = new Dictionary<string, object?> { ["timeout"] = 90 };
        await config.SaveAsync();

        Assert.NotNull(putBody);
        Assert.Contains("staging", putBody);
    }

    // ------------------------------------------------------------------
    // Items mutation — overwrite existing key
    // ------------------------------------------------------------------

    [Fact]
    public async Task Items_OverwriteExistingKey_SaveAsync()
    {
        string? putBody = null;
        int requestCount = 0;

        var (client, _) = CreateClient(async req =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                return JsonResponse(SingleConfigJson(
                    valuesJson: """{"timeout": {"value": 30, "type": "NUMBER"}}"""));
            }
            if (req.Method == HttpMethod.Put)
            {
                putBody = await req.Content!.ReadAsStringAsync();
                return JsonResponse(SingleConfigJson(
                    valuesJson: """{"timeout": {"value": 60, "type": "NUMBER"}}"""));
            }
            return JsonResponse("{}", HttpStatusCode.InternalServerError);
        });

        var config = await client.Config.GetAsync("my_id");
        config.Items["timeout"] = 60;
        await config.SaveAsync();

        Assert.NotNull(putBody);
    }

    // ------------------------------------------------------------------
    // Items mutation — set to null
    // ------------------------------------------------------------------

    [Fact]
    public async Task Items_SetNull_SaveAsync()
    {
        string? putBody = null;
        int requestCount = 0;

        var (client, _) = CreateClient(async req =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                return JsonResponse(SingleConfigJson(
                    valuesJson: """{"timeout": {"value": 30, "type": "NUMBER"}}"""));
            }
            if (req.Method == HttpMethod.Put)
            {
                putBody = await req.Content!.ReadAsStringAsync();
                return JsonResponse(SingleConfigJson());
            }
            return JsonResponse("{}", HttpStatusCode.InternalServerError);
        });

        var config = await client.Config.GetAsync("my_id");
        config.Items["timeout"] = null;
        await config.SaveAsync();

        Assert.NotNull(putBody);
    }

    // ------------------------------------------------------------------
    // Items mutation with merging existing values
    // ------------------------------------------------------------------

    [Fact]
    public async Task Items_AddKey_PreservesExistingKeys()
    {
        string? putBody = null;
        int requestCount = 0;

        var (client, _) = CreateClient(async req =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                return JsonResponse(SingleConfigJson(
                    valuesJson: """{"timeout": {"value": 30, "type": "NUMBER"}, "retries": {"value": 3, "type": "NUMBER"}}"""));
            }
            if (req.Method == HttpMethod.Put)
            {
                putBody = await req.Content!.ReadAsStringAsync();
                return JsonResponse(SingleConfigJson(
                    valuesJson: """{"timeout": {"value": 30, "type": "NUMBER"}, "retries": {"value": 3, "type": "NUMBER"}, "debug": {"value": true, "type": "BOOLEAN"}}"""));
            }
            return JsonResponse("{}", HttpStatusCode.InternalServerError);
        });

        var config = await client.Config.GetAsync("my_id");
        config.Items["debug"] = true;
        await config.SaveAsync();

        Assert.NotNull(putBody);
        // Should contain old and new keys
        Assert.Contains("timeout", putBody);
        Assert.Contains("retries", putBody);
        Assert.Contains("debug", putBody);
    }

    // ------------------------------------------------------------------
    // Environments mutation — preserve other environments
    // ------------------------------------------------------------------

    [Fact]
    public async Task Environments_ModifyOne_PreservesOtherEnvs()
    {
        string? putBody = null;
        int requestCount = 0;

        var (client, _) = CreateClient(async req =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                return JsonResponse(SingleConfigJson(
                    valuesJson: """{"timeout": {"value": 30, "type": "NUMBER"}}""",
                    environmentsJson: """{"production": {"timeout": {"value": 60}}, "staging": {"debug": {"value": true}}}"""));
            }
            if (req.Method == HttpMethod.Put)
            {
                putBody = await req.Content!.ReadAsStringAsync();
                return JsonResponse(SingleConfigJson());
            }
            return JsonResponse("{}", HttpStatusCode.InternalServerError);
        });

        var config = await client.Config.GetAsync("my_id");
        config.Environments["production"] = new Dictionary<string, object?> { ["timeout"] = 120 };
        await config.SaveAsync();

        Assert.NotNull(putBody);
        Assert.Contains("production", putBody);
        Assert.Contains("staging", putBody);
    }

    // ------------------------------------------------------------------
    // Config with parent — preserves parent in update
    // ------------------------------------------------------------------

    [Fact]
    public async Task Items_Mutation_PreservesParentInUpdate()
    {
        string? putBody = null;
        int requestCount = 0;

        var (client, _) = CreateClient(async req =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                return JsonResponse(SingleConfigJson(
                    parent: "parent-uuid",
                    valuesJson: """{"timeout": {"value": 30, "type": "NUMBER"}}"""));
            }
            if (req.Method == HttpMethod.Put)
            {
                putBody = await req.Content!.ReadAsStringAsync();
                return JsonResponse(SingleConfigJson());
            }
            return JsonResponse("{}", HttpStatusCode.InternalServerError);
        });

        var config = await client.Config.GetAsync("my_id");
        config.Items["new_key"] = "val";
        await config.SaveAsync();

        Assert.NotNull(putBody);
        Assert.Contains("parent-uuid", putBody);
    }

    // ------------------------------------------------------------------
    // Config with description and parent — preserves metadata
    // ------------------------------------------------------------------

    [Fact]
    public async Task Items_Mutation_PreservesDescriptionAndParent()
    {
        string? putBody = null;
        int requestCount = 0;

        var (client, _) = CreateClient(async req =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                var getJson = """
                {
                    "data": {
                        "id": "my_id",
                        "type": "config",
                        "attributes": {
                            "id": "my_id",
                            "name": "My Config",
                            "description": "My config desc",
                            "parent": "parent-uuid",
                            "items": {"timeout": {"value": 30, "type": "NUMBER"}},
                            "environments": {},
                            "created_at": "2024-01-15T10:30:00Z",
                            "updated_at": "2024-01-15T10:30:00Z"
                        }
                    }
                }
                """;
                return JsonResponse(getJson);
            }
            if (req.Method == HttpMethod.Put)
            {
                putBody = await req.Content!.ReadAsStringAsync();
                return JsonResponse(SingleConfigJson());
            }
            return JsonResponse("{}", HttpStatusCode.InternalServerError);
        });

        var config = await client.Config.GetAsync("my_id");
        config.Items["debug"] = true;
        await config.SaveAsync();

        Assert.NotNull(putBody);
        Assert.Contains("My config desc", putBody);
        Assert.Contains("parent-uuid", putBody);
    }

    // ------------------------------------------------------------------
    // SaveAsync (create) preserves metadata
    // ------------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_Create_PreservesConfigMetadata()
    {
        string? postBody = null;

        var (client, _) = CreateClient(async req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                postBody = await req.Content!.ReadAsStringAsync();
                return JsonResponse(SingleConfigJson(), HttpStatusCode.Created);
            }
            return JsonResponse("{}", HttpStatusCode.InternalServerError);
        });

        var config = client.Config.New("svc_id", "Service Name");
        config.Items["new"] = "val";
        await config.SaveAsync();

        Assert.NotNull(postBody);
        Assert.Contains("Service Name", postBody);
        Assert.Contains("svc_id", postBody);
    }

    // ------------------------------------------------------------------
    // SaveAsync (create) with all fields
    // ------------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_Create_WithAllFields_IncludesAllInBody()
    {
        string? postBody = null;

        var (client, _) = CreateClient(async req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                postBody = await req.Content!.ReadAsStringAsync();
                return JsonResponse(SingleConfigJson(), HttpStatusCode.Created);
            }
            return JsonResponse("{}", HttpStatusCode.InternalServerError);
        });

        var config = client.Config.New("full_id", "Full Config", "A full description", "parent-uuid");
        config.Items = new() { ["a"] = 1 };
        config.Environments = new() { ["prod"] = new Dictionary<string, object?> { ["b"] = 2 } };
        await config.SaveAsync();

        Assert.NotNull(postBody);
        Assert.Contains("Full Config", postBody);
        Assert.Contains("full_id", postBody);
        Assert.Contains("A full description", postBody);
        Assert.Contains("parent-uuid", postBody);
    }

    // ------------------------------------------------------------------
    // Items mutation — empty environments preserved
    // ------------------------------------------------------------------

    [Fact]
    public async Task Items_Mutation_EmptyEnvironments_Preserved()
    {
        string? putBody = null;
        int requestCount = 0;

        var (client, _) = CreateClient(async req =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                return JsonResponse(SingleConfigJson(
                    valuesJson: """{"old": {"value": "val", "type": "STRING"}}""",
                    environmentsJson: """{}"""));
            }
            if (req.Method == HttpMethod.Put)
            {
                putBody = await req.Content!.ReadAsStringAsync();
                return JsonResponse(SingleConfigJson());
            }
            return JsonResponse("{}", HttpStatusCode.InternalServerError);
        });

        var config = await client.Config.GetAsync("my_id");
        config.Items["new"] = "val";
        await config.SaveAsync();

        Assert.NotNull(putBody);
        Assert.Contains("new", putBody);
    }

    // ------------------------------------------------------------------
    // SaveAsync (update) — error during PUT
    // ------------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_Update_PutFails_ThrowsSmplException()
    {
        int requestCount = 0;
        var (client, _) = CreateClient(_ =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                return Task.FromResult(JsonResponse(SingleConfigJson(
                    valuesJson: """{"timeout": {"value": 30, "type": "NUMBER"}}""")));
            }
            return Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Validation failed"}]}""",
                (HttpStatusCode)422));
        });

        var config = await client.Config.GetAsync("my_id");
        config.Items["key"] = "val";

        await Assert.ThrowsAsync<SmplValidationException>(
            () => config.SaveAsync());
    }

    // ------------------------------------------------------------------
    // Special characters in id for GetAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetAsync_SpecialCharacters_AreUrlEncoded()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Not found"}]}""",
                HttpStatusCode.NotFound)));

        // This will throw NotFound, but we want to check the URL encoding
        try
        {
            await client.Config.GetAsync("my id&value=test");
        }
        catch (SmplNotFoundException) { }

        Assert.NotNull(handler.LastRequest);
        var url = handler.LastRequest.RequestUri!.AbsoluteUri;
        // Space should be encoded, & should be encoded
        Assert.DoesNotContain(" ", url);
    }

    // ------------------------------------------------------------------
    // Items — replace entire dict, then SaveAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task Items_ReplaceEntireDict_SaveAsync()
    {
        string? putBody = null;
        int requestCount = 0;

        var (client, _) = CreateClient(async req =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                return JsonResponse(SingleConfigJson(
                    valuesJson: """{"timeout": {"value": 30, "type": "NUMBER"}, "retries": {"value": 3, "type": "NUMBER"}}"""));
            }
            if (req.Method == HttpMethod.Put)
            {
                putBody = await req.Content!.ReadAsStringAsync();
                return JsonResponse(SingleConfigJson());
            }
            return JsonResponse("{}", HttpStatusCode.InternalServerError);
        });

        var config = await client.Config.GetAsync("my_id");
        // Replace entire items dict
        config.Items = new Dictionary<string, object?> { ["new_only"] = "value" };
        await config.SaveAsync();

        Assert.NotNull(putBody);
        Assert.Contains("new_only", putBody);
    }

    // ------------------------------------------------------------------
    // Environments — replace entire dict, then SaveAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task Environments_ReplaceEntireDict_SaveAsync()
    {
        string? putBody = null;
        int requestCount = 0;

        var (client, _) = CreateClient(async req =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                return JsonResponse(SingleConfigJson(
                    environmentsJson: """{"production": {"timeout": {"value": 60}}}"""));
            }
            if (req.Method == HttpMethod.Put)
            {
                putBody = await req.Content!.ReadAsStringAsync();
                return JsonResponse(SingleConfigJson());
            }
            return JsonResponse("{}", HttpStatusCode.InternalServerError);
        });

        var config = await client.Config.GetAsync("my_id");
        config.Environments = new Dictionary<string, Dictionary<string, object?>>
        {
            ["development"] = new() { ["debug"] = true },
        };
        await config.SaveAsync();

        Assert.NotNull(putBody);
        Assert.Contains("development", putBody);
        Assert.Contains("debug", putBody);
    }
}
