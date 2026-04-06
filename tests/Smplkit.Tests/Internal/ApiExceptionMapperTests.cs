using Smplkit.Errors;
using Smplkit.Internal;
using Xunit;

namespace Smplkit.Tests.Internal;

/// <summary>
/// Direct tests for <see cref="ApiExceptionMapper"/> covering all catch branches.
/// </summary>
public class ApiExceptionMapperTests
{
    /// <summary>
    /// A fake exception that mimics an NSwag-generated ApiException via duck-typing.
    /// <see cref="ApiExceptionMapper"/> detects NSwag exceptions by checking for
    /// <c>StatusCode</c> and <c>Response</c> properties via reflection.
    /// </summary>
    private class FakeApiException : Exception
    {
        public int StatusCode { get; }
        public string Response { get; }

        public FakeApiException(int statusCode, string response = "")
            : base($"HTTP {statusCode}")
        {
            StatusCode = statusCode;
            Response = response;
        }
    }

    // ------------------------------------------------------------------
    // Generic overload: ExecuteAsync<T>
    // ------------------------------------------------------------------

    [Fact]
    public async Task Generic_SmplException_RethrowsUnchanged()
    {
        var original = new SmplNotFoundException("not found");

        var ex = await Assert.ThrowsAsync<SmplNotFoundException>(
            () => ApiExceptionMapper.ExecuteAsync<int>(() => throw original));

        Assert.Same(original, ex);
    }

    [Fact]
    public async Task Generic_FormatException_ThrowsSmplValidationException()
    {
        var ex = await Assert.ThrowsAsync<SmplValidationException>(
            () => ApiExceptionMapper.ExecuteAsync<int>(
                () => throw new FormatException("Bad GUID")));

        Assert.Contains("Invalid identifier format", ex.Message);
        Assert.Contains("Bad GUID", ex.Message);
    }

    [Fact]
    public async Task Generic_2xxApiException_ReturnsDefault()
    {
        var result = await ApiExceptionMapper.ExecuteAsync<int>(
            () => throw new FakeApiException(201));

        Assert.Equal(default, result);
    }

    [Fact]
    public async Task Generic_ErrorApiException_ThrowsSmplException()
    {
        var ex = await Assert.ThrowsAsync<SmplException>(
            () => ApiExceptionMapper.ExecuteAsync<int>(
                () => throw new FakeApiException(500, """{"errors":[{"detail":"boom"}]}""")));

        Assert.Contains("boom", ex.Message);
    }

    // ------------------------------------------------------------------
    // Void overload: ExecuteAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task Void_SmplException_RethrowsUnchanged()
    {
        var original = new SmplNotFoundException("not found");

        var ex = await Assert.ThrowsAsync<SmplNotFoundException>(
            () => ApiExceptionMapper.ExecuteAsync(() => throw original));

        Assert.Same(original, ex);
    }

    [Fact]
    public async Task Void_FormatException_ThrowsSmplValidationException()
    {
        var ex = await Assert.ThrowsAsync<SmplValidationException>(
            () => ApiExceptionMapper.ExecuteAsync(
                () => throw new FormatException("Bad GUID")));

        Assert.Contains("Invalid identifier format", ex.Message);
    }

    [Fact]
    public async Task Void_CancelledToken_RethrowsTaskCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => ApiExceptionMapper.ExecuteAsync(
                () => throw new TaskCanceledException(null, null, cts.Token)));
    }

    [Fact]
    public async Task Void_HttpRequestException_ThrowsSmplConnectionException()
    {
        var ex = await Assert.ThrowsAsync<SmplConnectionException>(
            () => ApiExceptionMapper.ExecuteAsync(
                () => throw new HttpRequestException("connection refused")));

        Assert.Contains("Connection failed", ex.Message);
    }

    [Fact]
    public async Task Void_TimeoutException_ThrowsSmplTimeoutException()
    {
        var ex = await Assert.ThrowsAsync<SmplTimeoutException>(
            () => ApiExceptionMapper.ExecuteAsync(
                () => throw new TaskCanceledException("timeout", null, CancellationToken.None)));

        Assert.Contains("Request timed out", ex.Message);
    }

    [Fact]
    public async Task Void_2xxApiException_ReturnsWithoutThrowing()
    {
        await ApiExceptionMapper.ExecuteAsync(
            () => throw new FakeApiException(204));
    }

    [Fact]
    public async Task Void_ErrorApiException_ThrowsSmplException()
    {
        var ex = await Assert.ThrowsAsync<SmplNotFoundException>(
            () => ApiExceptionMapper.ExecuteAsync(
                () => throw new FakeApiException(404, """{"errors":[{"detail":"missing"}]}""")));

        Assert.Contains("missing", ex.Message);
    }
}
