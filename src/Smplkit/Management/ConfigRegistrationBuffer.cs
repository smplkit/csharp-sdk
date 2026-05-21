namespace Smplkit.Management;

/// <summary>
/// Thread-safe batch buffer for config declarations. Mirrors Python's
/// <c>_ConfigRegistrationBuffer</c>: per-config metadata is retained across
/// flushes so post-drain deltas re-attribute correctly, and items are
/// dedup'd per <c>(configId, itemKey)</c> so an already-sent item is
/// never re-sent.
/// </summary>
internal sealed class ConfigRegistrationBuffer
{
    internal readonly record struct ItemEntry(object? DefaultValue, string ItemType, string? Description);

    internal sealed class Entry
    {
        public required string Id { get; init; }
        public Dictionary<string, ItemEntry> Items { get; } = new();
        public string? Service { get; init; }
        public string? Environment { get; init; }
        public string? Parent { get; init; }
        public string? Name { get; init; }
        public string? Description { get; init; }
    }

    private readonly record struct Meta(string? Service, string? Environment, string? Parent, string? Name, string? Description);

    private readonly Dictionary<string, Entry> _pending = new();
    private readonly Dictionary<string, Meta> _meta = new();
    private readonly HashSet<string> _sentItems = new();
    private readonly object _lock = new();

    /// <summary>Idempotent — first writer's metadata wins.</summary>
    internal void Declare(string configId, string? service, string? environment,
        string? parent, string? name, string? description)
    {
        lock (_lock)
        {
            if (_meta.ContainsKey(configId)) return;
            var meta = new Meta(service, environment, parent, name, description);
            _meta[configId] = meta;
            _pending[configId] = BuildEntry(configId, meta);
        }
    }

    /// <summary>
    /// Queue an item declaration for an already-declared config. Items
    /// already sent in a previous Drain are skipped.
    /// </summary>
    internal void AddItem(string configId, string itemKey, string itemType,
        object? defaultValue, string? description)
    {
        lock (_lock)
        {
            if (!_meta.TryGetValue(configId, out var meta)) return;
            var sentKey = configId + "::" + itemKey;
            if (_sentItems.Contains(sentKey)) return;
            if (!_pending.TryGetValue(configId, out var entry))
            {
                entry = BuildEntry(configId, meta);
                _pending[configId] = entry;
            }
            if (entry.Items.ContainsKey(itemKey)) return;
            entry.Items[itemKey] = new ItemEntry(defaultValue, itemType, description);
        }
    }

    private static Entry BuildEntry(string configId, Meta meta) => new()
    {
        Id = configId,
        Service = meta.Service,
        Environment = meta.Environment,
        Parent = meta.Parent,
        Name = meta.Name,
        Description = meta.Description,
    };

    /// <summary>Returns and clears the pending batch; records sent items.</summary>
    internal List<Entry> Drain()
    {
        lock (_lock)
        {
            if (_pending.Count == 0) return new List<Entry>();
            var batch = new List<Entry>(_pending.Values);
            foreach (var entry in batch)
            {
                foreach (var itemKey in entry.Items.Keys)
                {
                    _sentItems.Add(entry.Id + "::" + itemKey);
                }
            }
            _pending.Clear();
            return batch;
        }
    }

    internal int PendingCount
    {
        get { lock (_lock) { return _pending.Count; } }
    }
}
