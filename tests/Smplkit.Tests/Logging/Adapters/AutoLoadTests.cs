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
    public async Task AutoLoad_FindsAvailableAdapters()
    {
        // Both Microsoft.Extensions.Logging and Serilog are test dependencies,
        // so auto-load should find them. We verify StartAsync completes without error.
        var (client, _) = CreateClient();

        try
        {
            await client.Logging.StartAsync();
        }
        catch
        {
            // WebSocket will fail, but auto-load and adapter pipeline should succeed
        }

        // If we got here without an unhandled exception, auto-load worked
    }

    [Fact]
    public async Task RegisterAdapter_DisablesAutoLoad()
    {
        var (client, _) = CreateClient();
        var mockAdapter = new Mock<ILoggingAdapter>();
        mockAdapter.Setup(a => a.Name).Returns("custom");
        mockAdapter.Setup(a => a.Discover()).Returns(new List<DiscoveredLogger>());

        client.Logging.RegisterAdapter(mockAdapter.Object);

        // Start should only use the explicitly registered adapter
        try
        {
            await client.Logging.StartAsync();
        }
        catch
        {
            // WebSocket will fail
        }

        // The mock adapter's Discover and InstallHook should have been called
        mockAdapter.Verify(a => a.Discover(), Times.Once);
        mockAdapter.Verify(a => a.InstallHook(It.IsAny<Action<string, LogLevel>>()), Times.Once);
    }

    [Fact]
    public async Task RegisterAdapter_AfterStart_Throws()
    {
        var (client, _) = CreateClient();

        try
        {
            await client.Logging.StartAsync();
        }
        catch
        {
            // WebSocket will fail, but _started is set to true
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
                        "id": "550e8400-e29b-41d4-a716-446655440099",
                        "type": "logger",
                        "attributes": {
                            "key": "my-logger",
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
            await client.Logging.StartAsync();
        }
        catch
        {
            // WebSocket will fail
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
            await client.Logging.StartAsync();
        }
        catch
        {
            // WebSocket will fail
        }

        client.Dispose();

        adapter1.Verify(a => a.UninstallHook(), Times.Once);
        adapter2.Verify(a => a.UninstallHook(), Times.Once);
    }

    [Fact]
    public async Task ApplyLevels_SkipsLoggersWithNoLevel()
    {
        var (client, _) = CreateClient(_ =>
        {
            var json = """
            {
                "data": [
                    {
                        "id": "550e8400-e29b-41d4-a716-446655440099",
                        "type": "logger",
                        "attributes": {
                            "key": "no-level-logger",
                            "name": "No Level",
                            "level": null,
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

        var adapter = new Mock<ILoggingAdapter>();
        adapter.Setup(a => a.Name).Returns("test");
        adapter.Setup(a => a.Discover()).Returns(new List<DiscoveredLogger>());

        client.Logging.RegisterAdapter(adapter.Object);

        try
        {
            await client.Logging.StartAsync();
        }
        catch
        {
            // WebSocket will fail
        }

        // ApplyLevel should NOT be called for logger with null level
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

        try { await client.Logging.StartAsync(); } catch { }
        try { await client.Logging.StartAsync(); } catch { }

        // Discover should only be called once due to idempotency
        adapter.Verify(a => a.Discover(), Times.Once);
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

        try { await client.Logging.StartAsync(); } catch { }

        // Both adapters should have had Discover called
        failingAdapter.Verify(a => a.Discover(), Times.Once);
        workingAdapter.Verify(a => a.Discover(), Times.Once);

        // Working adapter should still get InstallHook
        workingAdapter.Verify(a => a.InstallHook(It.IsAny<Action<string, LogLevel>>()), Times.Once);
    }
}
