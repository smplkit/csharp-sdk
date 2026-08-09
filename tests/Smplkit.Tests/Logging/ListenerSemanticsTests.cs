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
/// Direct coverage for the listener-fanout contract:
///   • global subscribers fire ONCE per affected logger, never as a
///     summary event
///   • key-scoped subscribers fire when their id's effective level moves,
///     including via group cascade and dot-ancestry cascade
///   • deletion is a cache eviction — no "deleted" event for the removed
///     id; dependents re-resolve through the normal apply path
///   • a no-op edit (name/description only) fires nothing
///
/// Diagnostics 1–4 from the SDK-wide listener-semantics audit.
/// </summary>
public class ListenerSemanticsTests
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

    private static string LoggerListJson(params (string Id, string? Level, string? Group)[] loggers)
    {
        var sb = new StringBuilder();
        sb.Append("{\"data\":[");
        for (var i = 0; i < loggers.Length; i++)
        {
            if (i > 0) sb.Append(',');
            var (id, level, group) = loggers[i];
            sb.Append("{\"id\":\"").Append(id).Append("\",\"type\":\"logger\",\"attributes\":{")
              .Append("\"name\":\"").Append(id).Append("\",")
              .Append("\"level\":").Append(level is null ? "null" : $"\"{level}\"").Append(',')
              .Append("\"group\":").Append(group is null ? "null" : $"\"{group}\"").Append(',')
              .Append("\"managed\":true,\"sources\":[],\"environments\":{}}}");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private static string LogGroupListJson(params (string Id, string Level, string? Parent)[] groups)
    {
        var sb = new StringBuilder();
        sb.Append("{\"data\":[");
        for (var i = 0; i < groups.Length; i++)
        {
            if (i > 0) sb.Append(',');
            var (id, level, parent) = groups[i];
            sb.Append("{\"id\":\"").Append(id).Append("\",\"type\":\"log_group\",\"attributes\":{")
              .Append("\"name\":\"").Append(id).Append("\",")
              .Append("\"level\":\"").Append(level).Append("\",")
              .Append("\"parent_id\":").Append(parent is null ? "null" : $"\"{parent}\"").Append(',')
              .Append("\"environments\":{}}}");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private static string SingleLogger(string id, string level, string? group = null) =>
        "{\"data\":{\"id\":\"" + id + "\",\"type\":\"logger\",\"attributes\":{"
        + "\"name\":\"" + id + "\",\"level\":\"" + level + "\","
        + "\"group\":" + (group is null ? "null" : $"\"{group}\"") + ","
        + "\"managed\":true,\"sources\":[],\"environments\":{}}}}";

    private static string SingleLoggerNullLevel(string id, string? group = null) =>
        "{\"data\":{\"id\":\"" + id + "\",\"type\":\"logger\",\"attributes\":{"
        + "\"name\":\"" + id + "\",\"level\":null,"
        + "\"group\":" + (group is null ? "null" : $"\"{group}\"") + ","
        + "\"managed\":true,\"sources\":[],\"environments\":{}}}}";

    private static string SingleLogGroup(string id, string level) =>
        "{\"data\":{\"id\":\"" + id + "\",\"type\":\"log_group\",\"attributes\":{"
        + "\"name\":\"" + id + "\",\"level\":\"" + level + "\","
        + "\"parent_id\":null,\"environments\":{}}}}";

    // ------------------------------------------------------------------
    // Diagnostic 1: dot-ancestor cascade via logger_changed
    // ------------------------------------------------------------------

    [Fact]
    public async Task Diagnostic1_DotAncestorCascade_GlobalFiresOncePerAffectedLogger()
    {
        // com.acme (WARN) + 5 descendants com.acme.* (no own level, no group).
        // Flipping com.acme to ERROR cascades to all 6 — global must fire 6
        // times, NOT once as a summary.
        var ancestorLevel = "WARN";
        var listJson = LoggerListJson(
            ("com.acme", "WARN", null),
            ("com.acme.payments", null, null),
            ("com.acme.billing", null, null),
            ("com.acme.queue", null, null),
            ("com.acme.api", null, null),
            ("com.acme.workers", null, null));
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers") && req.Method == HttpMethod.Get)
                return Task.FromResult(Json(listJson));
            if (path.EndsWith("/log_groups") && req.Method == HttpMethod.Get)
                return Task.FromResult(Json("""{"data":[]}"""));
            if (path.EndsWith("/loggers/com.acme") && req.Method == HttpMethod.Get)
                return Task.FromResult(Json(SingleLogger("com.acme", ancestorLevel)));
            return Task.FromResult(Json("{}"));
        });
        await client.Logging.InstallAsync();

        var globalEvents = new List<LoggerChangeEvent>();
        client.Logging.OnChange(evt => globalEvents.Add(evt));

        ancestorLevel = "ERROR";
        var method = typeof(LoggingClient).GetMethod("HandleLoggerChangedAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(client.Logging, new object?[] { "com.acme" })!;

        Assert.Equal(6, globalEvents.Count);
        var byId = globalEvents.ToDictionary(e => e.Id);
        Assert.True(byId.ContainsKey("com.acme") && byId["com.acme"].Level == LogLevel.Error);
        foreach (var descendant in new[] { "com.acme.payments", "com.acme.billing", "com.acme.queue", "com.acme.api", "com.acme.workers" })
        {
            Assert.True(byId.ContainsKey(descendant));
            Assert.Equal(LogLevel.Error, byId[descendant].Level);
            Assert.Equal("push", byId[descendant].Source);
        }
    }

    // ------------------------------------------------------------------
    // Diagnostic 2: group cascade via group_changed
    // ------------------------------------------------------------------

    [Fact]
    public async Task Diagnostic2_GroupCascade_GlobalFiresOncePerAffectedLogger()
    {
        // 3 loggers inherit from group "app" (WARN). Flipping "app" to ERROR
        // cascades to all 3.
        var groupLevel = "WARN";
        var listJson = LoggerListJson(
            ("app.db", null, "app"),
            ("app.queue", null, "app"),
            ("app.api", null, "app"));
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers") && req.Method == HttpMethod.Get)
                return Task.FromResult(Json(listJson));
            if (path.EndsWith("/log_groups") && req.Method == HttpMethod.Get)
                return Task.FromResult(Json(LogGroupListJson(("app", groupLevel, null))));
            if (path.EndsWith("/log_groups/app") && req.Method == HttpMethod.Get)
                return Task.FromResult(Json(SingleLogGroup("app", groupLevel)));
            return Task.FromResult(Json("{}"));
        });
        await client.Logging.InstallAsync();

        var globalEvents = new List<LoggerChangeEvent>();
        client.Logging.OnChange(evt => globalEvents.Add(evt));

        groupLevel = "ERROR";
        var method = typeof(LoggingClient).GetMethod("HandleGroupChangedAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(client.Logging, new object?[] { "app" })!;

        Assert.Equal(3, globalEvents.Count);
        foreach (var id in new[] { "app.db", "app.queue", "app.api" })
        {
            var evt = globalEvents.Single(e => e.Id == id);
            Assert.Equal(LogLevel.Error, evt.Level);
            Assert.Equal("push", evt.Source);
        }
    }

    // ------------------------------------------------------------------
    // Diagnostic 3: deletion — group_deleted fires per dependent, NOT for the group
    // ------------------------------------------------------------------

    [Fact]
    public async Task Diagnostic3_GroupDeleted_FiresPerDependent_NoEventForGroupId()
    {
        // Same 3-inheritor setup. Deleting group "app" causes each logger to
        // resolve to INFO (fallback). Global fires 3 times — once per moved
        // logger — and NO event with id="app" or any deletion flag exists.
        var listJson = LoggerListJson(
            ("app.db", null, "app"),
            ("app.queue", null, "app"),
            ("app.api", null, "app"));
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers")) return Task.FromResult(Json(listJson));
            if (path.EndsWith("/log_groups")) return Task.FromResult(Json(LogGroupListJson(("app", "WARN", null))));
            return Task.FromResult(Json("{}"));
        });
        await client.Logging.InstallAsync();

        var globalEvents = new List<LoggerChangeEvent>();
        var groupListenerFired = 0;
        client.Logging.OnChange(evt => globalEvents.Add(evt));
        client.Logging.OnChange("app", _ => groupListenerFired++);

        var method = typeof(LoggingClient).GetMethod("HandleGroupDeleted",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Logging, new object?[]
        {
            new Dictionary<string, object?> { ["id"] = "app" },
        });

        Assert.Equal(3, globalEvents.Count);
        Assert.DoesNotContain(globalEvents, e => e.Id == "app");
        Assert.Equal(0, groupListenerFired);
        foreach (var id in new[] { "app.db", "app.queue", "app.api" })
            Assert.Equal(LogLevel.Info, globalEvents.Single(e => e.Id == id).Level);
    }

    // ------------------------------------------------------------------
    // Diagnostic 4: no-op edit — no listener fires
    // ------------------------------------------------------------------

    [Fact]
    public async Task Diagnostic4_LoggerChanged_LevelUnchanged_FiresNoListener()
    {
        // logger_changed payload arrives but the logger's resolved level is
        // the same as before (only a name/description-style change). The
        // listener must NOT fire.
        var listJson = LoggerListJson(("svc.api", "WARN", null));
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers") && req.Method == HttpMethod.Get)
                return Task.FromResult(Json(listJson));
            if (path.EndsWith("/log_groups") && req.Method == HttpMethod.Get)
                return Task.FromResult(Json("""{"data":[]}"""));
            if (path.EndsWith("/loggers/svc.api") && req.Method == HttpMethod.Get)
                // Same level returned — only name would have moved upstream.
                return Task.FromResult(Json(SingleLogger("svc.api", "WARN")));
            return Task.FromResult(Json("{}"));
        });
        await client.Logging.InstallAsync();

        var keyFired = 0;
        var globalFired = 0;
        client.Logging.OnChange("svc.api", _ => keyFired++);
        client.Logging.OnChange(_ => globalFired++);

        var method = typeof(LoggingClient).GetMethod("HandleLoggerChangedAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(client.Logging, new object?[] { "svc.api" })!;

        Assert.Equal(0, keyFired);
        Assert.Equal(0, globalFired);
    }

    // ------------------------------------------------------------------
    // Bonus coverage: refresh / loggers_changed use the same per-logger fanout
    // ------------------------------------------------------------------

    [Fact]
    public async Task RefreshAsync_FiresGlobalOncePerAffectedLogger_WithManualSource()
    {
        // Pre-refresh: 2 loggers at WARN. Post-refresh: both at ERROR. Global
        // must fire 2 times with source="manual" — not once as a batch.
        var level = "WARN";
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers") && req.Method == HttpMethod.Get)
                return Task.FromResult(Json(LoggerListJson(
                    ("svc.api", level, null),
                    ("svc.db", level, null))));
            if (path.EndsWith("/log_groups") && req.Method == HttpMethod.Get)
                return Task.FromResult(Json("""{"data":[]}"""));
            return Task.FromResult(Json("{}"));
        });
        await client.Logging.InstallAsync();

        var globalEvents = new List<LoggerChangeEvent>();
        client.Logging.OnChange(evt => globalEvents.Add(evt));

        level = "ERROR";
        await client.Logging.RefreshAsync();

        Assert.Equal(2, globalEvents.Count);
        Assert.All(globalEvents, e => Assert.Equal("manual", e.Source));
        Assert.All(globalEvents, e => Assert.Equal(LogLevel.Error, e.Level));
        Assert.Contains(globalEvents, e => e.Id == "svc.api");
        Assert.Contains(globalEvents, e => e.Id == "svc.db");
    }

    [Fact]
    public async Task LoggersChanged_FiresGlobalOncePerAffectedLogger_NotSummaryOnce()
    {
        var level = "WARN";
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers") && req.Method == HttpMethod.Get)
                return Task.FromResult(Json(LoggerListJson(
                    ("svc.api", level, null),
                    ("svc.db", level, null))));
            if (path.EndsWith("/log_groups") && req.Method == HttpMethod.Get)
                return Task.FromResult(Json("""{"data":[]}"""));
            return Task.FromResult(Json("{}"));
        });
        await client.Logging.InstallAsync();

        var globalEvents = new List<LoggerChangeEvent>();
        client.Logging.OnChange(evt => globalEvents.Add(evt));

        level = "ERROR";
        var method = typeof(LoggingClient).GetMethod("HandleLoggersChanged",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Logging, new object?[] { new Dictionary<string, object?>() });
        var taskField = typeof(LoggingClient).GetField("_lastLoggersChangedTask",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        if (taskField.GetValue(client.Logging) is Task tl) await tl;

        Assert.Equal(2, globalEvents.Count);
        Assert.All(globalEvents, e => Assert.Equal("push", e.Source));
    }

    // ------------------------------------------------------------------
    // Per-event payload shape: matching key-scoped + every global subscriber
    // ------------------------------------------------------------------

    [Fact]
    public async Task PerEvent_BothKeyScopedAndAllGlobalsFire_WithMatchingPayload()
    {
        var listJson = LoggerListJson(("svc.api", "WARN", null));
        var serverLevel = "WARN";
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers") && req.Method == HttpMethod.Get)
                return Task.FromResult(Json(listJson));
            if (path.EndsWith("/log_groups") && req.Method == HttpMethod.Get)
                return Task.FromResult(Json("""{"data":[]}"""));
            if (path.EndsWith("/loggers/svc.api") && req.Method == HttpMethod.Get)
                return Task.FromResult(Json(SingleLogger("svc.api", serverLevel)));
            return Task.FromResult(Json("{}"));
        });
        await client.Logging.InstallAsync();

        LoggerChangeEvent? keyEvt = null;
        LoggerChangeEvent? globalEvtA = null;
        LoggerChangeEvent? globalEvtB = null;
        client.Logging.OnChange("svc.api", evt => keyEvt = evt);
        client.Logging.OnChange(evt => globalEvtA = evt);
        client.Logging.OnChange(evt => globalEvtB = evt);

        serverLevel = "ERROR";
        var method = typeof(LoggingClient).GetMethod("HandleLoggerChangedAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(client.Logging, new object?[] { "svc.api" })!;

        Assert.NotNull(keyEvt);
        Assert.NotNull(globalEvtA);
        Assert.NotNull(globalEvtB);
        Assert.Equal(keyEvt, globalEvtA);
        Assert.Equal(keyEvt, globalEvtB);
        Assert.Equal("svc.api", keyEvt!.Id);
        Assert.Equal(LogLevel.Error, keyEvt.Level);
    }
}
