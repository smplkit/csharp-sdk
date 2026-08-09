using System.Net;
using System.Reflection;
using System.Text;
using Smplkit;
using Smplkit.Errors;
using Smplkit.Logging;
using Smplkit.Logging.Adapters;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Logging;

/// <summary>
/// Tests for the runtime <see cref="LoggingClient"/>: InstallAsync, listener
/// registration, the push-style handler delegates, the registration buffer
/// flush, ApplyLevels, FireListeners (with throwing listener resilience),
/// RefreshAsync, and Close lifecycle.
/// </summary>
public class LoggingRuntimeTests
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

    // Test adapter that exposes hooks
    private sealed class FakeAdapter : ILoggingAdapter
    {
        private Action<string, LogLevel>? _hookCallback;
        public string Name => "fake";
        public bool DiscoverThrows { get; set; }
        public bool InstallHookThrows { get; set; }
        public bool UninstallHookThrows { get; set; }
        public List<DiscoveredLogger> Discovered { get; } = new();
        public List<(string, LogLevel)> AppliedLevels { get; } = new();

        public IReadOnlyList<DiscoveredLogger> Discover()
        {
            if (DiscoverThrows) throw new InvalidOperationException("discover boom");
            return Discovered;
        }

        public void InstallHook(Action<string, LogLevel> callback)
        {
            if (InstallHookThrows) throw new InvalidOperationException("install boom");
            _hookCallback = callback;
        }

        public void UninstallHook()
        {
            if (UninstallHookThrows) throw new InvalidOperationException("uninstall boom");
            _hookCallback = null;
        }

        public void ApplyLevel(string loggerName, LogLevel level)
        {
            AppliedLevels.Add((loggerName, level));
        }

        public void TriggerHook(string name, LogLevel level) => _hookCallback?.Invoke(name, level);
    }

    private const string LoggerListJson = """
        {
            "data": [
                {
                    "id": "showcase",
                    "type": "logger",
                    "attributes": {
                        "name": "showcase",
                        "level": "INFO",
                        "group": null,
                        "managed": true,
                        "sources": [],
                        "environments": {}
                    }
                }
            ]
        }
        """;

    private const string LogGroupListJson = """
        {
            "data": [
                {
                    "id": "billing",
                    "type": "log_group",
                    "attributes": {
                        "name": "Billing",
                        "level": "WARN",
                        "parent_id": null,
                        "environments": {}
                    }
                }
            ]
        }
        """;

    [Fact]
    public async Task InstallAsync_LoadsLoggersAndAppliesLevels()
    {
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers")) return Task.FromResult(Json(LoggerListJson));
            if (path.EndsWith("/log_groups")) return Task.FromResult(Json(LogGroupListJson));
            return Task.FromResult(Json("{}"));
        });

        var fake = new FakeAdapter();
        fake.Discovered.Add(new DiscoveredLogger("startup-logger", LogLevel.Debug));
        client.Logging.RegisterAdapter(fake);

        await client.Logging.InstallAsync();

        // Adapter discovered logger flushed; level from server applied
        Assert.Contains(fake.AppliedLevels, x => x.Item1 == "showcase" && x.Item2 == LogLevel.Info);
    }

    [Fact]
    public async Task InstallAsync_Idempotent()
    {
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers")) return Task.FromResult(Json(LoggerListJson));
            if (path.EndsWith("/log_groups")) return Task.FromResult(Json(LogGroupListJson));
            return Task.FromResult(Json("{}"));
        });

        var fake = new FakeAdapter();
        client.Logging.RegisterAdapter(fake);

        await client.Logging.InstallAsync();
        await client.Logging.InstallAsync(); // second call should no-op
    }

    [Fact]
    public async Task RegisterAdapter_AfterInstall_Throws()
    {
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers")) return Task.FromResult(Json(LoggerListJson));
            if (path.EndsWith("/log_groups")) return Task.FromResult(Json(LogGroupListJson));
            return Task.FromResult(Json("{}"));
        });

        var fake = new FakeAdapter();
        client.Logging.RegisterAdapter(fake);
        await client.Logging.InstallAsync();
        Assert.Throws<InvalidOperationException>(
            () => client.Logging.RegisterAdapter(new FakeAdapter()));
    }

    [Fact]
    public async Task DiscoveryThrows_DoesNotPropagate()
    {
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers")) return Task.FromResult(Json(LoggerListJson));
            if (path.EndsWith("/log_groups")) return Task.FromResult(Json(LogGroupListJson));
            return Task.FromResult(Json("{}"));
        });

        var bad = new FakeAdapter { DiscoverThrows = true };
        client.Logging.RegisterAdapter(bad);
        await client.Logging.InstallAsync(); // should not throw
    }

    [Fact]
    public async Task InstallHookThrows_DoesNotPropagate()
    {
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers")) return Task.FromResult(Json(LoggerListJson));
            if (path.EndsWith("/log_groups")) return Task.FromResult(Json(LogGroupListJson));
            return Task.FromResult(Json("{}"));
        });

        var bad = new FakeAdapter { InstallHookThrows = true };
        client.Logging.RegisterAdapter(bad);
        await client.Logging.InstallAsync();
    }

    [Fact]
    public async Task AdapterNewLogger_BuffersAndFires()
    {
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers")) return Task.FromResult(Json(LoggerListJson));
            if (path.EndsWith("/log_groups")) return Task.FromResult(Json(LogGroupListJson));
            return Task.FromResult(Json("{}"));
        });

        var fake = new FakeAdapter();
        client.Logging.RegisterAdapter(fake);
        await client.Logging.InstallAsync();

        var fired = false;
        client.Logging.OnChange("new-logger", _ => fired = true);

        fake.TriggerHook("new-logger", LogLevel.Trace);
        Assert.True(fired);
    }

    [Fact]
    public async Task FlushLoggerBufferAsync_NoPending_NoOp()
    {
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers")) return Task.FromResult(Json(LoggerListJson));
            if (path.EndsWith("/log_groups")) return Task.FromResult(Json(LogGroupListJson));
            return Task.FromResult(Json("{}"));
        });

        var fake = new FakeAdapter();
        client.Logging.RegisterAdapter(fake);
        await client.Logging.InstallAsync();

        var method = typeof(LoggingClient).GetMethod("FlushLoggerBufferAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(client.Logging, new object[] { CancellationToken.None })!;
        await task;
    }

    [Fact]
    public async Task FlushLoggerBufferAsync_ServerError_Swallowed()
    {
        int call = 0;
        var (client, _) = MakeClient(req =>
        {
            call++;
            // First call (discovery → bulk) succeeds; subsequent fail
            if (req.RequestUri!.AbsolutePath.Contains("/bulk"))
                return Task.FromResult(Json("""{"errors":[{"detail":"x"}]}""", HttpStatusCode.InternalServerError));
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers")) return Task.FromResult(Json(LoggerListJson));
            if (path.EndsWith("/log_groups")) return Task.FromResult(Json(LogGroupListJson));
            return Task.FromResult(Json("{}"));
        });

        var fake = new FakeAdapter();
        fake.Discovered.Add(new DiscoveredLogger("any", LogLevel.Info));
        client.Logging.RegisterAdapter(fake);
        await client.Logging.InstallAsync(); // should not throw on bulk fail
    }

    [Fact]
    public async Task OnChange_GlobalListener_RegistersAndFires()
    {
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers")) return Task.FromResult(Json(LoggerListJson));
            if (path.EndsWith("/log_groups")) return Task.FromResult(Json(LogGroupListJson));
            return Task.FromResult(Json("{}"));
        });
        var fake = new FakeAdapter();
        client.Logging.RegisterAdapter(fake);
        await client.Logging.InstallAsync();

        var fired = false;
        client.Logging.OnChange(_ => fired = true);

        fake.TriggerHook("any-logger", LogLevel.Info);
        Assert.True(fired);
    }

    [Fact]
    public async Task OnChange_ListenerThrows_DoesNotPropagate()
    {
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers")) return Task.FromResult(Json(LoggerListJson));
            if (path.EndsWith("/log_groups")) return Task.FromResult(Json(LogGroupListJson));
            return Task.FromResult(Json("{}"));
        });
        var fake = new FakeAdapter();
        client.Logging.RegisterAdapter(fake);
        await client.Logging.InstallAsync();

        client.Logging.OnChange(_ => throw new InvalidOperationException("bad"));
        var second = false;
        client.Logging.OnChange(_ => second = true);

        fake.TriggerHook("logger", LogLevel.Info);
        Assert.True(second);
    }

    [Fact]
    public async Task HandleLoggerDeleted_FiresNoEventForDeletedKey()
    {
        // Deletion is a cache eviction, not a level change. The deleted key's
        // own listener must NOT fire — there is no "deleted" event in the
        // public contract. (Inheriting descendants are exercised separately
        // in InheritanceTests.)
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers")) return Task.FromResult(Json(LoggerListJson));
            if (path.EndsWith("/log_groups")) return Task.FromResult(Json(LogGroupListJson));
            return Task.FromResult(Json("{}"));
        });
        var fake = new FakeAdapter();
        client.Logging.RegisterAdapter(fake);
        await client.Logging.InstallAsync();

        var keyFired = 0;
        var globalFired = 0;
        client.Logging.OnChange("showcase", _ => keyFired++);
        client.Logging.OnChange(_ => globalFired++);

        var method = typeof(LoggingClient).GetMethod("HandleLoggerDeleted",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Logging, new object?[]
        {
            new Dictionary<string, object?> { ["id"] = "showcase" },
        });

        Assert.Equal(0, keyFired);
        Assert.Equal(0, globalFired);
    }

    [Fact]
    public async Task HandleLoggerDeleted_NoIdInData_NoOp()
    {
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers")) return Task.FromResult(Json(LoggerListJson));
            if (path.EndsWith("/log_groups")) return Task.FromResult(Json(LogGroupListJson));
            return Task.FromResult(Json("{}"));
        });
        var fake = new FakeAdapter();
        client.Logging.RegisterAdapter(fake);
        await client.Logging.InstallAsync();
        var method = typeof(LoggingClient).GetMethod("HandleLoggerDeleted",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Logging, new object?[]
        {
            new Dictionary<string, object?>(),
        });
    }

    [Fact]
    public async Task HandleLoggerDeleted_NotStarted_NoOp()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json("{}")));
        var method = typeof(LoggingClient).GetMethod("HandleLoggerDeleted",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Logging, new object?[]
        {
            new Dictionary<string, object?> { ["id"] = "x" },
        });
        await Task.Yield();
    }

    [Fact]
    public async Task HandleGroupDeleted_FiresDeltasForInheritingLoggers()
    {
        // showcase-inh inherits from billing (level=WARN). Deleting billing
        // should re-resolve showcase-inh; with no other source, it falls
        // through to the INFO fallback — that's a delta from WARN → INFO and
        // must fire a listener.
        var loggerListWithInheritor = """
        {
            "data": [
                {
                    "id": "showcase-inh",
                    "type": "logger",
                    "attributes": {
                        "name": "Inheritor",
                        "level": null,
                        "group": "billing",
                        "managed": true,
                        "sources": [],
                        "environments": {}
                    }
                }
            ]
        }
        """;
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers")) return Task.FromResult(Json(loggerListWithInheritor));
            if (path.EndsWith("/log_groups")) return Task.FromResult(Json(LogGroupListJson));
            return Task.FromResult(Json("{}"));
        });
        var fake = new FakeAdapter();
        client.Logging.RegisterAdapter(fake);
        await client.Logging.InstallAsync();

        LoggerChangeEvent? captured = null;
        client.Logging.OnChange("showcase-inh", evt => captured = evt);

        var method = typeof(LoggingClient).GetMethod("HandleGroupDeleted",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Logging, new object?[]
        {
            new Dictionary<string, object?> { ["id"] = "billing" },
        });

        Assert.NotNull(captured);
        Assert.Equal("showcase-inh", captured!.Id);
        Assert.Equal(LogLevel.Info, captured.Level); // fell back from WARN to INFO
    }

    [Fact]
    public async Task HandleGroupDeleted_NoIdInData_NoOp()
    {
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers")) return Task.FromResult(Json(LoggerListJson));
            if (path.EndsWith("/log_groups")) return Task.FromResult(Json(LogGroupListJson));
            return Task.FromResult(Json("{}"));
        });
        var fake = new FakeAdapter();
        client.Logging.RegisterAdapter(fake);
        await client.Logging.InstallAsync();
        var method = typeof(LoggingClient).GetMethod("HandleGroupDeleted",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Logging, new object?[]
        {
            new Dictionary<string, object?>(),
        });
    }

    [Fact]
    public async Task HandleLoggersChanged_NotStarted_NoOp()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json("{}")));
        var method = typeof(LoggingClient).GetMethod("HandleLoggersChanged",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Logging, new object?[]
        {
            new Dictionary<string, object?>(),
        });
        await Task.Yield();
    }

    [Fact]
    public async Task LoggersRegisterAsync_SendsBulk()
    {
        // The one-client refactor moved explicit source registration to the
        // Loggers sub-client: RegisterAsync buffers, then flushes a bulk POST.
        HttpRequestMessage? captured = null;
        var (client, _) = MakeClient(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/bulk"))
            {
                captured = req;
                return Task.FromResult(Json("{}"));
            }
            return Task.FromResult(Json("{}"));
        });

        await client.Logging.Loggers.RegisterAsync(
            new[] { new LoggerSource("a", level: LogLevel.Info, resolvedLevel: LogLevel.Info) },
            flush: true);
        Assert.NotNull(captured);
    }

    [Fact]
    public async Task FlushLoggerBufferAsync_SendsRequest()
    {
        // Adapter-discovered loggers are buffered on the fused client and drained
        // by FlushLoggerBufferAsync (the timer / threshold flush path).
        HttpRequestMessage? captured = null;
        var (client, _) = MakeClient(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/bulk"))
            {
                captured = req;
                return Task.FromResult(Json("{}"));
            }
            return Task.FromResult(Json("{}"));
        });

        // Feed the buffer through the adapter-new-logger entry point.
        var handle = typeof(LoggingClient).GetMethod("HandleAdapterNewLogger",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        handle.Invoke(client.Logging, new object[] { "logger-a", LogLevel.Info });

        var flush = typeof(LoggingClient).GetMethod("FlushLoggerBufferAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)flush.Invoke(client.Logging, new object[] { CancellationToken.None })!;
        Assert.NotNull(captured);
    }

    [Fact]
    public async Task Close_AfterInstall_ReleasesResources()
    {
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers")) return Task.FromResult(Json(LoggerListJson));
            if (path.EndsWith("/log_groups")) return Task.FromResult(Json(LogGroupListJson));
            return Task.FromResult(Json("{}"));
        });
        var fake = new FakeAdapter();
        client.Logging.RegisterAdapter(fake);
        await client.Logging.InstallAsync();

        var method = typeof(LoggingClient).GetMethod("Close",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Logging, Array.Empty<object>());
        method.Invoke(client.Logging, Array.Empty<object>()); // idempotent
    }

    [Fact]
    public async Task Close_UninstallHookThrows_StillCloses()
    {
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers")) return Task.FromResult(Json(LoggerListJson));
            if (path.EndsWith("/log_groups")) return Task.FromResult(Json(LogGroupListJson));
            return Task.FromResult(Json("{}"));
        });
        var fake = new FakeAdapter { UninstallHookThrows = true };
        client.Logging.RegisterAdapter(fake);
        await client.Logging.InstallAsync();

        var method = typeof(LoggingClient).GetMethod("Close",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Logging, Array.Empty<object>());
        // No exception
    }

    [Fact]
    public void OnFlushTimer_NoState_NoOp()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json("{}")));
        var method = typeof(LoggingClient).GetMethod("OnFlushTimer",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Logging, Array.Empty<object>());
    }

    // The async push handlers (Task.Run inside) — invoke and wait.

    [Fact]
    public async Task HandleLoggerChanged_FetchesAndAppliesLevel()
    {
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers")) return Task.FromResult(Json(LoggerListJson));
            if (path.EndsWith("/log_groups")) return Task.FromResult(Json(LogGroupListJson));
            if (path.Contains("/loggers/showcase"))
                return Task.FromResult(Json("""
                    {"data":{"id":"showcase","type":"logger","attributes":{"name":"showcase","level":"DEBUG","managed":true,"environments":{}}}}
                    """));
            return Task.FromResult(Json("{}"));
        });
        var fake = new FakeAdapter();
        client.Logging.RegisterAdapter(fake);
        await client.Logging.InstallAsync();

        var fireCount = 0;
        client.Logging.OnChange("showcase", _ => Interlocked.Increment(ref fireCount));

        var method = typeof(LoggingClient).GetMethod("HandleLoggerChanged",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Logging, new object?[]
        {
            new Dictionary<string, object?> { ["id"] = "showcase" },
        });

        // Await the tracked Task.Run so coverage is deterministic.
        var taskField = typeof(LoggingClient).GetField("_lastLoggerChangedTask",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        if (taskField.GetValue(client.Logging) is Task t) await t;
        Assert.True(fireCount >= 1);
    }

    [Fact]
    public async Task HandleLoggerChanged_NotStarted_NoOp()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json("{}")));
        var method = typeof(LoggingClient).GetMethod("HandleLoggerChanged",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Logging, new object?[]
        {
            new Dictionary<string, object?> { ["id"] = "showcase" },
        });
        await Task.Yield(); // let any pending work settle
    }

    [Fact]
    public async Task HandleLoggerChanged_NoIdInData_NoOp()
    {
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers")) return Task.FromResult(Json(LoggerListJson));
            if (path.EndsWith("/log_groups")) return Task.FromResult(Json(LogGroupListJson));
            return Task.FromResult(Json("{}"));
        });
        var fake = new FakeAdapter();
        client.Logging.RegisterAdapter(fake);
        await client.Logging.InstallAsync();

        var method = typeof(LoggingClient).GetMethod("HandleLoggerChanged",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Logging, new object?[]
        {
            new Dictionary<string, object?>(),
        });
    }

    [Fact]
    public async Task HandleLoggerChanged_ServerError_Swallowed()
    {
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers")) return Task.FromResult(Json(LoggerListJson));
            if (path.EndsWith("/log_groups")) return Task.FromResult(Json(LogGroupListJson));
            if (path.Contains("/loggers/showcase"))
                return Task.FromResult(Json("""{"errors":[{"detail":"x"}]}""", HttpStatusCode.InternalServerError));
            return Task.FromResult(Json("{}"));
        });
        var fake = new FakeAdapter();
        client.Logging.RegisterAdapter(fake);
        await client.Logging.InstallAsync();

        var method = typeof(LoggingClient).GetMethod("HandleLoggerChanged",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Logging, new object?[]
        {
            new Dictionary<string, object?> { ["id"] = "showcase" },
        });
        // Await the tracked Task.Run so coverage is deterministic.
        var taskField = typeof(LoggingClient).GetField("_lastLoggerChangedTask",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        if (taskField.GetValue(client.Logging) is Task t) await t;
    }

    [Fact]
    public async Task HandleGroupChanged_FetchesAndDiffs()
    {
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers")) return Task.FromResult(Json(LoggerListJson));
            if (path.EndsWith("/log_groups")) return Task.FromResult(Json(LogGroupListJson));
            if (path.Contains("/log_groups/billing"))
                return Task.FromResult(Json("""
                    {"data":{"id":"billing","type":"log_group","attributes":{"name":"Billing","level":"DEBUG","environments":{}}}}
                    """));
            return Task.FromResult(Json("{}"));
        });
        var fake = new FakeAdapter();
        client.Logging.RegisterAdapter(fake);
        await client.Logging.InstallAsync();

        var method = typeof(LoggingClient).GetMethod("HandleGroupChanged",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Logging, new object?[]
        {
            new Dictionary<string, object?> { ["id"] = "billing" },
        });
        var taskField = typeof(LoggingClient).GetField("_lastGroupChangedTask",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        if (taskField.GetValue(client.Logging) is Task tg) await tg;
    }

    [Fact]
    public async Task HandleGroupChanged_NotStarted_NoOp()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json("{}")));
        var method = typeof(LoggingClient).GetMethod("HandleGroupChanged",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Logging, new object?[]
        {
            new Dictionary<string, object?> { ["id"] = "x" },
        });
        await Task.Yield();
    }

    [Fact]
    public async Task HandleGroupChanged_NoIdInData_NoOp()
    {
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers")) return Task.FromResult(Json(LoggerListJson));
            if (path.EndsWith("/log_groups")) return Task.FromResult(Json(LogGroupListJson));
            return Task.FromResult(Json("{}"));
        });
        var fake = new FakeAdapter();
        client.Logging.RegisterAdapter(fake);
        await client.Logging.InstallAsync();
        var method = typeof(LoggingClient).GetMethod("HandleGroupChanged",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Logging, new object?[]
        {
            new Dictionary<string, object?>(),
        });
    }

    [Fact]
    public async Task HandleGroupChanged_ServerError_Swallowed()
    {
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers")) return Task.FromResult(Json(LoggerListJson));
            if (path.EndsWith("/log_groups")) return Task.FromResult(Json(LogGroupListJson));
            if (path.Contains("/log_groups/billing"))
                return Task.FromResult(Json("""{"errors":[{"detail":"x"}]}""", HttpStatusCode.InternalServerError));
            return Task.FromResult(Json("{}"));
        });
        var fake = new FakeAdapter();
        client.Logging.RegisterAdapter(fake);
        await client.Logging.InstallAsync();

        var method = typeof(LoggingClient).GetMethod("HandleGroupChanged",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Logging, new object?[]
        {
            new Dictionary<string, object?> { ["id"] = "billing" },
        });
        var taskField = typeof(LoggingClient).GetField("_lastGroupChangedTask",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        if (taskField.GetValue(client.Logging) is Task tg) await tg;
    }

    [Fact]
    public async Task HandleLoggersChanged_RefetchesAll()
    {
        int loggersCall = 0;
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers"))
            {
                loggersCall++;
                if (loggersCall <= 1) return Task.FromResult(Json(LoggerListJson));
                // 2nd list returns extra logger
                return Task.FromResult(Json("""
                    {"data":[
                        {"id":"new-logger","type":"logger","attributes":{"name":"new-logger","level":"WARN","managed":true,"environments":{}}}
                    ]}
                    """));
            }
            if (path.EndsWith("/log_groups")) return Task.FromResult(Json(LogGroupListJson));
            return Task.FromResult(Json("{}"));
        });
        var fake = new FakeAdapter();
        client.Logging.RegisterAdapter(fake);
        await client.Logging.InstallAsync();

        var fireCount = 0;
        client.Logging.OnChange(_ => Interlocked.Increment(ref fireCount));

        var method = typeof(LoggingClient).GetMethod("HandleLoggersChanged",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Logging, new object?[]
        {
            new Dictionary<string, object?>(),
        });

        var taskField = typeof(LoggingClient).GetField("_lastLoggersChangedTask",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        if (taskField.GetValue(client.Logging) is Task t) await t;
        Assert.True(fireCount >= 1);
    }

    [Fact]
    public async Task HandleReconnectRefetch_ReusesLoggersChangedBulkPath()
    {
        int loggersCall = 0;
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers"))
            {
                loggersCall++;
                if (loggersCall <= 1) return Task.FromResult(Json(LoggerListJson));
                // The refetch sees a different server state (an extra logger).
                return Task.FromResult(Json("""
                    {"data":[
                        {"id":"new-logger","type":"logger","attributes":{"name":"new-logger","level":"WARN","managed":true,"environments":{}}}
                    ]}
                    """));
            }
            if (path.EndsWith("/log_groups")) return Task.FromResult(Json(LogGroupListJson));
            return Task.FromResult(Json("{}"));
        });
        var fake = new FakeAdapter();
        client.Logging.RegisterAdapter(fake);
        await client.Logging.InstallAsync();

        var events = new List<LoggerChangeEvent>();
        client.Logging.OnChange(evt => events.Add(evt));

        var method = typeof(LoggingClient).GetMethod("HandleReconnectRefetch",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Logging, Array.Empty<object>());

        var taskField = typeof(LoggingClient).GetField("_lastLoggersChangedTask",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        if (taskField.GetValue(client.Logging) is Task t) await t;

        // The refetch reuses the loggers_changed bulk path (loggers + groups):
        // listeners fire per moved logger with the push source label.
        Assert.NotEmpty(events);
        Assert.All(events, e => Assert.Equal("push", e.Source));
    }

    [Fact]
    public async Task HandleLoggersChanged_ServerError_Swallowed()
    {
        int loggersCall = 0;
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers"))
            {
                loggersCall++;
                if (loggersCall <= 1) return Task.FromResult(Json(LoggerListJson));
                return Task.FromResult(Json("""{"errors":[{"detail":"x"}]}""", HttpStatusCode.InternalServerError));
            }
            if (path.EndsWith("/log_groups")) return Task.FromResult(Json(LogGroupListJson));
            return Task.FromResult(Json("{}"));
        });
        var fake = new FakeAdapter();
        client.Logging.RegisterAdapter(fake);
        await client.Logging.InstallAsync();

        var method = typeof(LoggingClient).GetMethod("HandleLoggersChanged",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Logging, new object?[]
        {
            new Dictionary<string, object?>(),
        });
        var taskField = typeof(LoggingClient).GetField("_lastLoggersChangedTask",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        if (taskField.GetValue(client.Logging) is Task tl) await tl;
    }

    // Listener fire paths — exception swallowing
    [Fact]
    public async Task FireListeners_ScopedListenerThrows_DoesNotPropagate()
    {
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers")) return Task.FromResult(Json(LoggerListJson));
            if (path.EndsWith("/log_groups")) return Task.FromResult(Json(LogGroupListJson));
            return Task.FromResult(Json("{}"));
        });
        var fake = new FakeAdapter();
        client.Logging.RegisterAdapter(fake);
        await client.Logging.InstallAsync();

        client.Logging.OnChange("X", _ => throw new InvalidOperationException("bad"));
        bool second = false;
        client.Logging.OnChange("X", _ => second = true);

        var method = typeof(LoggingClient).GetMethod("FireListeners",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Logging, new object?[] { "X", new LoggerChangeEvent("X", LogLevel.Info, "test") });

        Assert.True(second);
    }

    [Fact]
    public async Task FireListeners_NoScopedListenersForId_OnlyGlobalFires()
    {
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers")) return Task.FromResult(Json(LoggerListJson));
            if (path.EndsWith("/log_groups")) return Task.FromResult(Json(LogGroupListJson));
            return Task.FromResult(Json("{}"));
        });
        var fake = new FakeAdapter();
        client.Logging.RegisterAdapter(fake);
        await client.Logging.InstallAsync();

        var globalFired = 0;
        client.Logging.OnChange(_ => globalFired++);
        client.Logging.OnChange("other-id", _ => { });

        var method = typeof(LoggingClient).GetMethod("FireListeners",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Logging, new object?[]
        {
            "no-listeners",
            new LoggerChangeEvent("no-listeners", LogLevel.Info, "test")
        });

        Assert.Equal(1, globalFired);
    }

    [Fact]
    public async Task FireListeners_GlobalListenerThrows_DoesNotPropagate()
    {
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers")) return Task.FromResult(Json(LoggerListJson));
            if (path.EndsWith("/log_groups")) return Task.FromResult(Json(LogGroupListJson));
            return Task.FromResult(Json("{}"));
        });
        var fake = new FakeAdapter();
        client.Logging.RegisterAdapter(fake);
        await client.Logging.InstallAsync();

        client.Logging.OnChange(_ => throw new InvalidOperationException("bad"));
        bool second = false;
        client.Logging.OnChange(_ => second = true);

        var method = typeof(LoggingClient).GetMethod("FireListeners",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Logging, new object?[] { "x", new LoggerChangeEvent("x", LogLevel.Info, "test") });
        Assert.True(second);
    }

    [Fact]
    public async Task MapLoggerResource_MissingLevel_ReturnsNullLevel()
    {
        var body = """
            {"data":[{
                "id":"x","type":"logger","attributes":{
                    "name":"x","managed":true,"environments":{}
                }
            }]}
            """;
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers")) return Task.FromResult(Json(body));
            if (path.EndsWith("/log_groups")) return Task.FromResult(Json("""{"data":[]}"""));
            return Task.FromResult(Json("{}"));
        });

        var loggers = await client.Logging.Loggers.ListAsync();
        Assert.Single(loggers);
        Assert.Null(loggers[0].Level); // missing level -> null
    }

    [Fact]
    public async Task MapLogGroupResource_MissingLevel_ReturnsNullLevel()
    {
        var body = """
            {"data":[{
                "id":"g","type":"log_group","attributes":{
                    "name":"g","environments":{}
                }
            }]}
            """;
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/log_groups")) return Task.FromResult(Json(body));
            return Task.FromResult(Json("{}"));
        });

        var groups = await client.Logging.LogGroups.ListAsync();
        Assert.Single(groups);
        Assert.Null(groups[0].Level);
    }

    [Fact]
    public async Task DiscoveryReturnsLoggers_TheyAreApplied()
    {
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers")) return Task.FromResult(Json("""
                {"data":[
                    {"id":"discovered","type":"logger","attributes":{"name":"discovered","level":"INFO","managed":true,"environments":{}}}
                ]}
                """));
            if (path.EndsWith("/log_groups")) return Task.FromResult(Json("""{"data":[]}"""));
            return Task.FromResult(Json("{}"));
        });

        var fake = new FakeAdapter();
        fake.Discovered.Add(new DiscoveredLogger("discovered", LogLevel.Trace));
        client.Logging.RegisterAdapter(fake);
        await client.Logging.InstallAsync();
        Assert.NotEmpty(fake.AppliedLevels);
    }

    [Fact]
    public async Task RefreshAsync_ReappliesServerLevelsToAdapters()
    {
        // First /loggers fetch returns INFO; second returns ERROR.
        // RefreshAsync should re-fetch and re-apply the new level.
        int loggersCalls = 0;
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers") && req.Method == HttpMethod.Get)
            {
                loggersCalls++;
                var level = loggersCalls == 1 ? "INFO" : "ERROR";
                return Task.FromResult(Json(
                    "{\"data\":[{\"id\":\"showcase\",\"type\":\"logger\",\"attributes\":"
                    + "{\"name\":\"showcase\",\"level\":\"" + level
                    + "\",\"managed\":true,\"environments\":{}}}]}"));
            }
            if (path.EndsWith("/log_groups")) return Task.FromResult(Json("""{"data":[]}"""));
            return Task.FromResult(Json("{}"));
        });

        var fake = new FakeAdapter();
        client.Logging.RegisterAdapter(fake);
        await client.Logging.InstallAsync();

        // Install applied INFO once.
        Assert.Single(fake.AppliedLevels);
        Assert.Equal(("showcase", LogLevel.Info), fake.AppliedLevels[0]);

        await client.Logging.RefreshAsync();

        // Refresh re-fetched and re-applied ERROR.
        Assert.Equal(2, fake.AppliedLevels.Count);
        Assert.Equal(("showcase", LogLevel.Error), fake.AppliedLevels[1]);
    }

    [Fact]
    public async Task RefreshAsync_FiresChangeListeners_OnDiff()
    {
        int loggersCalls = 0;
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers") && req.Method == HttpMethod.Get)
            {
                loggersCalls++;
                var level = loggersCalls == 1 ? "INFO" : "ERROR";
                return Task.FromResult(Json(
                    "{\"data\":[{\"id\":\"showcase\",\"type\":\"logger\",\"attributes\":"
                    + "{\"name\":\"showcase\",\"level\":\"" + level
                    + "\",\"managed\":true,\"environments\":{}}}]}"));
            }
            if (path.EndsWith("/log_groups")) return Task.FromResult(Json("""{"data":[]}"""));
            return Task.FromResult(Json("{}"));
        });

        var fake = new FakeAdapter();
        client.Logging.RegisterAdapter(fake);
        await client.Logging.InstallAsync();

        LoggerChangeEvent? globalEvt = null;
        LoggerChangeEvent? scopedEvt = null;
        client.Logging.OnChange(e => globalEvt = e);
        client.Logging.OnChange("showcase", e => scopedEvt = e);

        await client.Logging.RefreshAsync();

        Assert.NotNull(globalEvt);
        Assert.NotNull(scopedEvt);
        Assert.Equal("manual", globalEvt!.Source);
        Assert.Equal("manual", scopedEvt!.Source);
        Assert.Equal(LogLevel.Error, scopedEvt.Level);
    }

    [Fact]
    public async Task RefreshAsync_NoChange_DoesNotFireListeners()
    {
        // Server returns same INFO level on every call.
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers")) return Task.FromResult(Json(LoggerListJson));
            if (path.EndsWith("/log_groups")) return Task.FromResult(Json(LogGroupListJson));
            return Task.FromResult(Json("{}"));
        });

        var fake = new FakeAdapter();
        client.Logging.RegisterAdapter(fake);
        await client.Logging.InstallAsync();

        var fired = 0;
        client.Logging.OnChange(_ => fired++);

        await client.Logging.RefreshAsync();

        Assert.Equal(0, fired);
    }

    [Fact]
    public async Task RefreshAsync_PropagatesErrors()
    {
        // First fetch (during install) succeeds; subsequent fetches fail.
        int loggersCalls = 0;
        var (client, _) = MakeClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/loggers") && req.Method == HttpMethod.Get)
            {
                loggersCalls++;
                if (loggersCalls == 1) return Task.FromResult(Json(LoggerListJson));
                return Task.FromResult(Json("""{"errors":[{"detail":"boom"}]}""", HttpStatusCode.InternalServerError));
            }
            if (path.EndsWith("/log_groups")) return Task.FromResult(Json(LogGroupListJson));
            return Task.FromResult(Json("{}"));
        });

        var fake = new FakeAdapter();
        client.Logging.RegisterAdapter(fake);
        await client.Logging.InstallAsync();

        await Assert.ThrowsAnyAsync<SmplkitException>(
            () => client.Logging.RefreshAsync());
    }
}
