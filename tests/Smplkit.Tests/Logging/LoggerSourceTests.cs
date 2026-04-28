using System.Net;
using System.Text;
using Smplkit.Logging;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Logging;

public class LoggerSourceTests
{
    private static (SmplClient client, MockHttpMessageHandler handler) CreateClient(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handlerFn)
    {
        var handler = new MockHttpMessageHandler(handlerFn);
        var httpClient = new HttpClient(handler);
        var options = TestData.DefaultOptions();
        var client = new SmplClient(options, httpClient);
        return (client, handler);
    }

    // ------------------------------------------------------------------
    // LoggerSource model
    // ------------------------------------------------------------------

    [Fact]
    public void LoggerSource_StoresAllProperties()
    {
        var source = new LoggerSource(
            name: "sqlalchemy.engine",
            service: "api-gateway",
            environment: "production",
            resolvedLevel: LogLevel.Info,
            level: LogLevel.Debug);

        Assert.Equal("sqlalchemy.engine", source.Name);
        Assert.Equal("api-gateway", source.Service);
        Assert.Equal("production", source.Environment);
        Assert.Equal(LogLevel.Info, source.ResolvedLevel);
        Assert.Equal(LogLevel.Debug, source.Level);
    }

    [Fact]
    public void LoggerSource_DefaultOptionals_AreNull()
    {
        var source = new LoggerSource("my-logger");

        Assert.Equal("my-logger", source.Name);
        Assert.Null(source.Service);
        Assert.Null(source.Environment);
        Assert.Null(source.ResolvedLevel);
        Assert.Null(source.Level);
    }

    [Fact]
    public void LoggerSource_ToString_ContainsNameServiceEnvironment()
    {
        var source = new LoggerSource("httpx", service: "checkout", environment: "staging");
        var str = source.ToString();

        Assert.Contains("httpx", str);
        Assert.Contains("checkout", str);
        Assert.Contains("staging", str);
    }

    [Fact]
    public void LoggerSource_ToString_NullFields_DoesNotThrow()
    {
        var source = new LoggerSource("test-logger");
        var str = source.ToString();

        Assert.NotNull(str);
        Assert.Contains("test-logger", str);
    }

    // ------------------------------------------------------------------
    // RegisterSourcesAsync — HTTP payload
    // ------------------------------------------------------------------

    [Fact]
    public async Task RegisterSourcesAsync_SendsBulkRequest()
    {
        string? capturedBody = null;
        var (client, handler) = CreateClient(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"registered":2}""",
                    Encoding.UTF8, "application/json"),
            };
        });

        var sources = new[]
        {
            new LoggerSource("sqlalchemy.engine", service: "api", environment: "prod",
                resolvedLevel: LogLevel.Info, level: LogLevel.Debug),
            new LoggerSource("httpx", resolvedLevel: LogLevel.Warn),
        };

        await client.Logging.Management.RegisterSourcesAsync(sources);

        Assert.Single(handler.Requests);
        var req = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Contains("bulk", req.RequestUri!.AbsoluteUri);

        Assert.NotNull(capturedBody);
        Assert.Contains("sqlalchemy.engine", capturedBody);
        Assert.Contains("httpx", capturedBody);
    }

    [Fact]
    public async Task RegisterSourcesAsync_MapsLevelsToWireFormat()
    {
        string? capturedBody = null;
        var (client, _) = CreateClient(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"registered":1}""",
                    Encoding.UTF8, "application/json"),
            };
        });

        var sources = new[]
        {
            new LoggerSource("my-logger",
                resolvedLevel: LogLevel.Error,
                level: LogLevel.Warn),
        };

        await client.Logging.Management.RegisterSourcesAsync(sources);

        Assert.NotNull(capturedBody);
        Assert.Contains("ERROR", capturedBody);
        Assert.Contains("WARN", capturedBody);
    }

    [Fact]
    public async Task RegisterSourcesAsync_NullLevels_SentAsNull()
    {
        string? capturedBody = null;
        var (client, _) = CreateClient(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"registered":1}""",
                    Encoding.UTF8, "application/json"),
            };
        });

        var sources = new[] { new LoggerSource("no-level-logger") };

        await client.Logging.Management.RegisterSourcesAsync(sources);

        Assert.NotNull(capturedBody);
        Assert.Contains("\"level\":null", capturedBody);
        Assert.Contains("\"resolved_level\":null", capturedBody);
    }

    [Fact]
    public async Task RegisterSourcesAsync_MapsServiceAndEnvironment()
    {
        string? capturedBody = null;
        var (client, _) = CreateClient(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"registered":1}""",
                    Encoding.UTF8, "application/json"),
            };
        });

        var sources = new[]
        {
            new LoggerSource("svc-logger", service: "my-service", environment: "staging"),
        };

        await client.Logging.Management.RegisterSourcesAsync(sources);

        Assert.NotNull(capturedBody);
        Assert.Contains("my-service", capturedBody);
        Assert.Contains("staging", capturedBody);
    }

    [Fact]
    public async Task RegisterSourcesAsync_EmptyList_SendsEmptyBulkRequest()
    {
        string? capturedBody = null;
        var (client, handler) = CreateClient(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"registered":0}""",
                    Encoding.UTF8, "application/json"),
            };
        });

        await client.Logging.Management.RegisterSourcesAsync(Array.Empty<LoggerSource>());

        Assert.Single(handler.Requests);
        Assert.NotNull(capturedBody);
        Assert.Contains("\"loggers\":[]", capturedBody);
    }

    [Theory]
    [InlineData(LogLevel.Trace, "TRACE")]
    [InlineData(LogLevel.Debug, "DEBUG")]
    [InlineData(LogLevel.Info, "INFO")]
    [InlineData(LogLevel.Warn, "WARN")]
    [InlineData(LogLevel.Error, "ERROR")]
    [InlineData(LogLevel.Fatal, "FATAL")]
    [InlineData(LogLevel.Silent, "SILENT")]
    public async Task RegisterSourcesAsync_AllResolvedLevels_MappedToWireFormat(LogLevel level, string expected)
    {
        string? capturedBody = null;
        var (client, _) = CreateClient(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"registered":1}""",
                    Encoding.UTF8, "application/json"),
            };
        });

        var sources = new[] { new LoggerSource("logger", resolvedLevel: level) };
        await client.Logging.Management.RegisterSourcesAsync(sources);

        Assert.NotNull(capturedBody);
        Assert.Contains(expected, capturedBody);
    }
}
