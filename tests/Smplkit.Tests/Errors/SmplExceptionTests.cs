using Smplkit.Errors;
using Xunit;

namespace Smplkit.Tests.Errors;

public class SmplExceptionTests
{
    [Fact]
    public void SmplException_HasCorrectProperties()
    {
        var ex = new SmplException("test error", statusCode: 500, responseBody: "body");

        Assert.Equal("test error", ex.Message);
        Assert.Equal(500, ex.StatusCode);
        Assert.Equal("body", ex.ResponseBody);
    }

    [Fact]
    public void SmplException_WithInnerException()
    {
        var inner = new InvalidOperationException("inner");
        var ex = new SmplException("test error", innerException: inner);

        Assert.Same(inner, ex.InnerException);
        Assert.Null(ex.StatusCode);
        Assert.Null(ex.ResponseBody);
    }

    [Fact]
    public void SmplConnectionException_IsSmplException()
    {
        var ex = new SmplConnectionException("connection failed");

        Assert.IsAssignableFrom<SmplException>(ex);
        Assert.IsAssignableFrom<Exception>(ex);
        Assert.Equal("connection failed", ex.Message);
        Assert.Null(ex.StatusCode);
    }

    [Fact]
    public void SmplConnectionException_WithInnerException()
    {
        var inner = new HttpRequestException("network error");
        var ex = new SmplConnectionException("connection failed", inner);

        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void SmplTimeoutException_IsSmplException()
    {
        var ex = new SmplTimeoutException("timed out");

        Assert.IsAssignableFrom<SmplException>(ex);
        Assert.Equal("timed out", ex.Message);
        Assert.Null(ex.StatusCode);
    }

    [Fact]
    public void SmplNotFoundException_IsSmplException()
    {
        var ex = new SmplNotFoundException("not found", "response body");

        Assert.IsAssignableFrom<SmplException>(ex);
        Assert.Equal("not found", ex.Message);
        Assert.Equal(404, ex.StatusCode);
        Assert.Equal("response body", ex.ResponseBody);
    }

    [Fact]
    public void SmplConflictException_IsSmplException()
    {
        var ex = new SmplConflictException("conflict", "response body");

        Assert.IsAssignableFrom<SmplException>(ex);
        Assert.Equal("conflict", ex.Message);
        Assert.Equal(409, ex.StatusCode);
        Assert.Equal("response body", ex.ResponseBody);
    }

    [Fact]
    public void SmplValidationException_IsSmplException()
    {
        var ex = new SmplValidationException("validation failed", "response body");

        Assert.IsAssignableFrom<SmplException>(ex);
        Assert.Equal("validation failed", ex.Message);
        Assert.Equal(422, ex.StatusCode);
        Assert.Equal("response body", ex.ResponseBody);
    }

    [Fact]
    public void AllExceptions_AreInstanceOfSmplException()
    {
        SmplException[] exceptions =
        [
            new SmplConnectionException("test"),
            new SmplTimeoutException("test"),
            new SmplNotFoundException("test"),
            new SmplConflictException("test"),
            new SmplValidationException("test"),
        ];

        foreach (var ex in exceptions)
        {
            Assert.IsAssignableFrom<SmplException>(ex);
        }
    }

    [Fact]
    public void AllExceptions_AreInstanceOfSystemException()
    {
        SmplException[] exceptions =
        [
            new SmplException("base"),
            new SmplConnectionException("connection"),
            new SmplTimeoutException("timeout"),
            new SmplNotFoundException("not found"),
            new SmplConflictException("conflict"),
            new SmplValidationException("validation"),
        ];

        foreach (var ex in exceptions)
        {
            Assert.IsAssignableFrom<Exception>(ex);
        }
    }

    // ------------------------------------------------------------------
    // SmplTimeoutException with inner exception
    // ------------------------------------------------------------------

    [Fact]
    public void SmplTimeoutException_WithInnerException()
    {
        var inner = new TaskCanceledException("timed out");
        var ex = new SmplTimeoutException("Request timed out", inner);

        Assert.Equal("Request timed out", ex.Message);
        Assert.Same(inner, ex.InnerException);
        Assert.Null(ex.StatusCode);
        Assert.Null(ex.ResponseBody);
    }

    // ------------------------------------------------------------------
    // Default responseBody parameter (null)
    // ------------------------------------------------------------------

    [Fact]
    public void SmplNotFoundException_WithoutResponseBody_HasNullResponseBody()
    {
        var ex = new SmplNotFoundException("not found");

        Assert.Equal(404, ex.StatusCode);
        Assert.Null(ex.ResponseBody);
    }

    [Fact]
    public void SmplConflictException_WithoutResponseBody_HasNullResponseBody()
    {
        var ex = new SmplConflictException("conflict");

        Assert.Equal(409, ex.StatusCode);
        Assert.Null(ex.ResponseBody);
    }

    [Fact]
    public void SmplValidationException_WithoutResponseBody_HasNullResponseBody()
    {
        var ex = new SmplValidationException("validation failed");

        Assert.Equal(422, ex.StatusCode);
        Assert.Null(ex.ResponseBody);
    }

    // ------------------------------------------------------------------
    // SmplException with all null optional params
    // ------------------------------------------------------------------

    [Fact]
    public void SmplException_MessageOnly_HasNullOptionalProps()
    {
        var ex = new SmplException("simple error");

        Assert.Equal("simple error", ex.Message);
        Assert.Null(ex.StatusCode);
        Assert.Null(ex.ResponseBody);
        Assert.Null(ex.InnerException);
    }

    // ------------------------------------------------------------------
    // SmplConnectionException properties
    // ------------------------------------------------------------------

    [Fact]
    public void SmplConnectionException_HasNullResponseBody()
    {
        var ex = new SmplConnectionException("failed");

        Assert.Null(ex.ResponseBody);
        Assert.Null(ex.StatusCode);
    }
}
