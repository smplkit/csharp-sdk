using System.Net;
using System.Text;
using System.Text.Json;
using Smplkit.Errors;
using Smplkit.Internal;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Internal;

/// <summary>
/// Additional Transport tests for 100% code coverage.
/// Targets specific error mapping paths and edge cases.
/// </summary>
public class TransportCoverageTests
{
    private static Transport CreateTransport(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handlerFn)
    {
        var handler = new MockHttpMessageHandler(handlerFn);
        var httpClient = new HttpClient(handler);
        var options = new SmplClientOptions
        {
            ApiKey = "sk_test_key",
            Timeout = TimeSpan.FromSeconds(30),
        };
        return new Transport(httpClient, options);
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/vnd.api+json"),
        };
    }

    // ------------------------------------------------------------------
    // HandleResponseAsync — 200 returns body directly
    // ------------------------------------------------------------------

    [Fact]
    public async Task HandleResponse_200_ReturnsBody()
    {
        var transport = CreateTransport(_ =>
            Task.FromResult(JsonResponse("""{"status": "ok"}""")));

        var result = await transport.GetAsync("https://example.com/api");
        Assert.Contains("ok", result);
    }

    // ------------------------------------------------------------------
    // HandleResponseAsync — 201 returns body
    // ------------------------------------------------------------------

    [Fact]
    public async Task HandleResponse_201_ReturnsBody()
    {
        var transport = CreateTransport(_ =>
            Task.FromResult(JsonResponse("""{"created": true}""", HttpStatusCode.Created)));

        var result = await transport.PostAsync("https://example.com/api", new { name = "test" });
        Assert.Contains("created", result);
    }

    // ------------------------------------------------------------------
    // PostAsync — 409 (Conflict) error path
    // ------------------------------------------------------------------

    [Fact]
    public async Task PostAsync_409_ThrowsConflictException()
    {
        var transport = CreateTransport(_ =>
            Task.FromResult(JsonResponse("conflict body", HttpStatusCode.Conflict)));

        var ex = await Assert.ThrowsAsync<SmplConflictException>(
            () => transport.PostAsync("https://example.com/api", new { }));
        Assert.Equal(409, ex.StatusCode);
        Assert.Equal("conflict body", ex.ResponseBody);
    }

    // ------------------------------------------------------------------
    // PutAsync — 409 (Conflict) error path
    // ------------------------------------------------------------------

    [Fact]
    public async Task PutAsync_409_ThrowsConflictException()
    {
        var transport = CreateTransport(_ =>
            Task.FromResult(JsonResponse("conflict body", HttpStatusCode.Conflict)));

        var ex = await Assert.ThrowsAsync<SmplConflictException>(
            () => transport.PutAsync("https://example.com/api", new { }));
        Assert.Equal(409, ex.StatusCode);
    }

    // ------------------------------------------------------------------
    // PostAsync — 500 generic error
    // ------------------------------------------------------------------

    [Fact]
    public async Task PostAsync_500_ThrowsGenericSmplException()
    {
        var transport = CreateTransport(_ =>
            Task.FromResult(JsonResponse("server error", HttpStatusCode.InternalServerError)));

        var ex = await Assert.ThrowsAsync<SmplException>(
            () => transport.PostAsync("https://example.com/api", new { }));
        Assert.Equal(500, ex.StatusCode);
        Assert.Equal("server error", ex.ResponseBody);
    }

    // ------------------------------------------------------------------
    // PutAsync — 500 generic error
    // ------------------------------------------------------------------

    [Fact]
    public async Task PutAsync_500_ThrowsGenericSmplException()
    {
        var transport = CreateTransport(_ =>
            Task.FromResult(JsonResponse("server error", HttpStatusCode.InternalServerError)));

        var ex = await Assert.ThrowsAsync<SmplException>(
            () => transport.PutAsync("https://example.com/api", new { }));
        Assert.Equal(500, ex.StatusCode);
    }

    // ------------------------------------------------------------------
    // PostAsync — 422 (Validation) error path
    // ------------------------------------------------------------------

    [Fact]
    public async Task PostAsync_422_ThrowsValidationException()
    {
        var transport = CreateTransport(_ =>
            Task.FromResult(JsonResponse("validation error", (HttpStatusCode)422)));

        var ex = await Assert.ThrowsAsync<SmplValidationException>(
            () => transport.PostAsync("https://example.com/api", new { }));
        Assert.Equal(422, ex.StatusCode);
        Assert.Equal("validation error", ex.ResponseBody);
    }

    // ------------------------------------------------------------------
    // PutAsync — 404 error path
    // ------------------------------------------------------------------

    [Fact]
    public async Task PutAsync_404_ThrowsNotFoundExceptionWithBody()
    {
        var transport = CreateTransport(_ =>
            Task.FromResult(JsonResponse("not found body", HttpStatusCode.NotFound)));

        var ex = await Assert.ThrowsAsync<SmplNotFoundException>(
            () => transport.PutAsync("https://example.com/api", new { }));
        Assert.Equal(404, ex.StatusCode);
        Assert.Equal("not found body", ex.ResponseBody);
    }

    // ------------------------------------------------------------------
    // DeleteAsync — 202 Accepted (non-204 success)
    // ------------------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_202_Succeeds()
    {
        var transport = CreateTransport(_ =>
            Task.FromResult(JsonResponse("{}", HttpStatusCode.Accepted)));

        await transport.DeleteAsync("https://example.com/api/1");
        // No exception = success
    }

    // ------------------------------------------------------------------
    // GetAsync — SmplException (not subclass) rethrown directly
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetAsync_SmplExceptionFromHandler_IsRethrown()
    {
        // Force a 503 which creates a base SmplException (not a subclass)
        var transport = CreateTransport(_ =>
            Task.FromResult(JsonResponse("service unavailable", HttpStatusCode.ServiceUnavailable)));

        var ex = await Assert.ThrowsAsync<SmplException>(
            () => transport.GetAsync("https://example.com/api"));
        Assert.Equal(503, ex.StatusCode);
        // Verify it's not a subclass
        Assert.Equal(typeof(SmplException), ex.GetType());
    }

    // ------------------------------------------------------------------
    // HandleResponseAsync — empty body
    // ------------------------------------------------------------------

    [Fact]
    public async Task HandleResponse_EmptyBody404_ThrowsWithEmptyResponseBody()
    {
        var transport = CreateTransport(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("", Encoding.UTF8, "application/json"),
            }));

        var ex = await Assert.ThrowsAsync<SmplNotFoundException>(
            () => transport.GetAsync("https://example.com/api"));
        Assert.Equal("", ex.ResponseBody);
    }

    // ------------------------------------------------------------------
    // SerializerOptions — default ignore condition
    // ------------------------------------------------------------------

    [Fact]
    public void SerializerOptions_SerializesNullFieldsCorrectly()
    {
        var obj = new { name = "test", description = (string?)null };
        var json = JsonSerializer.Serialize(obj, Transport.SerializerOptions);

        // WhenWritingNull should exclude the null field
        Assert.DoesNotContain("description", json);
        Assert.Contains("name", json);
    }

    // ------------------------------------------------------------------
    // PostAsync — SmplException (subclass) rethrown
    // ------------------------------------------------------------------

    [Fact]
    public async Task PostAsync_SmplNotFoundException_IsRethrown()
    {
        var transport = CreateTransport(_ =>
            Task.FromResult(JsonResponse("not found", HttpStatusCode.NotFound)));

        var ex = await Assert.ThrowsAsync<SmplNotFoundException>(
            () => transport.PostAsync("https://example.com/api", new { }));
        Assert.Equal(404, ex.StatusCode);
    }

    // ------------------------------------------------------------------
    // PutAsync — SmplException (subclass) rethrown
    // ------------------------------------------------------------------

    [Fact]
    public async Task PutAsync_SmplValidationException_IsRethrown()
    {
        var transport = CreateTransport(_ =>
            Task.FromResult(JsonResponse("invalid", (HttpStatusCode)422)));

        var ex = await Assert.ThrowsAsync<SmplValidationException>(
            () => transport.PutAsync("https://example.com/api", new { }));
        Assert.Equal(422, ex.StatusCode);
    }

    // ------------------------------------------------------------------
    // DeleteAsync — SmplException (subclass) rethrown
    // ------------------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_SmplNotFoundException_IsRethrown()
    {
        var transport = CreateTransport(_ =>
            Task.FromResult(JsonResponse("not found", HttpStatusCode.NotFound)));

        var ex = await Assert.ThrowsAsync<SmplNotFoundException>(
            () => transport.DeleteAsync("https://example.com/api/1"));
        Assert.Equal(404, ex.StatusCode);
    }

    // ------------------------------------------------------------------
    // HandleResponseAsync — verifies message format for default case
    // ------------------------------------------------------------------

    [Fact]
    public async Task HandleResponse_DefaultCase_MessageIncludesStatusAndBody()
    {
        var transport = CreateTransport(_ =>
            Task.FromResult(JsonResponse("custom error body", HttpStatusCode.BadGateway)));

        var ex = await Assert.ThrowsAsync<SmplException>(
            () => transport.GetAsync("https://example.com/api"));
        Assert.Contains("502", ex.Message);
        Assert.Contains("custom error body", ex.Message);
        Assert.Equal(502, ex.StatusCode);
        Assert.Equal("custom error body", ex.ResponseBody);
    }

    // ------------------------------------------------------------------
    // GetAsync — timeout message includes URL
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetAsync_Timeout_MessageIncludesUrl()
    {
        var transport = CreateTransport(_ =>
            throw new TaskCanceledException("timeout"));

        var ex = await Assert.ThrowsAsync<SmplTimeoutException>(
            () => transport.GetAsync("https://example.com/api/test"));
        Assert.Contains("https://example.com/api/test", ex.Message);
        Assert.Contains("timed out", ex.Message);
    }

    // ------------------------------------------------------------------
    // PostAsync — timeout message includes URL
    // ------------------------------------------------------------------

    [Fact]
    public async Task PostAsync_Timeout_MessageIncludesUrl()
    {
        var transport = CreateTransport(_ =>
            throw new TaskCanceledException("timeout"));

        var ex = await Assert.ThrowsAsync<SmplTimeoutException>(
            () => transport.PostAsync("https://example.com/api/test", new { }));
        Assert.Contains("https://example.com/api/test", ex.Message);
    }

    // ------------------------------------------------------------------
    // PutAsync — timeout message includes URL
    // ------------------------------------------------------------------

    [Fact]
    public async Task PutAsync_Timeout_MessageIncludesUrl()
    {
        var transport = CreateTransport(_ =>
            throw new TaskCanceledException("timeout"));

        var ex = await Assert.ThrowsAsync<SmplTimeoutException>(
            () => transport.PutAsync("https://example.com/api/test", new { }));
        Assert.Contains("https://example.com/api/test", ex.Message);
    }

    // ------------------------------------------------------------------
    // DeleteAsync — timeout message includes URL
    // ------------------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_Timeout_MessageIncludesUrl()
    {
        var transport = CreateTransport(_ =>
            throw new TaskCanceledException("timeout"));

        var ex = await Assert.ThrowsAsync<SmplTimeoutException>(
            () => transport.DeleteAsync("https://example.com/api/test"));
        Assert.Contains("https://example.com/api/test", ex.Message);
    }

    // ------------------------------------------------------------------
    // GetAsync — connection error message includes URL and details
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetAsync_ConnectionError_MessageIncludesUrl()
    {
        var transport = CreateTransport(_ =>
            throw new HttpRequestException("DNS resolution failed"));

        var ex = await Assert.ThrowsAsync<SmplConnectionException>(
            () => transport.GetAsync("https://example.com/api/test"));
        Assert.Contains("https://example.com/api/test", ex.Message);
        Assert.Contains("DNS resolution failed", ex.Message);
    }

    // ------------------------------------------------------------------
    // PostAsync — connection error preserves inner exception
    // ------------------------------------------------------------------

    [Fact]
    public async Task PostAsync_ConnectionError_PreservesInnerException()
    {
        var transport = CreateTransport(_ =>
            throw new HttpRequestException("connection refused"));

        var ex = await Assert.ThrowsAsync<SmplConnectionException>(
            () => transport.PostAsync("https://example.com/api", new { }));
        Assert.NotNull(ex.InnerException);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    // ------------------------------------------------------------------
    // PutAsync — connection error preserves inner exception
    // ------------------------------------------------------------------

    [Fact]
    public async Task PutAsync_ConnectionError_PreservesInnerException()
    {
        var transport = CreateTransport(_ =>
            throw new HttpRequestException("connection refused"));

        var ex = await Assert.ThrowsAsync<SmplConnectionException>(
            () => transport.PutAsync("https://example.com/api", new { }));
        Assert.NotNull(ex.InnerException);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    // ------------------------------------------------------------------
    // DeleteAsync — connection error preserves inner exception
    // ------------------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_ConnectionError_PreservesInnerException()
    {
        var transport = CreateTransport(_ =>
            throw new HttpRequestException("connection refused"));

        var ex = await Assert.ThrowsAsync<SmplConnectionException>(
            () => transport.DeleteAsync("https://example.com/api/1"));
        Assert.NotNull(ex.InnerException);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    // ------------------------------------------------------------------
    // GetAsync — timeout preserves inner exception
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetAsync_Timeout_PreservesInnerException()
    {
        var transport = CreateTransport(_ =>
            throw new TaskCanceledException("timeout"));

        var ex = await Assert.ThrowsAsync<SmplTimeoutException>(
            () => transport.GetAsync("https://example.com/api"));
        Assert.NotNull(ex.InnerException);
        Assert.IsType<TaskCanceledException>(ex.InnerException);
    }
}
