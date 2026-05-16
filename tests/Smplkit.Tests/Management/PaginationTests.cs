using System.Net;
using System.Text;
using Smplkit;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Management;

/// <summary>
/// Verifies that the handwritten management <c>ListAsync</c> wrappers forward
/// the optional <c>pageNumber</c> / <c>pageSize</c> parameters through to the
/// generated client, producing the expected JSON:API <c>page[number]</c> and
/// <c>page[size]</c> query parameters. The wrappers should NOT loop — that's
/// the customer's job at this surface.
/// </summary>
public class PaginationTests
{
    private static (SmplManagementClient mgmt, MockHttpMessageHandler handler) Make(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        var handler = new MockHttpMessageHandler(respond);
        var httpClient = new HttpClient(handler);
        var mgmt = new SmplManagementClient(
            new SmplClientOptions { ApiKey = "sk_test_key" },
            httpClient);
        return (mgmt, handler);
    }

    private static HttpResponseMessage EmptyData() =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"data":[]}""", Encoding.UTF8, "application/vnd.api+json"),
        };

    private static void AssertHasPaging(HttpRequestMessage? req, int page, int size)
    {
        Assert.NotNull(req);
        var url = req!.RequestUri!.ToString();
        Assert.Contains($"page%5Bnumber%5D={page}", url);
        Assert.Contains($"page%5Bsize%5D={size}", url);
    }

    private static void AssertNoPaging(HttpRequestMessage? req)
    {
        Assert.NotNull(req);
        var url = req!.RequestUri!.ToString();
        Assert.DoesNotContain("page%5Bnumber%5D", url);
        Assert.DoesNotContain("page%5Bsize%5D", url);
    }

    [Fact]
    public async Task Configs_ListAsync_DefaultsOmitPagingParams()
    {
        HttpRequestMessage? captured = null;
        var (mgmt, _) = Make(req => { captured = req; return Task.FromResult(EmptyData()); });
        await mgmt.Config.ListAsync();
        AssertNoPaging(captured);
    }

    [Fact]
    public async Task Configs_ListAsync_ForwardsPaging()
    {
        HttpRequestMessage? captured = null;
        var (mgmt, _) = Make(req => { captured = req; return Task.FromResult(EmptyData()); });
        await mgmt.Config.ListAsync(pageNumber: 2, pageSize: 50);
        AssertHasPaging(captured, 2, 50);
    }

    [Fact]
    public async Task Flags_ListAsync_ForwardsPaging()
    {
        HttpRequestMessage? captured = null;
        var (mgmt, _) = Make(req => { captured = req; return Task.FromResult(EmptyData()); });
        await mgmt.Flags.ListAsync(pageNumber: 3, pageSize: 25);
        AssertHasPaging(captured, 3, 25);
    }

    [Fact]
    public async Task Loggers_ListAsync_ForwardsPaging()
    {
        HttpRequestMessage? captured = null;
        var (mgmt, _) = Make(req => { captured = req; return Task.FromResult(EmptyData()); });
        await mgmt.Loggers.ListAsync(pageNumber: 4, pageSize: 10);
        AssertHasPaging(captured, 4, 10);
    }

    [Fact]
    public async Task LogGroups_ListAsync_ForwardsPaging()
    {
        HttpRequestMessage? captured = null;
        var (mgmt, _) = Make(req => { captured = req; return Task.FromResult(EmptyData()); });
        await mgmt.LogGroups.ListAsync(pageNumber: 5, pageSize: 7);
        AssertHasPaging(captured, 5, 7);
    }

    [Fact]
    public async Task Environments_ListAsync_ForwardsPaging()
    {
        HttpRequestMessage? captured = null;
        var (mgmt, _) = Make(req => { captured = req; return Task.FromResult(EmptyData()); });
        await mgmt.Environments.ListAsync(pageNumber: 6, pageSize: 5);
        AssertHasPaging(captured, 6, 5);
    }

    [Fact]
    public async Task ContextTypes_ListAsync_ForwardsPaging()
    {
        HttpRequestMessage? captured = null;
        var (mgmt, _) = Make(req => { captured = req; return Task.FromResult(EmptyData()); });
        await mgmt.ContextTypes.ListAsync(pageNumber: 7, pageSize: 3);
        AssertHasPaging(captured, 7, 3);
    }

    [Fact]
    public async Task Contexts_ListAsync_ForwardsPaging()
    {
        HttpRequestMessage? captured = null;
        var (mgmt, _) = Make(req => { captured = req; return Task.FromResult(EmptyData()); });
        await mgmt.Contexts.ListAsync(type: "user", pageNumber: 2, pageSize: 11);
        AssertHasPaging(captured, 2, 11);
        Assert.Contains("filter%5Bcontext_type%5D=user", captured!.RequestUri!.ToString());
    }
}
