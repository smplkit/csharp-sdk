// The Smpl Config client — one unified ConfigClient.
//
// Smpl Config has two surfaces on a single client, mirroring how the audit
// and jobs clients expose their full surface from one class:
//
// * Management surface — pure CRUD, no live connection: New / Get
//   / List / Delete and the discovery buffer (RegisterConfig /
//   RegisterConfigItem / Flush / PendingCount). The client owns
//   the discovery buffer directly.
// * Live surface — lazily connects to your running service on first use:
//   Subscribe (a live dict-like LiveConfigProxy), GetValue
//   (an ad-hoc resolved read), Bind (a live POCO/dict binding),
//   OnChange, and Refresh. The first live call transparently flushes
//   discovery, fetches and resolves every config into the local cache,
//   and opens the live-updates WebSocket — no explicit install step.
//
// The client supports two construction shapes:
//
// * Wired into SmplClient — borrows the parent's config
//   transport for both runtime fetch and CRUD and the parent's shared
//   WebSocket for the live channel. This is the common path.
// * Standalone — ConfigClient(apiKey: ..., baseDomain: ..., ...) builds
//   and owns its own config transport, and on first live use opens and owns
//   its own WebSocket. Dispose() tears down only the owned
//   transport and owned WebSocket.

using System.Reflection;
using System.Text.Json;
using Smplkit.Errors;
using Smplkit.Internal;
using GenConfig = Smplkit.Internal.Generated.Config;
using DebugLog = Smplkit.Internal.Debug;

namespace Smplkit.Config;

/// <summary>
/// The Smpl Config client.
/// </summary>
/// <remarks>
/// <para>One client exposes the full surface, reachable as <c>client.Config</c>
/// (<see cref="Smplkit.SmplClient"/>) or constructed directly:</para>
/// <code>
/// using var config = new ConfigClient(environment: "production");
/// var billing = config.New("billing", name: "Billing");
/// billing.SetNumber("max_seats", 50);
/// await billing.SaveAsync();
/// var proxy = config.Subscribe("billing");
/// Console.WriteLine(proxy["max_seats"]);
/// </code>
/// <para>The management surface (<see cref="New(string, string?, string?, object?)"/> /
/// <see cref="GetAsync(string, CancellationToken)"/> / <see cref="ListAsync(int?, int?, CancellationToken)"/> /
/// <see cref="DeleteAsync(string, CancellationToken)"/> and discovery) is pure CRUD. The live surface
/// (<see cref="Subscribe(string)"/> / <see cref="GetValue(string, string)"/> /
/// <see cref="Bind{T}(string, T, object?)"/> / <c>OnChange</c> / <see cref="RefreshAsync(CancellationToken)"/>)
/// connects lazily on first use — the first call flushes discovery, fetches and resolves all configs into
/// the local cache, and opens the live-updates WebSocket. No explicit
/// install step is required.</para>
/// </remarks>
public sealed class ConfigClient : IDisposable
{
    private readonly GenConfig.ConfigClient _genClient;
    private readonly SmplClient? _parent;
    private readonly Func<SharedWebSocket>? _ensureWs;
    private readonly MetricsReporter? _metrics;
    private readonly MetricsReporter? _ownedMetrics;
    private readonly HttpClient? _ownedHttpClient;

    // Environment + service: borrowed from the parent when wired, else resolved
    // standalone. Environment scopes runtime resolution and discovery; service
    // scopes discovery declarations.
    private readonly string? _environment;
    private readonly string? _service;

    // Standalone-only: the app base URL the owned WebSocket connects to, and the
    // api key it authenticates with.
    private readonly string? _appBaseUrl;
    private readonly string? _standaloneApiKey;

    // Discovery buffer is owned by this client (no management delegation).
    private readonly ConfigRegistrationBuffer _buffer = new();
    private const int RegistrationFlushSize = 50;

    // Live-surface state.
    private volatile bool _connected;
    private readonly object _initLock = new();
    private Dictionary<string, Dictionary<string, object?>> _configCache = new();
    private readonly List<(Action<ConfigChangeEvent> Callback, string? ConfigId, string? ItemKey)> _listeners = new();
    private readonly object _listenerLock = new();
    private SharedWebSocket? _wsManager;
    private bool _ownsWs;

    // LiveConfigProxy instances for Subscribe(id) callers; one per config id.
    private readonly Dictionary<string, LiveConfigProxy> _proxies = new();
    private readonly object _proxyLock = new();

    // POCO / dict instances bound via Bind(id, ...); one per config id.
    // WebSocket dispatch mutates these in place when values change.
    private readonly Dictionary<string, object> _bindings = new();
    private readonly object _bindingsLock = new();

    // Parent config id each binding was bound under (null for roots) —
    // drives in-memory cache seeding through the bound parent chain.
    private readonly Dictionary<string, string?> _boundParents = new();

    /// <summary>
    /// Initializes a new standalone <see cref="ConfigClient"/>.
    /// </summary>
    /// <param name="apiKey">API key. When omitted, resolved from <c>SMPLKIT_API_KEY</c> or <c>~/.smplkit</c>.</param>
    /// <param name="environment">Deployment environment used to resolve runtime config
    /// values and to scope discovery declarations. Optional.</param>
    /// <param name="service">Service identifier used to scope discovery declarations. Optional.</param>
    /// <param name="profile">Named <c>~/.smplkit</c> profile section.</param>
    /// <param name="baseDomain">Base domain for API requests (default <c>smplkit.com</c>).</param>
    /// <param name="scheme">URL scheme (default <c>https</c>).</param>
    /// <param name="debug">Enable SDK debug logging.</param>
    /// <param name="telemetry">Enable SDK telemetry reporting. Defaults to enabled.</param>
    /// <param name="extraHeaders">Extra headers attached to every request.</param>
    public ConfigClient(
        string? apiKey = null,
        string? environment = null,
        string? service = null,
        string? profile = null,
        string? baseDomain = null,
        string? scheme = null,
        bool? debug = null,
        bool? telemetry = null,
        IReadOnlyDictionary<string, string>? extraHeaders = null)
    {
        // Reuse the management config resolver, filling in whatever is missing
        // (~/.smplkit / env vars / defaults). Environment is not required for
        // management resolution — a standalone config client without an
        // environment simply resolves base values.
        var resolved = ConfigResolver.ResolveForManagement(new SmplClientOptions
        {
            ApiKey = apiKey,
            Environment = environment,
            Service = service,
            Profile = profile,
            BaseDomain = baseDomain,
            Scheme = scheme,
            Debug = debug,
            DisableTelemetry = telemetry is null ? null : !telemetry.Value,
        });
        if (resolved.Debug)
            DebugLog.Enabled = true;

        _environment = string.IsNullOrEmpty(environment) ? NullIfEmpty(resolved.Environment) : environment;
        _service = string.IsNullOrEmpty(service) ? NullIfEmpty(resolved.Service) : service;

        _ownedHttpClient = new HttpClient();
        var clients = new GeneratedClientFactory(_ownedHttpClient, new SmplClientOptions
        {
            ApiKey = resolved.ApiKey,
            BaseDomain = resolved.BaseDomain,
            Scheme = resolved.Scheme,
            Environment = _environment,
            ExtraHeaders = extraHeaders is null ? null : new Dictionary<string, string>(extraHeaders),
        });
        _genClient = clients.Config;

        // A standalone client opens its own WebSocket against the app event
        // gateway on first live use, and its own metrics reporter.
        _appBaseUrl = ConfigResolver.ServiceUrl(resolved.Scheme, "app", resolved.BaseDomain);
        _standaloneApiKey = resolved.ApiKey;
        _parent = null;
        _ensureWs = null;
        _ownedMetrics = resolved.DisableTelemetry
            ? null
            : new MetricsReporter(_ownedHttpClient, _environment ?? string.Empty, _service ?? string.Empty, appBaseUrl: _appBaseUrl);
        _metrics = _ownedMetrics;
    }

    /// <summary>
    /// Initializes a new <see cref="ConfigClient"/> wired into a parent <see cref="SmplClient"/>.
    /// </summary>
    /// <param name="clients">The generated client factory.</param>
    /// <param name="ensureWs">Factory for the shared WebSocket.</param>
    /// <param name="parent">The parent <see cref="SmplClient"/>, if any.</param>
    /// <param name="metrics">Optional metrics reporter for telemetry.</param>
    internal ConfigClient(GeneratedClientFactory clients, Func<SharedWebSocket>? ensureWs = null, SmplClient? parent = null, MetricsReporter? metrics = null)
    {
        _genClient = clients.Config;
        _ensureWs = ensureWs;
        _parent = parent;
        _metrics = metrics;
        _ownedMetrics = null;
        _ownedHttpClient = null;
        _environment = parent?.Environment;
        _service = parent?.Service;
        _appBaseUrl = null;
        _standaloneApiKey = null;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    // ------------------------------------------------------------------
    // Management surface: CRUD (no live connection)
    // ------------------------------------------------------------------

    /// <summary>
    /// Return a new unsaved <see cref="Config"/>. Call <see cref="Config.SaveAsync"/> to persist.
    /// </summary>
    /// <remarks>
    /// <paramref name="parent"/> accepts either a config id (string) or an existing
    /// <see cref="Config"/> instance — passing the instance lets you skip naming
    /// the id explicitly when you already have the parent in scope.
    /// </remarks>
    /// <param name="id">The config identifier (slug).</param>
    /// <param name="name">Display name. Auto-generated from id if null.</param>
    /// <param name="description">Optional description.</param>
    /// <param name="parent">Optional parent: a config id string or a <see cref="Config"/> instance.</param>
    public Config New(string id, string? name = null, string? description = null, object? parent = null)
    {
        return new Config(
            this,
            id: id,
            name: name ?? Helpers.KeyToDisplayName(id),
            description: description,
            parent: ResolveParentId(parent),
            items: new Dictionary<string, object?>(),
            environments: new Dictionary<string, Dictionary<string, object?>>(),
            createdAt: null,
            updatedAt: null);
    }

    /// <summary>
    /// Fetch the editable <see cref="Config"/> resource by id.
    /// </summary>
    /// <remarks>
    /// Raises <see cref="NotFoundException"/> if no config with that id exists.
    /// </remarks>
    public async Task<Config> GetAsync(string id, CancellationToken ct = default)
    {
        var response = await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.Get_configAsync(id: id, cancellationToken: ct)).ConfigureAwait(false);

        return MapResource(response.Data)
            ?? throw new NotFoundException($"Config with id '{id}' not found");
    }

    /// <summary>List configs for the authenticated account.</summary>
    /// <param name="pageNumber">1-based page number; null lets the server default (1) apply.</param>
    /// <param name="pageSize">Items per page; null lets the server default (1000) apply.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<List<Config>> ListAsync(
        int? pageNumber = null,
        int? pageSize = null,
        CancellationToken ct = default)
    {
        var response = await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.List_configsAsync(
                pagenumber: pageNumber,
                pagesize: pageSize,
                cancellationToken: ct)).ConfigureAwait(false);

        if (response.Data is null)
            return new List<Config>();

        var results = new List<Config>(response.Data.Count);
        foreach (var resource in response.Data)
        {
            var config = MapResource(resource);
            if (config is not null)
                results.Add(config);
        }
        return results;
    }

    /// <summary>Delete a config by id.</summary>
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.Delete_configAsync(id, ct)).ConfigureAwait(false);
    }

    /// <summary>Internal: POST a new config. Called by <see cref="Config.SaveAsync"/>.</summary>
    internal async Task<Config> CreateConfigInternalAsync(Config config, CancellationToken ct = default)
    {
        var createBody = BuildCreateRequestBody(config);
        var response = await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.Create_configAsync(createBody, ct)).ConfigureAwait(false);
        return MapResource(response.Data)
            ?? throw new ValidationException("Failed to create config");
    }

    /// <summary>Internal: full-replace PUT. Called by <see cref="Config.SaveAsync"/>.</summary>
    internal async Task<Config> UpdateConfigFromModelInternalAsync(Config config, CancellationToken ct = default)
    {
        var configId = config.Id ?? throw new ValidationException("Cannot update a config without an id");
        var updateBody = BuildRequestBody(config);
        var response = await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.Update_configAsync(configId, updateBody, ct)).ConfigureAwait(false);
        return MapResource(response.Data)
            ?? throw new ValidationException("Failed to update config");
    }

    // ------------------------------------------------------------------
    // Management surface: discovery buffer (owned directly)
    // ------------------------------------------------------------------

    /// <summary>Queue a configuration declaration for bulk-discovery upload.</summary>
    internal void RegisterConfig(string configId, string? service, string? environment,
        string? parent = null, string? name = null, string? description = null)
    {
        _buffer.Declare(configId, service, environment, parent, name, description);
        if (_buffer.PendingCount >= RegistrationFlushSize)
        {
            _ = Task.Run(async () => { try { await FlushAsync().ConfigureAwait(false); } catch { } });
        }
    }

    /// <summary>Queue a config item declaration. <see cref="RegisterConfig"/> must run first.</summary>
    internal void RegisterConfigItem(string configId, string itemKey, string itemType,
        object? defaultValue, string? description = null)
    {
        _buffer.AddItem(configId, itemKey, itemType, defaultValue, description);
        if (_buffer.PendingCount >= RegistrationFlushSize)
        {
            _ = Task.Run(async () => { try { await FlushAsync().ConfigureAwait(false); } catch { } });
        }
    }

    /// <summary>Number of pending config declarations awaiting flush.</summary>
    public int PendingCount => _buffer.PendingCount;

    /// <summary>
    /// POST pending declarations to <c>/api/v1/configs/bulk</c>.
    /// </summary>
    /// <remarks>
    /// Per ADR-024 §2.9, bulk registration always lands rows as
    /// <c>managed=false</c> and is plan-limit-exempt — failures here never
    /// propagate to customer code. Drained entries are not requeued;
    /// the SDK will re-observe on the next process start.
    /// </remarks>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        var batch = _buffer.Drain();
        if (batch.Count == 0) return;

        var items = new List<GenConfig.ConfigBulkItem>(batch.Count);
        foreach (var entry in batch)
        {
            var item = new GenConfig.ConfigBulkItem { Id = entry.Id };
            if (entry.Service is not null) item.Service = entry.Service;
            if (entry.Environment is not null) item.Environment = entry.Environment;
            if (entry.Parent is not null) item.Parent = entry.Parent;
            if (entry.Name is not null) item.Name = entry.Name;
            if (entry.Description is not null) item.Description = entry.Description;
            if (entry.Items.Count > 0)
            {
                var dict = new Dictionary<string, GenConfig.ConfigItemDefinition>(entry.Items.Count);
                foreach (var (key, def) in entry.Items)
                {
                    var gd = new GenConfig.ConfigItemDefinition
                    {
                        Value = def.DefaultValue!,
                        Type = def.ItemType switch
                        {
                            "STRING" => GenConfig.ConfigItemDefinitionType.STRING,
                            "NUMBER" => GenConfig.ConfigItemDefinitionType.NUMBER,
                            "BOOLEAN" => GenConfig.ConfigItemDefinitionType.BOOLEAN,
                            "JSON" => GenConfig.ConfigItemDefinitionType.JSON,
                            _ => null,
                        },
                    };
                    if (def.Description is not null) gd.Description = def.Description;
                    dict[key] = gd;
                }
                item.Items = dict;
            }
            items.Add(item);
        }
        var body = new GenConfig.ConfigBulkRequest { Configs = items };
        // Failures propagate to the caller; the discovery-flush call sites
        // (threshold flush, EnsureConnected, periodic flush) swallow them so
        // they never reach customer code (ADR-024 §2.9).
        await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.Bulk_register_configsAsync(body, ct)).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Live surface: bind, subscribe
    // ------------------------------------------------------------------

    /// <summary>
    /// Bind a POCO instance or dictionary to a config id; return it live.
    /// </summary>
    /// <remarks>
    /// <para>Declarative, code-first API. Two flavors:</para>
    /// <list type="bullet">
    /// <item><description><b>POCO instance:</b> the class is the schema;
    /// the instance carries the in-code defaults. Public read/write
    /// properties are walked; nested POCO properties flatten to
    /// dot-notation. PascalCase property names are converted to snake_case
    /// on the wire (e.g. <c>MaxSeats</c> → <c>max_seats</c>). Every
    /// reachable leaf property is registered as an explicit override —
    /// C# has no equivalent of Python's <c>model_fields_set</c>, so to get
    /// omit-to-inherit semantics, use a dictionary instead.</description></item>
    /// <item><description><b>Dictionary:</b> every key in the dict is a leaf
    /// to register, with its value as the in-code default. Nested
    /// <c>IDictionary&lt;string, object?&gt;</c> values flatten to
    /// dot-notation. There is no class-default concept — keys the
    /// caller wants to inherit are simply omitted from the dict.</description></item>
    /// </list>
    /// <para>On first boot the schema and values are registered with the
    /// server. The local cache is then seeded so reads work immediately:
    /// if the config already exists server-side (fetched on connect) its
    /// values are authoritative and synced onto the bound object; if it is
    /// brand-new, the cache entry is seeded in-memory from the bound
    /// object's values resolved through its bound parent chain (no network
    /// round-trip). On every WebSocket-delivered change thereafter the
    /// bound object is mutated in place. Readers always
    /// see the current resolved value with no proxy indirection.</para>
    /// <para>Idempotent. Repeated calls with the same <paramref name="id"/>
    /// return the originally-bound object; the new <paramref name="target"/>
    /// argument is ignored.</para>
    /// <para>Connects lazily on first use — no explicit install step.</para>
    /// </remarks>
    /// <typeparam name="T">The POCO type, or <c>IDictionary&lt;string, object?&gt;</c>.</typeparam>
    /// <param name="id">The config id to register under.</param>
    /// <param name="target">A populated POCO instance or
    /// <c>IDictionary&lt;string, object?&gt;</c>. Both supply the schema
    /// and the in-code defaults.</param>
    /// <param name="parent">Optional parent — any object previously returned from a
    /// <see cref="Bind{T}(string, T, object?)"/> call (POCO or dict). Activates
    /// parent-chain inheritance for fields the caller omitted.</param>
    /// <returns>The same <paramref name="target"/> object, registered and live.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="target"/> is null.</exception>
    /// <exception cref="ArgumentException">If <paramref name="parent"/> is provided but was not previously
    /// bound via <see cref="Bind{T}(string, T, object?)"/>.</exception>
    public T Bind<T>(string id, T target, object? parent = null) where T : class
    {
        ArgumentNullException.ThrowIfNull(target);
        EnsureConnected();

        // Parent lookup is read-only and can race safely against other
        // Binds — it walks _bindings under the lock and returns.
        string? parentId = null;
        if (parent is not null)
        {
            parentId = ConfigIdFor(parent);
            if (parentId is null)
                throw new ArgumentException(
                    "Bind(): parent must be an object previously returned from Config.Bind(). " +
                    "Bind the parent first.", nameof(parent));
        }

        // Hold the lock for the full registration so two concurrent Binds
        // for the same id can't both run the discovery / item-declaration
        // path and then race on `_bindings[id] = …`. The buffer is fast
        // (in-memory dict mutation), so contention here is negligible.
        T resolved;
        lock (_bindingsLock)
        {
            if (_bindings.TryGetValue(id, out var existing))
                return (T)existing;

            RegisterBindingDeclaration(id, target, parentId);

            // Register the binding BEFORE syncing so WebSocket dispatch finds it.
            _bindings[id] = target;
            _boundParents[id] = parentId;
            resolved = target;
        }

        SeedOrSyncBinding(id, resolved);
        return resolved;
    }

    /// <summary>
    /// Return a live, dict-like <see cref="LiveConfigProxy"/> for a config id.
    /// </summary>
    /// <remarks>
    /// The proxy always reflects the latest resolved values; reads happen
    /// through it (<c>proxy["key"]</c>, <c>proxy.GetOrDefault("key", default)</c>).
    /// Subscribing registers the config declaration for code-first
    /// observability so the reference appears in the smplkit console.
    /// <para>Connects lazily on first use — no explicit install step. Raises
    /// <see cref="NotFoundException"/> if the config is unknown.</para>
    /// </remarks>
    public LiveConfigProxy Subscribe(string id)
    {
        EnsureConnected();
        ObserveConfigDeclaration(id, parent: null, name: null, description: null);
        if (!_configCache.ContainsKey(id))
            throw new NotFoundException($"Config with id '{id}' not found");
        _metrics?.Record("config.resolutions", unit: "resolutions",
            dimensions: new Dictionary<string, string> { ["config"] = id });
        return CachedProxy(id);
    }

    /// <summary>
    /// Read a single resolved config value (inheritance-aware).
    /// </summary>
    /// <remarks>
    /// The value comes from the locally-cached resolved chain, so parent
    /// configs are already folded in.
    /// <para>Returns the resolved value. Raises
    /// <see cref="NotFoundException"/> if the config is unknown and
    /// <see cref="KeyNotFoundException"/> if the key is absent. For the
    /// default-providing form that never raises (and registers the key for
    /// discovery), use <see cref="GetValueOr{T}(string, string, T)"/>.</para>
    /// <para>For a live dict-like view use <see cref="Subscribe(string)"/>; for typed access via
    /// a POCO schema use <see cref="Bind{T}(string, T, object?)"/>. Connects lazily on first use — no
    /// explicit install step.</para>
    /// </remarks>
    /// <exception cref="NotFoundException">If the config is missing.</exception>
    /// <exception cref="KeyNotFoundException">If the key is missing within the config.</exception>
    public object? GetValue(string id, string key)
    {
        EnsureConnected();
        if (!_configCache.TryGetValue(id, out var values))
            throw new NotFoundException($"Config with id '{id}' not found");
        if (!values.TryGetValue(key, out var value))
            throw new KeyNotFoundException($"Config item '{key}' not found in config '{id}'");
        return value;
    }

    /// <summary>
    /// Read a single resolved config value (inheritance-aware), falling back to
    /// <paramref name="defaultValue"/> if the config or key is missing. Never
    /// raises. <b>Registers</b> the config (if new) and the key (inferred
    /// type, <paramref name="defaultValue"/> as default) for code-first observability, so the
    /// reference appears in the smplkit console.
    /// </summary>
    /// <remarks>
    /// The value comes from the locally-cached resolved chain, so parent
    /// configs are already folded in.
    /// <para>For a live dict-like view use <see cref="Subscribe(string)"/>; for typed access via
    /// a POCO schema use <see cref="Bind{T}(string, T, object?)"/>. Connects lazily on first use — no
    /// explicit install step.</para>
    /// </remarks>
    public T GetValueOr<T>(string id, string key, T defaultValue)
    {
        EnsureConnected();

        // Register the config + key so the reference shows up in the
        // console even if it's never been declared via Bind(). The
        // buffer is idempotent at the (configId, itemKey) level.
        ObserveConfigDeclaration(id, parent: null, name: null, description: null);
        ObserveItemDeclaration(id, key, InferItemType(defaultValue), defaultValue, null);

        if (!_configCache.TryGetValue(id, out var values)) return defaultValue;
        if (!values.TryGetValue(key, out var raw)) return defaultValue;
        if (raw is null) return defaultValue!;

        var coerced = CoerceValue(raw, typeof(T));
        return coerced is T t ? t : defaultValue;
    }

    // ------------------------------------------------------------------
    // Internal: binding helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Validate the parent, register the config + item declarations.
    /// </summary>
    private void RegisterBindingDeclaration(string id, object config, string? parentId)
    {
        var isDict = config is IDictionary<string, object?>;
        string? configName;
        if (isDict)
        {
            // Dict bind: no class to introspect for name/description.
            configName = null;
        }
        else
        {
            // No runtime-accessible XML doc comments → no description for POCO bind.
            configName = config.GetType().Name;
        }

        ObserveConfigDeclaration(id, parentId, configName, description: null);

        var items = isDict
            ? IterDictItems((IDictionary<string, object?>)config)
            : IterPocoItems(config);
        foreach (var (key, type, value) in items)
            ObserveItemDeclaration(id, key, type, value, null);
    }

    /// <summary>Return the config_id this object was bound under, or null.</summary>
    private string? ConfigIdFor(object target)
    {
        lock (_bindingsLock)
        {
            foreach (var (cid, bound) in _bindings)
            {
                if (ReferenceEquals(bound, target)) return cid;
            }
        }
        return null;
    }

    /// <summary>Apply current cached values to a freshly-bound target.</summary>
    private void SyncTargetFromCache(object target, string configId)
    {
        if (!_configCache.TryGetValue(configId, out var cache)) return;
        foreach (var (dottedKey, value) in cache)
            ApplyChangeToTarget(target, dottedKey, value);
    }

    /// <summary>
    /// Seed the resolved cache for a freshly-bound config, or sync from it.
    /// </summary>
    /// <remarks>
    /// If <paramref name="configId"/> is already in the resolved cache it existed
    /// server-side (fetched on connect), so server values are authoritative
    /// — sync them onto the bound object (today's behavior). Otherwise the
    /// config is brand-new: seed the cache entry in-memory by resolving this
    /// object's values through its bound parent chain, so <see cref="Subscribe(string)"/> /
    /// <see cref="GetValue(string, string)"/> work immediately with no flush
    /// or refresh. Pure in-memory — no network.
    /// </remarks>
    private void SeedOrSyncBinding(string configId, object target)
    {
        if (_configCache.ContainsKey(configId))
        {
            SyncTargetFromCache(target, configId);
            return;
        }
        _configCache[configId] = ResolveBoundChain(configId);
    }

    /// <summary>
    /// Resolve a bound config's values through its bound parent chain.
    /// </summary>
    /// <remarks>
    /// Walks the bound-parent links from the child up through already-bound
    /// ancestors, flattening each bound object's in-code values, then runs
    /// the same deep-merge <see cref="Resolver.Resolve"/> used everywhere else (child wins
    /// over parent). Ancestors that aren't bound objects stop the walk.
    /// </remarks>
    private Dictionary<string, object?> ResolveBoundChain(string configId)
    {
        var chain = new List<ConfigChainEntry>();
        string? current = configId;
        var seen = new HashSet<string>();
        while (current is not null && _bindings.ContainsKey(current) && !seen.Contains(current))
        {
            seen.Add(current);
            var items = BoundItemsToFlat(_bindings[current]);
            chain.Add(new ConfigChainEntry { Id = current, Values = Resolver.NormalizeDict(items) });
            current = _boundParents.GetValueOrDefault(current);
        }
        return Resolver.Resolve(chain, _environment ?? string.Empty);
    }

    /// <summary>
    /// Flatten a bound POCO instance or dict to <c>{dotted_key: value}</c>.
    /// </summary>
    /// <remarks>
    /// Mirrors the discovery-declaration walk (<see cref="IterPocoItems"/> /
    /// <see cref="IterDictItems"/>). Used to seed the local resolved cache from
    /// in-memory bindings without any network round-trip.
    /// </remarks>
    private static Dictionary<string, object?> BoundItemsToFlat(object config)
    {
        var items = config is IDictionary<string, object?> dict
            ? IterDictItems(dict)
            : IterPocoItems(config);
        var result = new Dictionary<string, object?>();
        foreach (var (key, _type, value) in items)
            result[key] = value;
        return result;
    }

    private LiveConfigProxy CachedProxy(string id)
    {
        lock (_proxyLock)
        {
            if (!_proxies.TryGetValue(id, out var proxy))
            {
                proxy = new LiveConfigProxy(this, id);
                _proxies[id] = proxy;
            }
            return proxy;
        }
    }

    /// <summary>Queue a config declaration with the owned discovery buffer.</summary>
    private void ObserveConfigDeclaration(string configId, string? parent, string? name, string? description)
    {
        RegisterConfig(configId, _service, _environment, parent, name, description);
    }

    /// <summary>Queue a config item declaration with the owned discovery buffer.</summary>
    private void ObserveItemDeclaration(string configId, string itemKey, string itemType, object? defaultValue, string? description)
    {
        RegisterConfigItem(configId, itemKey, itemType, defaultValue, description);
    }

    /// <summary>Internal: used by <see cref="LiveConfigProxy"/> to read cached values.</summary>
    internal IReadOnlyDictionary<string, object?>? GetCachedValues(string id)
        => _configCache.TryGetValue(id, out var values)
            ? new Dictionary<string, object?>(values)
            : null;

    /// <summary>Internal: used by <see cref="LiveConfigProxy"/> to test cache without forcing connect.</summary>
    internal bool HasResolved(string id) => _connected && _configCache.ContainsKey(id);

    // ------------------------------------------------------------------
    // Live surface: lazy connect + transport / WebSocket helpers
    // ------------------------------------------------------------------

    /// <summary>Return the shared WebSocket — the parent's when wired, else our own.</summary>
    private SharedWebSocket EnsureWs()
    {
        if (_ensureWs is not null)
            return _ensureWs();
        if (_wsManager is null)
        {
            _wsManager = new SharedWebSocket(
                _standaloneApiKey ?? string.Empty,
                metrics: _metrics,
                appBaseUrl: _appBaseUrl ?? "https://app.smplkit.com");
            _wsManager.Start();
            _ownsWs = true;
        }
        return _wsManager;
    }

    /// <summary>
    /// Open the live connection to the running Smpl Config service.
    /// </summary>
    /// <remarks>
    /// Flushes any buffered discovery declarations, fetches and resolves
    /// every config for the configured environment into the local cache,
    /// opens the shared WebSocket, and subscribes to <c>config_changed</c> /
    /// <c>config_deleted</c> / <c>configs_changed</c> events.
    /// <para>Idempotent and internal — every live method calls it on first use, so
    /// the live surface auto-connects with no explicit step.</para>
    /// </remarks>
    internal void EnsureConnected()
    {
        if (_connected) return;
        lock (_initLock)
        {
            if (_connected) return;

            // Flush any buffered discovery declarations BEFORE the initial fetch,
            // so newly-discovered configs appear in the cache on first read.
            // FlushAsync swallows network/server failures internally.
            try { FlushAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { DebugLog.Log("registration", $"Config discovery flush before connect failed: {ex.Message}"); }

            // Fetch + resolve + cache + fire change listeners (against empty old_cache,
            // so any registered listeners see "initial" events).
            DoRefresh("initial");
            _connected = true;

            DebugLog.Log("registration", "registering config_changed, config_deleted, and configs_changed handlers");
            _wsManager = EnsureWs();
            _wsManager.On("config_changed", HandleConfigChanged);
            _wsManager.On("config_deleted", HandleConfigDeleted);
            _wsManager.On("configs_changed", HandleConfigsChanged);
            _wsManager.WaitForInitialConnectAsync().GetAwaiter().GetResult();
            DebugLog.Log("websocket", "config runtime connected");
        }
    }

    /// <summary>
    /// Async pre-warm of the live connection. Mirrors <see cref="EnsureConnected"/>;
    /// the integrator's <see cref="Smplkit.SmplClient.WaitUntilReadyAsync"/> calls
    /// this so the live surface is hot before the first read.
    /// </summary>
    internal Task EnsureConnectedAsync(CancellationToken ct = default)
        => Task.Run(EnsureConnected, ct);

    private async Task<List<Config>> FetchAllConfigsAsync(CancellationToken ct)
    {
        return await Helpers.FetchAllPagesAsync(
            (page, size, c) => ListAsync(page, size, c), ct).ConfigureAwait(false);
    }

    private async Task<Config?> FetchConfigAsync(string configId, CancellationToken ct)
    {
        var response = await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.Get_configAsync(id: configId, cancellationToken: ct)).ConfigureAwait(false);
        return MapResource(response.Data);
    }

    // ------------------------------------------------------------------
    // Live surface: refresh / change listeners
    // ------------------------------------------------------------------

    /// <summary>
    /// Re-fetch all configs and update resolved values.
    /// </summary>
    /// <remarks>
    /// Fires change listeners for any values that differ from the previous
    /// state. Connects lazily on first use — no explicit install step.
    /// </remarks>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ConnectionException">If the fetch fails.</exception>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        EnsureConnected();

        var allConfigs = await FetchAllConfigsAsync(ct).ConfigureAwait(false);
        var environment = _environment ?? string.Empty;

        var newCache = BuildResolvedCache(allConfigs, environment);
        MergePendingSeeds(newCache);
        var oldCache = _configCache;
        _configCache = newCache;
        DiffAndFire(oldCache, newCache, "manual");
    }

    /// <summary>
    /// Re-apply in-memory seeds for bound configs not yet present server-side.
    /// </summary>
    /// <remarks>
    /// A freshly-bound config lives only as a seed until it is flushed and
    /// fetched; without this, any cache rebuild (a manual refresh, or a
    /// WebSocket event for another config) would drop it. Server-present
    /// configs are already in <paramref name="newCache"/> and are authoritative — only
    /// bound ids missing from it are re-seeded.
    /// </remarks>
    private void MergePendingSeeds(Dictionary<string, Dictionary<string, object?>> newCache)
    {
        lock (_bindingsLock)
        {
            foreach (var boundId in _bindings.Keys)
            {
                if (!newCache.ContainsKey(boundId))
                    newCache[boundId] = ResolveBoundChain(boundId);
            }
        }
    }

    private void DoRefresh(string source)
    {
        var allConfigs = FetchAllConfigsAsync(default).GetAwaiter().GetResult();
        var environment = _environment ?? string.Empty;
        var newCache = BuildResolvedCache(allConfigs, environment);
        MergePendingSeeds(newCache);
        var oldCache = _configCache;
        _configCache = newCache;
        DiffAndFire(oldCache, newCache, source);
    }

    /// <summary>Register a global change listener that fires on any config change.</summary>
    /// <remarks>Connects lazily on first use — no explicit install step.</remarks>
    public void OnChange(Action<ConfigChangeEvent> callback)
    {
        EnsureConnected();
        lock (_listenerLock) { _listeners.Add((callback, null, null)); }
    }

    /// <summary>Register a change listener scoped to a specific config id.</summary>
    /// <remarks>Connects lazily on first use — no explicit install step.</remarks>
    public void OnChange(string configId, Action<ConfigChangeEvent> callback)
    {
        EnsureConnected();
        lock (_listenerLock) { _listeners.Add((callback, configId, null)); }
    }

    /// <summary>Register a change listener scoped to a specific config id and item key.</summary>
    /// <remarks>Connects lazily on first use — no explicit install step.</remarks>
    public void OnChange(string configId, string itemKey, Action<ConfigChangeEvent> callback)
    {
        EnsureConnected();
        lock (_listenerLock) { _listeners.Add((callback, configId, itemKey)); }
    }

    // ------------------------------------------------------------------
    // Internal: cache management
    // ------------------------------------------------------------------

    /// <summary>
    /// Re-resolve every config in <paramref name="allConfigs"/> against the parent
    /// hierarchy and return the resolved-values cache.
    /// </summary>
    /// <remarks>
    /// Inheritance means a single config change can shift descendants' resolved
    /// values too — so whenever the raw config set changes (config added, updated,
    /// or deleted), every config gets re-resolved against the new snapshot.
    /// </remarks>
    private static Dictionary<string, Dictionary<string, object?>> BuildResolvedCache(
        List<Config> allConfigs, string environment)
    {
        var configById = new Dictionary<string, Config>();
        foreach (var cfg in allConfigs)
        {
            if (cfg.Id is not null)
                configById[cfg.Id] = cfg;
        }

        var cache = new Dictionary<string, Dictionary<string, object?>>();

        foreach (var cfg in allConfigs)
        {
            var chain = new List<ConfigChainEntry> { Resolver.ToChainEntry(cfg) };
            var current = cfg;
            while (current.Parent is not null && configById.TryGetValue(current.Parent, out var parent))
            {
                chain.Add(Resolver.ToChainEntry(parent));
                current = parent;
            }

            var resolved = Resolver.Resolve(chain, environment);
            cache[cfg.Id!] = resolved;
        }

        return cache;
    }

    // ------------------------------------------------------------------
    // Internal: WebSocket event handlers
    // ------------------------------------------------------------------

    private void HandleConfigChanged(Dictionary<string, object?> data)
    {
        var configId = data.TryGetValue("id", out var k) ? k as string : null;
        DebugLog.Log("websocket", $"config_changed event received, id={configId ?? "<unknown>"}");
        if (!_connected || configId is null)
        {
            HandleConfigsChanged(data);
            return;
        }

        var environment = _environment ?? string.Empty;

        try
        {
            var config = FetchConfigAsync(configId, default).GetAwaiter().GetResult();
            if (config is null) return;

            var oldCache = new Dictionary<string, Dictionary<string, object?>>(_configCache);

            var chain = new List<ConfigChainEntry> { Resolver.ToChainEntry(config) };
            var newCache = new Dictionary<string, Dictionary<string, object?>>(_configCache);
            newCache[configId] = Resolver.Resolve(chain, environment);
            MergePendingSeeds(newCache);
            _configCache = newCache;

            DiffAndFire(oldCache, _configCache, "websocket");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("[smplkit] Config refresh failed: {0}", ex.Message);
            DebugLog.Log("websocket", $"Config refresh failed: {ex}");
        }
    }

    private void HandleConfigDeleted(Dictionary<string, object?> data)
    {
        var configId = data.TryGetValue("id", out var k) ? k as string : null;
        DebugLog.Log("websocket", $"config_deleted event received, id={configId ?? "<unknown>"}");
        if (!_connected || configId is null)
        {
            HandleConfigsChanged(data);
            return;
        }

        var oldCache = new Dictionary<string, Dictionary<string, object?>>(_configCache);
        var newCache = new Dictionary<string, Dictionary<string, object?>>(_configCache);
        if (!newCache.Remove(configId)) return;
        MergePendingSeeds(newCache);
        _configCache = newCache;

        DiffAndFire(oldCache, _configCache, "websocket");
    }

    private void HandleConfigsChanged(Dictionary<string, object?> data)
    {
        DebugLog.Log("websocket", "configs_changed event received — full list refetch");
        if (!_connected) return;

        try
        {
            DoRefresh("websocket");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("[smplkit] Configs bulk refresh failed: {0}", ex.Message);
            DebugLog.Log("websocket", $"Configs bulk refresh failed: {ex}");
        }
    }

    // ------------------------------------------------------------------
    // Internal: diff and fire listeners
    // ------------------------------------------------------------------

    /// <summary>Diff two caches, apply changes to bound instances, fire listeners.</summary>
    internal void DiffAndFire(
        Dictionary<string, Dictionary<string, object?>> oldCache,
        Dictionary<string, Dictionary<string, object?>> newCache,
        string source)
    {
        List<(Action<ConfigChangeEvent> Callback, string? ConfigId, string? ItemKey)> listeners;
        lock (_listenerLock)
        {
            listeners = new(_listeners);
        }

        var allConfigIds = new HashSet<string>(oldCache.Keys);
        allConfigIds.UnionWith(newCache.Keys);

        foreach (var cfgId in allConfigIds)
        {
            var oldItems = oldCache.GetValueOrDefault(cfgId) ?? new Dictionary<string, object?>();
            var newItems = newCache.GetValueOrDefault(cfgId) ?? new Dictionary<string, object?>();

            var allItemKeys = new HashSet<string>(oldItems.Keys);
            allItemKeys.UnionWith(newItems.Keys);

            object? target;
            lock (_bindingsLock) { _bindings.TryGetValue(cfgId, out target); }

            foreach (var iKey in allItemKeys)
            {
                var oldVal = oldItems.GetValueOrDefault(iKey);
                var newVal = newItems.GetValueOrDefault(iKey);
                if (Equals(oldVal, newVal)) continue;

                // Apply to bound target first so listeners reading the
                // object see the new value.
                if (target is not null) ApplyChangeToTarget(target, iKey, newVal);

                _metrics?.Record("config.changes", unit: "changes",
                    dimensions: new Dictionary<string, string> { ["config"] = cfgId });

                if (listeners.Count == 0) continue;

                var evt = new ConfigChangeEvent(cfgId, iKey, oldVal, newVal, source);
                foreach (var (callback, filterCfgId, filterItemKey) in listeners)
                {
                    if (filterCfgId is not null && filterCfgId != cfgId) continue;
                    if (filterItemKey is not null && filterItemKey != iKey) continue;
                    try { callback(evt); }
                    catch { /* Ignore listener exceptions */ }
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    /// <summary>
    /// Release resources — only those this client owns.
    /// </summary>
    /// <remarks>
    /// Tears down the owned WebSocket (opened by a standalone client on
    /// first live use) and the owned HTTP transport (standalone
    /// construction). A wired client borrows the parent's transport and
    /// WebSocket and closes neither.
    /// </remarks>
    public void Dispose()
    {
        if (_ownsWs && _wsManager is not null)
        {
            _wsManager.StopAsync().GetAwaiter().GetResult();
            _wsManager = null;
            _ownsWs = false;
        }
        _ownedMetrics?.Dispose();
        _ownedHttpClient?.Dispose();
    }

    // ==================================================================
    // Wire helpers — request body construction + response mapping.
    // ==================================================================

    private string? ResolveParentId(object? parent) => parent switch
    {
        null => null,
        string s => s,
        Config c => c.Id ?? throw new ArgumentException(
            "parent config must be saved (have an id) before being used as a parent", nameof(parent)),
        _ => throw new ArgumentException(
            $"parent must be a string id or a Config instance; got {parent.GetType().Name}", nameof(parent)),
    };

    private static GenConfig.ConfigRequest BuildRequestBody(Config config) =>
        new()
        {
            Data = new GenConfig.ConfigResource
            {
                Type = "config",
                Id = config.Id,
                Attributes = BuildConfigAttributes(config),
            },
        };

    private static GenConfig.ConfigCreateRequest BuildCreateRequestBody(Config config) =>
        new()
        {
            Data = new GenConfig.ConfigCreateResource
            {
                Type = "config",
                Id = config.Id ?? throw new ValidationException("Cannot create a config without an id"),
                Attributes = BuildConfigAttributes(config),
            },
        };

    private static GenConfig.Config BuildConfigAttributes(Config config) =>
        new()
        {
            Name = config.Name,
            Description = config.Description,
            Parent = config.Parent,
            Items = WrapItemsForRequest(config.Items),
            Environments = WrapEnvsForRequest(config.Environments),
        };

    private static IDictionary<string, GenConfig.ConfigItemDefinition>? WrapItemsForRequest(
        Dictionary<string, object?>? items)
    {
        if (items is null || items.Count == 0) return null;

        var result = new Dictionary<string, GenConfig.ConfigItemDefinition>(items.Count);
        foreach (var (key, value) in items)
        {
            result[key] = new GenConfig.ConfigItemDefinition
            {
                Value = value!,
                Type = InferType(value),
            };
        }
        return result;
    }

    // Per ADR-024 §2.4 the wire shape is now flat — `{env: {key: rawValue}}` —
    // so we emit each env's overrides as a plain dict of key → raw value with
    // no wrapper envelope.
    private static IDictionary<string, object>? WrapEnvsForRequest(
        Dictionary<string, Dictionary<string, object?>>? environments)
    {
        if (environments is null || environments.Count == 0) return null;

        var result = new Dictionary<string, object>(environments.Count);
        foreach (var (envName, envData) in environments)
        {
            var values = new Dictionary<string, object?>(envData.Count);
            foreach (var (key, value) in envData)
            {
                values[key] = value;
            }
            result[envName] = values;
        }
        return result;
    }

    private static GenConfig.ConfigItemDefinitionType? InferType(object? value) => value switch
    {
        string => GenConfig.ConfigItemDefinitionType.STRING,
        bool => GenConfig.ConfigItemDefinitionType.BOOLEAN,
        int or long or float or double or decimal => GenConfig.ConfigItemDefinitionType.NUMBER,
        _ => null,
    };

    private Config? MapResource(GenConfig.ConfigResource? resource)
    {
        if (resource?.Attributes is null)
            return null;

        var attrs = resource.Attributes;
        var items = ExtractRawItems(attrs.Items);
        var environments = ExtractRawEnvironments(attrs.Environments);

        return new Config(
            client: this,
            id: resource.Id ?? string.Empty,
            name: attrs.Name ?? string.Empty,
            description: attrs.Description,
            parent: attrs.Parent,
            items: items,
            environments: environments,
            createdAt: attrs.Created_at?.DateTime,
            updatedAt: attrs.Updated_at?.DateTime
        );
    }

    private static Dictionary<string, object?> ExtractRawItems(
        IDictionary<string, GenConfig.ConfigItemDefinition>? items)
    {
        if (items is null)
            return new Dictionary<string, object?>();

        var result = new Dictionary<string, object?>(items.Count);
        foreach (var (key, definition) in items)
        {
            result[key] = Resolver.Normalize(definition.Value);
        }
        return result;
    }

    // Per ADR-024 §2.4 the wire shape is flat — `{env: {key: rawValue}}`. The
    // generated client surfaces each env as `object` (after JSON deserialization
    // it's typically a `JsonElement` for an object, or a `Dictionary<string,
    // object?>` if constructed in-memory). Normalize unwraps both into a flat
    // dict of key → raw value, dropping anything that isn't an object shape.
    private static Dictionary<string, Dictionary<string, object?>> ExtractRawEnvironments(
        IDictionary<string, object>? environments)
    {
        if (environments is null)
            return new Dictionary<string, Dictionary<string, object?>>();

        var result = new Dictionary<string, Dictionary<string, object?>>(environments.Count);
        foreach (var (envName, envValue) in environments)
        {
            var normalized = Resolver.Normalize(envValue);
            result[envName] = normalized is Dictionary<string, object?> d
                ? d
                : new Dictionary<string, object?>();
        }
        return result;
    }

    // ==================================================================
    // Discovery helpers (POCO + dict iteration, change application)
    // ==================================================================

    /// <summary>
    /// Map a runtime value (POCO leaf, dict value, or GetValueOr default) to
    /// a Config item type. <c>bool</c> is checked before number — although
    /// C# bool isn't a subclass of int, mirroring the Python ordering keeps
    /// the cross-language semantics identical.
    /// </summary>
    internal static string InferItemType(object? value) => value switch
    {
        bool => "BOOLEAN",
        int or long or float or double or decimal => "NUMBER",
        string => "STRING",
        _ => "STRING",
    };

    /// <summary>
    /// Walk an <c>IDictionary&lt;string, object?&gt;</c> and yield
    /// <c>(key, type, value)</c> triples flattened to dot-notation. Nested
    /// dicts are descended; every key is treated as an explicit override.
    /// </summary>
    private static IEnumerable<(string Key, string Type, object? Value)> IterDictItems(
        IDictionary<string, object?> dict, string prefix = "")
    {
        foreach (var (rawKey, value) in dict)
        {
            var flatKey = prefix + rawKey;
            if (value is IDictionary<string, object?> sub)
            {
                foreach (var nested in IterDictItems(sub, flatKey + "."))
                    yield return nested;
                continue;
            }
            yield return (flatKey, InferItemType(value), value);
        }
    }

    /// <summary>
    /// Walk a POCO instance's public, readable, non-indexer properties and
    /// yield <c>(key, type, value)</c> triples flattened to dot-notation.
    /// Property names are converted PascalCase → snake_case on the wire.
    /// Nested POCO properties (non-primitive, non-string, non-enumerable
    /// reference types) are descended; arrays, dictionaries, and other
    /// collections are treated as opaque leaves.
    /// </summary>
    private static IEnumerable<(string Key, string Type, object? Value)> IterPocoItems(
        object instance, string prefix = "")
    {
        var type = instance.GetType();
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length > 0) continue;
            if (!prop.CanRead) continue;

            var flatKey = prefix + ToSnakeCase(prop.Name);
            object? value;
            try { value = prop.GetValue(instance); }
            catch { continue; }

            if (value is not null && IsNestablePocoType(prop.PropertyType))
            {
                foreach (var nested in IterPocoItems(value, flatKey + "."))
                    yield return nested;
                continue;
            }

            yield return (flatKey, InferItemType(value), value);
        }
    }

    /// <summary>
    /// Tells whether a property type should be recursed into (true) or
    /// treated as a leaf (false). Primitives, strings, value types, enums,
    /// and any <see cref="System.Collections.IEnumerable"/> (arrays, lists,
    /// dictionaries) are leaves.
    /// </summary>
    private static bool IsNestablePocoType(Type t)
    {
        var u = Nullable.GetUnderlyingType(t) ?? t;
        if (u == typeof(string)) return false;
        if (u.IsPrimitive) return false;
        if (u.IsValueType) return false;
        if (u == typeof(object)) return false;
        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(u)) return false;
        return true;
    }

    /// <summary>
    /// Apply a server-pushed value to a bound target in place. Walks the
    /// dotted key path to the leaf's parent, then assigns the value.
    /// Handles dicts (via indexer), POCOs (via setter or backing field),
    /// and mixed nesting. Bails silently if any intermediate is missing.
    /// </summary>
    private static void ApplyChangeToTarget(object target, string dottedKey, object? value)
    {
        var parts = dottedKey.Split('.');
        object? current = target;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (current is null) return;
            var part = parts[i];
            if (current is IDictionary<string, object?> dict)
            {
                if (!dict.TryGetValue(part, out var next)) return;
                current = next;
            }
            else
            {
                var prop = FindPropertyBySnakeName(current.GetType(), part);
                if (prop is null) return;
                try { current = prop.GetValue(current); }
                catch { return; }
            }
        }

        if (current is null) return;
        var last = parts[^1];
        if (current is IDictionary<string, object?> dictCurrent)
        {
            dictCurrent[last] = value;
        }
        else
        {
            var prop = FindPropertyBySnakeName(current.GetType(), last);
            if (prop is null) return;
            var coerced = CoerceValue(value, prop.PropertyType);
            SetPropertyValue(current, prop, coerced);
        }
    }

    private static PropertyInfo? FindPropertyBySnakeName(Type type, string snakeName)
    {
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length > 0) continue;
            if (prop.Name == snakeName) return prop;
            if (ToSnakeCase(prop.Name) == snakeName) return prop;
        }
        return null;
    }

    /// <summary>
    /// Set a property value, using the setter if available, otherwise the
    /// compiler-generated backing field. This is the C# equivalent of
    /// Python's <c>object.__setattr__</c> — it bypasses init-only setters
    /// on records so the in-place mutation contract holds for any binding
    /// target with a backing field.
    /// </summary>
    private static void SetPropertyValue(object target, PropertyInfo prop, object? value)
    {
        if (prop.CanWrite && prop.SetMethod is not null && prop.SetMethod.IsPublic)
        {
            try
            {
                prop.SetValue(target, value);
                return;
            }
            catch { /* Init-only on a record — fall through to the backing field. */ }
        }

        var backingField = target.GetType().GetField(
            $"<{prop.Name}>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        try { backingField?.SetValue(target, value); }
        catch { /* Best-effort: malformed/non-standard layouts are skipped. */ }
    }

    /// <summary>
    /// Coerce a server-supplied value (typically <c>long</c> / <c>double</c>
    /// / <c>string</c> / <c>bool</c> after resolver normalization) to the
    /// target property type. Numeric widening / narrowing handled
    /// explicitly; everything else falls back to <see cref="Convert.ChangeType(object, Type)"/>
    /// or passes the raw value through.
    /// </summary>
    private static object? CoerceValue(object? value, Type targetType)
    {
        if (value is null) return null;
        if (targetType.IsInstanceOfType(value)) return value;

        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        // JsonElement passthrough — rare after resolver normalization, but
        // the resolver doesn't touch single-config-replace WS payloads if
        // the management mapper hasn't already unwrapped them.
        if (value is JsonElement je) value = UnwrapJsonElement(je);

        if (value is null) return null;
        if (underlying.IsInstanceOfType(value)) return value;

        if (underlying.IsEnum && value is string s) return Enum.Parse(underlying, s, ignoreCase: true);

        try { return Convert.ChangeType(value, underlying, System.Globalization.CultureInfo.InvariantCulture); }
        catch { return value; }
    }

    private static object? UnwrapJsonElement(JsonElement je) => je.ValueKind switch
    {
        JsonValueKind.String => je.GetString(),
        JsonValueKind.Number => je.TryGetInt64(out var l) ? l : je.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => je,
    };

    /// <summary>
    /// Convert a PascalCase identifier to snake_case. Mirrors
    /// <c>JsonNamingPolicy.SnakeCaseLower</c> closely enough for property
    /// names (which never contain spaces or punctuation), but is a simple
    /// in-place loop so we don't allocate a serializer just to map a name.
    /// </summary>
    internal static string ToSnakeCase(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new System.Text.StringBuilder(s.Length + 4);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (i > 0 && char.IsUpper(c))
            {
                bool prevLower = char.IsLower(s[i - 1]);
                bool nextLower = i + 1 < s.Length && char.IsLower(s[i + 1]);
                if (prevLower || nextLower) sb.Append('_');
            }
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
