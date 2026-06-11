namespace Smplkit.Flags;

// ------------------------------------------------------------------
// Context registration buffer
// ------------------------------------------------------------------

/// <summary>
/// LRU-deduplicated buffer of evaluation contexts awaiting bulk registration.
/// </summary>
/// <remarks>
/// Defined here in <c>Smplkit.Flags</c> and shared by <see cref="Smplkit.Platform.PlatformClient"/>
/// and <see cref="Smplkit.SmplClient"/> as <c>Smplkit.Flags.ContextRegistrationBuffer</c>:
/// the flags client borrows the platform contexts buffer as its evaluation-context
/// registration seam, mirroring how the Python SDK's flags client borrows
/// <c>client.platform.contexts</c>.
/// </remarks>
internal sealed class ContextRegistrationBuffer
{
    private readonly int _lruSize;
    private readonly int _flushSize;
    private readonly LinkedList<(string Type, string Key)> _seenOrder = new();
    private readonly Dictionary<(string Type, string Key), LinkedListNode<(string Type, string Key)>> _seenMap = new();
    private readonly List<Dictionary<string, object?>> _pending = new();
    private readonly object _lock = new();

    internal ContextRegistrationBuffer(int lruSize, int flushSize)
    {
        _lruSize = lruSize;
        _flushSize = flushSize;
    }

    internal void Observe(IEnumerable<Context> contexts)
    {
        lock (_lock)
        {
            foreach (var ctx in contexts)
            {
                var cacheKey = (ctx.Type, ctx.Key);
                if (!_seenMap.ContainsKey(cacheKey))
                {
                    if (_seenMap.Count >= _lruSize)
                    {
                        var oldest = _seenOrder.First!;
                        _seenMap.Remove(oldest.Value);
                        _seenOrder.RemoveFirst();
                    }
                    var node = _seenOrder.AddLast(cacheKey);
                    _seenMap[cacheKey] = node;
                    _pending.Add(new Dictionary<string, object?>
                    {
                        ["id"] = $"{ctx.Type}:{ctx.Key}",
                        ["attributes"] = new Dictionary<string, object?>(ctx.Attributes),
                    });
                }
            }
        }
    }

    internal List<Dictionary<string, object?>> Drain()
    {
        lock (_lock)
        {
            var batch = new List<Dictionary<string, object?>>(_pending);
            _pending.Clear();
            return batch;
        }
    }

    internal int PendingCount
    {
        get { lock (_lock) return _pending.Count; }
    }
}
