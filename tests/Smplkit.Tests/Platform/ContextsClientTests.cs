using System.Net;
using System.Text;
using Smplkit;
using Smplkit.Errors;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Platform;

public class ContextsClientTests
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

    private const string SingleContextJson = """
        {
            "data": {
                "id": "user:u-1",
                "type": "context",
                "attributes": {
                    "name": "Alice",
                    "attributes": { "plan": "enterprise", "beta": true },
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;

    private const string ContextListJson = """
        {
            "data": [
                {
                    "id": "user:u-1",
                    "type": "context",
                    "attributes": { "name": "Alice", "attributes": {"plan": "enterprise"} }
                },
                {
                    "id": "user:u-2",
                    "type": "context",
                    "attributes": { "attributes": "not-an-object" }
                }
            ]
        }
        """;

    [Fact]
    public async Task RegisterAsync_Single_NoFlush_QueuesAndReturns()
    {
        var calls = 0;
        var (mgmt, _) = Make(_ => { calls++; return Task.FromResult(Json("{}")); });
        var ctx = new Context("user", "u-1", new() { ["plan"] = "enterprise" });
        await mgmt.Platform.Contexts.RegisterAsync(ctx);
        Assert.Equal(0, calls);
        Assert.Equal(1, mgmt.Platform.Contexts.PendingCount);
    }

    [Fact]
    public async Task RegisterAsync_Multiple_NoFlush()
    {
        var calls = 0;
        var (mgmt, _) = Make(_ => { calls++; return Task.FromResult(Json("{}")); });
        await mgmt.Platform.Contexts.RegisterAsync(new[]
        {
            new Context("user", "u-1"),
            new Context("user", "u-2"),
        });
        Assert.Equal(0, calls);
        Assert.Equal(2, mgmt.Platform.Contexts.PendingCount);
    }

    [Fact]
    public async Task RegisterAsync_Flush_True_SendsImmediately()
    {
        HttpRequestMessage? captured = null;
        var (mgmt, _) = Make(req =>
        {
            captured = req;
            return Task.FromResult(Json("{}"));
        });
        await mgmt.Platform.Contexts.RegisterAsync(new Context("user", "u-1"), flush: true);
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured!.Method);
    }

    [Fact]
    public async Task FlushAsync_NoPending_NoCall()
    {
        var calls = 0;
        var (mgmt, _) = Make(_ => { calls++; return Task.FromResult(Json("{}")); });
        await mgmt.Platform.Contexts.FlushAsync();
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task FlushAsync_DrainsPending()
    {
        HttpRequestMessage? captured = null;
        var (mgmt, _) = Make(req =>
        {
            captured = req;
            return Task.FromResult(Json("{}"));
        });
        await mgmt.Platform.Contexts.RegisterAsync(new Context("user", "u-1"));
        Assert.Equal(1, mgmt.Platform.Contexts.PendingCount);
        await mgmt.Platform.Contexts.FlushAsync();
        Assert.Equal(0, mgmt.Platform.Contexts.PendingCount);
        Assert.NotNull(captured);
    }

    [Fact]
    public async Task ListAsync_ParsesContexts()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json(ContextListJson)));
        var contexts = await mgmt.Platform.Contexts.ListAsync("user");
        Assert.Equal(2, contexts.Count);
        Assert.Equal("user:u-1", contexts[0].Id);
        Assert.Equal("Alice", contexts[0].Name);
        Assert.Equal("enterprise", contexts[0].Attributes["plan"]);
        // Second context has non-object attributes — should parse to empty dict
        Assert.Empty(contexts[1].Attributes);
    }

    [Fact]
    public async Task GetAsync_Composite()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json(SingleContextJson)));
        var ctx = await mgmt.Platform.Contexts.GetAsync("user:u-1");
        Assert.Equal("user", ctx.Type);
        Assert.Equal("u-1", ctx.Key);
        Assert.Equal("Alice", ctx.Name);
        Assert.NotNull(ctx.CreatedAt);
    }

    [Fact]
    public async Task GetAsync_TypeKey()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json(SingleContextJson)));
        var ctx = await mgmt.Platform.Contexts.GetAsync("user", "u-1");
        Assert.Equal("user", ctx.Type);
        Assert.Equal("u-1", ctx.Key);
    }

    [Fact]
    public async Task DeleteAsync_Composite_SendsDelete()
    {
        HttpRequestMessage? captured = null;
        var (mgmt, _) = Make(req =>
        {
            captured = req;
            return Task.FromResult(Json("{}", HttpStatusCode.NoContent));
        });
        await mgmt.Platform.Contexts.DeleteAsync("user:u-1");
        Assert.Equal(HttpMethod.Delete, captured!.Method);
    }

    [Fact]
    public async Task DeleteAsync_TypeKey_SendsDelete()
    {
        HttpRequestMessage? captured = null;
        var (mgmt, _) = Make(req =>
        {
            captured = req;
            return Task.FromResult(Json("{}", HttpStatusCode.NoContent));
        });
        await mgmt.Platform.Contexts.DeleteAsync("user", "u-2");
        Assert.Equal(HttpMethod.Delete, captured!.Method);
    }

    [Fact]
    public async Task ContextSaveAsync_RoundTripsViaIContextSink()
    {
        var requests = new List<HttpRequestMessage>();
        var (mgmt, _) = Make(req =>
        {
            requests.Add(req);
            return Task.FromResult(Json(SingleContextJson));
        });

        var ctx = await mgmt.Platform.Contexts.GetAsync("user:u-1");
        ctx.Attributes["plan"] = "starter";
        await ctx.SaveAsync();

        // Save invokes flush (POST) + GET to refresh state
        Assert.True(requests.Count >= 2);
    }

    [Fact]
    public async Task ContextDeleteAsync_RoundTripsViaIContextSink()
    {
        HttpRequestMessage? lastDelete = null;
        var (mgmt, _) = Make(req =>
        {
            if (req.Method == HttpMethod.Delete) lastDelete = req;
            return Task.FromResult(Json(SingleContextJson));
        });
        var ctx = await mgmt.Platform.Contexts.GetAsync("user:u-1");
        await ctx.DeleteAsync();
        Assert.NotNull(lastDelete);
    }
}
