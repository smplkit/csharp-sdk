using System.Net;
using System.Text;
using Smplkit;
using Smplkit.Errors;
using Smplkit.Platform;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Platform;

public class ServicesClientTests
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

    private const string SingleSvcJson = """
        {
            "data": {
                "id": "user_service",
                "type": "service",
                "attributes": {
                    "name": "User Service",
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;

    private const string SvcListJson = """
        {
            "data": [
                {
                    "id": "user_service",
                    "type": "service",
                    "attributes": {
                        "name": "User Service",
                        "created_at": "2024-01-15T10:30:00Z",
                        "updated_at": "2024-01-15T10:30:00Z"
                    }
                },
                {
                    "id": "billing",
                    "type": "service",
                    "attributes": {
                        "name": "Billing"
                    }
                }
            ]
        }
        """;

    [Fact]
    public void New_CreatesUnsavedService()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var svc = mgmt.Platform.Services.New("user_service", "User Service");
        Assert.Equal("user_service", svc.Id);
        Assert.Equal("User Service", svc.Name);
        Assert.Null(svc.CreatedAt);
    }

    [Fact]
    public async Task ListAsync_ParsesAll()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json(SvcListJson)));
        var svcs = await mgmt.Platform.Services.ListAsync();
        Assert.Equal(2, svcs.Count);
        Assert.Equal("user_service", svcs[0].Id);
        Assert.Equal("User Service", svcs[0].Name);
        Assert.Equal("billing", svcs[1].Id);
        Assert.Equal("Billing", svcs[1].Name);
    }

    [Fact]
    public async Task ListAsync_WithPagination_PassesParams()
    {
        HttpRequestMessage? captured = null;
        var (mgmt, _) = Make(req =>
        {
            captured = req;
            return Task.FromResult(Json("""{"data":[]}"""));
        });
        await mgmt.Platform.Services.ListAsync(pageNumber: 2, pageSize: 50);
        var url = captured!.RequestUri!.ToString();
        Assert.Contains("page%5Bnumber%5D=2", url);
        Assert.Contains("page%5Bsize%5D=50", url);
    }

    [Fact]
    public async Task GetAsync_ParsesResponse()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json(SingleSvcJson)));
        var svc = await mgmt.Platform.Services.GetAsync("user_service");
        Assert.Equal("user_service", svc.Id);
        Assert.Equal("User Service", svc.Name);
        Assert.NotNull(svc.CreatedAt);
        Assert.NotNull(svc.UpdatedAt);
    }

    [Fact]
    public async Task GetAsync_NotFound_Throws()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json(
            """{"errors":[{"detail":"not found"}]}""",
            HttpStatusCode.NotFound)));
        await Assert.ThrowsAsync<NotFoundException>(() => mgmt.Platform.Services.GetAsync("missing"));
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
        await mgmt.Platform.Services.DeleteAsync("user_service");
        Assert.Equal(HttpMethod.Delete, captured!.Method);
        Assert.Contains("user_service", captured.RequestUri!.ToString());
    }

    [Fact]
    public async Task SaveAsync_NewService_SendsPost()
    {
        HttpRequestMessage? captured = null;
        var (mgmt, _) = Make(req =>
        {
            captured = req;
            return Task.FromResult(Json(SingleSvcJson, HttpStatusCode.Created));
        });
        var svc = mgmt.Platform.Services.New("user_service", "User Service");
        await svc.SaveAsync();
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.NotNull(svc.CreatedAt);
    }

    [Fact]
    public async Task SaveAsync_NewService_DuplicateConflict_Throws()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json(
            """{"errors":[{"status":"409","title":"Conflict","detail":"service exists"}]}""",
            HttpStatusCode.Conflict)));
        var svc = mgmt.Platform.Services.New("user_service", "User Service");
        await Assert.ThrowsAsync<ConflictException>(() => svc.SaveAsync());
    }

    [Fact]
    public async Task SaveAsync_ExistingService_SendsPut()
    {
        HttpRequestMessage? captured = null;
        var (mgmt, _) = Make(req =>
        {
            // First request (GET) returns the svc so CreatedAt is non-null
            if (req.Method == HttpMethod.Get) return Task.FromResult(Json(SingleSvcJson));
            captured = req;
            return Task.FromResult(Json(SingleSvcJson));
        });
        var svc = await mgmt.Platform.Services.GetAsync("user_service");
        svc.Name = "User Service v2";
        await svc.SaveAsync();
        Assert.Equal(HttpMethod.Put, captured!.Method);
    }

    [Fact]
    public async Task DeleteAsync_OnUnsavedService_Throws()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var svc = mgmt.Platform.Services.New("nope", "Nope");
        svc.Id = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.DeleteAsync());
    }

    [Fact]
    public async Task SaveAsync_OnUnsavedWithoutId_Throws()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var svc = mgmt.Platform.Services.New("placeholder", "Placeholder");
        svc.Id = null;
        await Assert.ThrowsAsync<ValidationException>(() => svc.SaveAsync());
    }

    [Fact]
    public async Task DeleteAsync_OnSavedService_SendsDelete()
    {
        HttpRequestMessage? captured = null;
        var (mgmt, _) = Make(req =>
        {
            if (req.Method == HttpMethod.Get) return Task.FromResult(Json(SingleSvcJson));
            captured = req;
            return Task.FromResult(Json("{}", HttpStatusCode.NoContent));
        });
        var svc = await mgmt.Platform.Services.GetAsync("user_service");
        await svc.DeleteAsync();
        Assert.Equal(HttpMethod.Delete, captured!.Method);
    }

    [Fact]
    public void Service_ToString_IncludesIdAndName()
    {
        var (mgmt, _) = Make(_ => Task.FromResult(Json("{}")));
        var svc = mgmt.Platform.Services.New("user_service", "User Service");
        var s = svc.ToString();
        Assert.Contains("user_service", s);
        Assert.Contains("User Service", s);
    }
}
