using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JsonLogic.Net;
using Newtonsoft.Json.Linq;
using Smplkit.Errors;
using Smplkit.Internal;
using GenApp = Smplkit.Internal.Generated.App;
using GenFlags = Smplkit.Internal.Generated.Flags;

namespace Smplkit.Flags;

/// <summary>
/// Client for the smplkit Flags service. Provides management CRUD operations
/// and prescriptive runtime evaluation via typed flag handles.
/// </summary>
public sealed class FlagsClient
{
    private const int CacheMaxSize = 10_000;
    private const int ContextRegistrationLruSize = 10_000;
    private const int ContextBatchFlushSize = 100;

    private readonly GenFlags.FlagsClient _genFlagsClient;
    private readonly GenApp.AppClient _genAppClient;
    private readonly string _apiKey;
    private readonly Func<SharedWebSocket> _ensureWs;
    private readonly SmplClient? _parent;

    // Runtime state
    private string? _environment;
    private readonly ConcurrentDictionary<string, Dictionary<string, object?>> _flagStore = new();
    private volatile bool _connected;
    private readonly object _initLock = new();
    private readonly ResolutionCache _cache = new(CacheMaxSize);
    private Func<IReadOnlyList<Context>>? _contextProvider;
    private readonly ContextRegistrationBuffer _contextBuffer = new(ContextRegistrationLruSize, ContextBatchFlushSize);
    private readonly ConcurrentDictionary<string, Flag> _handles = new();
    private readonly List<Action<FlagChangeEvent>> _globalListeners = new();
    private readonly ConcurrentDictionary<string, List<Action<FlagChangeEvent>>> _scopedListeners = new();

    // Shared WebSocket
    private SharedWebSocket? _wsManager;

    internal FlagsClient(GeneratedClientFactory clients, string apiKey, Func<SharedWebSocket> ensureWs, SmplClient? parent = null)
    {
        _genFlagsClient = clients.Flags;
        _genAppClient = clients.App;
        _apiKey = apiKey;
        _ensureWs = ensureWs;
        _parent = parent;
    }

    // ------------------------------------------------------------------
    // Management: typed factory methods
    // ------------------------------------------------------------------

    /// <summary>
    /// Create an unsaved boolean flag. Call <see cref="Flag.SaveAsync"/> to persist.
    /// </summary>
    /// <param name="key">The flag key.</param>
    /// <param name="defaultValue">Default boolean value.</param>
    /// <param name="name">Display name. Auto-generated from key if null.</param>
    /// <param name="description">Optional description.</param>
    /// <returns>An unsaved <see cref="BooleanFlag"/>.</returns>
    public BooleanFlag NewBooleanFlag(string key, bool defaultValue, string? name = null, string? description = null)
    {
        return new BooleanFlag(
            client: this,
            id: null,
            key: key,
            name: name ?? Helpers.KeyToDisplayName(key),
            @default: defaultValue,
            values: new List<Dictionary<string, object?>>
            {
                new() { ["name"] = "True", ["value"] = true },
                new() { ["name"] = "False", ["value"] = false },
            },
            description: description,
            environments: new Dictionary<string, Dictionary<string, object?>>(),
            createdAt: null,
            updatedAt: null);
    }

    /// <summary>
    /// Create an unsaved string flag. Call <see cref="Flag.SaveAsync"/> to persist.
    /// </summary>
    /// <param name="key">The flag key.</param>
    /// <param name="defaultValue">Default string value.</param>
    /// <param name="name">Display name. Auto-generated from key if null.</param>
    /// <param name="description">Optional description.</param>
    /// <param name="values">Optional closed set of allowed values.</param>
    /// <returns>An unsaved <see cref="StringFlag"/>.</returns>
    public StringFlag NewStringFlag(string key, string defaultValue, string? name = null, string? description = null, List<Dictionary<string, object?>>? values = null)
    {
        return new StringFlag(
            client: this,
            id: null,
            key: key,
            name: name ?? Helpers.KeyToDisplayName(key),
            @default: defaultValue,
            values: values,
            description: description,
            environments: new Dictionary<string, Dictionary<string, object?>>(),
            createdAt: null,
            updatedAt: null);
    }

    /// <summary>
    /// Create an unsaved number flag. Call <see cref="Flag.SaveAsync"/> to persist.
    /// </summary>
    /// <param name="key">The flag key.</param>
    /// <param name="defaultValue">Default numeric value.</param>
    /// <param name="name">Display name. Auto-generated from key if null.</param>
    /// <param name="description">Optional description.</param>
    /// <param name="values">Optional closed set of allowed values.</param>
    /// <returns>An unsaved <see cref="NumberFlag"/>.</returns>
    public NumberFlag NewNumberFlag(string key, double defaultValue, string? name = null, string? description = null, List<Dictionary<string, object?>>? values = null)
    {
        return new NumberFlag(
            client: this,
            id: null,
            key: key,
            name: name ?? Helpers.KeyToDisplayName(key),
            @default: defaultValue,
            values: values,
            description: description,
            environments: new Dictionary<string, Dictionary<string, object?>>(),
            createdAt: null,
            updatedAt: null);
    }

    /// <summary>
    /// Create an unsaved JSON flag. Call <see cref="Flag.SaveAsync"/> to persist.
    /// </summary>
    /// <param name="key">The flag key.</param>
    /// <param name="defaultValue">Default JSON value.</param>
    /// <param name="name">Display name. Auto-generated from key if null.</param>
    /// <param name="description">Optional description.</param>
    /// <param name="values">Optional closed set of allowed values.</param>
    /// <returns>An unsaved <see cref="JsonFlag"/>.</returns>
    public JsonFlag NewJsonFlag(string key, Dictionary<string, object?> defaultValue, string? name = null, string? description = null, List<Dictionary<string, object?>>? values = null)
    {
        return new JsonFlag(
            client: this,
            id: null,
            key: key,
            name: name ?? Helpers.KeyToDisplayName(key),
            @default: defaultValue,
            values: values,
            description: description,
            environments: new Dictionary<string, Dictionary<string, object?>>(),
            createdAt: null,
            updatedAt: null);
    }

    // ------------------------------------------------------------------
    // Management: CRUD by key
    // ------------------------------------------------------------------

    /// <summary>
    /// Fetches a flag by its human-readable key.
    /// </summary>
    /// <param name="key">The flag key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching <see cref="Flag"/>.</returns>
    /// <exception cref="SmplNotFoundException">If no matching flag exists.</exception>
    public async Task<Flag> GetAsync(string key, CancellationToken ct = default)
    {
        var response = await ApiExceptionMapper.ExecuteAsync(
            () => _genFlagsClient.List_flagsAsync(filterkey: key, cancellationToken: ct)).ConfigureAwait(false);
        if (response.Data is null || response.Data.Count == 0)
            throw new SmplNotFoundException($"Flag with key '{key}' not found");
        return MapFlagResource(response.Data[0])
            ?? throw new SmplNotFoundException($"Flag with key '{key}' not found");
    }

    /// <summary>
    /// Lists all flags.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of <see cref="Flag"/> objects.</returns>
    public async Task<List<Flag>> ListAsync(CancellationToken ct = default)
    {
        var response = await ApiExceptionMapper.ExecuteAsync(
            () => _genFlagsClient.List_flagsAsync(cancellationToken: ct)).ConfigureAwait(false);
        if (response.Data is null) return new List<Flag>();
        return response.Data.Select(r => MapFlagResource(r)!).Where(f => f is not null).ToList();
    }

    /// <summary>
    /// Deletes a flag by its human-readable key.
    /// </summary>
    /// <param name="key">The flag key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="SmplNotFoundException">If no matching flag exists.</exception>
    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var flag = await GetAsync(key, ct).ConfigureAwait(false);
        await ApiExceptionMapper.ExecuteAsync(
            () => _genFlagsClient.Delete_flagAsync(Guid.Parse(flag.Id!), ct)).ConfigureAwait(false);
    }

    /// <summary>Internal: save a flag (create or update).</summary>
    internal async Task<Flag> SaveFlagInternalAsync(Flag flag, CancellationToken ct = default)
    {
        if (flag.Id is null)
        {
            // Create
            var body = BuildCreateFlagBody(flag.Key, flag.Name, flag.Type, flag.Default, flag.Description, flag.Values);
            var response = await ApiExceptionMapper.ExecuteAsync(
                () => _genFlagsClient.Create_flagAsync(body, ct)).ConfigureAwait(false);
            return MapFlagResource(response.Data)
                ?? throw new SmplValidationException("Failed to create flag");
        }
        else
        {
            // Update
            var body = BuildUpdateFlagBody(flag.Key, flag.Name, flag.Type, flag.Default, flag.Values, flag.Description, flag.Environments);
            var response = await ApiExceptionMapper.ExecuteAsync(
                () => _genFlagsClient.Update_flagAsync(Guid.Parse(flag.Id), body, ct)).ConfigureAwait(false);
            return MapFlagResource(response.Data)
                ?? throw new SmplValidationException("Failed to update flag");
        }
    }

    // ------------------------------------------------------------------
    // Runtime: typed flag handles
    // ------------------------------------------------------------------

    /// <summary>
    /// Declare a boolean flag handle with a code-level default.
    /// Evaluation is deferred until <see cref="Flag.Get"/> is called.
    /// </summary>
    /// <param name="key">The flag key.</param>
    /// <param name="defaultValue">The code-level default value.</param>
    /// <returns>A typed flag handle.</returns>
    public BooleanFlag BooleanFlag(string key, bool defaultValue)
    {
        var handle = new BooleanFlag(
            client: this, id: null, key: key, name: key,
            @default: defaultValue,
            values: new List<Dictionary<string, object?>>(),
            description: null,
            environments: new Dictionary<string, Dictionary<string, object?>>(),
            createdAt: null, updatedAt: null);
        _handles[key] = handle;
        return handle;
    }

    /// <summary>
    /// Declare a string flag handle with a code-level default.
    /// Evaluation is deferred until <see cref="Flag.Get"/> is called.
    /// </summary>
    /// <param name="key">The flag key.</param>
    /// <param name="defaultValue">The code-level default value.</param>
    /// <returns>A typed flag handle.</returns>
    public StringFlag StringFlag(string key, string defaultValue)
    {
        var handle = new StringFlag(
            client: this, id: null, key: key, name: key,
            @default: defaultValue,
            values: new List<Dictionary<string, object?>>(),
            description: null,
            environments: new Dictionary<string, Dictionary<string, object?>>(),
            createdAt: null, updatedAt: null);
        _handles[key] = handle;
        return handle;
    }

    /// <summary>
    /// Declare a number flag handle with a code-level default.
    /// Evaluation is deferred until <see cref="Flag.Get"/> is called.
    /// </summary>
    /// <param name="key">The flag key.</param>
    /// <param name="defaultValue">The code-level default value.</param>
    /// <returns>A typed flag handle.</returns>
    public NumberFlag NumberFlag(string key, double defaultValue)
    {
        var handle = new NumberFlag(
            client: this, id: null, key: key, name: key,
            @default: defaultValue,
            values: new List<Dictionary<string, object?>>(),
            description: null,
            environments: new Dictionary<string, Dictionary<string, object?>>(),
            createdAt: null, updatedAt: null);
        _handles[key] = handle;
        return handle;
    }

    /// <summary>
    /// Declare a JSON flag handle with a code-level default.
    /// Evaluation is deferred until <see cref="Flag.Get"/> is called.
    /// </summary>
    /// <param name="key">The flag key.</param>
    /// <param name="defaultValue">The code-level default value.</param>
    /// <returns>A typed flag handle.</returns>
    public JsonFlag JsonFlag(string key, Dictionary<string, object?> defaultValue)
    {
        var handle = new JsonFlag(
            client: this, id: null, key: key, name: key,
            @default: defaultValue,
            values: new List<Dictionary<string, object?>>(),
            description: null,
            environments: new Dictionary<string, Dictionary<string, object?>>(),
            createdAt: null, updatedAt: null);
        _handles[key] = handle;
        return handle;
    }

    // ------------------------------------------------------------------
    // Runtime: context provider
    // ------------------------------------------------------------------

    /// <summary>
    /// Register a context provider function. Called on every flag evaluation
    /// to supply the current request context.
    /// </summary>
    /// <param name="provider">A function returning the current contexts.</param>
    public void SetContextProvider(Func<IReadOnlyList<Context>> provider)
    {
        _contextProvider = provider;
    }

    // ------------------------------------------------------------------
    // Runtime: lazy initialization
    // ------------------------------------------------------------------

    /// <summary>
    /// Ensures the flags runtime is initialized. Called on first <see cref="Flag.Get"/>.
    /// Fetches all flag definitions and opens the shared WebSocket.
    /// </summary>
    internal void EnsureInitialized()
    {
        if (_connected) return;
        lock (_initLock)
        {
            if (_connected) return;
            _environment = _parent?.Environment;

            // Fire-and-forget service context registration
            if (_parent?.Service is { Length: > 0 } svc)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ApiExceptionMapper.ExecuteAsync(async () =>
                            await _genAppClient.Bulk_register_contextsAsync(
                                new GenApp.ContextBulkRegister
                                {
                                    Contexts = new List<GenApp.ContextBulkItem>
                                    {
                                        new()
                                        {
                                            Type = "service",
                                            Key = svc,
                                            Attributes = new Dictionary<string, object?> { ["name"] = svc },
                                        },
                                    },
                                }).ConfigureAwait(false)).ConfigureAwait(false);
                    }
                    catch { /* fire-and-forget */ }
                });
            }

            FetchAllFlagsAsync().GetAwaiter().GetResult();
            _connected = true;
            _cache.Clear();

            _wsManager = _ensureWs();
            _wsManager.On("flag_changed", HandleFlagChanged);
            _wsManager.On("flag_deleted", HandleFlagDeleted);
        }
    }

    // ------------------------------------------------------------------
    // Runtime: refresh
    // ------------------------------------------------------------------

    /// <summary>
    /// Re-fetch all flag definitions and clear evaluation results. Fires change
    /// listeners for all flags.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await FetchAllFlagsAsync(ct).ConfigureAwait(false);
        _cache.Clear();
        FireChangeListenersAll("manual");
    }

    /// <summary>
    /// Gets the current WebSocket connection status.
    /// </summary>
    public string ConnectionStatus => _wsManager?.ConnectionStatus ?? "disconnected";

    /// <summary>
    /// Gets the cache statistics.
    /// </summary>
    public FlagStats Stats => new(_cache.CacheHits, _cache.CacheMisses);

    // ------------------------------------------------------------------
    // Runtime: change listeners
    // ------------------------------------------------------------------

    /// <summary>
    /// Register a global change listener that fires when any flag changes.
    /// </summary>
    /// <param name="callback">Called with a <see cref="FlagChangeEvent"/> on each change.</param>
    public void OnChange(Action<FlagChangeEvent> callback)
    {
        _globalListeners.Add(callback);
    }

    /// <summary>
    /// Register a change listener scoped to a specific flag key.
    /// </summary>
    /// <param name="flagKey">The flag key to listen for.</param>
    /// <param name="callback">Called with a <see cref="FlagChangeEvent"/> when this flag changes.</param>
    public void OnChange(string flagKey, Action<FlagChangeEvent> callback)
    {
        var list = _scopedListeners.GetOrAdd(flagKey, _ => new List<Action<FlagChangeEvent>>());
        lock (list)
        {
            list.Add(callback);
        }
    }

    // ------------------------------------------------------------------
    // Runtime: context registration
    // ------------------------------------------------------------------

    /// <summary>
    /// Register a context for server-side targeting and analytics.
    /// </summary>
    /// <param name="context">A single context to register.</param>
    public void Register(Context context)
    {
        _contextBuffer.Observe(new[] { context });
    }

    /// <summary>
    /// Register contexts for server-side targeting and analytics.
    /// </summary>
    /// <param name="contexts">Contexts to register.</param>
    public void Register(IEnumerable<Context> contexts)
    {
        _contextBuffer.Observe(contexts);
    }

    /// <summary>
    /// Send any buffered context registrations to the server.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task FlushContextsAsync(CancellationToken ct = default)
    {
        var batch = _contextBuffer.Drain();
        if (batch.Count == 0) return;
        try
        {
            var items = batch.Select(b => new GenApp.ContextBulkItem
            {
                Type = b.TryGetValue("id", out var id) && id is string idStr && idStr.Contains(':')
                    ? idStr.Split(':')[0]
                    : "",
                Key = b.TryGetValue("id", out var id2) && id2 is string idStr2 && idStr2.Contains(':')
                    ? idStr2[(idStr2.IndexOf(':') + 1)..]
                    : "",
                Attributes = b.TryGetValue("attributes", out var attrs) ? attrs ?? new object() : new object(),
            }).ToList();

            await ApiExceptionMapper.ExecuteAsync(
                () => _genAppClient.Bulk_register_contextsAsync(
                    new GenApp.ContextBulkRegister { Contexts = items }, ct)).ConfigureAwait(false);
        }
        catch { /* Context registration is fire-and-forget */ }
    }

    // ------------------------------------------------------------------
    // Internal: evaluation
    // ------------------------------------------------------------------

    internal object? EvaluateHandle(string key, object? defaultValue, IReadOnlyList<Context>? context)
    {
        EnsureInitialized();

        Dictionary<string, object?> evalDict;
        if (context is not null)
        {
            evalDict = ContextsToEvalDict(context);
        }
        else if (_contextProvider is not null)
        {
            var contexts = _contextProvider();
            evalDict = ContextsToEvalDict(contexts);
            _contextBuffer.Observe(contexts);
            if (_contextBuffer.PendingCount >= ContextBatchFlushSize)
                Task.Run(() => FlushContextsAsync());
        }
        else
        {
            evalDict = new Dictionary<string, object?>();
        }

        // Auto-inject service context from parent SmplClient
        if (_parent?.Service is { Length: > 0 } svc && !evalDict.ContainsKey("service"))
            evalDict["service"] = new Dictionary<string, object?> { ["key"] = svc };

        var ctxHash = HashContext(evalDict);
        var cacheKey = $"{key}:{ctxHash}";

        var (hit, cachedValue) = _cache.Get(cacheKey);
        if (hit) return cachedValue;

        if (!_flagStore.TryGetValue(key, out var flagDef))
        {
            _cache.Put(cacheKey, defaultValue);
            return defaultValue;
        }

        var value = EvaluateFlag(flagDef, _environment, evalDict);
        value ??= defaultValue;

        _cache.Put(cacheKey, value);
        return value;
    }

    // ------------------------------------------------------------------
    // Internal: event handlers (called by SharedWebSocket)
    // ------------------------------------------------------------------

    private void HandleFlagChanged(Dictionary<string, object?> data)
    {
        var flagKey = data.TryGetValue("key", out var k) ? k as string : null;
        try
        {
            var response = _genFlagsClient.List_flagsAsync().GetAwaiter().GetResult();
            if (response.Data is not null)
            {
                _flagStore.Clear();
                foreach (var resource in response.Data)
                {
                    var flag = ParseFlagDef(resource);
                    if (flag is not null && flag.TryGetValue("key", out var fk) && fk is string fks)
                        _flagStore[fks] = flag;
                }
            }
        }
        catch { /* Ignore refresh errors */ }

        _cache.Clear();
        FireChangeListeners(flagKey, "websocket");
    }

    private void HandleFlagDeleted(Dictionary<string, object?> data)
    {
        HandleFlagChanged(data);
    }

    // ------------------------------------------------------------------
    // Internal: flag store
    // ------------------------------------------------------------------

    private async Task FetchAllFlagsAsync(CancellationToken ct = default)
    {
        var response = await ApiExceptionMapper.ExecuteAsync(
            () => _genFlagsClient.List_flagsAsync(cancellationToken: ct)).ConfigureAwait(false);
        _flagStore.Clear();
        if (response.Data is null) return;
        foreach (var resource in response.Data)
        {
            var flag = ParseFlagDef(resource);
            if (flag is not null && flag.TryGetValue("key", out var k) && k is string ks)
                _flagStore[ks] = flag;
        }
    }

    // ------------------------------------------------------------------
    // Internal: change listeners
    // ------------------------------------------------------------------

    private void FireChangeListeners(string? flagKey, string source)
    {
        if (flagKey is null) return;
        var evt = new FlagChangeEvent(flagKey, source);
        foreach (var cb in _globalListeners)
        {
            try { cb(evt); }
            catch { /* Ignore listener exceptions */ }
        }
        if (_scopedListeners.TryGetValue(flagKey, out var scopedList))
        {
            List<Action<FlagChangeEvent>> snapshot;
            lock (scopedList)
            {
                snapshot = new List<Action<FlagChangeEvent>>(scopedList);
            }
            foreach (var cb in snapshot)
            {
                try { cb(evt); }
                catch { /* Ignore listener exceptions */ }
            }
        }
    }

    private void FireChangeListenersAll(string source)
    {
        foreach (var key in _flagStore.Keys)
            FireChangeListeners(key, source);
    }

    // ------------------------------------------------------------------
    // Helpers: JSON Logic evaluation
    // ------------------------------------------------------------------

    private static readonly JsonLogicEvaluator JsonLogicEval = new(EvaluateOperators.Default);

    internal static object? EvaluateFlag(
        Dictionary<string, object?> flagDef,
        string? environment,
        Dictionary<string, object?> evalDict)
    {
        var flagDefault = flagDef.TryGetValue("default", out var fd) ? fd : null;

        if (environment is null || !flagDef.TryGetValue("environments", out var envsObj) || envsObj is null)
            return flagDefault;

        Dictionary<string, object?>? envConfig = null;
        if (envsObj is Dictionary<string, Dictionary<string, object?>> typedEnvs)
        {
            if (!typedEnvs.TryGetValue(environment, out var ec)) return flagDefault;
            envConfig = ec;
        }
        else if (envsObj is Dictionary<string, object?> untypedEnvs)
        {
            if (!untypedEnvs.TryGetValue(environment, out var ecObj)) return flagDefault;
            envConfig = ecObj as Dictionary<string, object?>;
        }

        if (envConfig is null) return flagDefault;

        var envDefault = envConfig.TryGetValue("default", out var ed) ? ed : null;
        var fallback = envDefault ?? flagDefault;

        if (envConfig.TryGetValue("enabled", out var enabledObj))
        {
            bool enabled = enabledObj switch
            {
                bool b => b,
                JsonElement je when je.ValueKind == JsonValueKind.True => true,
                JsonElement je when je.ValueKind == JsonValueKind.False => false,
                _ => false,
            };
            if (!enabled) return fallback;
        }
        else
        {
            return fallback;
        }

        var rules = GetRulesList(envConfig);
        foreach (var rule in rules)
        {
            if (rule is not Dictionary<string, object?> ruleDict) continue;
            var logic = ruleDict.TryGetValue("logic", out var l) ? l : null;
            if (logic is null || (logic is Dictionary<string, object?> ld && ld.Count == 0))
                continue;

            try
            {
                var logicJson = JsonSerializer.Serialize(logic, JsonOptions.Default);
                var dataJson = JsonSerializer.Serialize(evalDict, JsonOptions.Default);
                var logicToken = JToken.Parse(logicJson);
                var dataToken = JToken.Parse(dataJson);

                var result = JsonLogicEval.Apply(logicToken, dataToken);
                if (IsTruthy(result as JToken ?? JToken.FromObject(result ?? false)))
                    return ruleDict.TryGetValue("value", out var v) ? NormalizeValue(v) : null;
            }
            catch
            {
                continue;
            }
        }

        return fallback;
    }

    private static List<object?> GetRulesList(Dictionary<string, object?> envConfig)
    {
        if (!envConfig.TryGetValue("rules", out var rulesObj)) return new List<object?>();

        if (rulesObj is List<object?> list) return list;

        if (rulesObj is JsonElement je && je.ValueKind == JsonValueKind.Array)
        {
            var result = new List<object?>();
            foreach (var elem in je.EnumerateArray())
            {
                result.Add(Config.Resolver.Normalize(elem));
            }
            return result;
        }

        if (rulesObj is object?[] arr)
            return arr.ToList();

        return new List<object?>();
    }

    private static bool IsTruthy(JToken? token)
    {
        if (token is null) return false;
        return token.Type switch
        {
            JTokenType.Boolean => token.Value<bool>(),
            JTokenType.Integer => token.Value<long>() != 0,
            JTokenType.Float => token.Value<double>() != 0.0,
            JTokenType.String => !string.IsNullOrEmpty(token.Value<string>()),
            JTokenType.Null => false,
            _ => true,
        };
    }

    private static object? NormalizeValue(object? value)
    {
        if (value is JsonElement je)
            return Config.Resolver.Normalize(je);
        return value;
    }

    // ------------------------------------------------------------------
    // Helpers: context
    // ------------------------------------------------------------------

    private static Dictionary<string, object?> ContextsToEvalDict(IEnumerable<Context> contexts)
    {
        var result = new Dictionary<string, object?>();
        foreach (var ctx in contexts)
        {
            var entry = new Dictionary<string, object?>(ctx.Attributes) { ["key"] = ctx.Key };
            result[ctx.Type] = entry;
        }
        return result;
    }

    private static string HashContext(Dictionary<string, object?> evalDict)
    {
        var serialized = JsonSerializer.Serialize(evalDict, new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            WriteIndented = false,
        });
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(serialized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // ------------------------------------------------------------------
    // Helpers: model mapping
    // ------------------------------------------------------------------

    private Flag? MapFlagResource(GenFlags.FlagResource? resource)
    {
        if (resource?.Attributes is null) return null;
        var attrs = resource.Attributes;

        List<Dictionary<string, object?>>? values = null;
        if (attrs.Values is not null)
        {
            values = new List<Dictionary<string, object?>>();
            foreach (var v in attrs.Values)
                values.Add(new Dictionary<string, object?> { ["name"] = v.Name, ["value"] = NormalizeValue(v.Value) });
        }

        var environments = ExtractEnvironments(attrs.Environments);

        DateTime? createdAt = null;
        if (attrs.Created_at is DateTimeOffset createdDto) createdAt = createdDto.DateTime;

        DateTime? updatedAt = null;
        if (attrs.Updated_at is DateTimeOffset updatedDto) updatedAt = updatedDto.DateTime;

        return new Flag(
            client: this,
            id: resource.Id ?? string.Empty,
            key: attrs.Key ?? string.Empty,
            name: attrs.Name ?? string.Empty,
            type: attrs.Type ?? "BOOLEAN",
            @default: NormalizeValue(attrs.Default),
            values: values,
            description: attrs.Description,
            environments: environments,
            createdAt: createdAt,
            updatedAt: updatedAt);
    }

    private static Dictionary<string, object?>? ParseFlagDef(GenFlags.FlagResource? resource)
    {
        if (resource?.Attributes is null) return null;
        var attrs = resource.Attributes;

        List<Dictionary<string, object?>>? values = null;
        if (attrs.Values is not null)
        {
            values = new List<Dictionary<string, object?>>();
            foreach (var v in attrs.Values)
                values.Add(new Dictionary<string, object?> { ["name"] = v.Name, ["value"] = NormalizeValue(v.Value) });
        }

        var environments = ExtractEnvironments(attrs.Environments);

        return new Dictionary<string, object?>
        {
            ["key"] = attrs.Key,
            ["name"] = attrs.Name,
            ["type"] = attrs.Type,
            ["default"] = NormalizeValue(attrs.Default),
            ["values"] = values,
            ["description"] = attrs.Description,
            ["environments"] = environments,
        };
    }

    private static Dictionary<string, Dictionary<string, object?>> ExtractEnvironments(
        IDictionary<string, GenFlags.FlagEnvironment>? environments)
    {
        if (environments is null) return new Dictionary<string, Dictionary<string, object?>>();

        var result = new Dictionary<string, Dictionary<string, object?>>();
        foreach (var (envName, envData) in environments)
        {
            var normalized = new Dictionary<string, object?>
            {
                ["enabled"] = envData.Enabled,
                ["default"] = NormalizeValue(envData.Default),
            };
            if (envData.Rules is not null)
            {
                var rules = new List<object?>();
                foreach (var rule in envData.Rules)
                {
                    rules.Add(new Dictionary<string, object?>
                    {
                        ["description"] = rule.Description,
                        ["logic"] = NormalizeValue(rule.Logic),
                        ["value"] = NormalizeValue(rule.Value),
                    });
                }
                normalized["rules"] = rules;
            }
            result[envName] = normalized;
        }
        return result;
    }

    // ------------------------------------------------------------------
    // Helpers: request body building
    // ------------------------------------------------------------------

    private static GenFlags.Response_Flag_ BuildCreateFlagBody(
        string key, string name, string type, object? @default,
        string? description, List<Dictionary<string, object?>>? values)
    {
        var flagValues = values?.Select(v => new GenFlags.FlagValue
        {
            Name = v.TryGetValue("name", out var n) ? n?.ToString() ?? "" : "",
            Value = v.TryGetValue("value", out var val) ? val! : new object(),
        }).ToList();

        return new GenFlags.Response_Flag_
        {
            Data = new GenFlags.Resource_Flag_
            {
                Type = "flag",
                Attributes = new GenFlags.Flag
                {
                    Key = key,
                    Name = name,
                    Type = type,
                    Default = @default ?? new object(),
                    Description = description ?? "",
                    Values = flagValues!,
                    Environments = new Dictionary<string, GenFlags.FlagEnvironment>(),
                },
            }
        };
    }

    private static GenFlags.Response_Flag_ BuildUpdateFlagBody(
        string key, string name, string type, object? @default,
        List<Dictionary<string, object?>>? values, string? description,
        Dictionary<string, Dictionary<string, object?>> environments)
    {
        var flagValues = values?.Select(v => new GenFlags.FlagValue
        {
            Name = v.TryGetValue("name", out var n) ? n?.ToString() ?? "" : "",
            Value = v.TryGetValue("value", out var val) ? val! : new object(),
        }).ToList();

        var flagEnvs = new Dictionary<string, GenFlags.FlagEnvironment>();
        foreach (var (envName, envData) in environments)
        {
            var flagEnv = new GenFlags.FlagEnvironment
            {
                Enabled = envData.TryGetValue("enabled", out var e) && e is bool eb && eb,
                Default = envData.TryGetValue("default", out var d) ? d : null,
            };
            if (envData.TryGetValue("rules", out var rulesObj) && rulesObj is List<object?> rulesList)
            {
                flagEnv.Rules = rulesList
                    .OfType<Dictionary<string, object?>>()
                    .Select(r => new GenFlags.FlagRule
                    {
                        Description = r.TryGetValue("description", out var desc) ? desc?.ToString() : null,
                        Logic = r.TryGetValue("logic", out var logic) ? logic ?? new object() : new object(),
                        Value = r.TryGetValue("value", out var v) ? v! : new object(),
                    }).ToList();
            }
            else
            {
                flagEnv.Rules = new List<GenFlags.FlagRule>();
            }
            flagEnvs[envName] = flagEnv;
        }

        return new GenFlags.Response_Flag_
        {
            Data = new GenFlags.Resource_Flag_
            {
                Type = "flag",
                Attributes = new GenFlags.Flag
                {
                    Key = key,
                    Name = name,
                    Type = type,
                    Default = @default ?? new object(),
                    Description = description ?? "",
                    Values = flagValues!,
                    Environments = flagEnvs,
                },
            }
        };
    }
}

// ------------------------------------------------------------------
// Resolution cache
// ------------------------------------------------------------------

internal sealed class ResolutionCache
{
    private readonly int _maxSize;
    private readonly LinkedList<(string Key, object? Value)> _list = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, object? Value)>> _map = new();
    private readonly object _lock = new();

    internal int CacheHits;
    internal int CacheMisses;

    internal ResolutionCache(int maxSize)
    {
        _maxSize = maxSize;
    }

    internal (bool Hit, object? Value) Get(string cacheKey)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(cacheKey, out var node))
            {
                _list.Remove(node);
                _list.AddLast(node);
                Interlocked.Increment(ref CacheHits);
                return (true, node.Value.Value);
            }
            Interlocked.Increment(ref CacheMisses);
            return (false, null);
        }
    }

    internal void Put(string cacheKey, object? value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(cacheKey, out var existing))
            {
                _list.Remove(existing);
                _map.Remove(cacheKey);
            }
            var node = _list.AddLast((cacheKey, value));
            _map[cacheKey] = node;
            if (_map.Count > _maxSize)
            {
                var oldest = _list.First!;
                _map.Remove(oldest.Value.Key);
                _list.RemoveFirst();
            }
        }
    }

    internal void Clear()
    {
        lock (_lock)
        {
            _list.Clear();
            _map.Clear();
        }
    }
}

// ------------------------------------------------------------------
// Context registration buffer
// ------------------------------------------------------------------

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
