using System.Net;
using System.Text;
using Smplkit.Errors;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Errors;

public class ApiErrorParserTests
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

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/vnd.api+json"),
        };
    }

    // ------------------------------------------------------------------
    // 1. Single-error 400 response
    // ------------------------------------------------------------------

    [Fact]
    public async Task SingleError400_ThrowsSmplValidationException_WithDetails()
    {
        var errorBody = """
        {
            "errors": [
                {
                    "status": "400",
                    "title": "Validation Error",
                    "detail": "The 'name' field is required.",
                    "source": {"pointer": "/data/attributes/name"}
                }
            ]
        }
        """;

        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(errorBody, HttpStatusCode.BadRequest)));

        var ex = await Assert.ThrowsAsync<SmplValidationException>(
            () => client.Config.Management.ListAsync());

        // Message comes from the first error's detail
        Assert.Contains("The 'name' field is required.", ex.Message);

        // Errors collection has 1 element
        Assert.Single(ex.Errors);

        // Error details are parsed
        Assert.Equal("400", ex.Errors[0].Status);
        Assert.Equal("Validation Error", ex.Errors[0].Title);
        Assert.Equal("The 'name' field is required.", ex.Errors[0].Detail);
        Assert.NotNull(ex.Errors[0].Source);
        Assert.Equal("/data/attributes/name", ex.Errors[0].Source!.Pointer);

        // StatusCode is 400
        Assert.Equal(400, ex.StatusCode);

        // ToString includes JSON
        var str = ex.ToString();
        Assert.Contains("\"status\"", str);
        Assert.Contains("\"detail\"", str);
        Assert.Contains("/data/attributes/name", str);
    }

    // ------------------------------------------------------------------
    // 2. Multi-error 400 response
    // ------------------------------------------------------------------

    [Fact]
    public async Task MultiError400_MessageIncludesCount_ErrorsHas2Elements()
    {
        var errorBody = """
        {
            "errors": [
                {
                    "status": "400",
                    "title": "Validation Error",
                    "detail": "The 'name' field is required.",
                    "source": {"pointer": "/data/attributes/name"}
                },
                {
                    "status": "400",
                    "title": "Validation Error",
                    "detail": "The 'id' field is required.",
                    "source": {"pointer": "/data/id"}
                }
            ]
        }
        """;

        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(errorBody, HttpStatusCode.BadRequest)));

        var ex = await Assert.ThrowsAsync<SmplValidationException>(
            () => client.Config.Management.ListAsync());

        // Message includes count suffix
        Assert.Contains("(and 1 more error)", ex.Message);
        Assert.Contains("The 'name' field is required.", ex.Message);

        // Errors has 2 elements
        Assert.Equal(2, ex.Errors.Count);
        Assert.Equal("The 'name' field is required.", ex.Errors[0].Detail);
        Assert.Equal("The 'id' field is required.", ex.Errors[1].Detail);

        // ToString shows both errors with indices
        var str = ex.ToString();
        Assert.Contains("[0]", str);
        Assert.Contains("[1]", str);
        Assert.Contains("/data/id", str);
    }

    // ------------------------------------------------------------------
    // 3. 404 response
    // ------------------------------------------------------------------

    [Fact]
    public async Task NotFound404_ThrowsSmplNotFoundException_WithServerDetail()
    {
        var errorBody = """
        {
            "errors": [
                {
                    "status": "404",
                    "title": "Not Found",
                    "detail": "Config with id '123' does not exist."
                }
            ]
        }
        """;

        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(errorBody, HttpStatusCode.NotFound)));

        var ex = await Assert.ThrowsAsync<SmplNotFoundException>(
            () => client.Config.Management.GetAsync("00000123-0000-0000-0000-000000000000"));

        Assert.Contains("Config with id '123' does not exist.", ex.Message);
        Assert.Equal(404, ex.StatusCode);
        Assert.Single(ex.Errors);
        Assert.Equal("Not Found", ex.Errors[0].Title);
    }

    // ------------------------------------------------------------------
    // 4. 409 response
    // ------------------------------------------------------------------

    [Fact]
    public async Task Conflict409_ThrowsSmplConflictException_WithServerDetail()
    {
        var errorBody = """
        {
            "errors": [
                {
                    "status": "409",
                    "title": "Conflict",
                    "detail": "Cannot delete config with children."
                }
            ]
        }
        """;

        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(errorBody, HttpStatusCode.Conflict)));

        var ex = await Assert.ThrowsAsync<SmplConflictException>(
            () => client.Config.Management.DeleteAsync("50000000-5000-5000-5000-500000000000"));

        Assert.Contains("Cannot delete config with children.", ex.Message);
        Assert.Equal(409, ex.StatusCode);
        Assert.Single(ex.Errors);
        Assert.Equal("Conflict", ex.Errors[0].Title);
    }

    // ------------------------------------------------------------------
    // 5. Non-JSON 502 response
    // ------------------------------------------------------------------

    [Fact]
    public async Task NonJson502_ThrowsSmplException_WithHttpStatusInMessage()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("<html>Bad Gateway</html>", Encoding.UTF8, "text/html"),
            }));

        var ex = await Assert.ThrowsAsync<SmplException>(
            () => client.Config.Management.ListAsync());

        // Ensure it's the base type, not a subclass
        Assert.Equal(typeof(SmplException), ex.GetType());

        // Message contains HTTP status
        Assert.Contains("HTTP 502", ex.Message);

        // Errors is empty
        Assert.Empty(ex.Errors);

        // StatusCode is 502
        Assert.Equal(502, ex.StatusCode);
    }

    // ------------------------------------------------------------------
    // Additional edge cases
    // ------------------------------------------------------------------

    [Fact]
    public async Task Error422_ThrowsSmplValidationException()
    {
        var errorBody = """
        {
            "errors": [
                {
                    "status": "422",
                    "title": "Unprocessable Entity",
                    "detail": "Invalid JSON structure."
                }
            ]
        }
        """;

        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(errorBody, HttpStatusCode.UnprocessableEntity)));

        var ex = await Assert.ThrowsAsync<SmplValidationException>(
            () => client.Config.Management.ListAsync());

        Assert.Contains("Invalid JSON structure.", ex.Message);
        Assert.Equal(422, ex.StatusCode);
    }

    [Fact]
    public async Task ErrorWithNoDetail_FallsBackToTitle()
    {
        var errorBody = """
        {
            "errors": [
                {
                    "status": "400",
                    "title": "Bad Request"
                }
            ]
        }
        """;

        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(errorBody, HttpStatusCode.BadRequest)));

        var ex = await Assert.ThrowsAsync<SmplValidationException>(
            () => client.Config.Management.ListAsync());

        Assert.Equal("Bad Request", ex.Message);
        Assert.Null(ex.Errors[0].Detail);
        Assert.Null(ex.Errors[0].Source);
    }

    [Fact]
    public async Task ErrorWithOnlyStatus_FallsBackToStatus()
    {
        var errorBody = """
        {
            "errors": [
                {
                    "status": "400"
                }
            ]
        }
        """;

        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(errorBody, HttpStatusCode.BadRequest)));

        var ex = await Assert.ThrowsAsync<SmplValidationException>(
            () => client.Config.Management.ListAsync());

        Assert.Equal("400", ex.Message);
    }

    [Fact]
    public async Task EmptyErrorsArray_FallsBackToHttpStatus()
    {
        var errorBody = """
        {
            "errors": []
        }
        """;

        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(errorBody, HttpStatusCode.BadRequest)));

        var ex = await Assert.ThrowsAsync<SmplValidationException>(
            () => client.Config.Management.ListAsync());

        Assert.Contains("HTTP 400", ex.Message);
        Assert.Empty(ex.Errors);
    }

    [Fact]
    public async Task MalformedJson_FallsBackToHttpStatus()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("{invalid json", Encoding.UTF8, "application/json"),
            }));

        var ex = await Assert.ThrowsAsync<SmplException>(
            () => client.Config.Management.ListAsync());

        Assert.Contains("HTTP 500", ex.Message);
        Assert.Empty(ex.Errors);
    }

    [Fact]
    public void MultipleErrors_MessageShowsPlural()
    {
        var errors = new List<ApiErrorDetail>
        {
            new("400", "Error 1", "Detail 1", null),
            new("400", "Error 2", "Detail 2", null),
            new("400", "Error 3", "Detail 3", null),
        };

        var message = SmplException.DeriveMessage(errors, 400);
        Assert.Contains("(and 2 more errors)", message);
    }

    [Fact]
    public void ApiErrorDetail_ToJsonString_IncludesAllFields()
    {
        var detail = new ApiErrorDetail("400", "Validation Error", "Field required.", new ApiErrorSource("/data/id"));
        var json = detail.ToJsonString();

        Assert.Contains("\"status\"", json);
        Assert.Contains("\"400\"", json);
        Assert.Contains("\"title\"", json);
        Assert.Contains("\"detail\"", json);
        Assert.Contains("\"source\"", json);
        Assert.Contains("\"pointer\"", json);
        Assert.Contains("/data/id", json);
    }

    [Fact]
    public void ApiErrorDetail_ToJsonString_OmitsNullFields()
    {
        var detail = new ApiErrorDetail(null, null, "Something failed.", null);
        var json = detail.ToJsonString();

        Assert.DoesNotContain("\"status\"", json);
        Assert.DoesNotContain("\"title\"", json);
        Assert.DoesNotContain("\"source\"", json);
        Assert.Contains("\"detail\"", json);
    }

    [Fact]
    public void SmplException_WithErrors_ToStringShowsSingleError()
    {
        var errors = new List<ApiErrorDetail>
        {
            new("400", "Validation Error", "The 'name' field is required.", new ApiErrorSource("/data/attributes/name")),
        };

        var ex = new SmplValidationException(
            SmplException.DeriveMessage(errors, 400),
            responseBody: "body",
            statusCode: 400,
            errors: errors);

        var str = ex.ToString();
        Assert.Contains("SmplValidationException: The 'name' field is required.", str);
        Assert.Contains("Error: {", str);
        Assert.Contains("/data/attributes/name", str);
    }

    [Fact]
    public void SmplException_WithErrors_ToStringShowsMultipleErrors()
    {
        var errors = new List<ApiErrorDetail>
        {
            new("400", "Validation Error", "The 'name' field is required.", new ApiErrorSource("/data/attributes/name")),
            new("400", "Validation Error", "The 'id' field is required.", new ApiErrorSource("/data/id")),
        };

        var ex = new SmplValidationException(
            SmplException.DeriveMessage(errors, 400),
            responseBody: "body",
            statusCode: 400,
            errors: errors);

        var str = ex.ToString();
        Assert.Contains("SmplValidationException: The 'name' field is required. (and 1 more error)", str);
        Assert.Contains("Errors:", str);
        Assert.Contains("[0]", str);
        Assert.Contains("[1]", str);
        Assert.Contains("/data/id", str);
    }

    [Fact]
    public void SmplException_WithoutErrors_ToStringIsDefault()
    {
        var ex = new SmplException("simple error");
        var str = ex.ToString();

        // Should be the normal Exception.ToString() format
        Assert.Contains("SmplException: simple error", str);
        Assert.DoesNotContain("Error:", str);
        Assert.DoesNotContain("Errors:", str);
    }

    [Fact]
    public void SmplException_EmptyErrors_DefaultsToEmptyCollection()
    {
        var ex = new SmplException("test");
        Assert.NotNull(ex.Errors);
        Assert.Empty(ex.Errors);
    }

    [Fact]
    public async Task ErrorsPropertyNotArray_FallsBackToHttpStatus()
    {
        var errorBody = """{"errors": "not-an-array"}""";

        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(errorBody, HttpStatusCode.BadRequest)));

        var ex = await Assert.ThrowsAsync<SmplValidationException>(
            () => client.Config.Management.ListAsync());

        Assert.Contains("HTTP 400", ex.Message);
        Assert.Empty(ex.Errors);
    }
}
