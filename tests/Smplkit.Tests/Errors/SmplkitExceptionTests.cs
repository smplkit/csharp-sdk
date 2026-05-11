using Smplkit.Errors;
using Xunit;

namespace Smplkit.Tests.Errors;

public class SmplkitExceptionTests
{
    [Fact]
    public void SmplkitException_HasCorrectProperties()
    {
        var ex = new SmplkitException("test error", statusCode: 500, responseBody: "body");

        Assert.Equal("test error", ex.Message);
        Assert.Equal(500, ex.StatusCode);
        Assert.Equal("body", ex.ResponseBody);
    }

    [Fact]
    public void SmplkitException_WithInnerException()
    {
        var inner = new InvalidOperationException("inner");
        var ex = new SmplkitException("test error", innerException: inner);

        Assert.Same(inner, ex.InnerException);
        Assert.Null(ex.StatusCode);
        Assert.Null(ex.ResponseBody);
    }

    [Fact]
    public void ConnectionException_IsSmplkitException()
    {
        var ex = new ConnectionException("connection failed");

        Assert.IsAssignableFrom<SmplkitException>(ex);
        Assert.IsAssignableFrom<Exception>(ex);
        Assert.Equal("connection failed", ex.Message);
        Assert.Null(ex.StatusCode);
    }

    [Fact]
    public void ConnectionException_WithInnerException()
    {
        var inner = new HttpRequestException("network error");
        var ex = new ConnectionException("connection failed", inner);

        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void TimeoutException_IsSmplkitException()
    {
        var ex = new Smplkit.Errors.TimeoutException("timed out");

        Assert.IsAssignableFrom<SmplkitException>(ex);
        Assert.Equal("timed out", ex.Message);
        Assert.Null(ex.StatusCode);
    }

    [Fact]
    public void NotFoundException_IsSmplkitException()
    {
        var ex = new NotFoundException("not found", "response body");

        Assert.IsAssignableFrom<SmplkitException>(ex);
        Assert.Equal("not found", ex.Message);
        Assert.Equal(404, ex.StatusCode);
        Assert.Equal("response body", ex.ResponseBody);
    }

    [Fact]
    public void ConflictException_IsSmplkitException()
    {
        var ex = new ConflictException("conflict", "response body");

        Assert.IsAssignableFrom<SmplkitException>(ex);
        Assert.Equal("conflict", ex.Message);
        Assert.Equal(409, ex.StatusCode);
        Assert.Equal("response body", ex.ResponseBody);
    }

    [Fact]
    public void ValidationException_IsSmplkitException()
    {
        var ex = new ValidationException("validation failed", "response body");

        Assert.IsAssignableFrom<SmplkitException>(ex);
        Assert.Equal("validation failed", ex.Message);
        Assert.Equal(422, ex.StatusCode);
        Assert.Equal("response body", ex.ResponseBody);
    }

    [Fact]
    public void PaymentRequiredException_IsSmplkitException()
    {
        var ex = new PaymentRequiredException("payment required", "response body");

        Assert.IsAssignableFrom<SmplkitException>(ex);
        Assert.Equal("payment required", ex.Message);
        Assert.Equal(402, ex.StatusCode);
        Assert.Equal("response body", ex.ResponseBody);
    }

    [Fact]
    public void PaymentRequiredException_WithoutResponseBody_HasNullResponseBody()
    {
        var ex = new PaymentRequiredException("payment required");

        Assert.Equal(402, ex.StatusCode);
        Assert.Null(ex.ResponseBody);
    }

    [Fact]
    public void PaymentRequiredException_CanBeCaughtAsSmplkitException()
    {
        Exception ex = new PaymentRequiredException("payment required", "body");
        Assert.True(ex is SmplkitException);
        var smpl = (SmplkitException)ex;
        Assert.Equal(402, smpl.StatusCode);
        Assert.Equal("body", smpl.ResponseBody);
    }

    [Fact]
    public void AllExceptions_AreInstanceOfSmplkitException()
    {
        SmplkitException[] exceptions =
        [
            new ConnectionException("test"),
            new Smplkit.Errors.TimeoutException("test"),
            new NotFoundException("test"),
            new ConflictException("test"),
            new ValidationException("test"),
            new PaymentRequiredException("test"),
        ];

        foreach (var ex in exceptions)
        {
            Assert.IsAssignableFrom<SmplkitException>(ex);
        }
    }

    [Fact]
    public void AllExceptions_AreInstanceOfSystemException()
    {
        SmplkitException[] exceptions =
        [
            new SmplkitException("base"),
            new ConnectionException("connection"),
            new Smplkit.Errors.TimeoutException("timeout"),
            new NotFoundException("not found"),
            new ConflictException("conflict"),
            new ValidationException("validation"),
            new PaymentRequiredException("payment required"),
        ];

        foreach (var ex in exceptions)
        {
            Assert.IsAssignableFrom<Exception>(ex);
        }
    }

    // ------------------------------------------------------------------
    // TimeoutException with inner exception
    // ------------------------------------------------------------------

    [Fact]
    public void TimeoutException_WithInnerException()
    {
        var inner = new TaskCanceledException("timed out");
        var ex = new Smplkit.Errors.TimeoutException("Request timed out", inner);

        Assert.Equal("Request timed out", ex.Message);
        Assert.Same(inner, ex.InnerException);
        Assert.Null(ex.StatusCode);
        Assert.Null(ex.ResponseBody);
    }

    // ------------------------------------------------------------------
    // Default responseBody parameter (null)
    // ------------------------------------------------------------------

    [Fact]
    public void NotFoundException_WithoutResponseBody_HasNullResponseBody()
    {
        var ex = new NotFoundException("not found");

        Assert.Equal(404, ex.StatusCode);
        Assert.Null(ex.ResponseBody);
    }

    [Fact]
    public void ConflictException_WithoutResponseBody_HasNullResponseBody()
    {
        var ex = new ConflictException("conflict");

        Assert.Equal(409, ex.StatusCode);
        Assert.Null(ex.ResponseBody);
    }

    [Fact]
    public void ValidationException_WithoutResponseBody_HasNullResponseBody()
    {
        var ex = new ValidationException("validation failed");

        Assert.Equal(422, ex.StatusCode);
        Assert.Null(ex.ResponseBody);
    }

    // ------------------------------------------------------------------
    // SmplkitException with all null optional params
    // ------------------------------------------------------------------

    [Fact]
    public void SmplkitException_MessageOnly_HasNullOptionalProps()
    {
        var ex = new SmplkitException("simple error");

        Assert.Equal("simple error", ex.Message);
        Assert.Null(ex.StatusCode);
        Assert.Null(ex.ResponseBody);
        Assert.Null(ex.InnerException);
    }

    // ------------------------------------------------------------------
    // ConnectionException properties
    // ------------------------------------------------------------------

    [Fact]
    public void ConnectionException_HasNullResponseBody()
    {
        var ex = new ConnectionException("failed");

        Assert.Null(ex.ResponseBody);
        Assert.Null(ex.StatusCode);
    }

    // ------------------------------------------------------------------
    // errors.Is / errors.As compatibility
    // ------------------------------------------------------------------

    [Fact]
    public void NotFoundException_CanBeCaughtAsSmplkitException()
    {
        Exception ex = new NotFoundException("not found", "body");
        Assert.True(ex is SmplkitException);
        var smpl = (SmplkitException)ex;
        Assert.Equal(404, smpl.StatusCode);
        Assert.Equal("body", smpl.ResponseBody);
    }

    [Fact]
    public void ConflictException_CanBeCaughtAsSmplkitException()
    {
        Exception ex = new ConflictException("conflict", "body");
        Assert.True(ex is SmplkitException);
        var smpl = (SmplkitException)ex;
        Assert.Equal(409, smpl.StatusCode);
    }

    [Fact]
    public void ValidationException_CanBeCaughtAsSmplkitException()
    {
        Exception ex = new ValidationException("validation", "body");
        Assert.True(ex is SmplkitException);
        var smpl = (SmplkitException)ex;
        Assert.Equal(422, smpl.StatusCode);
    }

    [Fact]
    public void TimeoutException_CanBeCaughtAsSmplkitException()
    {
        Exception ex = new Smplkit.Errors.TimeoutException("timeout");
        Assert.True(ex is SmplkitException);
        var smpl = (SmplkitException)ex;
        Assert.Null(smpl.StatusCode);
    }

    [Fact]
    public void ConnectionException_CanBeCaughtAsSmplkitException()
    {
        Exception ex = new ConnectionException("conn");
        Assert.True(ex is SmplkitException);
    }


    // ------------------------------------------------------------------
    // SmplkitException with all four params
    // ------------------------------------------------------------------

    [Fact]
    public void SmplkitException_AllFourParams()
    {
        var inner = new InvalidOperationException("inner");
        var ex = new SmplkitException("msg", statusCode: 503, responseBody: "body", innerException: inner);

        Assert.Equal("msg", ex.Message);
        Assert.Equal(503, ex.StatusCode);
        Assert.Equal("body", ex.ResponseBody);
        Assert.Same(inner, ex.InnerException);
    }

    // ------------------------------------------------------------------
    // ConnectionException without inner (default param)
    // ------------------------------------------------------------------

    [Fact]
    public void ConnectionException_WithoutInner_HasNullInnerException()
    {
        var ex = new ConnectionException("failed");
        Assert.Null(ex.InnerException);
    }

    // ------------------------------------------------------------------
    // TimeoutException without inner (default param)
    // ------------------------------------------------------------------

    [Fact]
    public void TimeoutException_WithoutInner_HasNullInnerException()
    {
        var ex = new Smplkit.Errors.TimeoutException("timed out");
        Assert.Null(ex.InnerException);
    }
}
