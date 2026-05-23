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
    public void ErrorsArray_ExtractsCodeAndMeta()
    {
        // Regression: ParseErrors was dropping the JSON:API `code` and
        // `meta` fields, so callers couldn't branch on machine-readable
        // codes like `environment_unmanaged` (added by the new
        // product-service env-validation work) without string-matching
        // the human `detail`.
        var body = """
            {"errors":[{
                "status":"400",
                "code":"environment_unmanaged",
                "title":"Environment is unmanaged",
                "detail":"Promote it first.",
                "meta":{"environment":"staging","count":2,"is_default":false,"ratio":0.5}
            }]}
            """;
        var ex = Invoke(400, body);
        var detail = ex.Errors[0];
        Assert.Equal("environment_unmanaged", detail.Code);
        Assert.NotNull(detail.Meta);
        Assert.Equal("staging", detail.Meta!["environment"]);
        Assert.Equal(2L, detail.Meta["count"]);
        Assert.Equal(false, detail.Meta["is_default"]);
        Assert.Equal(0.5, detail.Meta["ratio"]);
        // ToJsonString round-trips both new fields.
        var json = detail.ToJsonString();
        Assert.Contains("\"code\":\"environment_unmanaged\"", json);
        Assert.Contains("\"meta\":", json);
    }

    [Fact]
    public void ErrorsArray_MetaCoversAllJsonValueKinds()
    {
        // Lock in coverage for every branch of JsonElementToObject —
        // string, long, double, true, false, null, array, nested object.
        var body = """
            {"errors":[{
                "detail":"d",
                "meta":{
                    "s":"abc",
                    "i":42,
                    "f":3.14,
                    "t":true,
                    "f2":false,
                    "n":null,
                    "arr":[1, "two", true],
                    "obj":{"nested":"yes"}
                }
            }]}
            """;
        var ex = Invoke(400, body);
        var meta = ex.Errors[0].Meta!;
        Assert.Equal("abc", meta["s"]);
        Assert.Equal(42L, meta["i"]);
        Assert.Equal(3.14, meta["f"]);
        Assert.Equal(true, meta["t"]);
        Assert.Equal(false, meta["f2"]);
        Assert.Null(meta["n"]);
        Assert.IsType<List<object?>>(meta["arr"]);
        Assert.IsType<Dictionary<string, object?>>(meta["obj"]);
        var nested = (Dictionary<string, object?>)meta["obj"]!;
        Assert.Equal("yes", nested["nested"]);
    }

    [Fact]
    public void JsonElementToObject_UndefinedKind_ReturnsNull()
    {
        // Reach the default arm of JsonElementToObject. Normally
        // unreachable through public meta-parsing (every parsed JSON value
        // has a definite kind), but a default(JsonElement) has Undefined
        // and could in principle land in the meta dict from an exotic
        // caller path; the fallback returns null instead of throwing.
        var method = typeof(Smplkit.Errors.ApiErrorParser).GetMethod(
            "JsonElementToObject",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var result = method.Invoke(null, new object?[] { default(System.Text.Json.JsonElement) });
        Assert.Null(result);
    }

    [Fact]
    public void ErrorsArray_NonObjectMeta_HasNullMeta()
    {
        // Malformed meta (e.g. a string where the spec expects an object)
        // is ignored rather than blowing up.
        var body = """{"errors":[{"detail":"d","meta":"oops"}]}""";
        var ex = Invoke(500, body);
        Assert.Single(ex.Errors);
        Assert.Null(ex.Errors[0].Meta);
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
