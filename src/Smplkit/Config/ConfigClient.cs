using System.Text.Json;
using Smplkit.Errors;
using Smplkit.Internal;

namespace Smplkit.Config;

/// <summary>
/// Client for the smplkit Config service. Provides CRUD operations on
/// configuration resources plus runtime value resolution via
/// <see cref="ConnectAsync"/>.
/// </summary>
public sealed class ConfigClient
{
    private const string BaseUrl = "https://config.smplkit.com";

    private readonly Transport _transport;
    private readonly string _apiKey;
    private readonly SmplClient? _parent;
    private volatile bool _prescriptiveConnected;
    private Dictionary<string, Dictionary<string, object?>> _configCache = new();

    /// <summary>
    /// Initializes a new instance of <see cref="ConfigClient"/>.
    /// </summary>
    /// <param name="transport">The HTTP transport layer.</param>
    /// <param name="apiKey">The API key, forwarded to <see cref="ConfigRuntime"/> for WebSocket auth.</param>
    /// <param name="parent">The parent <see cref="SmplClient"/>, if any.</param>
    internal ConfigClient(Transport transport, string apiKey, SmplClient? parent = null)
    {
        _transport = transport;
        _apiKey = apiKey;
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
        var json = await _transport.GetAsync($"{BaseUrl}/api/v1/configs/{id}", ct).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<JsonApiSingleResponse>(json, Transport.SerializerOptions);
        return MapResource(response?.Data)
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
        var encodedKey = Uri.EscapeDataString(key);
        var json = await _transport.GetAsync($"{BaseUrl}/api/v1/configs?filter[key]={encodedKey}", ct).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<JsonApiListResponse>(json, Transport.SerializerOptions);

        if (response?.Data is null || response.Data.Count == 0)
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
        var json = await _transport.GetAsync($"{BaseUrl}/api/v1/configs", ct).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<JsonApiListResponse>(json, Transport.SerializerOptions);

        if (response?.Data is null)
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
        var json = await _transport.PostAsync($"{BaseUrl}/api/v1/configs", body, ct).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<JsonApiSingleResponse>(json, Transport.SerializerOptions);
        return MapResource(response?.Data)
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
        var json = await _transport.PutAsync($"{BaseUrl}/api/v1/configs/{id}", body, ct).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<JsonApiSingleResponse>(json, Transport.SerializerOptions);
        return MapResource(response?.Data)
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
        await _transport.DeleteAsync($"{BaseUrl}/api/v1/configs/{id}", ct).ConfigureAwait(false);
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

    /// <summary>
    /// Connect to a config for runtime value resolution. Eagerly fetches the
    /// config and its full parent chain, resolves values for the given
    /// environment via deep merge, opens a background WebSocket for real-time
    /// updates, and returns a fully populated <see cref="ConfigRuntime"/>.
    /// </summary>
    /// <param name="id">The config UUID.</param>
    /// <param name="environment">The environment to resolve for (e.g. <c>"production"</c>).</param>
    /// <param name="timeout">Maximum seconds to wait for the initial HTTP fetch.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="ConfigRuntime"/> ready for synchronous value reads.</returns>
    /// <exception cref="SmplTimeoutException">If the fetch exceeds <paramref name="timeout"/> seconds.</exception>
    public async Task<ConfigRuntime> ConnectAsync(
        string id,
        string environment,
        int timeout = 30,
        CancellationToken ct = default)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            var (configKey, chain) = await BuildChainAsync(id, linkedCts.Token).ConfigureAwait(false);

            return new ConfigRuntime(
                configKey: configKey,
                configId: id,
                environment: environment,
                chain: chain,
                apiKey: _apiKey,
                fetchChainFn: async fetchCt =>
                {
                    var (_, freshChain) = await BuildChainAsync(id, fetchCt).ConfigureAwait(false);
                    return freshChain;
                });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SmplTimeoutException($"ConnectAsync timed out after {timeout} seconds.");
        }
    }

    // ------------------------------------------------------------------
    // Prescriptive access: ConnectInternalAsync + GetValue
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

    /// <summary>Walk the parent chain starting at <paramref name="id"/> and return
    /// the config key and chain entries ordered child-first, root-last.</summary>
    private async Task<(string ConfigKey, List<ConfigChainEntry> Chain)> BuildChainAsync(
        string id, CancellationToken ct = default)
    {
        var config = await GetAsync(id, ct).ConfigureAwait(false);
        var chain = new List<ConfigChainEntry> { Resolver.ToChainEntry(config) };

        var current = config;
        while (current.Parent is not null)
        {
            var parent = await GetAsync(current.Parent, ct).ConfigureAwait(false);
            chain.Add(Resolver.ToChainEntry(parent));
            current = parent;
        }

        return (config.Key, chain);
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

    /// <summary>Wrap raw items into typed format for request bodies.
    /// SDK format: <c>{key: raw}</c> ->
    /// Wire format: <c>{key: {"value": raw, "type": "STRING"}}</c></summary>
    private static Dictionary<string, object?>? WrapItemsForRequest(
        Dictionary<string, object?>? items)
    {
        if (items is null) return null;

        var result = new Dictionary<string, object?>(items.Count);
        foreach (var (key, value) in items)
        {
            result[key] = new Dictionary<string, object?>
            {
                ["value"] = value,
                ["type"] = InferType(value),
            };
        }
        return result;
    }

    /// <summary>Wrap raw environment overrides into the wire format for request bodies.
    /// SDK format: <c>{env: {key: raw}}</c> ->
    /// Wire format: <c>{env: {"values": {key: {"value": raw}}}}</c></summary>
    private static Dictionary<string, object?>? WrapEnvsForRequest(
        Dictionary<string, object?>? environments)
    {
        if (environments is null) return null;

        var result = new Dictionary<string, object?>(environments.Count);
        foreach (var (envName, envData) in environments)
        {
            if (envData is Dictionary<string, object?> envDict)
            {
                var wrapped = new Dictionary<string, object?>(envDict.Count);
                foreach (var (key, value) in envDict)
                {
                    wrapped[key] = new Dictionary<string, object?> { ["value"] = value };
                }
                result[envName] = new Dictionary<string, object?> { ["values"] = wrapped };
            }
            else
            {
                result[envName] = envData;
            }
        }
        return result;
    }

    /// <summary>Infer the type string for a raw value.</summary>
    private static string InferType(object? value) => value switch
    {
        string => "STRING",
        bool => "BOOLEAN",
        int or long or float or double or decimal => "NUMBER",
        _ => "JSON",
    };

    private static JsonApiRequestBody BuildRequestBody(CreateConfigOptions options) =>
        new()
        {
            Data = new JsonApiRequestResource
            {
                Type = "config",
                Attributes = new JsonApiRequestAttributes
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
    /// Maps a JSON:API resource to a <see cref="Config"/> record.
    /// Extracts raw values from typed item wrappers and environment override wrappers.
    /// </summary>
    private static Config? MapResource(JsonApiResource? resource)
    {
        if (resource?.Attributes is null)
            return null;

        var attrs = resource.Attributes;

        // Extract raw values from typed items: {key: {"value": raw, "type": ..., "description": ...}} -> {key: raw}
        var items = ExtractRawItems(attrs.Items);

        // Extract raw values from environment overrides: {env: {key: {"value": raw}}} -> {env: {key: raw}}
        var environments = ExtractRawEnvironments(attrs.Environments);

        return new Config(
            Id: resource.Id ?? string.Empty,
            Key: attrs.Key ?? string.Empty,
            Name: attrs.Name ?? string.Empty,
            Description: attrs.Description,
            Parent: attrs.Parent,
            Items: items,
            Environments: environments,
            CreatedAt: attrs.CreatedAt,
            UpdatedAt: attrs.UpdatedAt
        );
    }

    /// <summary>
    /// Extracts raw values from typed item wrappers.
    /// Wire format: <c>{key: {"value": raw, "type": "STRING", "description": "..."}}</c>
    /// SDK format: <c>{key: raw}</c>
    /// </summary>
    private static Dictionary<string, object?> ExtractRawItems(
        Dictionary<string, Dictionary<string, object?>>? items)
    {
        if (items is null)
            return new Dictionary<string, object?>();

        var result = new Dictionary<string, object?>(items.Count);
        foreach (var (key, wrapper) in items)
        {
            var normalized = Resolver.NormalizeDict(wrapper);
            result[key] = normalized.TryGetValue("value", out var v) ? v : null;
        }
        return result;
    }

    /// <summary>
    /// Extracts raw values from environment override wrappers.
    /// Wire format: <c>{env: {key: {"value": raw}}}</c>
    /// SDK format: <c>{env: {key: raw}}</c>
    /// </summary>
    private static Dictionary<string, Dictionary<string, object?>> ExtractRawEnvironments(
        Dictionary<string, Dictionary<string, object?>>? environments)
    {
        if (environments is null)
            return new Dictionary<string, Dictionary<string, object?>>();

        var result = new Dictionary<string, Dictionary<string, object?>>(environments.Count);
        foreach (var (envName, envData) in environments)
        {
            var normalized = Resolver.NormalizeDict(envData);
            var envValues = new Dictionary<string, object?>();

            // Wire format: {env: {"values": {key: {"value": raw}}}}
            // Unwrap the "values" key from the EnvironmentOverride and extract raw values.
            if (normalized.TryGetValue("values", out var valuesObj)
                && valuesObj is Dictionary<string, object?> valuesDict)
            {
                foreach (var (key, wrapper) in valuesDict)
                {
                    if (wrapper is Dictionary<string, object?> wrapperDict
                        && wrapperDict.TryGetValue("value", out var v))
                    {
                        envValues[key] = v;
                    }
                    else
                    {
                        envValues[key] = wrapper;
                    }
                }
            }

            result[envName] = envValues;
        }
        return result;
    }
}
