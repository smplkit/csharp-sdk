using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JsonLogic.Net;
using Newtonsoft.Json.Linq;
using Smplkit.Errors;
using Smplkit.Internal;

namespace Smplkit.Flags;

/// <summary>
/// Client for the smplkit Flags service. Provides management CRUD operations
/// and prescriptive runtime evaluation via typed flag handles.
/// </summary>
public sealed class FlagsClient
{
    private const string FlagsBaseUrl = "https://flags.smplkit.com";
    private const string AppBaseUrl = "https://app.smplkit.com";
    private const int CacheMaxSize = 10_000;
    private const int ContextRegistrationLruSize = 10_000;
    private const int ContextBatchFlushSize = 100;

    private readonly Transport _transport;
    private readonly string _apiKey;
    private readonly Func<SharedWebSocket> _ensureWs;
    private readonly SmplClient? _parent;

    // Runtime state
    private string? _environment;
    private readonly ConcurrentDictionary<string, Dictionary<string, object?>> _flagStore = new();
    private volatile bool _connected;
    private readonly ResolutionCache _cache = new(CacheMaxSize);
    private Func<IReadOnlyList<Context>>? _contextProvider;
    private readonly ContextRegistrationBuffer _contextBuffer = new(ContextRegistrationLruSize, ContextBatchFlushSize);
    private readonly ConcurrentDictionary<string, FlagHandleBase> _handles = new();
    private readonly List<Action<FlagChangeEvent>> _globalListeners = new();

    // Shared WebSocket
    private SharedWebSocket? _wsManager;

    internal FlagsClient(Transport transport, string apiKey, Func<SharedWebSocket> ensureWs, SmplClient? parent = null)
    {
        _transport = transport;
        _apiKey = apiKey;
        _ensureWs = ensureWs;
        _parent = parent;
    }

    // ------------------------------------------------------------------
    // Management methods
    // ------------------------------------------------------------------

    /// <summary>
    /// Creates a new flag.
    /// </summary>
    /// <param name="key">The flag key.</param>
    /// <param name="name">Display name.</param>
    /// <param name="type">Flag value type.</param>
    /// <param name="default">Default value.</param>
    /// <param name="description">Optional description.</param>
    /// <param name="values">Optional values array.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created <see cref="Flag"/>.</returns>
    public async Task<Flag> CreateAsync(
        string key,
        string name,
        FlagType type,
        object? @default,
        string? description = null,
        List<Dictionary<string, object?>>? values = null,
        CancellationToken ct = default)
    {
        if (values is null && type == FlagType.Boolean)
            values = [new() { ["name"] = "True", ["value"] = true }, new() { ["name"] = "False", ["value"] = false }];

        var body = BuildCreateFlagBody(key, name, type.ToWireString(), @default, description, values);
        var json = await _transport.PostAsync($"{FlagsBaseUrl}/api/v1/flags", body, ct).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<FlagApiSingleResponse>(json, Transport.SerializerOptions);
        return MapFlagResource(response?.Data)
            ?? throw new SmplValidationException("Failed to create flag");
    }

    /// <summary>
    /// Fetches a flag by its UUID.
    /// </summary>
    /// <param name="flagId">The flag UUID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching <see cref="Flag"/>.</returns>
    public async Task<Flag> GetAsync(string flagId, CancellationToken ct = default)
    {
        var json = await _transport.GetAsync($"{FlagsBaseUrl}/api/v1/flags/{flagId}", ct).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<FlagApiSingleResponse>(json, Transport.SerializerOptions);
        return MapFlagResource(response?.Data)
            ?? throw new SmplNotFoundException($"Flag {flagId} not found");
    }

    /// <summary>
    /// Lists all flags.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of <see cref="Flag"/> objects.</returns>
    public async Task<List<Flag>> ListAsync(CancellationToken ct = default)
    {
        var json = await _transport.GetAsync($"{FlagsBaseUrl}/api/v1/flags", ct).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<FlagApiListResponse>(json, Transport.SerializerOptions);
        if (response?.Data is null) return new List<Flag>();
        return response.Data.Select(r => MapFlagResource(r)!).Where(f => f is not null).ToList();
    }

    /// <summary>
    /// Deletes a flag by its UUID.
    /// </summary>
    /// <param name="flagId">The flag UUID.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task DeleteAsync(string flagId, CancellationToken ct = default)
    {
        await _transport.DeleteAsync($"{FlagsBaseUrl}/api/v1/flags/{flagId}", ct).ConfigureAwait(false);
    }

    /// <summary>Internal: PUT a full flag update.</summary>
    internal async Task<Flag> UpdateFlagInternalAsync(
        Flag flag,
        Dictionary<string, Dictionary<string, object?>>? environments = null,
        List<Dictionary<string, object?>>? values = null,
        object? @default = null,
        string? description = null,
        string? name = null,
        CancellationToken ct = default)
    {
        var body = BuildUpdateFlagBody(
            key: flag.Key,
            name: name ?? flag.Name,
            type: flag.Type,
            @default: @default ?? flag.Default,
            values: values ?? flag.Values,
            description: description ?? flag.Description,
            environments: environments ?? flag.Environments);

        var json = await _transport.PutAsync($"{FlagsBaseUrl}/api/v1/flags/{flag.Id}", body, ct).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<FlagApiSingleResponse>(json, Transport.SerializerOptions);
        return MapFlagResource(response?.Data)
            ?? throw new SmplValidationException("Failed to update flag");
    }

    // ------------------------------------------------------------------
    // Context type management
    // ------------------------------------------------------------------

    /// <summary>
    /// Creates a context type.
    /// </summary>
    /// <param name="key">The context type key.</param>
    /// <param name="name">Display name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created <see cref="ContextType"/>.</returns>
    public async Task<ContextType> CreateContextTypeAsync(
        string key, string name, CancellationToken ct = default)
    {
        var body = new
        {
            data = new { type = "context_type", attributes = new { key, name } }
        };
        var json = await _transport.PostAsync($"{AppBaseUrl}/api/v1/context_types", body, ct).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<ContextTypeApiSingleResponse>(json, Transport.SerializerOptions);
        return ParseContextType(response?.Data);
    }

    /// <summary>
    /// Updates a context type (merge attributes).
    /// </summary>
    /// <param name="ctId">The context type UUID.</param>
    /// <param name="attributes">Attributes to merge.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated <see cref="ContextType"/>.</returns>
    public async Task<ContextType> UpdateContextTypeAsync(
        string ctId, Dictionary<string, object?> attributes, CancellationToken ct = default)
    {
        var body = new
        {
            data = new { type = "context_type", attributes = new { attributes } }
        };
        var json = await _transport.PutAsync($"{AppBaseUrl}/api/v1/context_types/{ctId}", body, ct).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<ContextTypeApiSingleResponse>(json, Transport.SerializerOptions);
        return ParseContextType(response?.Data);
    }

    /// <summary>
    /// Lists all context types.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of <see cref="ContextType"/> objects.</returns>
    public async Task<List<ContextType>> ListContextTypesAsync(CancellationToken ct = default)
    {
        var json = await _transport.GetAsync($"{AppBaseUrl}/api/v1/context_types", ct).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<ContextTypeApiListResponse>(json, Transport.SerializerOptions);
        if (response?.Data is null) return new List<ContextType>();
        return response.Data.Select(ParseContextType).ToList();
    }

    /// <summary>
    /// Deletes a context type.
    /// </summary>
    /// <param name="ctId">The context type UUID.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task DeleteContextTypeAsync(string ctId, CancellationToken ct = default)
    {
        await _transport.DeleteAsync($"{AppBaseUrl}/api/v1/context_types/{ctId}", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists context instances filtered by context type key.
    /// </summary>
    /// <param name="contextTypeKey">The context type key to filter by (required).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of context instance dictionaries.</returns>
    public async Task<List<Dictionary<string, object?>>> ListContextsAsync(
        string contextTypeKey, CancellationToken ct = default)
    {
        var encodedKey = Uri.EscapeDataString(contextTypeKey);
        var json = await _transport.GetAsync(
            $"{AppBaseUrl}/api/v1/contexts?filter[context_type]={encodedKey}", ct).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<ContextApiListResponse>(json, Transport.SerializerOptions);
        return response?.Data ?? new List<Dictionary<string, object?>>();
    }

    // ------------------------------------------------------------------
    // Runtime: typed flag handles
    // ------------------------------------------------------------------

    /// <summary>
    /// Declare a boolean flag handle with a code-level default.
    /// </summary>
    /// <param name="key">The flag key.</param>
    /// <param name="defaultValue">The code-level default value.</param>
    /// <returns>A typed flag handle.</returns>
    public BoolFlagHandle BoolFlag(string key, bool defaultValue)
    {
        var handle = new BoolFlagHandle(this, key, defaultValue);
        _handles[key] = handle;
        return handle;
    }

    /// <summary>
    /// Declare a string flag handle with a code-level default.
    /// </summary>
    /// <param name="key">The flag key.</param>
    /// <param name="defaultValue">The code-level default value.</param>
    /// <returns>A typed flag handle.</returns>
    public StringFlagHandle StringFlag(string key, string defaultValue)
    {
        var handle = new StringFlagHandle(this, key, defaultValue);
        _handles[key] = handle;
        return handle;
    }

    /// <summary>
    /// Declare a number flag handle with a code-level default.
    /// </summary>
    /// <param name="key">The flag key.</param>
    /// <param name="defaultValue">The code-level default value.</param>
    /// <returns>A typed flag handle.</returns>
    public NumberFlagHandle NumberFlag(string key, double defaultValue)
    {
        var handle = new NumberFlagHandle(this, key, defaultValue);
        _handles[key] = handle;
        return handle;
    }

    /// <summary>
    /// Declare a JSON flag handle with a code-level default.
    /// </summary>
    /// <param name="key">The flag key.</param>
    /// <param name="defaultValue">The code-level default value.</param>
    /// <returns>A typed flag handle.</returns>
    public JsonFlagHandle JsonFlag(string key, Dictionary<string, object?> defaultValue)
    {
        var handle = new JsonFlagHandle(this, key, defaultValue);
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
    // Runtime: connect / disconnect / refresh
    // ------------------------------------------------------------------

    /// <summary>
    /// Internal connect: fetches all flag definitions, opens a shared WebSocket
    /// for live updates, and enables local evaluation. Called by
    /// <see cref="SmplClient.ConnectAsync"/>.
    /// </summary>
    /// <param name="environment">The environment key (e.g., "staging", "production").</param>
    /// <param name="ct">Cancellation token.</param>
    internal async Task ConnectInternalAsync(string environment, CancellationToken ct = default)
    {
        _environment = environment;
        await FetchAllFlagsAsync(ct).ConfigureAwait(false);
        _connected = true;
        _cache.Clear();

        // Register on the shared WebSocket
        _wsManager = _ensureWs();
        _wsManager.On("flag_changed", HandleFlagChanged);
        _wsManager.On("flag_deleted", HandleFlagDeleted);
    }

    /// <summary>
    /// Disconnect: unregisters from WebSocket, flushes contexts, clears state.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_wsManager is not null)
        {
            _wsManager.Off("flag_changed", HandleFlagChanged);
            _wsManager.Off("flag_deleted", HandleFlagDeleted);
            _wsManager = null;
        }

        await FlushContextsAsync(ct).ConfigureAwait(false);
        _flagStore.Clear();
        _cache.Clear();
        _connected = false;
        _environment = null;
    }

    /// <summary>
    /// Re-fetch all flag definitions and clear cache. Fires change listeners
    /// for all flags with source "manual".
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

    // ------------------------------------------------------------------
    // Runtime: context registration
    // ------------------------------------------------------------------

    /// <summary>
    /// Explicitly register context(s) for background batch registration.
    /// Fire-and-forget; works before <see cref="SmplClient.ConnectAsync"/>.
    /// </summary>
    /// <param name="context">A single context to register.</param>
    public void Register(Context context)
    {
        _contextBuffer.Observe(new[] { context });
    }

    /// <summary>
    /// Explicitly register context(s) for background batch registration.
    /// Fire-and-forget; works before <see cref="SmplClient.ConnectAsync"/>.
    /// </summary>
    /// <param name="contexts">Contexts to register.</param>
    public void Register(IEnumerable<Context> contexts)
    {
        _contextBuffer.Observe(contexts);
    }

    /// <summary>
    /// Flush pending context registrations to the server.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task FlushContextsAsync(CancellationToken ct = default)
    {
        var batch = _contextBuffer.Drain();
        if (batch.Count == 0) return;
        try
        {
            await _transport.PutAsync(
                $"{AppBaseUrl}/api/v1/contexts/bulk",
                new { contexts = batch },
                ct).ConfigureAwait(false);
        }
        catch { /* Context registration is fire-and-forget */ }
    }

    // ------------------------------------------------------------------
    // Runtime: Tier 1 evaluate
    // ------------------------------------------------------------------

    /// <summary>
    /// Tier 1 explicit evaluation — stateless, no provider or cache.
    /// </summary>
    /// <param name="key">The flag key.</param>
    /// <param name="environment">The environment to evaluate in.</param>
    /// <param name="context">The contexts to evaluate against.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The evaluated value.</returns>
    public async Task<object?> EvaluateAsync(
        string key,
        string environment,
        IReadOnlyList<Context> context,
        CancellationToken ct = default)
    {
        var evalDict = ContextsToEvalDict(context);

        // Auto-inject service context from parent SmplClient
        if (_parent?.Service is { Length: > 0 } svc && !evalDict.ContainsKey("service"))
            evalDict["service"] = new Dictionary<string, object?> { ["key"] = svc };

        if (_connected && _flagStore.TryGetValue(key, out var flagDef))
            return EvaluateFlag(flagDef, environment, evalDict);

        // Fetch all flags to find the one we need
        var json = await _transport.GetAsync($"{FlagsBaseUrl}/api/v1/flags", ct).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<FlagApiListResponse>(json, Transport.SerializerOptions);
        if (response?.Data is not null)
        {
            foreach (var resource in response.Data)
            {
                var flag = ParseFlagDef(resource);
                if (flag is not null && flag.TryGetValue("key", out var k) && k is string ks && ks == key)
                    return EvaluateFlag(flag, environment, evalDict);
            }
        }

        return null;
    }

    // ------------------------------------------------------------------
    // Internal: evaluation
    // ------------------------------------------------------------------

    internal object? EvaluateHandle(string key, object? defaultValue, IReadOnlyList<Context>? context)
    {
        if (!_connected)
            throw new SmplNotConnectedException();

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
        // Re-fetch all flags synchronously (called from WS background thread)
        try
        {
            var json = _transport.GetAsync($"{FlagsBaseUrl}/api/v1/flags").GetAwaiter().GetResult();
            var response = JsonSerializer.Deserialize<FlagApiListResponse>(json, Transport.SerializerOptions);
            if (response?.Data is not null)
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
        var json = await _transport.GetAsync($"{FlagsBaseUrl}/api/v1/flags", ct).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<FlagApiListResponse>(json, Transport.SerializerOptions);
        _flagStore.Clear();
        if (response?.Data is null) return;
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
        if (_handles.TryGetValue(flagKey, out var handle))
        {
            foreach (var cb in handle.Listeners)
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

        // Environments can be Dictionary<string, Dictionary<string, object?>> or Dictionary<string, object?>
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
                var logicJson = JsonSerializer.Serialize(logic, Transport.SerializerOptions);
                var dataJson = JsonSerializer.Serialize(evalDict, Transport.SerializerOptions);
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

    private Flag? MapFlagResource(FlagApiResource? resource)
    {
        if (resource?.Attributes is null) return null;
        var attrs = resource.Attributes;

        var values = new List<Dictionary<string, object?>>();
        if (attrs.Values is not null)
        {
            foreach (var v in attrs.Values)
                values.Add(new Dictionary<string, object?> { ["name"] = v.Name, ["value"] = NormalizeValue(v.Value) });
        }

        var environments = ExtractEnvironments(attrs.Environments);

        return new Flag(
            client: this,
            id: resource.Id ?? string.Empty,
            key: attrs.Key ?? string.Empty,
            name: attrs.Name ?? string.Empty,
            type: attrs.FlagType ?? "BOOLEAN",
            @default: NormalizeValue(attrs.Default),
            values: values,
            description: attrs.Description,
            environments: environments,
            createdAt: attrs.CreatedAt,
            updatedAt: attrs.UpdatedAt);
    }

    private static Dictionary<string, object?>? ParseFlagDef(FlagApiResource? resource)
    {
        if (resource?.Attributes is null) return null;
        var attrs = resource.Attributes;

        var values = new List<Dictionary<string, object?>>();
        if (attrs.Values is not null)
        {
            foreach (var v in attrs.Values)
                values.Add(new Dictionary<string, object?> { ["name"] = v.Name, ["value"] = NormalizeValue(v.Value) });
        }

        var environments = ExtractEnvironments(attrs.Environments);

        return new Dictionary<string, object?>
        {
            ["key"] = attrs.Key,
            ["name"] = attrs.Name,
            ["type"] = attrs.FlagType,
            ["default"] = NormalizeValue(attrs.Default),
            ["values"] = values,
            ["description"] = attrs.Description,
            ["environments"] = environments,
        };
    }

    private static Dictionary<string, Dictionary<string, object?>> ExtractEnvironments(
        Dictionary<string, Dictionary<string, object?>>? environments)
    {
        if (environments is null) return new Dictionary<string, Dictionary<string, object?>>();

        var result = new Dictionary<string, Dictionary<string, object?>>();
        foreach (var (envName, envData) in environments)
        {
            var normalized = new Dictionary<string, object?>();
            foreach (var (key, value) in envData)
            {
                normalized[key] = NormalizeValue(value);
            }
            result[envName] = normalized;
        }
        return result;
    }

    private static ContextType ParseContextType(ContextTypeApiResource? resource)
    {
        var attrs = resource?.Attributes;
        return new ContextType(
            id: resource?.Id ?? string.Empty,
            key: attrs?.Key ?? string.Empty,
            name: attrs?.Name ?? string.Empty,
            attributes: NormalizeAttributes(attrs?.Attributes));
    }

    private static Dictionary<string, object?> NormalizeAttributes(Dictionary<string, object?>? attributes)
    {
        if (attributes is null) return new Dictionary<string, object?>();
        var result = new Dictionary<string, object?>();
        foreach (var (key, value) in attributes)
            result[key] = NormalizeValue(value);
        return result;
    }

    // ------------------------------------------------------------------
    // Helpers: request body building
    // ------------------------------------------------------------------

    private static object BuildCreateFlagBody(
        string key, string name, string type, object? @default,
        string? description, List<Dictionary<string, object?>>? values)
    {
        var attrs = new Dictionary<string, object?>
        {
            ["key"] = key,
            ["name"] = name,
            ["type"] = type,
            ["default"] = @default,
        };
        if (description is not null) attrs["description"] = description;
        if (values is not null) attrs["values"] = values;

        return new Dictionary<string, object?>
        {
            ["data"] = new Dictionary<string, object?>
            {
                ["type"] = "flag",
                ["attributes"] = attrs,
            }
        };
    }

    private static object BuildUpdateFlagBody(
        string key, string name, string type, object? @default,
        List<Dictionary<string, object?>> values, string? description,
        Dictionary<string, Dictionary<string, object?>> environments)
    {
        var attrs = new Dictionary<string, object?>
        {
            ["key"] = key,
            ["name"] = name,
            ["type"] = type,
            ["default"] = @default,
            ["values"] = values,
            ["environments"] = environments,
        };
        if (description is not null) attrs["description"] = description;

        return new Dictionary<string, object?>
        {
            ["data"] = new Dictionary<string, object?>
            {
                ["type"] = "flag",
                ["attributes"] = attrs,
            }
        };
    }
}

// ------------------------------------------------------------------
// Flag handles
// ------------------------------------------------------------------

/// <summary>
/// Base class for typed flag handles.
/// </summary>
public abstract class FlagHandleBase
{
    private readonly FlagsClient _client;
    internal readonly List<Action<FlagChangeEvent>> Listeners = new();

    /// <summary>Gets the flag key.</summary>
    public string Key { get; }

    /// <summary>Gets the code-level default value.</summary>
    public object? Default { get; }

    internal FlagHandleBase(FlagsClient client, string key, object? defaultValue)
    {
        _client = client;
        Key = key;
        Default = defaultValue;
    }

    /// <summary>
    /// Evaluate the flag. Returns the typed value from the server
    /// or the code-level default if unavailable.
    /// </summary>
    /// <param name="context">Optional explicit context override.</param>
    /// <returns>The raw evaluated value.</returns>
    protected object? GetRaw(IReadOnlyList<Context>? context = null)
    {
        return _client.EvaluateHandle(Key, Default, context);
    }

    /// <summary>
    /// Register a flag-specific change listener.
    /// </summary>
    /// <param name="callback">Called with a <see cref="FlagChangeEvent"/> when this flag changes.</param>
    public void OnChange(Action<FlagChangeEvent> callback)
    {
        Listeners.Add(callback);
    }
}

/// <summary>Typed handle for a boolean flag.</summary>
public sealed class BoolFlagHandle : FlagHandleBase
{
    internal BoolFlagHandle(FlagsClient client, string key, bool defaultValue)
        : base(client, key, defaultValue) { }

    /// <summary>Evaluate the flag and return a bool.</summary>
    /// <param name="context">Optional explicit context override.</param>
    /// <returns>The evaluated boolean value.</returns>
    public bool Get(IReadOnlyList<Context>? context = null)
    {
        var value = GetRaw(context);
        if (value is bool b) return b;
        if (value is JsonElement je && je.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return je.GetBoolean();
        return (bool)Default!;
    }
}

/// <summary>Typed handle for a string flag.</summary>
public sealed class StringFlagHandle : FlagHandleBase
{
    internal StringFlagHandle(FlagsClient client, string key, string defaultValue)
        : base(client, key, defaultValue) { }

    /// <summary>Evaluate the flag and return a string.</summary>
    /// <param name="context">Optional explicit context override.</param>
    /// <returns>The evaluated string value.</returns>
    public string Get(IReadOnlyList<Context>? context = null)
    {
        var value = GetRaw(context);
        if (value is string s) return s;
        if (value is JsonElement je && je.ValueKind == JsonValueKind.String)
            return je.GetString()!;
        return (string)Default!;
    }
}

/// <summary>Typed handle for a numeric flag.</summary>
public sealed class NumberFlagHandle : FlagHandleBase
{
    internal NumberFlagHandle(FlagsClient client, string key, double defaultValue)
        : base(client, key, defaultValue) { }

    /// <summary>Evaluate the flag and return a number.</summary>
    /// <param name="context">Optional explicit context override.</param>
    /// <returns>The evaluated numeric value.</returns>
    public double Get(IReadOnlyList<Context>? context = null)
    {
        var value = GetRaw(context);
        if (value is double d) return d;
        if (value is int i) return i;
        if (value is long l) return l;
        if (value is float f) return f;
        if (value is decimal dec) return (double)dec;
        if (value is JsonElement je && je.ValueKind == JsonValueKind.Number)
            return je.TryGetInt64(out var jl) ? jl : je.GetDouble();
        return (double)Default!;
    }
}

/// <summary>Typed handle for a JSON flag.</summary>
public sealed class JsonFlagHandle : FlagHandleBase
{
    internal JsonFlagHandle(FlagsClient client, string key, Dictionary<string, object?> defaultValue)
        : base(client, key, defaultValue) { }

    /// <summary>Evaluate the flag and return a dictionary.</summary>
    /// <param name="context">Optional explicit context override.</param>
    /// <returns>The evaluated dictionary value.</returns>
    public Dictionary<string, object?> Get(IReadOnlyList<Context>? context = null)
    {
        var value = GetRaw(context);
        if (value is Dictionary<string, object?> dict) return dict;
        return (Dictionary<string, object?>)Default!;
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
                        ["name"] = ctx.Name ?? ctx.Key,
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
