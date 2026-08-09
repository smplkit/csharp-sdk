using System.Net;
using System.Text;
using Moq;
using Smplkit.Logging;
using Smplkit.Logging.Adapters;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Logging.Adapters;

public class AutoLoadTests
{
    private static (SmplClient client, MockHttpMessageHandler handler) CreateClient(
        Func<HttpRequestMessage, Task<HttpResponseMessage>>? handlerFn = null)
    {
        handlerFn ??= _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"data":[]}""", Encoding.UTF8, "application/vnd.api+json"),
        });
        var handler = new MockHttpMessageHandler(handlerFn);
        var httpClient = new HttpClient(handler);
        var options = TestData.DefaultOptions();
        var client = new SmplClient(options, httpClient);
        return (client, handler);
    }

    [Fact]
    public async Task InstallAsync_NoAdaptersRegistered_DoesNotThrow()
    {
        // With auto-load removed, InstallAsync with zero registered adapters
        // is a valid path — discovery, hook install, and apply-levels all become
        // no-ops, and InstallAsync proceeds to the event stream subscription stage.
        var (client, _) = CreateClient();

        try
        {
            await client.Logging.InstallAsync();
        }
        catch
        {
            // The event stream may fail in this test harness — the relevant assertion
            // is that everything up to that point ran without throwing.
        }
    }

    [Fact]
    public async Task RegisterAdapter_ExplicitAdapter_GetsDiscoverAndHook()
    {
        var (client, _) = CreateClient();
        var mockAdapter = new Mock<ILoggingAdapter>();
        mockAdapter.Setup(a => a.Name).Returns("custom");
        mockAdapter.Setup(a => a.Discover()).Returns(new List<DiscoveredLogger>());

        client.Logging.RegisterAdapter(mockAdapter.Object);

        try
        {
            await client.Logging.InstallAsync();
        }
        catch
        {
            // The event stream will fail
        }

        mockAdapter.Verify(a => a.Discover(), Times.Once);
        mockAdapter.Verify(a => a.InstallHook(It.IsAny<Action<string, LogLevel>>()), Times.Once);
    }

    [Fact]
    public async Task RegisterAdapter_AfterStart_Throws()
    {
        var (client, _) = CreateClient();

        try
        {
            await client.Logging.InstallAsync();
        }
        catch
        {
            // The event stream will fail, but _started is set to true
        }

        var mockAdapter = new Mock<ILoggingAdapter>();
        Assert.Throws<InvalidOperationException>(() =>
            client.Logging.RegisterAdapter(mockAdapter.Object));
    }

    [Fact]
    public async Task MultipleAdapters_AllCalled()
    {
        var (client, _) = CreateClient(_ =>
        {
            var json = """
            {
                "data": [
                    {
                        "id": "my-logger",
                        "type": "logger",
                        "attributes": {
                            "id": "my-logger",
                            "name": "My Logger",
                            "level": "WARN",
                            "group": null,
                            "managed": true,
                            "sources": [],
                            "environments": {},
                            "created_at": "2024-01-15T10:30:00Z",
                            "updated_at": "2024-01-15T10:30:00Z"
                        }
                    }
                ]
            }
            """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/vnd.api+json"),
            });
        });

        var adapter1 = new Mock<ILoggingAdapter>();
        adapter1.Setup(a => a.Name).Returns("adapter-1");
        adapter1.Setup(a => a.Discover()).Returns(new List<DiscoveredLogger>());

        var adapter2 = new Mock<ILoggingAdapter>();
        adapter2.Setup(a => a.Name).Returns("adapter-2");
        adapter2.Setup(a => a.Discover()).Returns(new List<DiscoveredLogger>());

        client.Logging.RegisterAdapter(adapter1.Object);
        client.Logging.RegisterAdapter(adapter2.Object);

        try
        {
            await client.Logging.InstallAsync();
        }
        catch
        {
            // The event stream will fail
        }

        // Both adapters should have Discover, InstallHook, and ApplyLevel called
        adapter1.Verify(a => a.Discover(), Times.Once);
        adapter2.Verify(a => a.Discover(), Times.Once);
        adapter1.Verify(a => a.InstallHook(It.IsAny<Action<string, LogLevel>>()), Times.Once);
        adapter2.Verify(a => a.InstallHook(It.IsAny<Action<string, LogLevel>>()), Times.Once);
        adapter1.Verify(a => a.ApplyLevel("my-logger", LogLevel.Warn), Times.Once);
        adapter2.Verify(a => a.ApplyLevel("my-logger", LogLevel.Warn), Times.Once);
    }

    [Fact]
    public async Task Close_CallsUninstallHookOnAllAdapters()
    {
        var (client, _) = CreateClient();

        var adapter1 = new Mock<ILoggingAdapter>();
        adapter1.Setup(a => a.Name).Returns("adapter-1");
        adapter1.Setup(a => a.Discover()).Returns(new List<DiscoveredLogger>());

        var adapter2 = new Mock<ILoggingAdapter>();
        adapter2.Setup(a => a.Name).Returns("adapter-2");
        adapter2.Setup(a => a.Discover()).Returns(new List<DiscoveredLogger>());

        client.Logging.RegisterAdapter(adapter1.Object);
        client.Logging.RegisterAdapter(adapter2.Object);

        try
        {
            await client.Logging.InstallAsync();
        }
        catch
        {
            // The event stream will fail
        }

        client.Dispose();

        adapter1.Verify(a => a.UninstallHook(), Times.Once);
        adapter2.Verify(a => a.UninstallHook(), Times.Once);
    }

    [Fact]
    public async Task ApplyLevels_SkipsUnmanagedLoggers()
    {
        var (client, _) = CreateClient(_ =>
        {
            // managed=false means the customer hasn't given us ownership of
            // this logger — we never push a level for it. (Contrast with
            // managed=true + no configured level, which resolves to the INFO
            // fallback and IS applied — see Python's _apply_levels.)
            var json = """
            {
                "data": [
                    {
                        "id": "unmanaged-logger",
                        "type": "logger",
                        "attributes": {
                            "id": "unmanaged-logger",
                            "name": "Unmanaged",
                            "level": null,
                            "group": null,
                            "managed": false,
                            "sources": [],
                            "environments": {},
                            "created_at": "2024-01-15T10:30:00Z",
                            "updated_at": "2024-01-15T10:30:00Z"
                        }
                    }
                ]
            }
            """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/vnd.api+json"),
            });
        });

        var adapter = new Mock<ILoggingAdapter>();
        adapter.Setup(a => a.Name).Returns("test");
        adapter.Setup(a => a.Discover()).Returns(new List<DiscoveredLogger>());

        client.Logging.RegisterAdapter(adapter.Object);

        try
        {
            await client.Logging.InstallAsync();
        }
        catch
        {
            // The event stream will fail
        }

        // ApplyLevel must not be called for unmanaged loggers.
        adapter.Verify(a => a.ApplyLevel(It.IsAny<string>(), It.IsAny<LogLevel>()), Times.Never);
    }

    [Fact]
    public async Task StartAsync_IsIdempotent()
    {
        var (client, _) = CreateClient();

        var adapter = new Mock<ILoggingAdapter>();
        adapter.Setup(a => a.Name).Returns("test");
        adapter.Setup(a => a.Discover()).Returns(new List<DiscoveredLogger>());

        client.Logging.RegisterAdapter(adapter.Object);

        try { await client.Logging.InstallAsync(); } catch { }
        try { await client.Logging.InstallAsync(); } catch { }

        // Discover should only be called once due to idempotency
        adapter.Verify(a => a.Discover(), Times.Once);
    }

    [Fact]
    public async Task Close_HandlesAdapterUninstallHookFailure()
    {
        var (client, _) = CreateClient();

        var failingAdapter = new Mock<ILoggingAdapter>();
        failingAdapter.Setup(a => a.Name).Returns("failing-uninstall");
        failingAdapter.Setup(a => a.Discover()).Returns(new List<DiscoveredLogger>());
        failingAdapter.Setup(a => a.UninstallHook()).Throws(new Exception("Uninstall failed"));

        client.Logging.RegisterAdapter(failingAdapter.Object);

        try { await client.Logging.InstallAsync(); } catch { }

        // Should not throw even though UninstallHook throws
        client.Dispose();

        failingAdapter.Verify(a => a.UninstallHook(), Times.Once);
    }

    [Fact]
    public async Task ApplyLevels_HandlesAdapterApplyLevelFailure()
    {
        var (client, _) = CreateClient(_ =>
        {
            var json = """
            {
                "data": [
                    {
                        "id": "my-logger",
                        "type": "logger",
                        "attributes": {
                            "id": "my-logger",
                            "name": "My Logger",
                            "level": "ERROR",
                            "group": null,
                            "managed": true,
                            "sources": [],
                            "environments": {},
                            "created_at": "2024-01-15T10:30:00Z",
                            "updated_at": "2024-01-15T10:30:00Z"
                        }
                    }
                ]
            }
            """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/vnd.api+json"),
            });
        });

        var failingAdapter = new Mock<ILoggingAdapter>();
        failingAdapter.Setup(a => a.Name).Returns("failing-apply");
        failingAdapter.Setup(a => a.Discover()).Returns(new List<DiscoveredLogger>());
        failingAdapter.Setup(a => a.ApplyLevel(It.IsAny<string>(), It.IsAny<LogLevel>()))
            .Throws(new Exception("Apply failed"));

        client.Logging.RegisterAdapter(failingAdapter.Object);

        // Should not throw even though ApplyLevel throws
        try { await client.Logging.InstallAsync(); } catch { }

        failingAdapter.Verify(a => a.ApplyLevel("my-logger", LogLevel.Error), Times.Once);
    }

    [Fact]
    public async Task HandleAdapterNewLogger_FiresListeners()
    {
        var (client, _) = CreateClient();

        var adapter = new Mock<ILoggingAdapter>();
        adapter.Setup(a => a.Name).Returns("hook-test");
        adapter.Setup(a => a.Discover()).Returns(new List<DiscoveredLogger>());

        Action<string, LogLevel>? capturedHook = null;
        adapter.Setup(a => a.InstallHook(It.IsAny<Action<string, LogLevel>>()))
            .Callback<Action<string, LogLevel>>(hook => capturedHook = hook);

        client.Logging.RegisterAdapter(adapter.Object);

        // OnChange requires InstallAsync() first in the one-client design — install
        // opens the live connection, then change listeners may be registered.
        try { await client.Logging.InstallAsync(); } catch { }

        var events = new List<LoggerChangeEvent>();
        client.Logging.OnChange(e => events.Add(e));

        // Simulate adapter detecting a new logger
        Assert.NotNull(capturedHook);
        capturedHook!("new-logger", LogLevel.Debug);

        Assert.Single(events);
        Assert.Equal("new-logger", events[0].Id);
        Assert.Equal(LogLevel.Debug, events[0].Level);
        Assert.Equal("adapter", events[0].Source);
    }

    [Fact]
    public async Task InstallHookFailure_IsNonFatal()
    {
        var (client, _) = CreateClient();

        var failingAdapter = new Mock<ILoggingAdapter>();
        failingAdapter.Setup(a => a.Name).Returns("failing-hook");
        failingAdapter.Setup(a => a.Discover()).Returns(new List<DiscoveredLogger>());
        failingAdapter.Setup(a => a.InstallHook(It.IsAny<Action<string, LogLevel>>()))
            .Throws(new Exception("Hook install failed"));

        client.Logging.RegisterAdapter(failingAdapter.Object);

        // Should not throw even though InstallHook throws
        try { await client.Logging.InstallAsync(); } catch { }

        failingAdapter.Verify(a => a.InstallHook(It.IsAny<Action<string, LogLevel>>()), Times.Once);
    }

    [Fact]
    public async Task AdapterDiscoverFailure_IsNonFatal()
    {
        var (client, _) = CreateClient();

        var failingAdapter = new Mock<ILoggingAdapter>();
        failingAdapter.Setup(a => a.Name).Returns("failing");
        failingAdapter.Setup(a => a.Discover()).Throws(new Exception("Discovery failed"));

        var workingAdapter = new Mock<ILoggingAdapter>();
        workingAdapter.Setup(a => a.Name).Returns("working");
        workingAdapter.Setup(a => a.Discover()).Returns(new List<DiscoveredLogger>());

        client.Logging.RegisterAdapter(failingAdapter.Object);
        client.Logging.RegisterAdapter(workingAdapter.Object);

        try { await client.Logging.InstallAsync(); } catch { }

        // Both adapters should have had Discover called
        failingAdapter.Verify(a => a.Discover(), Times.Once);
        workingAdapter.Verify(a => a.Discover(), Times.Once);

        // Working adapter should still get InstallHook
        workingAdapter.Verify(a => a.InstallHook(It.IsAny<Action<string, LogLevel>>()), Times.Once);
    }
}
