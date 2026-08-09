using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Smplkit.Tests.Helpers;

/// <summary>
/// A readable stream tests push SSE bytes into. Reads block until bytes are
/// pushed; <see cref="Complete"/> simulates the server closing the stream
/// (EOF). Chunk boundaries are preserved, so tests can split frames across
/// reads exactly as a network would.
/// </summary>
internal sealed class SsePushStream : Stream
{
    private readonly SemaphoreSlim _available = new(0);
    private readonly ConcurrentQueue<byte[]> _chunks = new();
    private byte[]? _current;
    private int _offset;
    private volatile bool _completed;

    public void Push(string text) => Push(Encoding.UTF8.GetBytes(text));

    public void Push(byte[] bytes)
    {
        _chunks.Enqueue(bytes);
        _available.Release();
    }

    /// <summary>Simulates the server closing the stream (EOF).</summary>
    public void Complete()
    {
        _completed = true;
        _available.Release();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        while (true)
        {
            if (_current is not null)
            {
                var n = Math.Min(buffer.Length, _current.Length - _offset);
                _current.AsMemory(_offset, n).CopyTo(buffer);
                _offset += n;
                if (_offset >= _current.Length)
                {
                    _current = null;
                    _offset = 0;
                }
                return n;
            }
            if (_chunks.TryDequeue(out var next))
            {
                _current = next;
                _offset = 0;
                continue;
            }
            if (_completed) return 0;
            await _available.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        Complete();
        base.Dispose(disposing);
    }
}

/// <summary>
/// A fake SSE endpoint handing out one response per connect attempt (1-based).
/// Plugs into <c>EventStream</c> as its <c>sendAsync</c> seam.
/// </summary>
internal sealed class SseTestServer
{
    private readonly Func<int, HttpResponseMessage> _respond;
    private int _attempts;

    public SseTestServer(Func<int, HttpResponseMessage> respond) => _respond = respond;

    public List<HttpRequestMessage> Requests { get; } = new();

    public int Attempts => Volatile.Read(ref _attempts);

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        lock (Requests) Requests.Add(request);
        var n = Interlocked.Increment(ref _attempts);
        return Task.FromResult(_respond(n));
    }

    /// <summary>A 200 <c>text/event-stream</c> response wrapping the given stream.</summary>
    public static HttpResponseMessage CreateSseResponse(SsePushStream stream)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream") { CharSet = "utf-8" };
        return response;
    }
}
