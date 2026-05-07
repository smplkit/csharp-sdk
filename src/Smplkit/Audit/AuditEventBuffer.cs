using System.Collections.Concurrent;
using GenAudit = Smplkit.Internal.Generated.Audit;

namespace Smplkit.Audit;

/// <summary>
/// Bounded in-memory buffer + worker task for fire-and-forget audit emits
/// (ADR-047 §2.6).
///
/// <para>Calls to <see cref="Enqueue"/> return immediately. The worker
/// drains on a periodic tick or once depth crosses the high-water mark,
/// retries transient failures with exponential backoff, drops permanent
/// 4xx (other than 429), and evicts the oldest item under sustained
/// back-pressure.</para>
/// </summary>
internal sealed class AuditEventBuffer : IAsyncDisposable
{
    internal const int MaxBufferSize = 1000;
    internal const int Watermark = 50;
    internal const int FlushIntervalMs = 5_000;
    internal const int MaxAttempts = 5;
    internal const int InitialBackoffMs = 250;
    internal const int MaxBackoffMs = 8_000;

    private readonly GenAudit.AuditClient _gen;
    private readonly LinkedList<PendingEvent> _queue = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _runner;
    private long _droppedCount;
    private bool _closed;
    private bool _disposed;

    public AuditEventBuffer(GenAudit.AuditClient gen)
    {
        _gen = gen;
        _runner = Task.Run(RunAsync);
    }

    /// <summary>Enqueue an event; may evict the oldest item under overflow.</summary>
    public void Enqueue(GenAudit.EventResponse body, string? idempotencyKey)
    {
        int depth;
        lock (_lock)
        {
            if (_closed) return;
            if (_queue.Count >= MaxBufferSize)
            {
                _queue.RemoveFirst();
                _droppedCount++;
                Console.Error.WriteLine(
                    $"[smplkit.audit] buffer full (size={MaxBufferSize}); dropped oldest event (total dropped={_droppedCount})");
            }
            _queue.AddLast(new PendingEvent(body, idempotencyKey));
            depth = _queue.Count;
        }
        if (depth >= Watermark)
        {
            TrySignalWake();
        }
    }

    /// <summary>Block until the queue is empty or the timeout elapses.</summary>
    public async Task FlushAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            bool empty;
            lock (_lock) empty = _queue.Count == 0;
            if (empty) return;
            if (DateTime.UtcNow >= deadline)
            {
                Console.Error.WriteLine($"[smplkit.audit] flush timed out after {timeout.TotalMilliseconds}ms");
                return;
            }
            TrySignalWake();
            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await FlushAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        lock (_lock) _closed = true;
        TrySignalWake();
        // Worker exits cleanly when ``_closed && _queue.Count == 0`` —
        // no need to cancel the ambient token, which would force us into
        // the catch-OperationCanceledException defensive path.
        await _runner.ConfigureAwait(false);
        _cts.Dispose();
        _wake.Dispose();
    }

    private void TrySignalWake()
    {
        try
        {
            // Release returns false if at max — treat that as "already pending".
            _wake.Release();
        }
        catch (SemaphoreFullException) { /* a wake is already pending */ }
    }

    private async Task RunAsync()
    {
        while (true)
        {
            await DrainOnceAsync().ConfigureAwait(false);
            bool shouldExit;
            int sleepMs = FlushIntervalMs;
            lock (_lock)
            {
                shouldExit = _closed && _queue.Count == 0;
                if (_queue.First != null && _queue.First.Value.NextRetryAt > DateTime.MinValue)
                {
                    var until = (int)(_queue.First.Value.NextRetryAt - DateTime.UtcNow).TotalMilliseconds;
                    if (until > 0 && until < sleepMs) sleepMs = until;
                }
            }
            if (shouldExit) return;
            try
            {
                await _wake.WaitAsync(sleepMs, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task DrainOnceAsync()
    {
        while (true)
        {
            PendingEvent? head;
            lock (_lock)
            {
                if (_queue.First == null)
                {
                    return;
                }
                head = _queue.First.Value;
                if (head.NextRetryAt > DateTime.UtcNow)
                {
                    return;
                }
                _queue.RemoveFirst();
            }

            int status = 0;
            try
            {
                await _gen.Create_eventAsync(head.Body, head.IdempotencyKey).ConfigureAwait(false);
                status = 201;
            }
            catch (GenAudit.ApiException apiEx)
            {
                status = apiEx.StatusCode;
            }
            catch
            {
                status = 0; // transient
            }

            var requeue = HandleOutcome(head, status);
            if (requeue != null)
            {
                lock (_lock) _queue.AddFirst(requeue);
                return;
            }
        }
    }

    private static PendingEvent? HandleOutcome(PendingEvent item, int status)
    {
        if (status >= 200 && status < 300) return null;
        if (status >= 400 && status < 500 && status != 429)
        {
            Console.Error.WriteLine($"[smplkit.audit] permanent failure status={status}; event dropped");
            return null;
        }
        item.Attempts++;
        if (item.Attempts >= MaxAttempts)
        {
            Console.Error.WriteLine($"[smplkit.audit] gave up after {item.Attempts} attempts (status={status})");
            return null;
        }
        var backoff = Math.Min(MaxBackoffMs, InitialBackoffMs * (1 << (item.Attempts - 1)));
        var jitter = Random.Shared.Next(0, backoff / 4 + 1);
        item.NextRetryAt = DateTime.UtcNow.AddMilliseconds(backoff + jitter);
        return item;
    }

    private sealed class PendingEvent
    {
        public GenAudit.EventResponse Body { get; }
        public string? IdempotencyKey { get; }
        public int Attempts { get; set; }
        public DateTime NextRetryAt { get; set; }
        public PendingEvent(GenAudit.EventResponse body, string? idempotencyKey)
        {
            Body = body;
            IdempotencyKey = idempotencyKey;
        }
    }
}
