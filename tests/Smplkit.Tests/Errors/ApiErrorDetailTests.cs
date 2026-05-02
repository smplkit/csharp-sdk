using Smplkit.Errors;
using Xunit;

namespace Smplkit.Tests.Errors;

public class ApiErrorDetailTests
{
    [Fact]
    public void ToJsonString_AllFields()
    {
        var detail = new ApiErrorDetail("400", "Title", "Detail", new ApiErrorSource("/data/0/key"));
        var json = detail.ToJsonString();
        Assert.Contains("\"status\":\"400\"", json);
        Assert.Contains("\"title\":\"Title\"", json);
        Assert.Contains("\"detail\":\"Detail\"", json);
        Assert.Contains("\"pointer\":\"/data/0/key\"", json);
    }

    [Fact]
    public void ToJsonString_AllNull_ReturnsEmptyObject()
    {
        var detail = new ApiErrorDetail(null, null, null, null);
        Assert.Equal("{}", detail.ToJsonString());
    }

    [Fact]
    public void ToJsonString_NoSourcePointer_OmitsSource()
    {
        var detail = new ApiErrorDetail("400", null, null, new ApiErrorSource(null));
        var json = detail.ToJsonString();
        Assert.Contains("\"status\":\"400\"", json);
        Assert.DoesNotContain("source", json);
    }

    [Fact]
    public void Constructor_RoundTripsAllFields()
    {
        var src = new ApiErrorSource("/foo");
        var detail = new ApiErrorDetail("S", "T", "D", src);
        Assert.Equal("S", detail.Status);
        Assert.Equal("T", detail.Title);
        Assert.Equal("D", detail.Detail);
        Assert.Same(src, detail.Source);
    }

    [Fact]
    public void ApiErrorSource_RoundTripsPointer()
    {
        var src = new ApiErrorSource("/bar");
        Assert.Equal("/bar", src.Pointer);
    }
}

public class SmplkitExceptionToStringTests
{
    [Fact]
    public void ToString_NoErrors_DelegatesToBase()
    {
        var ex = new SmplkitException("plain");
        var str = ex.ToString();
        Assert.Contains("plain", str);
    }

    [Fact]
    public void ToString_OneError_IncludesError()
    {
        var detail = new ApiErrorDetail("400", "Bad", "details here", null);
        var ex = new SmplkitException("validation", errors: new[] { detail });
        var str = ex.ToString();
        Assert.Contains("validation", str);
        Assert.Contains("Bad", str);
        Assert.Contains("details here", str);
    }

    [Fact]
    public void ToString_MultipleErrors_IncludesAll()
    {
        var d1 = new ApiErrorDetail("400", "First", null, null);
        var d2 = new ApiErrorDetail("400", "Second", null, null);
        var ex = new SmplkitException("multi", errors: new[] { d1, d2 });
        var str = ex.ToString();
        Assert.Contains("First", str);
        Assert.Contains("Second", str);
        Assert.Contains("Errors:", str);
    }

    [Fact]
    public void Errors_DefaultsToEmpty()
    {
        var ex = new SmplkitException("hi");
        Assert.NotNull(ex.Errors);
        Assert.Empty(ex.Errors);
    }

    [Fact]
    public void DeriveMessage_NoErrors_UsesStatusCode()
    {
        var ex = new SmplkitException(
            SmplkitException.DeriveMessage(Array.Empty<ApiErrorDetail>(), 503));
        Assert.Equal("HTTP 503", ex.Message);
    }

    [Fact]
    public void DeriveMessage_OneError_UsesDetail()
    {
        var d = new ApiErrorDetail(null, null, "Server died", null);
        var msg = SmplkitException.DeriveMessage(new[] { d }, 500);
        Assert.Equal("Server died", msg);
    }

    [Fact]
    public void DeriveMessage_OneError_FallsBackToTitle()
    {
        var d = new ApiErrorDetail(null, "Oops", null, null);
        var msg = SmplkitException.DeriveMessage(new[] { d }, 500);
        Assert.Equal("Oops", msg);
    }

    [Fact]
    public void DeriveMessage_OneError_FallsBackToStatus()
    {
        var d = new ApiErrorDetail("418", null, null, null);
        var msg = SmplkitException.DeriveMessage(new[] { d }, 418);
        Assert.Equal("418", msg);
    }

    [Fact]
    public void DeriveMessage_OneError_AllNull_UsesGeneric()
    {
        var d = new ApiErrorDetail(null, null, null, null);
        var msg = SmplkitException.DeriveMessage(new[] { d }, 500);
        Assert.Equal("An API error occurred", msg);
    }

    [Fact]
    public void DeriveMessage_TwoErrors_AppendsCount()
    {
        var d1 = new ApiErrorDetail(null, null, "first", null);
        var d2 = new ApiErrorDetail(null, null, "second", null);
        var msg = SmplkitException.DeriveMessage(new[] { d1, d2 }, 500);
        Assert.Equal("first (and 1 more error)", msg);
    }

    [Fact]
    public void DeriveMessage_ThreeErrors_AppendsPluralCount()
    {
        var d1 = new ApiErrorDetail(null, null, "first", null);
        var d2 = new ApiErrorDetail(null, null, "second", null);
        var d3 = new ApiErrorDetail(null, null, "third", null);
        var msg = SmplkitException.DeriveMessage(new[] { d1, d2, d3 }, 500);
        Assert.Equal("first (and 2 more errors)", msg);
    }
}
