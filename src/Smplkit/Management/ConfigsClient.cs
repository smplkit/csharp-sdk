using Smplkit.Errors;
using Smplkit.Internal;
using GenConfig = Smplkit.Internal.Generated.Config;

namespace Smplkit.Management;

/// <summary>
/// Provides config CRUD operations on the management plane. Owns the wire code
/// (HTTP request bodies, response mapping, generated-client invocation) so it
/// has no dependency on the runtime <see cref="Smplkit.Config.ConfigClient"/>.
/// Accessible via <see cref="SmplManagementClient.Config"/>.
/// </summary>
public sealed class ConfigsClient
{
    private const int RegistrationFlushSize = 50;

    private readonly GenConfig.ConfigClient _genClient;
    private readonly ConfigRegistrationBuffer _buffer = new();

    internal ConfigsClient(GeneratedClientFactory clients)
    {
        _genClient = clients.Config;
    }

    /// <summary>
    /// Internal: queue a configuration declaration for bulk-discovery upload.
    /// Called from <see cref="Smplkit.Config.ConfigClient.Bind{T}(string, T, object?)"/>
    /// and <see cref="Smplkit.Config.ConfigClient.GetValueOr{T}(string, string, T)"/>.
    /// </summary>
    internal void RegisterConfig(string configId, string? service, string? environment,
        string? parent = null, string? name = null, string? description = null)
    {
        _buffer.Declare(configId, service, environment, parent, name, description);
        if (_buffer.PendingCount >= RegistrationFlushSize)
        {
            _ = Task.Run(async () => { try { await FlushAsync().ConfigureAwait(false); } catch { } });
        }
    }

    /// <summary>
    /// Internal: queue a config item declaration. Called from
    /// <see cref="Smplkit.Config.ConfigClient.Bind{T}(string, T, object?)"/>
    /// and <see cref="Smplkit.Config.ConfigClient.GetValueOr{T}(string, string, T)"/>.
    /// </summary>
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
    /// Sends any pending config declarations to <c>POST /api/v1/configs/bulk</c>.
    /// Per ADR-024 §2.9 the bulk endpoint is plan-limit-exempt; failures here
    /// never propagate to customer code. Drained entries are not requeued.
    /// </summary>
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
        try
        {
            await _genClient.Bulk_register_configsAsync(body, ct).ConfigureAwait(false);
        }
        catch
        {
            // Fire-and-forget per ADR-024 §2.9.
        }
    }

    /// <summary>Creates an unsaved config.</summary>
    /// <param name="id">The config identifier (slug).</param>
    /// <param name="name">Display name. Auto-generated from id if null.</param>
    /// <param name="description">Optional description.</param>
    /// <param name="parent">Optional parent config identifier (string id or <see cref="Smplkit.Config.Config"/>).</param>
    public Smplkit.Config.Config New(string id, string? name = null, string? description = null, object? parent = null)
    {
        string? parentId = parent switch
        {
            null => null,
            string s => s,
            Smplkit.Config.Config c => c.Id,
            _ => throw new ArgumentException(
                $"parent must be a string id or a Config instance; got {parent.GetType().Name}",
                nameof(parent)),
        };
        return new Smplkit.Config.Config(
            client: this,
            id: id,
            name: name ?? Helpers.KeyToDisplayName(id),
            description: description,
            parent: parentId,
            items: new Dictionary<string, object?>(),
            environments: new Dictionary<string, Dictionary<string, object?>>(),
            createdAt: null,
            updatedAt: null);
    }

    /// <summary>Lists configs. Returns one page; defaults to the server's first page.</summary>
    /// <param name="pageNumber">1-based page number; null lets the server default (1) apply.</param>
    /// <param name="pageSize">Items per page; null lets the server default (1000) apply.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<List<Smplkit.Config.Config>> ListAsync(
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
            return new List<Smplkit.Config.Config>();

        var results = new List<Smplkit.Config.Config>(response.Data.Count);
        foreach (var resource in response.Data)
        {
            var config = MapResource(resource);
            if (config is not null)
                results.Add(config);
        }
        return results;
    }

    /// <summary>Fetches a config by id.</summary>
    /// <exception cref="NotFoundException">If no matching config exists.</exception>
    public async Task<Smplkit.Config.Config> GetAsync(string id, CancellationToken ct = default)
    {
        var response = await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.Get_configAsync(id: id, cancellationToken: ct)).ConfigureAwait(false);

        return MapResource(response.Data)
            ?? throw new NotFoundException($"Config with id '{id}' not found");
    }

    /// <summary>Deletes a config by id.</summary>
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.Delete_configAsync(id, ct)).ConfigureAwait(false);
    }

    /// <summary>Internal: save a config (create or update).</summary>
    internal async Task<Smplkit.Config.Config> SaveConfigInternalAsync(Smplkit.Config.Config config, CancellationToken ct = default)
    {
        var body = BuildRequestBody(config);
        if (config.CreatedAt is null)
        {
            var response = await ApiExceptionMapper.ExecuteAsync(
                () => _genClient.Create_configAsync(body, ct)).ConfigureAwait(false);
            return MapResource(response.Data)
                ?? throw new ValidationException("Failed to create config");
        }
        else
        {
            var configId = config.Id ?? throw new ValidationException("Cannot update a config without an id");
            var response = await ApiExceptionMapper.ExecuteAsync(
                () => _genClient.Update_configAsync(configId, body, ct)).ConfigureAwait(false);
            return MapResource(response.Data)
                ?? throw new ValidationException("Failed to update config");
        }
    }

    // ------------------------------------------------------------------
    // Wire helpers — moved from runtime ConfigClient.
    // ------------------------------------------------------------------

    private static GenConfig.ConfigRequest BuildRequestBody(Smplkit.Config.Config config) =>
        new()
        {
            Data = new GenConfig.ConfigResource
            {
                Type = "config",
                Id = config.Id,
                Attributes = new GenConfig.Config
                {
                    Name = config.Name,
                    Description = config.Description,
                    Parent = config.Parent,
                    Items = WrapItemsForRequest(config.Items),
                    Environments = WrapEnvsForRequest(config.Environments),
                },
            },
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

    private static IDictionary<string, GenConfig.EnvironmentOverride>? WrapEnvsForRequest(
        Dictionary<string, Dictionary<string, object?>>? environments)
    {
        if (environments is null || environments.Count == 0) return null;

        var result = new Dictionary<string, GenConfig.EnvironmentOverride>(environments.Count);
        foreach (var (envName, envData) in environments)
        {
            var values = new Dictionary<string, GenConfig.ConfigItemOverride>(envData.Count);
            foreach (var (key, value) in envData)
            {
                values[key] = new GenConfig.ConfigItemOverride { Value = value! };
            }
            result[envName] = new GenConfig.EnvironmentOverride { Values = values };
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

    private Smplkit.Config.Config? MapResource(GenConfig.ConfigResource? resource)
    {
        if (resource?.Attributes is null)
            return null;

        var attrs = resource.Attributes;
        var items = ExtractRawItems(attrs.Items);
        var environments = ExtractRawEnvironments(attrs.Environments);

        return new Smplkit.Config.Config(
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
            result[key] = Smplkit.Config.Resolver.Normalize(definition.Value);
        }
        return result;
    }

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
                    envValues[key] = Smplkit.Config.Resolver.Normalize(itemOverride.Value);
                }
            }
            result[envName] = envValues;
        }
        return result;
    }
}
