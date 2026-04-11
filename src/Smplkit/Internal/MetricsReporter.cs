using System.Text;
using System.Text.Json;

namespace Smplkit.Internal;

/// <summary>
/// Accumulates usage metrics in memory and periodically flushes them to the
/// app service via POST /api/v1/metrics/bulk.
/// </summary>
internal sealed class MetricsReporter : IDisposable
{
    private const string AppBaseUrl = "https://app.smplkit.com";
    private const string MetricsEndpoint = "/api/v1/metrics/bulk";
    private const string JsonApiMediaType = "application/vnd.api+json";
    private const int DefaultFlushIntervalSeconds = 60;

    private readonly HttpClient _httpClient;
    private readonly string _environment;
    private readonly string _service;
    private readonly int _flushIntervalSeconds;

    private Dictionary<string, Counter> _counters = new();
    private Dictionary<string, Counter> _gauges = new();
    private readonly object _lock = new();
    private Timer? _timer;
    private bool _closed;

    internal MetricsReporter(
        HttpClient httpClient,
        string environment,
        string service,
        int flushIntervalSeconds = DefaultFlushIntervalSeconds)
    {
        _httpClient = httpClient;
        _environment = environment;
        _service = service;
        _flushIntervalSeconds = flushIntervalSeconds;
    }

    // ------------------------------------------------------------------
    // Recording API
    // ------------------------------------------------------------------

    internal void Record(
        string name,
        int value = 1,
        string? unit = null,
        Dictionary<string, string>? dimensions = null)
    {
        var key = MakeKey(name, dimensions);
        lock (_lock)
        {
            if (!_counters.TryGetValue(key, out var counter))
            {
                counter = new Counter { Unit = unit };
                _counters[key] = counter;
            }
            counter.Value += value;
            if (counter.Unit is null && unit is not null)
                counter.Unit = unit;
            MaybeStartTimer();
        }
    }

    internal void RecordGauge(
        string name,
        int value,
        string? unit = null,
        Dictionary<string, string>? dimensions = null)
    {
        var key = MakeKey(name, dimensions);
        lock (_lock)
        {
            if (!_gauges.TryGetValue(key, out var gauge))
            {
                gauge = new Counter { Unit = unit };
                _gauges[key] = gauge;
            }
            gauge.Value = value;
            if (gauge.Unit is null && unit is not null)
                gauge.Unit = unit;
            MaybeStartTimer();
        }
    }

    // ------------------------------------------------------------------
    // Flush / close
    // ------------------------------------------------------------------

    internal void Flush()
    {
        FlushInternal();
    }

    internal void Close()
    {
        if (_closed) return;
        _closed = true;
        _timer?.Dispose();
        _timer = null;
        FlushInternal();
    }

    public void Dispose()
    {
        Close();
    }

    // ------------------------------------------------------------------
    // Internal
    // ------------------------------------------------------------------

    private string MakeKey(string name, Dictionary<string, string>? dimensions)
    {
        // Build merged dimensions: always include environment + service
        var merged = new SortedDictionary<string, string>
        {
            ["environment"] = _environment,
            ["service"] = _service,
        };
        if (dimensions is not null)
        {
            foreach (var (k, v) in dimensions)
                merged[k] = v;
        }

        // Build a stable key: "name|k1=v1,k2=v2"
        var sb = new StringBuilder(name);
        sb.Append('|');
        var first = true;
        foreach (var (k, v) in merged)
        {
            if (!first) sb.Append(',');
            sb.Append(k).Append('=').Append(v);
            first = false;
        }
        return sb.ToString();
    }

    private static Dictionary<string, string> ParseDimensions(string key)
    {
        var pipeIdx = key.IndexOf('|');
        if (pipeIdx < 0 || pipeIdx == key.Length - 1)
            return new Dictionary<string, string>();

        var dimsPart = key[(pipeIdx + 1)..];
        var result = new Dictionary<string, string>();
        foreach (var pair in dimsPart.Split(','))
        {
            var eqIdx = pair.IndexOf('=');
            if (eqIdx > 0)
                result[pair[..eqIdx]] = pair[(eqIdx + 1)..];
        }
        return result;
    }

    private static string ParseName(string key)
    {
        var pipeIdx = key.IndexOf('|');
        return pipeIdx < 0 ? key : key[..pipeIdx];
    }

    private void MaybeStartTimer()
    {
        // Must be called under _lock
        if (_timer is null && !_closed)
        {
            _timer = new Timer(
                _ => Tick(),
                null,
                TimeSpan.FromSeconds(_flushIntervalSeconds),
                Timeout.InfiniteTimeSpan);
        }
    }

    private void Tick()
    {
        FlushInternal();
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
            if (!_closed && (_counters.Count > 0 || _gauges.Count > 0))
                MaybeStartTimer();
        }
    }

    private void FlushInternal()
    {
        Dictionary<string, Counter> counters;
        Dictionary<string, Counter> gauges;

        lock (_lock)
        {
            counters = _counters;
            gauges = _gauges;
            _counters = new Dictionary<string, Counter>();
            _gauges = new Dictionary<string, Counter>();
        }

        if (counters.Count == 0 && gauges.Count == 0)
            return;

        var payload = BuildPayload(counters, gauges);
        try
        {
            var json = JsonSerializer.Serialize(payload, JsonOptions.Default);
            var content = new StringContent(json, Encoding.UTF8, JsonApiMediaType);
            _httpClient.PostAsync($"{AppBaseUrl}{MetricsEndpoint}", content)
                .GetAwaiter().GetResult();
        }
        catch
        {
            // Telemetry never throws — silently discard all errors
        }
    }

    private Dictionary<string, object> BuildPayload(
        Dictionary<string, Counter> counters,
        Dictionary<string, Counter> gauges)
    {
        var data = new List<object>(counters.Count + gauges.Count);
        foreach (var (key, counter) in counters)
            data.Add(BuildEntry(key, counter));
        foreach (var (key, gauge) in gauges)
            data.Add(BuildEntry(key, gauge));
        return new Dictionary<string, object> { ["data"] = data };
    }

    private object BuildEntry(string key, Counter counter)
    {
        return new Dictionary<string, object?>
        {
            ["type"] = "metric",
            ["attributes"] = new Dictionary<string, object?>
            {
                ["name"] = ParseName(key),
                ["value"] = counter.Value,
                ["unit"] = counter.Unit,
                ["period_seconds"] = _flushIntervalSeconds,
                ["dimensions"] = ParseDimensions(key),
                ["recorded_at"] = counter.WindowStart.ToString("O"),
            },
        };
    }

    // ------------------------------------------------------------------
    // Counter type
    // ------------------------------------------------------------------

    internal sealed class Counter
    {
        internal int Value { get; set; }
        internal string? Unit { get; set; }
        internal DateTime WindowStart { get; } = DateTime.UtcNow;
    }
}
