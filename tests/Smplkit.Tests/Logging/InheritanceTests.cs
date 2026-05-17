using System.Net;
using System.Reflection;
using System.Text;
using Smplkit;
using Smplkit.Logging;
using Smplkit.Logging.Adapters;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Logging;

/// <summary>
/// End-to-end coverage for the resolver-driven runtime: a logger with no
/// configured level inherits from its group; group_changed re-resolves;
/// group_deleted falls through to the INFO fallback. Verifies the SDK does
/// the client-side inheritance work that the platform deliberately doesn't.
/// </summary>
public class InheritanceTests
{
    private static (SmplClient client, MockHttpMessageHandler handler) MakeClient(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        var handler = new MockHttpMessageHandler(respond);
        var http = new HttpClient(handler);
        var client = new SmplClient(TestData.DefaultOptions(), http);
        return (client, handler);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/vnd.api+json") };

    private sealed class CaptureAdapter : ILoggingAdapter
    {
        public string Name => "capture";
        public List<(string Id, LogLevel Level)> Applied { get; } = new();
        public IReadOnlyList<DiscoveredLogger> Discover() => Array.Empty<DiscoveredLogger>();
        public void InstallHook(Action<string, LogLevel> callback) { }
        public void UninstallHook() { }
        public void ApplyLevel(string loggerName, LogLevel level) => Applied.Add((loggerName, level));
    }

    private static string LoggerListJson(string id, string? level, string? group) =>
        $$"""
        {
          "data": [
            {
              "id": "{{id}}",
              "type": "logger",
              "attributes": {
                "name": "{{id}}",
                "level": {{(level is null ? "null" : $"\"{level}\"")}},
                "group": {{(group is null ? "null" : $"\"{group}\"")}},
                "managed": true,
                "sources": [],
                "environments": {}
              }
            }
          ]
        }
        """;

    private static string LogGroupListJson(string id, string? level, string? parent = null) =>
        $$"""
        {
          "data": [
            {
              "id": "{{id}}",
              "type": "log_group",
              "attributes": {
                "name": "{{id}}",
                "level": {{(level is null ? "null" : $"\"{level}\"")}},
                "parent_id": {{(parent is null ? "null" : $"\"{parent}\"")}},
                "environments": {}
              }
            }
          ]
        }
        """;

    private static string SingleLogGroup(string id, string? level, string? parent = null) =>
        $$"""
        {
          "data": {
            "id": "{{id}}",
            "type": "log_group",
            "attributes": {
              "name": "{{id}}",
              "level": {{(level is null ? "null" : $"\"{level}\"")}},
              "parent_id": {{(parent is null ? "null" : $"\"{parent}\"")}},
              "environments": {}
            }
          }
        }
        """;

    [Fact]
    public async Task Install_LoggerWithGroupLevelOnly_AppliesGroupLevel()
    {
        // The logger has no level of its own; the group it belongs to is WARN.
        // The SDK must walk the group chain and apply WARN to adapters.
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers")) return Task.FromResult(Json(LoggerListJson("my-logger", null, "billing")));
            if (path.EndsWith("/log_groups")) return Task.FromResult(Json(LogGroupListJson("billing", "WARN")));
            return Task.FromResult(Json("{}"));
        });
        var capture = new CaptureAdapter();
        client.Logging.RegisterAdapter(capture);
        await client.Logging.InstallAsync();

        Assert.Contains((Id: "my-logger", Level: LogLevel.Warn), capture.Applied);
    }

    [Fact]
    public async Task Install_LoggerWithNoConfiguration_AppliesInfoFallback()
    {
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers")) return Task.FromResult(Json(LoggerListJson("plain", null, null)));
            if (path.EndsWith("/log_groups")) return Task.FromResult(Json("""{"data":[]}"""));
            return Task.FromResult(Json("{}"));
        });
        var capture = new CaptureAdapter();
        client.Logging.RegisterAdapter(capture);
        await client.Logging.InstallAsync();

        Assert.Contains((Id: "plain", Level: LogLevel.Info), capture.Applied);
    }

    [Fact]
    public async Task GroupChanged_ReResolvesInheritingLoggers_AndFiresListener()
    {
        // billing: WARN at install. After group_changed event, server returns ERROR.
        // Expect a re-application of ERROR to the adapter AND a change listener firing.
        var groupLevel = "WARN";
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers"))
                return Task.FromResult(Json(LoggerListJson("my-logger", null, "billing")));
            if (path.EndsWith("/log_groups") && req.Method == HttpMethod.Get)
                return Task.FromResult(Json(LogGroupListJson("billing", groupLevel)));
            if (path.EndsWith("/log_groups/billing") && req.Method == HttpMethod.Get)
                return Task.FromResult(Json(SingleLogGroup("billing", groupLevel)));
            return Task.FromResult(Json("{}"));
        });
        var capture = new CaptureAdapter();
        client.Logging.RegisterAdapter(capture);
        await client.Logging.InstallAsync();
        Assert.Contains((Id: "my-logger", Level: LogLevel.Warn), capture.Applied);

        LoggerChangeEvent? captured = null;
        client.Logging.OnChange("my-logger", evt => captured = evt);

        groupLevel = "ERROR";
        var method = typeof(LoggingClient).GetMethod("HandleGroupChangedAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(client.Logging, new object?[] { "billing" })!;
        await task;

        Assert.Contains((Id: "my-logger", Level: LogLevel.Error), capture.Applied);
        Assert.NotNull(captured);
        Assert.Equal("my-logger", captured!.Id);
        Assert.Equal(LogLevel.Error, captured.Level);
        Assert.Equal("websocket", captured.Source);
    }

    [Fact]
    public async Task GroupDeleted_FallsBackToInfo_AndFiresListener()
    {
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers"))
                return Task.FromResult(Json(LoggerListJson("my-logger", null, "billing")));
            if (path.EndsWith("/log_groups") && req.Method == HttpMethod.Get)
                return Task.FromResult(Json(LogGroupListJson("billing", "WARN")));
            return Task.FromResult(Json("{}"));
        });
        var capture = new CaptureAdapter();
        client.Logging.RegisterAdapter(capture);
        await client.Logging.InstallAsync();

        LoggerChangeEvent? captured = null;
        client.Logging.OnChange("my-logger", evt => captured = evt);

        var method = typeof(LoggingClient).GetMethod("HandleGroupDeleted",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Logging, new object?[]
        {
            new Dictionary<string, object?> { ["id"] = "billing" },
        });

        Assert.Contains((Id: "my-logger", Level: LogLevel.Info), capture.Applied);
        Assert.NotNull(captured);
        Assert.Equal(LogLevel.Info, captured!.Level);
    }

    [Fact]
    public async Task GroupDeleted_UnknownGroup_NoOp()
    {
        // Deleting a group we never knew about must not fire any listener
        // (no logger's resolved level could possibly have changed).
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers"))
                return Task.FromResult(Json(LoggerListJson("my-logger", "INFO", null)));
            if (path.EndsWith("/log_groups"))
                return Task.FromResult(Json("""{"data":[]}"""));
            return Task.FromResult(Json("{}"));
        });
        var capture = new CaptureAdapter();
        client.Logging.RegisterAdapter(capture);
        await client.Logging.InstallAsync();

        var fired = 0;
        client.Logging.OnChange(_ => fired++);

        var method = typeof(LoggingClient).GetMethod("HandleGroupDeleted",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Logging, new object?[]
        {
            new Dictionary<string, object?> { ["id"] = "phantom" },
        });

        Assert.Equal(0, fired);
    }

    [Fact]
    public async Task GroupChanged_NoLevelDelta_DoesNotFireListener()
    {
        // Group's level unchanged after the websocket nudge — apply work
        // still happens, but no listener fires.
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers"))
                return Task.FromResult(Json(LoggerListJson("my-logger", null, "billing")));
            if (path.EndsWith("/log_groups") && req.Method == HttpMethod.Get)
                return Task.FromResult(Json(LogGroupListJson("billing", "WARN")));
            if (path.EndsWith("/log_groups/billing") && req.Method == HttpMethod.Get)
                return Task.FromResult(Json(SingleLogGroup("billing", "WARN")));
            return Task.FromResult(Json("{}"));
        });
        var capture = new CaptureAdapter();
        client.Logging.RegisterAdapter(capture);
        await client.Logging.InstallAsync();

        var fired = 0;
        client.Logging.OnChange(_ => fired++);

        var method = typeof(LoggingClient).GetMethod("HandleGroupChangedAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(client.Logging, new object?[] { "billing" })!;
        await task;

        Assert.Equal(0, fired);
    }

    [Fact]
    public async Task LoggerDeleted_FiresNoEventForDeletedKey_AndDescendantReResolves()
    {
        // Setup: com.acme (WARN) + com.acme.payments inheriting via dot-ancestry.
        // Deleting com.acme removes it from cache; com.acme.payments now resolves
        // to INFO (fallback). The deleted key's own listener does NOT fire — only
        // the descendant's, with the new effective level.
        var listJson = """
        {
          "data": [
            {"id":"com.acme","type":"logger","attributes":{
                "name":"com.acme","level":"WARN","group":null,
                "managed":true,"sources":[],"environments":{}}},
            {"id":"com.acme.payments","type":"logger","attributes":{
                "name":"com.acme.payments","level":null,"group":null,
                "managed":true,"sources":[],"environments":{}}}
          ]
        }
        """;
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers")) return Task.FromResult(Json(listJson));
            if (path.EndsWith("/log_groups")) return Task.FromResult(Json("""{"data":[]}"""));
            return Task.FromResult(Json("{}"));
        });
        var capture = new CaptureAdapter();
        client.Logging.RegisterAdapter(capture);
        await client.Logging.InstallAsync();
        Assert.Contains((Id: "com.acme.payments", Level: LogLevel.Warn), capture.Applied);

        var deletedKeyFired = 0;
        LoggerChangeEvent? descendantEvt = null;
        client.Logging.OnChange("com.acme", _ => deletedKeyFired++);
        client.Logging.OnChange("com.acme.payments", evt => descendantEvt = evt);

        var method = typeof(LoggingClient).GetMethod("HandleLoggerDeleted",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Logging, new object?[]
        {
            new Dictionary<string, object?> { ["id"] = "com.acme" },
        });

        Assert.Equal(0, deletedKeyFired);
        Assert.NotNull(descendantEvt);
        Assert.Equal("com.acme.payments", descendantEvt!.Id);
        Assert.Equal(LogLevel.Info, descendantEvt.Level);
    }

    [Fact]
    public async Task LoggerDeleted_UnknownId_NoOp()
    {
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers"))
                return Task.FromResult(Json(LoggerListJson("my-logger", "WARN", null)));
            if (path.EndsWith("/log_groups"))
                return Task.FromResult(Json("""{"data":[]}"""));
            return Task.FromResult(Json("{}"));
        });
        var capture = new CaptureAdapter();
        client.Logging.RegisterAdapter(capture);
        await client.Logging.InstallAsync();

        var fired = 0;
        client.Logging.OnChange(_ => fired++);

        var method = typeof(LoggingClient).GetMethod("HandleLoggerDeleted",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Logging, new object?[]
        {
            new Dictionary<string, object?> { ["id"] = "phantom" },
        });

        Assert.Equal(0, fired);
    }

    [Fact]
    public async Task LoggerChanged_OnDotAncestor_FiresListenerForDescendant()
    {
        // Setup: com.acme.payments inherits from com.acme (WARN), no group.
        // logger_changed on com.acme flipping WARN → ERROR must fire the
        // listener registered on com.acme.payments (resolved level changed via
        // dot-notation ancestry walk).
        var ancestorLevel = "WARN";
        var listResponse = """
        {
          "data": [
            {"id":"com.acme.payments","type":"logger","attributes":{
                "name":"com.acme.payments","level":null,"group":null,
                "managed":true,"sources":[],"environments":{}}},
            {"id":"com.acme","type":"logger","attributes":{
                "name":"com.acme","level":"WARN","group":null,
                "managed":true,"sources":[],"environments":{}}}
          ]
        }
        """;
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers") && req.Method == HttpMethod.Get)
                return Task.FromResult(Json(listResponse));
            if (path.EndsWith("/log_groups") && req.Method == HttpMethod.Get)
                return Task.FromResult(Json("""{"data":[]}"""));
            if (path.EndsWith("/loggers/com.acme") && req.Method == HttpMethod.Get)
                return Task.FromResult(Json(
                    "{\"data\":{\"id\":\"com.acme\",\"type\":\"logger\",\"attributes\":{"
                    + "\"name\":\"com.acme\",\"level\":\"" + ancestorLevel + "\","
                    + "\"group\":null,\"managed\":true,\"sources\":[],\"environments\":{}"
                    + "}}}"));
            return Task.FromResult(Json("{}"));
        });
        var capture = new CaptureAdapter();
        client.Logging.RegisterAdapter(capture);
        await client.Logging.InstallAsync();
        Assert.Contains((Id: "com.acme.payments", Level: LogLevel.Warn), capture.Applied);

        LoggerChangeEvent? captured = null;
        client.Logging.OnChange("com.acme.payments", evt => captured = evt);

        ancestorLevel = "ERROR";
        var method = typeof(LoggingClient).GetMethod("HandleLoggerChangedAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(client.Logging, new object?[] { "com.acme" })!;
        await task;

        Assert.NotNull(captured);
        Assert.Equal("com.acme.payments", captured!.Id);
        Assert.Equal(LogLevel.Error, captured.Level);
        Assert.Contains((Id: "com.acme.payments", Level: LogLevel.Error), capture.Applied);
    }

    [Fact]
    public async Task LoggerChanged_UpdatesLoggerCache_AndAppliesResolvedLevel()
    {
        // Initial state: logger inherits WARN from billing. After logger_changed,
        // logger gains its own explicit DEBUG level — should override the group.
        var loggerLevel = (string?)null;
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers") && req.Method == HttpMethod.Get)
                return Task.FromResult(Json(LoggerListJson("my-logger", loggerLevel, "billing")));
            if (path.EndsWith("/log_groups") && req.Method == HttpMethod.Get)
                return Task.FromResult(Json(LogGroupListJson("billing", "WARN")));
            if (path.EndsWith("/loggers/my-logger") && req.Method == HttpMethod.Get)
            {
                var levelJson = loggerLevel is null ? "null" : $"\"{loggerLevel}\"";
                return Task.FromResult(Json(
                    "{\"data\":{\"id\":\"my-logger\",\"type\":\"logger\",\"attributes\":{"
                    + "\"name\":\"my-logger\",\"level\":" + levelJson + ","
                    + "\"group\":\"billing\",\"managed\":true,\"sources\":[],\"environments\":{}"
                    + "}}}"));
            }
            return Task.FromResult(Json("{}"));
        });
        var capture = new CaptureAdapter();
        client.Logging.RegisterAdapter(capture);
        await client.Logging.InstallAsync();
        Assert.Contains((Id: "my-logger", Level: LogLevel.Warn), capture.Applied);

        LoggerChangeEvent? captured = null;
        client.Logging.OnChange("my-logger", evt => captured = evt);

        loggerLevel = "DEBUG";
        var method = typeof(LoggingClient).GetMethod("HandleLoggerChangedAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(client.Logging, new object?[] { "my-logger" })!;
        await task;

        Assert.Contains((Id: "my-logger", Level: LogLevel.Debug), capture.Applied);
        Assert.NotNull(captured);
        Assert.Equal(LogLevel.Debug, captured!.Level);
    }
}
