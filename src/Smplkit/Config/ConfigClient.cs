using Smplkit.Errors;
using Smplkit.Internal;
using GenConfig = Smplkit.Internal.Generated.Config;

namespace Smplkit.Config;

/// <summary>
/// Client for the smplkit Config service. Provides CRUD operations on
/// configuration resources plus prescriptive value access after
/// <see cref="SmplClient.ConnectAsync"/>.
/// </summary>
public sealed class ConfigClient
{
    private readonly GenConfig.ConfigClient _genClient;
    private readonly SmplClient? _parent;
    private volatile bool _prescriptiveConnected;
    private Dictionary<string, Dictionary<string, object?>> _configCache = new();
    private readonly List<(Action<ConfigChangeEvent> Callback, string? ConfigKey, string? ItemKey)> _listeners = new();
    private readonly object _listenerLock = new();

    /// <summary>
    /// Initializes a new instance of <see cref="ConfigClient"/>.
    /// </summary>
    /// <param name="clients">The generated client factory.</param>
    /// <param name="parent">The parent <see cref="SmplClient"/>, if any.</param>
    internal ConfigClient(GeneratedClientFactory clients, SmplClient? parent = null)
    {
        _genClient = clients.Config;
        _parent = parent;
    }

    /// <summary>
    /// Fetches a single config by its UUID.
    /// </summary>
    /// <param name="id">The config UUID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching <see cref="Config"/>.</returns>
    /// <exception cref="SmplNotFoundException">If no matching config exists.</exception>
    public async Task<Config> GetAsync(string id, CancellationToken ct = default)
    {
        var response = await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.Get_configAsync(Guid.Parse(id), ct)).ConfigureAwait(false);
        return MapResource(response.Data)
            ?? throw new SmplNotFoundException($"Config {id} not found");
    }

    /// <summary>
    /// Fetches a single config by its human-readable key.
    /// Uses the list endpoint with a <c>filter[key]</c> query parameter.
    /// </summary>
    /// <param name="key">The config key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching <see cref="Config"/>.</returns>
    /// <exception cref="SmplNotFoundException">If no matching config exists.</exception>
    public async Task<Config> GetByKeyAsync(string key, CancellationToken ct = default)
    {
        var response = await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.List_configsAsync(filterkey: key, cancellationToken: ct)).ConfigureAwait(false);

        if (response.Data is null || response.Data.Count == 0)
            throw new SmplNotFoundException($"Config with key '{key}' not found");

        return MapResource(response.Data[0])
            ?? throw new SmplNotFoundException($"Config with key '{key}' not found");
    }

    /// <summary>
    /// Lists all configs for the account.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of <see cref="Config"/> objects.</returns>
    public async Task<List<Config>> ListAsync(CancellationToken ct = default)
    {
        var response = await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.List_configsAsync(cancellationToken: ct)).ConfigureAwait(false);

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

    /// <summary>
    /// Creates a new config.
    /// </summary>
    /// <param name="options">The creation options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created <see cref="Config"/>.</returns>
    /// <exception cref="SmplValidationException">If the server rejects the request.</exception>
    public async Task<Config> CreateAsync(CreateConfigOptions options, CancellationToken ct = default)
    {
        var body = BuildRequestBody(options);
        var response = await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.Create_configAsync(body, ct)).ConfigureAwait(false);
        return MapResource(response.Data)
            ?? throw new SmplValidationException("Failed to create config");
    }

    /// <summary>
    /// Updates an existing config by its UUID.
    /// </summary>
    /// <param name="id">The UUID of the config to update.</param>
    /// <param name="options">The fields to update.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated <see cref="Config"/>.</returns>
    /// <exception cref="SmplNotFoundException">If the config does not exist.</exception>
    /// <exception cref="SmplValidationException">If the server rejects the request.</exception>
    public async Task<Config> UpdateAsync(string id, CreateConfigOptions options, CancellationToken ct = default)
    {
        var body = BuildRequestBody(options);
        var response = await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.Update_configAsync(Guid.Parse(id), body, ct)).ConfigureAwait(false);
        return MapResource(response.Data)
            ?? throw new SmplValidationException("Failed to update config");
    }

    /// <summary>
    /// Deletes a config by its UUID.
    /// </summary>
    /// <param name="id">The UUID of the config to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="SmplNotFoundException">If the config does not exist.</exception>
    /// <exception cref="SmplConflictException">If the config has children.</exception>
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.Delete_configAsync(Guid.Parse(id), ct)).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces base or environment-specific items on a config and persists via PUT.
    /// </summary>
    /// <param name="id">The config UUID.</param>
    /// <param name="values">The raw values dict to set.</param>
    /// <param name="environment">
    /// If provided, replaces that environment's values. If <c>null</c>, replaces the base items.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated <see cref="Config"/>.</returns>
    public async Task<Config> SetValuesAsync(
        string id,
        Dictionary<string, object?> values,
        string? environment = null,
        CancellationToken ct = default)
    {
        var current = await GetAsync(id, ct).ConfigureAwait(false);
        return await ApplySetValues(current, values, environment, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets a single key within base or environment-specific values.
    /// Merges the key into existing values rather than replacing all values.
    /// </summary>
    /// <param name="id">The config UUID.</param>
    /// <param name="key">The config key to set.</param>
    /// <param name="value">The value to assign.</param>
    /// <param name="environment">Target environment, or <c>null</c> for base values.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated <see cref="Config"/>.</returns>
    public async Task<Config> SetValueAsync(
        string id,
        string key,
        object? value,
        string? environment = null,
        CancellationToken ct = default)
    {
        var current = await GetAsync(id, ct).ConfigureAwait(false);

        if (environment is null)
        {
            var merged = new Dictionary<string, object?>(current.Items) { [key] = value };
            return await ApplySetValues(current, merged, null, ct).ConfigureAwait(false);
        }
        else
        {
            // Get existing env values (already extracted as raw values by MapResource)
            Dictionary<string, object?> existingEnvValues;
            if (current.Environments.TryGetValue(environment, out var envData))
            {
                existingEnvValues = new Dictionary<string, object?>(envData);
            }
            else
            {
                existingEnvValues = new Dictionary<string, object?>();
            }
            existingEnvValues[key] = value;
            return await ApplySetValues(current, existingEnvValues, environment, ct).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------
    // Prescriptive access: ConnectInternalAsync + typed accessors
    // ------------------------------------------------------------------

    /// <summary>
    /// Internal connect: fetches all configs, builds chains, resolves values
    /// for the given environment, and populates the prescriptive cache.
    /// Called by <see cref="SmplClient.ConnectAsync"/>.
    /// </summary>
    /// <param name="environment">The environment key.</param>
    /// <param name="ct">Cancellation token.</param>
    internal async Task ConnectInternalAsync(string environment, CancellationToken ct = default)
    {
        var allConfigs = await ListAsync(ct).ConfigureAwait(false);
        var configById = new Dictionary<string, Config>();
        foreach (var cfg in allConfigs)
            configById[cfg.Id] = cfg;

        var cache = new Dictionary<string, Dictionary<string, object?>>();

        foreach (var cfg in allConfigs)
        {
            // Build chain: child-first, root-last
            var chain = new List<ConfigChainEntry> { Resolver.ToChainEntry(cfg) };
            var current = cfg;
            while (current.Parent is not null && configById.TryGetValue(current.Parent, out var parent))
            {
                chain.Add(Resolver.ToChainEntry(parent));
                current = parent;
            }

            var resolved = Resolver.Resolve(chain, environment);
            cache[cfg.Key] = resolved;
        }

        _configCache = cache;
        _prescriptiveConnected = true;
    }

    /// <summary>
    /// Prescriptive config access. Returns all resolved values for a config key,
    /// or a single item value if <paramref name="itemKey"/> is specified.
    /// </summary>
    /// <param name="configKey">The config key.</param>
    /// <param name="itemKey">Optional item key within the config.</param>
    /// <returns>
    /// When <paramref name="itemKey"/> is <c>null</c>, returns a copy of all
    /// resolved values as <c>Dictionary&lt;string, object?&gt;</c>.
    /// When <paramref name="itemKey"/> is specified, returns the single value
    /// (or <c>null</c> if the key is not found).
    /// </returns>
    /// <exception cref="SmplNotConnectedException">If <see cref="SmplClient.ConnectAsync"/>
    /// has not been called.</exception>
    /// <exception cref="SmplNotFoundException">If no config with the given key exists.</exception>
    public object? GetValue(string configKey, string? itemKey = null)
    {
        if (!_prescriptiveConnected)
            throw new SmplNotConnectedException();

        if (!_configCache.TryGetValue(configKey, out var values))
            throw new SmplNotFoundException($"Config with key '{configKey}' not found in cache.");

        if (itemKey is null)
            return new Dictionary<string, object?>(values);

        return values.TryGetValue(itemKey, out var value) ? value : null;
    }

    /// <summary>
    /// Return the value for <paramref name="itemKey"/> if it is a <see cref="string"/>,
    /// otherwise <paramref name="defaultValue"/>.
    /// </summary>
    /// <exception cref="SmplNotConnectedException">If <see cref="SmplClient.ConnectAsync"/>
    /// has not been called.</exception>
    public string? GetString(string configKey, string itemKey, string? defaultValue = null)
    {
        var val = GetValue(configKey, itemKey);
        return val is string s ? s : defaultValue;
    }

    /// <summary>
    /// Return the value for <paramref name="itemKey"/> if it is numeric and integral,
    /// otherwise <paramref name="defaultValue"/>.
    /// </summary>
    /// <exception cref="SmplNotConnectedException">If <see cref="SmplClient.ConnectAsync"/>
    /// has not been called.</exception>
    public int? GetInt(string configKey, string itemKey, int? defaultValue = null)
    {
        var val = GetValue(configKey, itemKey);
        return val switch
        {
            long l when l >= int.MinValue && l <= int.MaxValue => (int)l,
            double d when d == Math.Truncate(d) && d >= int.MinValue && d <= int.MaxValue
                => (int)d,
            _ => defaultValue,
        };
    }

    /// <summary>
    /// Return the value for <paramref name="itemKey"/> if it is a <see cref="bool"/>,
    /// otherwise <paramref name="defaultValue"/>.
    /// </summary>
    /// <exception cref="SmplNotConnectedException">If <see cref="SmplClient.ConnectAsync"/>
    /// has not been called.</exception>
    public bool? GetBool(string configKey, string itemKey, bool? defaultValue = null)
    {
        var val = GetValue(configKey, itemKey);
        return val is bool b ? b : defaultValue;
    }

    /// <summary>
    /// Re-fetches all configs, re-resolves values for the current environment,
    /// and fires change listeners for any values that differ from the previous cache.
    /// </summary>
    /// <exception cref="SmplNotConnectedException">If <see cref="SmplClient.ConnectAsync"/>
    /// has not been called.</exception>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (!_prescriptiveConnected)
            throw new SmplNotConnectedException();

        var environment = _parent?.Environment
            ?? throw new SmplException("No environment set.");

        var allConfigs = await ListAsync(ct).ConfigureAwait(false);
        var configById = new Dictionary<string, Config>();
        foreach (var cfg in allConfigs)
            configById[cfg.Id] = cfg;

        var newCache = new Dictionary<string, Dictionary<string, object?>>();
        foreach (var cfg in allConfigs)
        {
            var chain = new List<ConfigChainEntry> { Resolver.ToChainEntry(cfg) };
            var current = cfg;
            while (current.Parent is not null && configById.TryGetValue(current.Parent, out var parent))
            {
                chain.Add(Resolver.ToChainEntry(parent));
                current = parent;
            }
            newCache[cfg.Key] = Resolver.Resolve(chain, environment);
        }

        var oldCache = _configCache;
        _configCache = newCache;
        DiffAndFire(oldCache, newCache, "manual");
    }

    /// <summary>
    /// Register a listener that fires when a config value changes (on Refresh).
    /// </summary>
    /// <param name="callback">Called with a <see cref="ConfigChangeEvent"/> on each change.</param>
    /// <param name="configKey">If provided, fires only for changes to this config.</param>
    /// <param name="itemKey">If provided, fires only for changes to this specific item.</param>
    public void OnChange(Action<ConfigChangeEvent> callback, string? configKey = null, string? itemKey = null)
    {
        lock (_listenerLock)
        {
            _listeners.Add((callback, configKey, itemKey));
        }
    }

    /// <summary>
    /// Compare old and new caches and fire matching change listeners.
    /// </summary>
    internal void DiffAndFire(
        Dictionary<string, Dictionary<string, object?>> oldCache,
        Dictionary<string, Dictionary<string, object?>> newCache,
        string source)
    {
        List<(Action<ConfigChangeEvent> Callback, string? ConfigKey, string? ItemKey)> listeners;
        lock (_listenerLock)
        {
            listeners = new(_listeners);
        }

        if (listeners.Count == 0) return;

        var allConfigKeys = new HashSet<string>(oldCache.Keys);
        allConfigKeys.UnionWith(newCache.Keys);

        foreach (var cfgKey in allConfigKeys)
        {
            var oldItems = oldCache.GetValueOrDefault(cfgKey) ?? new Dictionary<string, object?>();
            var newItems = newCache.GetValueOrDefault(cfgKey) ?? new Dictionary<string, object?>();

            var allItemKeys = new HashSet<string>(oldItems.Keys);
            allItemKeys.UnionWith(newItems.Keys);

            foreach (var iKey in allItemKeys)
            {
                var oldVal = oldItems.GetValueOrDefault(iKey);
                var newVal = newItems.GetValueOrDefault(iKey);
                if (Equals(oldVal, newVal)) continue;

                var evt = new ConfigChangeEvent(cfgKey, iKey, oldVal, newVal, source);
                foreach (var (callback, filterCfgKey, filterItemKey) in listeners)
                {
                    if (filterCfgKey is not null && filterCfgKey != cfgKey) continue;
                    if (filterItemKey is not null && filterItemKey != iKey) continue;
                    try { callback(evt); }
                    catch { /* Ignore listener exceptions */ }
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // Private helpers
    // ------------------------------------------------------------------

    private async Task<Config> ApplySetValues(
        Config current,
        Dictionary<string, object?> values,
        string? environment,
        CancellationToken ct)
    {
        Dictionary<string, object?>? newItems;
        Dictionary<string, object?>? newEnvs;

        if (environment is null)
        {
            newItems = values;
            newEnvs = BuildEnvsForRequest(current.Environments);
        }
        else
        {
            newItems = current.Items;
            var reqEnvs = BuildEnvsForRequest(current.Environments);
            reqEnvs[environment] = values;
            newEnvs = reqEnvs;
        }

        return await UpdateAsync(current.Id, new CreateConfigOptions
        {
            Name = current.Name,
            Key = current.Key,
            Description = current.Description,
            Parent = current.Parent,
            Items = newItems,
            Environments = newEnvs,
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Convert <see cref="Config.Environments"/> (already extracted as raw values
    /// <c>{env: {key: raw}}</c>) to the flat <c>Dictionary&lt;string, object?&gt;</c> passed
    /// to <see cref="WrapEnvsForRequest"/> for the request body.</summary>
    private static Dictionary<string, object?> BuildEnvsForRequest(
        Dictionary<string, Dictionary<string, object?>> environments)
    {
        var result = new Dictionary<string, object?>(environments.Count);
        foreach (var (envName, envData) in environments)
            result[envName] = envData;
        return result;
    }

    /// <summary>Wrap raw items into generated DTO format for request bodies.
    /// SDK format: <c>{key: raw}</c> ->
    /// Generated DTO: <c>IDictionary&lt;string, ConfigItemDefinition&gt;</c></summary>
    private static IDictionary<string, GenConfig.ConfigItemDefinition>? WrapItemsForRequest(
        Dictionary<string, object?>? items)
    {
        if (items is null) return null;

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

    /// <summary>Wrap raw environment overrides into the generated DTO format for request bodies.
    /// SDK format: <c>{env: {key: raw}}</c> ->
    /// Generated DTO: <c>IDictionary&lt;string, EnvironmentOverride&gt;</c></summary>
    private static IDictionary<string, GenConfig.EnvironmentOverride>? WrapEnvsForRequest(
        Dictionary<string, object?>? environments)
    {
        if (environments is null) return null;

        var result = new Dictionary<string, GenConfig.EnvironmentOverride>(environments.Count);
        foreach (var (envName, envData) in environments)
        {
            if (envData is Dictionary<string, object?> envDict)
            {
                var values = new Dictionary<string, GenConfig.ConfigItemOverride>(envDict.Count);
                foreach (var (key, value) in envDict)
                {
                    values[key] = new GenConfig.ConfigItemOverride { Value = value! };
                }
                result[envName] = new GenConfig.EnvironmentOverride { Values = values };
            }
        }
        return result;
    }

    /// <summary>Infer the type enum for a raw value.</summary>
    private static GenConfig.ConfigItemDefinitionType? InferType(object? value) => value switch
    {
        string => GenConfig.ConfigItemDefinitionType.STRING,
        bool => GenConfig.ConfigItemDefinitionType.BOOLEAN,
        int or long or float or double or decimal => GenConfig.ConfigItemDefinitionType.NUMBER,
        _ => null,
    };

    private static GenConfig.Response_Config_ BuildRequestBody(CreateConfigOptions options) =>
        new()
        {
            Data = new GenConfig.Resource_Config_
            {
                Type = "config",
                Attributes = new GenConfig.Config
                {
                    Name = options.Name,
                    Key = options.Key,
                    Description = options.Description,
                    Parent = options.Parent,
                    Items = WrapItemsForRequest(options.Items),
                    Environments = WrapEnvsForRequest(options.Environments),
                },
            },
        };

    /// <summary>
    /// Maps a generated JSON:API resource to a <see cref="Config"/> record.
    /// Extracts raw values from typed item wrappers and environment override wrappers.
    /// </summary>
    private static Config? MapResource(GenConfig.ConfigResource? resource)
    {
        if (resource?.Attributes is null)
            return null;

        var attrs = resource.Attributes;

        // Extract raw values from typed items: ConfigItemDefinition -> raw value
        var items = ExtractRawItems(attrs.Items);

        // Extract raw values from environment overrides
        var environments = ExtractRawEnvironments(attrs.Environments);

        return new Config(
            Id: resource.Id ?? string.Empty,
            Key: attrs.Key ?? string.Empty,
            Name: attrs.Name ?? string.Empty,
            Description: attrs.Description,
            Parent: attrs.Parent,
            Items: items,
            Environments: environments,
            CreatedAt: attrs.Created_at?.DateTime,
            UpdatedAt: attrs.Updated_at?.DateTime
        );
    }

    /// <summary>
    /// Extracts raw values from generated <see cref="GenConfig.ConfigItemDefinition"/> wrappers.
    /// </summary>
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

    /// <summary>
    /// Extracts raw values from generated <see cref="GenConfig.EnvironmentOverride"/> wrappers.
    /// </summary>
    private static Dictionary<string, Dictionary<string, object?>> ExtractRawEnvironments(
        IDictionary<string, GenConfig.EnvironmentOverride>? environments)
    {
        if (environments is null)
            return new Dictionary<string, Dictionary<string, object?>>();

        var result = new Dictionary<string, Dictionary<string, object?>>(environments.Count);
        foreach (var (envName, envOverride) in environments)
        {
            var envValues = new Dictionary<string, object?>();
            if (envOverride.Values is not null)
            {
                foreach (var (key, itemOverride) in envOverride.Values)
                {
                    envValues[key] = Resolver.Normalize(itemOverride.Value);
                }
            }
            result[envName] = envValues;
        }
        return result;
    }
}
