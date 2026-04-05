using System.Net;
using System.Text;
using Smplkit.Errors;
using Smplkit.Internal;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Internal;

public class TransportTests
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
    // GetAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetAsync_SuccessResponse_ReturnsBody()
    {
        var transport = CreateTransport(_ =>
            Task.FromResult(JsonResponse("""{"data": "test"}""")));

        var result = await transport.GetAsync("https://example.com/api");

        Assert.Contains("test", result);
    }

    [Fact]
    public async Task GetAsync_SmplException_RethrowsUnchanged()
    {
        var transport = CreateTransport(_ =>
            Task.FromResult(JsonResponse("Not found", HttpStatusCode.NotFound)));

        var ex = await Assert.ThrowsAsync<SmplNotFoundException>(
            () => transport.GetAsync("https://example.com/api"));
        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public async Task GetAsync_TaskCanceledException_WithCancelledToken_Rethrows()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var transport = CreateTransport(_ =>
            Task.FromResult(JsonResponse("{}")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transport.GetAsync("https://example.com/api", cts.Token));
    }

    [Fact]
    public async Task GetAsync_TaskCanceledException_WithoutCancellation_ThrowsTimeout()
    {
        var transport = CreateTransport(_ =>
            throw new TaskCanceledException("timeout"));

        await Assert.ThrowsAsync<SmplTimeoutException>(
            () => transport.GetAsync("https://example.com/api"));
    }

    [Fact]
    public async Task GetAsync_HttpRequestException_ThrowsConnection()
    {
        var transport = CreateTransport(_ =>
            throw new HttpRequestException("refused"));

        await Assert.ThrowsAsync<SmplConnectionException>(
            () => transport.GetAsync("https://example.com/api"));
    }

    // ------------------------------------------------------------------
    // PostAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task PostAsync_SuccessResponse_ReturnsBody()
    {
        var transport = CreateTransport(_ =>
            Task.FromResult(JsonResponse("""{"created": true}""", HttpStatusCode.Created)));

        var result = await transport.PostAsync("https://example.com/api", new { name = "test" });

        Assert.Contains("created", result);
    }

    [Fact]
    public async Task PostAsync_SmplException_RethrowsUnchanged()
    {
        var transport = CreateTransport(_ =>
            Task.FromResult(JsonResponse("conflict", HttpStatusCode.Conflict)));

        await Assert.ThrowsAsync<SmplConflictException>(
            () => transport.PostAsync("https://example.com/api", new { }));
    }

    [Fact]
    public async Task PostAsync_TaskCanceledException_WithCancelledToken_Rethrows()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var transport = CreateTransport(_ =>
            Task.FromResult(JsonResponse("{}")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transport.PostAsync("https://example.com/api", new { }, cts.Token));
    }

    [Fact]
    public async Task PostAsync_TaskCanceledException_WithoutCancellation_ThrowsTimeout()
    {
        var transport = CreateTransport(_ =>
            throw new TaskCanceledException("timeout"));

        await Assert.ThrowsAsync<SmplTimeoutException>(
            () => transport.PostAsync("https://example.com/api", new { }));
    }

    [Fact]
    public async Task PostAsync_HttpRequestException_ThrowsConnection()
    {
        var transport = CreateTransport(_ =>
            throw new HttpRequestException("refused"));

        await Assert.ThrowsAsync<SmplConnectionException>(
            () => transport.PostAsync("https://example.com/api", new { }));
    }

    // ------------------------------------------------------------------
    // PutAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task PutAsync_SuccessResponse_ReturnsBody()
    {
        var transport = CreateTransport(_ =>
            Task.FromResult(JsonResponse("""{"updated": true}""")));

        var result = await transport.PutAsync("https://example.com/api", new { name = "updated" });

        Assert.Contains("updated", result);
    }

    [Fact]
    public async Task PutAsync_SmplException_RethrowsUnchanged()
    {
        var transport = CreateTransport(_ =>
            Task.FromResult(JsonResponse("validation", (HttpStatusCode)422)));

        await Assert.ThrowsAsync<SmplValidationException>(
            () => transport.PutAsync("https://example.com/api", new { }));
    }

    [Fact]
    public async Task PutAsync_TaskCanceledException_WithCancelledToken_Rethrows()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var transport = CreateTransport(_ =>
            Task.FromResult(JsonResponse("{}")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transport.PutAsync("https://example.com/api", new { }, cts.Token));
    }

    [Fact]
    public async Task PutAsync_TaskCanceledException_WithoutCancellation_ThrowsTimeout()
    {
        var transport = CreateTransport(_ =>
            throw new TaskCanceledException("timeout"));

        await Assert.ThrowsAsync<SmplTimeoutException>(
            () => transport.PutAsync("https://example.com/api", new { }));
    }

    [Fact]
    public async Task PutAsync_HttpRequestException_ThrowsConnection()
    {
        var transport = CreateTransport(_ =>
            throw new HttpRequestException("refused"));

        await Assert.ThrowsAsync<SmplConnectionException>(
            () => transport.PutAsync("https://example.com/api", new { }));
    }

    // ------------------------------------------------------------------
    // DeleteAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_204_ReturnsImmediately()
    {
        var transport = CreateTransport(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));

        await transport.DeleteAsync("https://example.com/api/1");
        // No exception = success
    }

    [Fact]
    public async Task DeleteAsync_200_ReturnsSuccessfully()
    {
        var transport = CreateTransport(_ =>
            Task.FromResult(JsonResponse("{}", HttpStatusCode.OK)));

        await transport.DeleteAsync("https://example.com/api/1");
    }

    [Fact]
    public async Task DeleteAsync_SmplException_RethrowsUnchanged()
    {
        var transport = CreateTransport(_ =>
            Task.FromResult(JsonResponse("conflict", HttpStatusCode.Conflict)));

        await Assert.ThrowsAsync<SmplConflictException>(
            () => transport.DeleteAsync("https://example.com/api/1"));
    }

    [Fact]
    public async Task DeleteAsync_TaskCanceledException_WithCancelledToken_Rethrows()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var transport = CreateTransport(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transport.DeleteAsync("https://example.com/api/1", cts.Token));
    }

    [Fact]
    public async Task DeleteAsync_TaskCanceledException_WithoutCancellation_ThrowsTimeout()
    {
        var transport = CreateTransport(_ =>
            throw new TaskCanceledException("timeout"));

        await Assert.ThrowsAsync<SmplTimeoutException>(
            () => transport.DeleteAsync("https://example.com/api/1"));
    }

    [Fact]
    public async Task DeleteAsync_HttpRequestException_ThrowsConnection()
    {
        var transport = CreateTransport(_ =>
            throw new HttpRequestException("refused"));

        await Assert.ThrowsAsync<SmplConnectionException>(
            () => transport.DeleteAsync("https://example.com/api/1"));
    }

    // ------------------------------------------------------------------
    // HandleResponseAsync — various status codes
    // ------------------------------------------------------------------

    [Fact]
    public async Task HandleResponse_404_ThrowsNotFoundException()
    {
        var transport = CreateTransport(_ =>
            Task.FromResult(JsonResponse("not found body", HttpStatusCode.NotFound)));

        var ex = await Assert.ThrowsAsync<SmplNotFoundException>(
            () => transport.GetAsync("https://example.com/api"));
        Assert.Equal(404, ex.StatusCode);
        Assert.Equal("not found body", ex.ResponseBody);
    }

    [Fact]
    public async Task HandleResponse_409_ThrowsConflictException()
    {
        var transport = CreateTransport(_ =>
            Task.FromResult(JsonResponse("conflict body", HttpStatusCode.Conflict)));

        var ex = await Assert.ThrowsAsync<SmplConflictException>(
            () => transport.GetAsync("https://example.com/api"));
        Assert.Equal(409, ex.StatusCode);
        Assert.Contains("HTTP 409", ex.Message);
        Assert.Equal("conflict body", ex.ResponseBody);
    }

    [Fact]
    public async Task HandleResponse_422_ThrowsValidationException()
    {
        var transport = CreateTransport(_ =>
            Task.FromResult(JsonResponse("validation body", (HttpStatusCode)422)));

        var ex = await Assert.ThrowsAsync<SmplValidationException>(
            () => transport.GetAsync("https://example.com/api"));
        Assert.Equal(422, ex.StatusCode);
        Assert.Contains("HTTP 422", ex.Message);
        Assert.Equal("validation body", ex.ResponseBody);
    }

    [Fact]
    public async Task HandleResponse_500_ThrowsGenericSmplException()
    {
        var transport = CreateTransport(_ =>
            Task.FromResult(JsonResponse("server error", HttpStatusCode.InternalServerError)));

        var ex = await Assert.ThrowsAsync<SmplException>(
            () => transport.GetAsync("https://example.com/api"));
        Assert.Equal(500, ex.StatusCode);
        Assert.Equal("server error", ex.ResponseBody);
        Assert.Contains("500", ex.Message);
    }

    [Fact]
    public async Task HandleResponse_401_ThrowsGenericSmplException()
    {
        var transport = CreateTransport(_ =>
            Task.FromResult(JsonResponse("unauthorized", HttpStatusCode.Unauthorized)));

        var ex = await Assert.ThrowsAsync<SmplException>(
            () => transport.GetAsync("https://example.com/api"));
        Assert.Equal(401, ex.StatusCode);
    }

    // ------------------------------------------------------------------
    // Transport constructor sets headers
    // ------------------------------------------------------------------

    [Fact]
    public async Task Transport_SetsUserAgent()
    {
        HttpRequestMessage? lastRequest = null;
        var handler = new MockHttpMessageHandler(req =>
        {
            lastRequest = req;
            return Task.FromResult(JsonResponse("{}"));
        });
        var httpClient = new HttpClient(handler);
        var transport = new Transport(httpClient, new SmplClientOptions { ApiKey = "test" });

        await transport.GetAsync("https://example.com/api");

        Assert.NotNull(lastRequest);
        Assert.Contains("smplkit-dotnet-sdk", lastRequest.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task Transport_SetsAcceptHeader()
    {
        HttpRequestMessage? lastRequest = null;
        var handler = new MockHttpMessageHandler(req =>
        {
            lastRequest = req;
            return Task.FromResult(JsonResponse("{}"));
        });
        var httpClient = new HttpClient(handler);
        var transport = new Transport(httpClient, new SmplClientOptions { ApiKey = "test" });

        await transport.GetAsync("https://example.com/api");

        Assert.NotNull(lastRequest);
        Assert.Contains("application/vnd.api+json", lastRequest.Headers.Accept.ToString());
    }

    [Fact]
    public async Task Transport_SetsBearerAuth()
    {
        HttpRequestMessage? lastRequest = null;
        var handler = new MockHttpMessageHandler(req =>
        {
            lastRequest = req;
            return Task.FromResult(JsonResponse("{}"));
        });
        var httpClient = new HttpClient(handler);
        var transport = new Transport(httpClient, new SmplClientOptions { ApiKey = "my_api_key" });

        await transport.GetAsync("https://example.com/api");

        Assert.NotNull(lastRequest);
        var auth = lastRequest.Headers.Authorization;
        Assert.NotNull(auth);
        Assert.Equal("Bearer", auth.Scheme);
        Assert.Equal("my_api_key", auth.Parameter);
    }

    [Fact]
    public void Transport_SetsTimeout()
    {
        var handler = new MockHttpMessageHandler(_ => Task.FromResult(JsonResponse("{}")));
        var httpClient = new HttpClient(handler);
        var timeout = TimeSpan.FromSeconds(120);
        _ = new Transport(httpClient, new SmplClientOptions
        {
            ApiKey = "test",
            Timeout = timeout,
        });

        Assert.Equal(timeout, httpClient.Timeout);
    }

    // ------------------------------------------------------------------
    // SerializerOptions
    // ------------------------------------------------------------------

    [Fact]
    public void SerializerOptions_HasCamelCaseNaming()
    {
        Assert.Equal(System.Text.Json.JsonNamingPolicy.CamelCase, Transport.SerializerOptions.PropertyNamingPolicy);
    }

    [Fact]
    public void SerializerOptions_IsCaseInsensitive()
    {
        Assert.True(Transport.SerializerOptions.PropertyNameCaseInsensitive);
    }

    [Fact]
    public void SerializerOptions_IgnoresNullWhenWriting()
    {
        Assert.Equal(
            System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            Transport.SerializerOptions.DefaultIgnoreCondition);
    }

    // ------------------------------------------------------------------
    // DeleteAsync — non-204 error response goes through HandleResponseAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_404_ThrowsNotFoundException()
    {
        var transport = CreateTransport(_ =>
            Task.FromResult(JsonResponse("not found", HttpStatusCode.NotFound)));

        await Assert.ThrowsAsync<SmplNotFoundException>(
            () => transport.DeleteAsync("https://example.com/api/1"));
    }

    // ------------------------------------------------------------------
    // PostAsync — serializes body and sets content-type
    // ------------------------------------------------------------------

    [Fact]
    public async Task PostAsync_SetsJsonApiContentType()
    {
        HttpRequestMessage? captured = null;
        var handler = new MockHttpMessageHandler(req =>
        {
            captured = req;
            return Task.FromResult(JsonResponse("""{"ok": true}""", HttpStatusCode.Created));
        });
        var httpClient = new HttpClient(handler);
        var transport = new Transport(httpClient, new SmplClientOptions { ApiKey = "test" });

        await transport.PostAsync("https://example.com/api", new { name = "test" });

        Assert.NotNull(captured);
        Assert.NotNull(captured.Content);
        var contentType = captured.Content!.Headers.ContentType!.MediaType;
        Assert.Equal("application/vnd.api+json", contentType);
    }

    [Fact]
    public async Task PostAsync_SerializesBodyAsJson()
    {
        string? body = null;
        var handler = new MockHttpMessageHandler(async req =>
        {
            body = await req.Content!.ReadAsStringAsync();
            return JsonResponse("""{"ok": true}""", HttpStatusCode.Created);
        });
        var httpClient = new HttpClient(handler);
        var transport = new Transport(httpClient, new SmplClientOptions { ApiKey = "test" });

        await transport.PostAsync("https://example.com/api", new { name = "hello" });

        Assert.NotNull(body);
        Assert.Contains("\"name\"", body);
        Assert.Contains("hello", body);
    }

    // ------------------------------------------------------------------
    // PutAsync — serializes body and sets content-type
    // ------------------------------------------------------------------

    [Fact]
    public async Task PutAsync_SetsJsonApiContentType()
    {
        HttpRequestMessage? captured = null;
        var handler = new MockHttpMessageHandler(req =>
        {
            captured = req;
            return Task.FromResult(JsonResponse("""{"ok": true}"""));
        });
        var httpClient = new HttpClient(handler);
        var transport = new Transport(httpClient, new SmplClientOptions { ApiKey = "test" });

        await transport.PutAsync("https://example.com/api", new { name = "test" });

        Assert.NotNull(captured);
        Assert.NotNull(captured.Content);
        var contentType = captured.Content!.Headers.ContentType!.MediaType;
        Assert.Equal("application/vnd.api+json", contentType);
    }

    [Fact]
    public async Task PutAsync_SerializesBodyAsJson()
    {
        string? body = null;
        var handler = new MockHttpMessageHandler(async req =>
        {
            body = await req.Content!.ReadAsStringAsync();
            return JsonResponse("""{"ok": true}""");
        });
        var httpClient = new HttpClient(handler);
        var transport = new Transport(httpClient, new SmplClientOptions { ApiKey = "test" });

        await transport.PutAsync("https://example.com/api", new { name = "updated" });

        Assert.NotNull(body);
        Assert.Contains("\"name\"", body);
        Assert.Contains("updated", body);
    }

    // ------------------------------------------------------------------
    // HandleResponseAsync — 403 goes through default case
    // ------------------------------------------------------------------

    [Fact]
    public async Task HandleResponse_403_ThrowsGenericSmplException()
    {
        var transport = CreateTransport(_ =>
            Task.FromResult(JsonResponse("forbidden", HttpStatusCode.Forbidden)));

        var ex = await Assert.ThrowsAsync<SmplException>(
            () => transport.GetAsync("https://example.com/api"));
        Assert.Equal(403, ex.StatusCode);
        Assert.Equal("forbidden", ex.ResponseBody);
    }

    // ------------------------------------------------------------------
    // DeleteAsync — 409 (Conflict)
    // ------------------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_409_ThrowsConflictException()
    {
        var transport = CreateTransport(_ =>
            Task.FromResult(JsonResponse("has children", HttpStatusCode.Conflict)));

        var ex = await Assert.ThrowsAsync<SmplConflictException>(
            () => transport.DeleteAsync("https://example.com/api/1"));
        Assert.Equal(409, ex.StatusCode);
        Assert.Contains("has children", ex.ResponseBody!);
    }

    // ------------------------------------------------------------------
    // DeleteAsync — 422 (Validation)
    // ------------------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_422_ThrowsValidationException()
    {
        var transport = CreateTransport(_ =>
            Task.FromResult(JsonResponse("invalid", (HttpStatusCode)422)));

        var ex = await Assert.ThrowsAsync<SmplValidationException>(
            () => transport.DeleteAsync("https://example.com/api/1"));
        Assert.Equal(422, ex.StatusCode);
    }

    // ------------------------------------------------------------------
    // DeleteAsync — 500 (generic error)
    // ------------------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_500_ThrowsGenericSmplException()
    {
        var transport = CreateTransport(_ =>
            Task.FromResult(JsonResponse("server error", HttpStatusCode.InternalServerError)));

        var ex = await Assert.ThrowsAsync<SmplException>(
            () => transport.DeleteAsync("https://example.com/api/1"));
        Assert.Equal(500, ex.StatusCode);
    }

    // ------------------------------------------------------------------
    // PostAsync — 404 goes through HandleResponseAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task PostAsync_404_ThrowsNotFoundException()
    {
        var transport = CreateTransport(_ =>
            Task.FromResult(JsonResponse("not found", HttpStatusCode.NotFound)));

        await Assert.ThrowsAsync<SmplNotFoundException>(
            () => transport.PostAsync("https://example.com/api", new { }));
    }

    // ------------------------------------------------------------------
    // PutAsync — 404 goes through HandleResponseAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task PutAsync_404_ThrowsNotFoundException()
    {
        var transport = CreateTransport(_ =>
            Task.FromResult(JsonResponse("not found", HttpStatusCode.NotFound)));

        await Assert.ThrowsAsync<SmplNotFoundException>(
            () => transport.PutAsync("https://example.com/api", new { }));
    }

    // ------------------------------------------------------------------
    // PostAsync — CancellationToken with active cancellation rethrows
    // ------------------------------------------------------------------

    [Fact]
    public async Task PostAsync_CancelledToken_RethrowsTaskCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var transport = CreateTransport(_ =>
            Task.FromResult(JsonResponse("{}", HttpStatusCode.Created)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transport.PostAsync("https://example.com/api", new { }, cts.Token));
    }

    // ------------------------------------------------------------------
    // PutAsync — CancellationToken with active cancellation rethrows
    // ------------------------------------------------------------------

    [Fact]
    public async Task PutAsync_CancelledToken_RethrowsTaskCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var transport = CreateTransport(_ =>
            Task.FromResult(JsonResponse("{}")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transport.PutAsync("https://example.com/api", new { }, cts.Token));
    }
}
