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
    private readonly Task _runner;
    private long _droppedCount;
    private bool _closed;
    private bool _disposed;
    // _inFlight is the number of items the worker has dequeued but
    // not yet finished POSTing. FlushAsync must wait on both queue
    // empty AND _inFlight == 0 — otherwise it can return while a
    // just-dequeued item is still in the middle of its HTTP round-
    // trip, and an immediately following ListAsync call would miss
    // the event.
    private int _inFlight;

    public AuditEventBuffer(GenAudit.AuditClient gen)
    {
        _gen = gen;
        _runner = Task.Run(RunAsync);
    }

    /// <summary>Enqueue an event; may evict the oldest item under overflow.</summary>
    public void Enqueue(GenAudit.EventRequest body, string? idempotencyKey)
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

    /// <summary>Block until the buffer is idle or the timeout elapses.</summary>
    public async Task FlushAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            bool idle;
            lock (_lock) idle = _queue.Count == 0 && _inFlight == 0;
            if (idle) return;
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
        // Best-effort drain inside the flush window, then drop anything
        // still queued. Bounding dispose is preferable to letting it
        // block until the queue empties — a saturated buffer at close
        // time would otherwise pin the caller for minutes.
        await FlushAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        lock (_lock) _closed = true;
        TrySignalWake();
        await _runner.ConfigureAwait(false);
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
                // Once DisposeAsync flips _closed we exit on the next loop
                // turn — anything left in the queue is dropped. The
                // pre-close FlushAsync(5s) is the best-effort drain
                // window; past that, bounded dispose wins over hopeful
                // delivery.
                shouldExit = _closed;
                if (_queue.First != null && _queue.First.Value.NextRetryAt > DateTime.MinValue)
                {
                    var until = (int)(_queue.First.Value.NextRetryAt - DateTime.UtcNow).TotalMilliseconds;
                    if (until > 0 && until < sleepMs) sleepMs = until;
                }
            }
            if (shouldExit) return;
            await _wake.WaitAsync(sleepMs).ConfigureAwait(false);
        }
    }

    private async Task DrainOnceAsync()
    {
        while (true)
        {
            PendingEvent? head;
            lock (_lock)
            {
                // Honor _closed mid-drain too — without this, the inner
                // loop will keep dispatching queued items even after
                // DisposeAsync has flipped _closed, blowing past the
                // bounded-dispose window.
                if (_closed) return;
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
                _inFlight++;
            }

            int status = 0;
            try
            {
                await _gen.Record_eventAsync(head.Body, head.IdempotencyKey).ConfigureAwait(false);
                status = 201;
            }
            catch (GenAudit.ApiException apiEx)
            {
                status = apiEx.StatusCode;
            }
            catch (ObjectDisposedException)
            {
                // The underlying HttpClient was disposed out from under us.
                // Retrying will keep failing forever; flush the queue and stop
                // the worker rather than burning MaxAttempts × backoff per item.
                lock (_lock)
                {
                    _droppedCount += _queue.Count + 1;
                    _queue.Clear();
                    _closed = true;
                    _inFlight--;
                }
                Console.Error.WriteLine("[smplkit.audit] HttpClient disposed; remaining events dropped");
                return;
            }
            catch
            {
                status = 0; // transient
            }

            var requeue = HandleOutcome(head, status);
            lock (_lock)
            {
                _inFlight--;
                if (requeue != null)
                {
                    _queue.AddFirst(requeue);
                }
            }
            if (requeue != null)
            {
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
        public GenAudit.EventRequest Body { get; }
        public string? IdempotencyKey { get; }
        public int Attempts { get; set; }
        public DateTime NextRetryAt { get; set; }
        public PendingEvent(GenAudit.EventRequest body, string? idempotencyKey)
        {
            Body = body;
            IdempotencyKey = idempotencyKey;
        }
    }
}
