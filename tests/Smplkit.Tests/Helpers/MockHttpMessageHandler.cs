namespace Smplkit.Tests.Helpers;

/// <summary>
/// A test double for <see cref="HttpMessageHandler"/> that delegates to a
/// caller-provided function. Allows injecting predetermined HTTP responses
/// into an <see cref="HttpClient"/> for unit testing.
/// </summary>
public class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

    /// <summary>
    /// Gets the last request that was sent through this handler.
    /// </summary>
    public HttpRequestMessage? LastRequest { get; private set; }

    /// <summary>
    /// Gets all requests that were sent through this handler.
    /// </summary>
    public List<HttpRequestMessage> Requests { get; } = new();

    public MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastRequest = request;
        Requests.Add(request);
        return await _handler(request);
    }
}
