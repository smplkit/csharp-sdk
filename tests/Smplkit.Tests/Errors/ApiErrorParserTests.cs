using System.Reflection;
using Smplkit.Errors;
using Xunit;

namespace Smplkit.Tests.Errors;

/// <summary>
/// Tests for <see cref="ApiErrorParser"/> — the JSON:API error body parser.
/// </summary>
public class ApiErrorParserTests
{
    private static SmplkitException Invoke(int statusCode, string body)
    {
        var type = typeof(SmplkitException).Assembly.GetType("Smplkit.Errors.ApiErrorParser")!;
        var method = type.GetMethod("CreateException",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        return (SmplkitException)method.Invoke(null, new object[] { statusCode, body })!;
    }

    [Fact]
    public void Status402_MapsToPaymentRequired()
    {
        var ex = Invoke(402, "{}");
        Assert.IsType<PaymentRequiredException>(ex);
        Assert.Equal(402, ex.StatusCode);
    }

    [Fact]
    public void Status404_MapsToNotFound()
    {
        var ex = Invoke(404, "{}");
        Assert.IsType<NotFoundException>(ex);
        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public void Status409_MapsToConflict()
    {
        var ex = Invoke(409, "{}");
        Assert.IsType<ConflictException>(ex);
        Assert.Equal(409, ex.StatusCode);
    }

    [Fact]
    public void Status400_MapsToValidation()
    {
        var ex = Invoke(400, "{}");
        Assert.IsType<ValidationException>(ex);
        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public void Status422_MapsToValidation()
    {
        var ex = Invoke(422, "{}");
        Assert.IsType<ValidationException>(ex);
        Assert.Equal(422, ex.StatusCode);
    }

    [Fact]
    public void Status500_MapsToBaseException()
    {
        var ex = Invoke(500, "{}");
        Assert.IsType<SmplkitException>(ex);
        Assert.Equal(500, ex.StatusCode);
    }

    [Fact]
    public void EmptyBody_HasEmptyErrors()
    {
        var ex = Invoke(500, "");
        Assert.Empty(ex.Errors);
    }

    [Fact]
    public void WhitespaceBody_HasEmptyErrors()
    {
        var ex = Invoke(500, "   ");
        Assert.Empty(ex.Errors);
    }

    [Fact]
    public void NonJsonBody_HasEmptyErrors()
    {
        var ex = Invoke(500, "not json at all");
        Assert.Empty(ex.Errors);
    }

    [Fact]
    public void NoErrorsKey_HasEmptyErrors()
    {
        var ex = Invoke(500, "{\"data\":{}}");
        Assert.Empty(ex.Errors);
    }

    [Fact]
    public void ErrorsNotArray_HasEmptyErrors()
    {
        var ex = Invoke(500, "{\"errors\":\"oops\"}");
        Assert.Empty(ex.Errors);
    }

    [Fact]
    public void ErrorsArray_ParsesAllFields()
    {
        var body = """
            {"errors":[{
                "status":"400",
                "title":"Bad Request",
                "detail":"key is required",
                "source":{"pointer":"/data/key"}
            }]}
            """;
        var ex = Invoke(400, body);
        Assert.Single(ex.Errors);
        var detail = ex.Errors[0];
        Assert.Equal("400", detail.Status);
        Assert.Equal("Bad Request", detail.Title);
        Assert.Equal("key is required", detail.Detail);
        Assert.NotNull(detail.Source);
        Assert.Equal("/data/key", detail.Source!.Pointer);
    }

    [Fact]
    public void ErrorsArray_PartialFields_ParsesAvailableOnes()
    {
        var body = """{"errors":[{"detail":"only detail"}]}""";
        var ex = Invoke(500, body);
        Assert.Single(ex.Errors);
        Assert.Equal("only detail", ex.Errors[0].Detail);
        Assert.Null(ex.Errors[0].Status);
        Assert.Null(ex.Errors[0].Title);
        Assert.Null(ex.Errors[0].Source);
    }

    [Fact]
    public void ErrorsArray_SourceObjectWithoutPointer_HasNullSource()
    {
        var body = """{"errors":[{"detail":"d","source":{}}]}""";
        var ex = Invoke(500, body);
        Assert.Single(ex.Errors);
        Assert.Null(ex.Errors[0].Source);
    }

    [Fact]
    public void ErrorsArray_NonObjectSource_HasNullSource()
    {
        var body = """{"errors":[{"detail":"d","source":"oops"}]}""";
        var ex = Invoke(500, body);
        Assert.Single(ex.Errors);
        Assert.Null(ex.Errors[0].Source);
    }

    [Fact]
    public void NonStringFieldsIgnored()
    {
        var body = """{"errors":[{"status":123,"title":null,"detail":"d"}]}""";
        var ex = Invoke(500, body);
        Assert.Single(ex.Errors);
        Assert.Null(ex.Errors[0].Status);
        Assert.Null(ex.Errors[0].Title);
        Assert.Equal("d", ex.Errors[0].Detail);
    }

    [Fact]
    public void Message_DerivedFromFirstError()
    {
        var body = """{"errors":[{"detail":"derived msg"}]}""";
        var ex = Invoke(400, body);
        Assert.Equal("derived msg", ex.Message);
    }

    [Fact]
    public void Message_NoErrors_FallsBackToHttpStatus()
    {
        var ex = Invoke(503, "");
        Assert.Equal("HTTP 503", ex.Message);
    }
}
