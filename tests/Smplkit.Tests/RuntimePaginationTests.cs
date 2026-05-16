using System.Net;
using System.Reflection;
using System.Text;
using System.Web;
using Smplkit;
using Smplkit.Tests.Helpers;
using Xunit;
using InternalHelpers = Smplkit.Internal.Helpers;

namespace Smplkit.Tests;

/// <summary>
/// End-to-end tests verifying that runtime fetch-all sites loop until the
/// server returns a short page. Each site must call once when the first page
/// is short, and at least twice when the first page is full (size ==
/// <see cref="InternalHelpers.RuntimePageSize"/>).
/// </summary>
public class RuntimePaginationTests
{
    private static SmplClient MakeClient(MockHttpMessageHandler handler)
        => new(TestData.DefaultOptions(), new HttpClient(handler));

    private static HttpResponseMessage JsonApi(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/vnd.api+json") };

    private static int? PageNumber(HttpRequestMessage req)
    {
        var q = HttpUtility.ParseQueryString(req.RequestUri!.Query);
        var raw = q["page[number]"];
        return raw is null ? null : int.Parse(raw);
    }

    // ------------------------------------------------------------------
    // Flag-list response generators
    // ------------------------------------------------------------------

    private static string FlagListJson(int count, int startId = 0)
    {
        var sb = new StringBuilder();
        sb.Append("{\"data\":[");
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            var id = $"flag_{startId + i}";
            sb.Append("{\"id\":\"").Append(id).Append("\",\"type\":\"flag\",\"attributes\":{")
              .Append("\"id\":\"").Append(id).Append("\",\"name\":\"").Append(id).Append("\",")
              .Append("\"type\":\"BOOLEAN\",\"default\":true,\"values\":[],")
              .Append("\"description\":null,\"environments\":{},")
              .Append("\"created_at\":\"2024-01-15T10:30:00Z\",")
              .Append("\"updated_at\":\"2024-01-15T10:30:00Z\"}}");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private static string ConfigListJson(int count, int startId = 0)
    {
        var sb = new StringBuilder();
        sb.Append("{\"data\":[");
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            var id = $"cfg_{startId + i}";
            sb.Append("{\"id\":\"").Append(id).Append("\",\"type\":\"config\",\"attributes\":{")
              .Append("\"id\":\"").Append(id).Append("\",\"name\":\"").Append(id).Append("\",")
              .Append("\"description\":null,\"parent\":null,\"items\":{},\"environments\":{},")
              .Append("\"created_at\":\"2024-01-15T10:30:00Z\",")
              .Append("\"updated_at\":\"2024-01-15T10:30:00Z\"}}");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private static string LoggerListJson(int count, int startId = 0)
    {
        var sb = new StringBuilder();
        sb.Append("{\"data\":[");
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            var id = $"lg_{startId + i}";
            sb.Append("{\"id\":\"").Append(id).Append("\",\"type\":\"logger\",\"attributes\":{")
              .Append("\"name\":\"").Append(id).Append("\",\"level\":\"INFO\",")
              .Append("\"group\":null,\"managed\":true,\"environments\":{}}}");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    // ------------------------------------------------------------------
    // Flags runtime — FetchAllFlagsAsync (init/refresh path)
    // ------------------------------------------------------------------

    [Fact]
    public async Task FlagsRuntime_Refresh_FetchesMultiplePagesUntilShort()
    {
        var pageCalls = new List<int?>();
        var handler = new MockHttpMessageHandler(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/flags"))
            {
                var p = PageNumber(req);
                pageCalls.Add(p);
                // page=1 returns a full page, page>=2 returns a short page —
                // so each fetch-all session walks 2 pages independently.
                var count = p == 1 ? InternalHelpers.RuntimePageSize : 5;
                var start = p == 1 ? 0 : InternalHelpers.RuntimePageSize;
                return Task.FromResult(JsonApi(FlagListJson(count, start)));
            }
            return Task.FromResult(JsonApi("{}"));
        });
        using var client = MakeClient(handler);

        client.Flags.BooleanFlag("flag_0", false).Get(); // forces init -> first list
        await client.Flags.RefreshAsync();

        // init (2 pages) + refresh (2 pages) = 4
        Assert.Equal(4, pageCalls.Count);
        Assert.Equal(new int?[] { 1, 2, 1, 2 }, pageCalls);
    }

    [Fact]
    public async Task FlagsRuntime_Refresh_SinglePage_NoSecondCall()
    {
        var pageCalls = new List<int?>();
        var handler = new MockHttpMessageHandler(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/flags"))
            {
                pageCalls.Add(PageNumber(req));
                return Task.FromResult(JsonApi(FlagListJson(3)));
            }
            return Task.FromResult(JsonApi("{}"));
        });
        using var client = MakeClient(handler);

        client.Flags.BooleanFlag("flag_0", false).Get(); // init
        await client.Flags.RefreshAsync();

        // init + refresh = 2 calls, all to page 1 (short page exits immediately)
        Assert.Equal(2, pageCalls.Count);
        Assert.All(pageCalls, p => Assert.Equal(1, p));
    }

    [Fact]
    public void FlagsRuntime_HandleFlagsChanged_FetchesMultiplePages()
    {
        var pageCalls = new List<int?>();
        var handler = new MockHttpMessageHandler(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/flags"))
            {
                var p = PageNumber(req);
                pageCalls.Add(p);
                var count = p == 1 ? InternalHelpers.RuntimePageSize : 1;
                var start = p == 1 ? 0 : InternalHelpers.RuntimePageSize;
                return Task.FromResult(JsonApi(FlagListJson(count, start)));
            }
            return Task.FromResult(JsonApi("{}"));
        });
        using var client = MakeClient(handler);

        client.Flags.BooleanFlag("flag_0", false).Get(); // init: 2 pages
        Assert.Equal(2, pageCalls.Count);

        var method = typeof(Smplkit.Flags.FlagsClient).GetMethod(
            "HandleFlagsChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Flags, new object?[] { new Dictionary<string, object?>() });

        // HandleFlagsChanged path: 2 more pages
        Assert.Equal(4, pageCalls.Count);
    }

    // ------------------------------------------------------------------
    // Config runtime — FetchAllConfigsAsync (init/refresh/configs_changed)
    // ------------------------------------------------------------------

    [Fact]
    public async Task ConfigRuntime_Refresh_FetchesMultiplePagesUntilShort()
    {
        var pageCalls = new List<int?>();
        var handler = new MockHttpMessageHandler(req =>
        {
            if (req.RequestUri!.Host.Contains("config.") && req.Method == HttpMethod.Get)
            {
                var p = PageNumber(req);
                pageCalls.Add(p);
                var count = p == 1 ? InternalHelpers.RuntimePageSize : 2;
                var start = p == 1 ? 0 : InternalHelpers.RuntimePageSize;
                return Task.FromResult(JsonApi(ConfigListJson(count, start)));
            }
            return Task.FromResult(JsonApi("{}"));
        });
        using var client = MakeClient(handler);

        // Init: forces a list. Use a config that exists in the response.
        client.Config.Get("cfg_0");
        await client.Config.RefreshAsync();

        // init (2 pages) + refresh (2 pages) = 4
        Assert.Equal(4, pageCalls.Count);
        Assert.Equal(new int?[] { 1, 2, 1, 2 }, pageCalls);
    }

    [Fact]
    public async Task ConfigRuntime_Refresh_SinglePage_NoSecondCall()
    {
        var pageCalls = new List<int?>();
        var handler = new MockHttpMessageHandler(req =>
        {
            if (req.RequestUri!.Host.Contains("config.") && req.Method == HttpMethod.Get)
            {
                pageCalls.Add(PageNumber(req));
                return Task.FromResult(JsonApi(ConfigListJson(3)));
            }
            return Task.FromResult(JsonApi("{}"));
        });
        using var client = MakeClient(handler);

        client.Config.Get("cfg_0");
        await client.Config.RefreshAsync();

        Assert.Equal(2, pageCalls.Count);
        Assert.All(pageCalls, p => Assert.Equal(1, p));
    }

    [Fact]
    public void ConfigRuntime_HandleConfigsChanged_FetchesMultiplePages()
    {
        var pageCalls = new List<int?>();
        var handler = new MockHttpMessageHandler(req =>
        {
            if (req.RequestUri!.Host.Contains("config.") && req.Method == HttpMethod.Get)
            {
                var p = PageNumber(req);
                pageCalls.Add(p);
                var count = p == 1 ? InternalHelpers.RuntimePageSize : 1;
                var start = p == 1 ? 0 : InternalHelpers.RuntimePageSize;
                return Task.FromResult(JsonApi(ConfigListJson(count, start)));
            }
            return Task.FromResult(JsonApi("{}"));
        });
        using var client = MakeClient(handler);

        client.Config.Get("cfg_0"); // init: 2 pages
        Assert.Equal(2, pageCalls.Count);

        var method = typeof(Smplkit.Config.ConfigClient).GetMethod(
            "HandleConfigsChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Config, new object?[] { new Dictionary<string, object?>() });

        Assert.Equal(4, pageCalls.Count);
    }

    // ------------------------------------------------------------------
    // Logging runtime — FetchAllLoggersAsync / FetchAllLogGroupsAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task LoggingRuntime_Install_FetchesLoggersAndGroupsAcrossPages()
    {
        var loggerCalls = new List<int?>();
        var groupCalls = new List<int?>();
        var handler = new MockHttpMessageHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers") && req.Method == HttpMethod.Get)
            {
                var p = PageNumber(req);
                loggerCalls.Add(p);
                var count = p == 1 ? InternalHelpers.RuntimePageSize : 0;
                var start = p == 1 ? 0 : InternalHelpers.RuntimePageSize;
                return Task.FromResult(JsonApi(LoggerListJson(count, start)));
            }
            if (path.EndsWith("/log_groups") && req.Method == HttpMethod.Get)
            {
                var p = PageNumber(req);
                groupCalls.Add(p);
                var groupCount = p == 1 ? InternalHelpers.RuntimePageSize : 3;
                return Task.FromResult(JsonApi(LoggerListJson(groupCount)
                    .Replace("\"type\":\"logger\"", "\"type\":\"log_group\"")));
            }
            return Task.FromResult(JsonApi("{}"));
        });
        using var client = MakeClient(handler);
        await client.Logging.InstallAsync();

        // Loggers: 2 pages (full + empty), Groups: 2 pages (full + short)
        Assert.Equal(2, loggerCalls.Count);
        Assert.Equal(new int?[] { 1, 2 }, loggerCalls);
        Assert.Equal(2, groupCalls.Count);
        Assert.Equal(new int?[] { 1, 2 }, groupCalls);
    }

    [Fact]
    public async Task LoggingRuntime_Refresh_SinglePage_NoSecondCall()
    {
        var loggerCalls = new List<int?>();
        var groupCalls = new List<int?>();
        var handler = new MockHttpMessageHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers") && req.Method == HttpMethod.Get)
            {
                loggerCalls.Add(PageNumber(req));
                return Task.FromResult(JsonApi(LoggerListJson(1)));
            }
            if (path.EndsWith("/log_groups") && req.Method == HttpMethod.Get)
            {
                groupCalls.Add(PageNumber(req));
                return Task.FromResult(JsonApi("""{"data":[]}"""));
            }
            return Task.FromResult(JsonApi("{}"));
        });
        using var client = MakeClient(handler);
        await client.Logging.InstallAsync();
        await client.Logging.RefreshAsync();

        Assert.Equal(2, loggerCalls.Count);
        Assert.Equal(2, groupCalls.Count);
        Assert.All(loggerCalls, p => Assert.Equal(1, p));
        Assert.All(groupCalls, p => Assert.Equal(1, p));
    }

    [Fact]
    public async Task LoggingRuntime_GroupChanged_LoggerRefetchLoops()
    {
        var loggerCalls = new List<int?>();
        var handler = new MockHttpMessageHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers") && req.Method == HttpMethod.Get)
            {
                var p = PageNumber(req);
                loggerCalls.Add(p);
                var count = p == 1 ? InternalHelpers.RuntimePageSize : 0;
                var start = p == 1 ? 0 : InternalHelpers.RuntimePageSize;
                return Task.FromResult(JsonApi(LoggerListJson(count, start)));
            }
            if (path.EndsWith("/log_groups") && req.Method == HttpMethod.Get)
                return Task.FromResult(JsonApi("""{"data":[]}"""));
            if (path.Contains("/log_groups/") && req.Method == HttpMethod.Get)
                return Task.FromResult(JsonApi("""
                    {"data":{"id":"g","type":"log_group","attributes":{"name":"g","level":"INFO","environments":{}}}}
                    """));
            return Task.FromResult(JsonApi("{}"));
        });
        using var client = MakeClient(handler);
        await client.Logging.InstallAsync(); // init: 2 logger pages
        Assert.Equal(2, loggerCalls.Count);

        var method = typeof(Smplkit.Logging.LoggingClient).GetMethod(
            "HandleGroupChangedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(client.Logging, new object?[] { "g" })!;
        await task;

        // group_changed should refetch loggers across 2 pages again
        Assert.Equal(4, loggerCalls.Count);
    }
}
