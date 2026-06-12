using System.Net;
using System.Text;
using Smplkit;
using Smplkit.Errors;
using Smplkit.Platform;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Platform;

public class ContextTypesClientTests
{
    private static (SmplClient mgmt, MockHttpMessageHandler handler) Make(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        var handler = new MockHttpMessageHandler(respond);
        var http = new HttpClient(handler);
        var mgmt = new SmplClient(TestData.DefaultOptions(), http);
        return (mgmt, handler);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/vnd.api+json") };

    private const string SingleTypeJson = """
        {
            "data": {
                "id": "user",
                "type": "context_type",
                "attributes": {
                    "name": "User",
                    "attributes": {
                        "plan": {"label": "Plan", "data_type": "string"},
                        "beta_tester": {"label": "Beta tester"}
                    },
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;

    private const string TypeListJson = """
        {
            "data": [
                {
                    "id": "user",
                    "type": "context_type",
                    "attributes": { "name": "User" }
                },
                {
                    "id": "account",
                    "type": "context_type",
                    "attributes": { "name": "Account", "attributes": "not-an-object" }
                }
            ]
        }
        """;

    [Fact]
    public void New_DefaultsNameToId()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var ct = mgmt.Platform.ContextTypes.New("user");
        Assert.Equal("user", ct.Id);
        Assert.Equal("user", ct.Name);
    }

    [Fact]
    public void New_ExplicitName()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var ct = mgmt.Platform.ContextTypes.New("user", name: "End User");
        Assert.Equal("End User", ct.Name);
    }

    [Fact]
    public void New_WithAttributes()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var ct = mgmt.Platform.ContextTypes.New("user", attributes: new()
        {
            ["plan"] = new() { ["label"] = "Plan" },
        });
        Assert.True(ct.Attributes.ContainsKey("plan"));
    }

    [Fact]
    public async Task GetAsync_ParsesAttributes()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json(SingleTypeJson)));
        var ct = await mgmt.Platform.ContextTypes.GetAsync("user");
        Assert.Equal("user", ct.Id);
        Assert.Equal("User", ct.Name);
        Assert.Equal(2, ct.Attributes.Count);
        Assert.True(ct.Attributes.ContainsKey("plan"));
    }

    [Fact]
    public async Task ListAsync_HandlesNonObjectAttributes()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json(TypeListJson)));
        var list = await mgmt.Platform.ContextTypes.ListAsync();
        Assert.Equal(2, list.Count);
        // The "not-an-object" attributes value should be parsed to empty dict
        Assert.Empty(list[1].Attributes);
    }

    [Fact]
    public async Task DeleteAsync_SendsDelete()
    {
        HttpRequestMessage? captured = null;
        var (mgmt, _) = Make(req =>
        {
            captured = req;
            return Task.FromResult(Json("{}", HttpStatusCode.NoContent));
        });
        await mgmt.Platform.ContextTypes.DeleteAsync("user");
        Assert.Equal(HttpMethod.Delete, captured!.Method);
    }

    [Fact]
    public void ContextType_AddRemoveUpdateAttribute()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var ct = mgmt.Platform.ContextTypes.New("user");

        ct.AddAttribute("plan", new Dictionary<string, object?> { ["label"] = "Plan" });
        Assert.Single(ct.Attributes);
        Assert.Equal("Plan", ct.Attributes["plan"]["label"]);

        ct.UpdateAttribute("plan", new Dictionary<string, object?> { ["label"] = "Subscription Plan" });
        Assert.Equal("Subscription Plan", ct.Attributes["plan"]["label"]);

        ct.RemoveAttribute("plan");
        Assert.Empty(ct.Attributes);

        // Add with null metadata
        ct.AddAttribute("foo");
        Assert.Empty(ct.Attributes["foo"]);

        // Update with null metadata
        ct.UpdateAttribute("foo");
        Assert.Empty(ct.Attributes["foo"]);
    }

    [Fact]
    public async Task SaveAsync_NewType_SendsPost()
    {
        HttpRequestMessage? captured = null;
        var (mgmt, _) = Make(req =>
        {
            captured = req;
            return Task.FromResult(Json(SingleTypeJson, HttpStatusCode.Created));
        });
        var ct = mgmt.Platform.ContextTypes.New("user");
        ct.AddAttribute("plan");
        await ct.SaveAsync();
        Assert.Equal(HttpMethod.Post, captured!.Method);
    }

    [Fact]
    public async Task SaveAsync_ExistingType_SendsPut()
    {
        HttpRequestMessage? captured = null;
        var (mgmt, _) = Make(req =>
        {
            if (req.Method == HttpMethod.Get) return Task.FromResult(Json(SingleTypeJson));
            captured = req;
            return Task.FromResult(Json(SingleTypeJson));
        });
        var ct = await mgmt.Platform.ContextTypes.GetAsync("user");
        ct.Name = "Renamed";
        await ct.SaveAsync();
        Assert.Equal(HttpMethod.Put, captured!.Method);
    }

    [Fact]
    public async Task DeleteAsync_Method_OnSaved()
    {
        HttpRequestMessage? captured = null;
        var (mgmt, _) = Make(req =>
        {
            if (req.Method == HttpMethod.Get) return Task.FromResult(Json(SingleTypeJson));
            captured = req;
            return Task.FromResult(Json("{}", HttpStatusCode.NoContent));
        });
        var ct = await mgmt.Platform.ContextTypes.GetAsync("user");
        await ct.DeleteAsync();
        Assert.Equal(HttpMethod.Delete, captured!.Method);
    }

    [Fact]
    public async Task DeleteAsync_OnUnsaved_Throws()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var ct = mgmt.Platform.ContextTypes.New("user");
        ct.Id = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ct.DeleteAsync());
    }

    [Fact]
    public void ContextType_ToString_IncludesIdAndName()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var ct = mgmt.Platform.ContextTypes.New("user", name: "User");
        var s = ct.ToString();
        Assert.Contains("user", s);
        Assert.Contains("User", s);
    }
}
