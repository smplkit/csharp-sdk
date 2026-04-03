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
    public void SmplNotConnectedException_IsSmplException()
    {
        var ex = new SmplNotConnectedException();

        Assert.IsAssignableFrom<SmplException>(ex);
        Assert.Equal("SmplClient is not connected. Call ConnectAsync() first.", ex.Message);
        Assert.Null(ex.StatusCode);
    }

    [Fact]
    public void SmplNotConnectedException_WithCustomMessage()
    {
        var ex = new SmplNotConnectedException("custom message");

        Assert.Equal("custom message", ex.Message);
        Assert.Null(ex.StatusCode);
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
            new SmplNotConnectedException(),
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
            new SmplNotConnectedException(),
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

    // ------------------------------------------------------------------
    // errors.Is / errors.As compatibility
    // ------------------------------------------------------------------

    [Fact]
    public void SmplNotFoundException_CanBeCaughtAsSmplException()
    {
        Exception ex = new SmplNotFoundException("not found", "body");
        Assert.True(ex is SmplException);
        var smpl = (SmplException)ex;
        Assert.Equal(404, smpl.StatusCode);
        Assert.Equal("body", smpl.ResponseBody);
    }

    [Fact]
    public void SmplConflictException_CanBeCaughtAsSmplException()
    {
        Exception ex = new SmplConflictException("conflict", "body");
        Assert.True(ex is SmplException);
        var smpl = (SmplException)ex;
        Assert.Equal(409, smpl.StatusCode);
    }

    [Fact]
    public void SmplValidationException_CanBeCaughtAsSmplException()
    {
        Exception ex = new SmplValidationException("validation", "body");
        Assert.True(ex is SmplException);
        var smpl = (SmplException)ex;
        Assert.Equal(422, smpl.StatusCode);
    }

    [Fact]
    public void SmplTimeoutException_CanBeCaughtAsSmplException()
    {
        Exception ex = new SmplTimeoutException("timeout");
        Assert.True(ex is SmplException);
        var smpl = (SmplException)ex;
        Assert.Null(smpl.StatusCode);
    }

    [Fact]
    public void SmplConnectionException_CanBeCaughtAsSmplException()
    {
        Exception ex = new SmplConnectionException("conn");
        Assert.True(ex is SmplException);
    }

    [Fact]
    public void SmplNotConnectedException_CanBeCaughtAsSmplException()
    {
        Exception ex = new SmplNotConnectedException();
        Assert.True(ex is SmplException);
    }

    // ------------------------------------------------------------------
    // SmplException with all four params
    // ------------------------------------------------------------------

    [Fact]
    public void SmplException_AllFourParams()
    {
        var inner = new InvalidOperationException("inner");
        var ex = new SmplException("msg", statusCode: 503, responseBody: "body", innerException: inner);

        Assert.Equal("msg", ex.Message);
        Assert.Equal(503, ex.StatusCode);
        Assert.Equal("body", ex.ResponseBody);
        Assert.Same(inner, ex.InnerException);
    }

    // ------------------------------------------------------------------
    // SmplConnectionException without inner (default param)
    // ------------------------------------------------------------------

    [Fact]
    public void SmplConnectionException_WithoutInner_HasNullInnerException()
    {
        var ex = new SmplConnectionException("failed");
        Assert.Null(ex.InnerException);
    }

    // ------------------------------------------------------------------
    // SmplTimeoutException without inner (default param)
    // ------------------------------------------------------------------

    [Fact]
    public void SmplTimeoutException_WithoutInner_HasNullInnerException()
    {
        var ex = new SmplTimeoutException("timed out");
        Assert.Null(ex.InnerException);
    }
}
