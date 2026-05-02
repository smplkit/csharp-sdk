using System.Text.Json;
using Smplkit.Errors;
using Smplkit.Flags;
using Smplkit.Internal;
using GenFlags = Smplkit.Internal.Generated.Flags;

namespace Smplkit.Management;

/// <summary>
/// Provides flag CRUD operations on the management plane. Owns the wire code
/// (HTTP request bodies, response mapping, generated-client invocation) so it
/// has no dependency on the runtime <see cref="Smplkit.Flags.FlagsClient"/>.
/// Accessible via <see cref="SmplManagementClient.Flags"/> or
/// <c>client.Manage.Flags</c>.
/// </summary>
public sealed class FlagsClient
{
    private readonly GenFlags.FlagsClient _genFlagsClient;

    internal FlagsClient(GeneratedClientFactory clients)
    {
        _genFlagsClient = clients.Flags;
    }

    /// <summary>Creates an unsaved boolean flag.</summary>
    public BooleanFlag NewBooleanFlag(string id, bool defaultValue, string? name = null, string? description = null)
    {
        return new BooleanFlag(
            evalClient: null,
            mgmtClient: this,
            id: id,
            name: name ?? Helpers.KeyToDisplayName(id),
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

    /// <summary>Creates an unsaved string flag.</summary>
    public StringFlag NewStringFlag(string id, string defaultValue, string? name = null, string? description = null, IEnumerable<FlagValue>? values = null)
    {
        return new StringFlag(
            evalClient: null,
            mgmtClient: this,
            id: id,
            name: name ?? Helpers.KeyToDisplayName(id),
            @default: defaultValue,
            values: FlagValuesToInternal(values),
            description: description,
            environments: new Dictionary<string, Dictionary<string, object?>>(),
            createdAt: null,
            updatedAt: null);
    }

    /// <summary>Creates an unsaved number flag.</summary>
    public NumberFlag NewNumberFlag(string id, double defaultValue, string? name = null, string? description = null, IEnumerable<FlagValue>? values = null)
    {
        return new NumberFlag(
            evalClient: null,
            mgmtClient: this,
            id: id,
            name: name ?? Helpers.KeyToDisplayName(id),
            @default: defaultValue,
            values: FlagValuesToInternal(values),
            description: description,
            environments: new Dictionary<string, Dictionary<string, object?>>(),
            createdAt: null,
            updatedAt: null);
    }

    /// <summary>Creates an unsaved JSON flag.</summary>
    public JsonFlag NewJsonFlag(string id, Dictionary<string, object?> defaultValue, string? name = null, string? description = null, IEnumerable<FlagValue>? values = null)
    {
        return new JsonFlag(
            evalClient: null,
            mgmtClient: this,
            id: id,
            name: name ?? Helpers.KeyToDisplayName(id),
            @default: defaultValue,
            values: FlagValuesToInternal(values),
            description: description,
            environments: new Dictionary<string, Dictionary<string, object?>>(),
            createdAt: null,
            updatedAt: null);
    }

    /// <summary>Lists all flags.</summary>
    public async Task<List<Flag>> ListAsync(CancellationToken ct = default)
    {
        var response = await ApiExceptionMapper.ExecuteAsync(
            () => _genFlagsClient.List_flagsAsync(cancellationToken: ct)).ConfigureAwait(false);
        if (response.Data is null) return new List<Flag>();
        return response.Data.Select(r => MapFlagResource(r)!).Where(f => f is not null).ToList();
    }

    /// <summary>Fetches a flag by id.</summary>
    /// <exception cref="NotFoundException">If no matching flag exists.</exception>
    public async Task<Flag> GetAsync(string id, CancellationToken ct = default)
    {
        var response = await ApiExceptionMapper.ExecuteAsync(
            () => _genFlagsClient.Get_flagAsync(id: id, cancellationToken: ct)).ConfigureAwait(false);
        return MapFlagResource(response.Data)
            ?? throw new NotFoundException($"Flag with id '{id}' not found");
    }

    /// <summary>Deletes a flag by id.</summary>
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await ApiExceptionMapper.ExecuteAsync(
            () => _genFlagsClient.Delete_flagAsync(id, ct)).ConfigureAwait(false);
    }

    /// <summary>Internal: save a flag (create or update).</summary>
    internal async Task<Flag> SaveFlagInternalAsync(Flag flag, CancellationToken ct = default)
    {
        if (flag.CreatedAt is null)
        {
            // Create — POST /flags
            var body = BuildCreateFlagBody(flag.Id, flag.Name, flag.Type, flag.Default, flag.Description, flag.Values);
            var response = await ApiExceptionMapper.ExecuteAsync(
                () => _genFlagsClient.Create_flagAsync(body, ct)).ConfigureAwait(false);
            return MapFlagResource(response.Data)
                ?? throw new ValidationException("Failed to create flag");
        }
        else
        {
            // Update — PUT /flags/{id}
            var flagId = flag.Id ?? throw new ValidationException("Cannot update a flag without an id");
            var body = BuildUpdateFlagBody(flagId, flag.Name, flag.Type, flag.Default, flag.Values, flag.Description, flag.Environments);
            var response = await ApiExceptionMapper.ExecuteAsync(
                () => _genFlagsClient.Update_flagAsync(flagId, body, ct)).ConfigureAwait(false);
            return MapFlagResource(response.Data)
                ?? throw new ValidationException("Failed to update flag");
        }
    }

    private static List<Dictionary<string, object?>>? FlagValuesToInternal(IEnumerable<FlagValue>? values)
    {
        if (values is null) return null;
        return values.Select(v => new Dictionary<string, object?>
        {
            ["name"] = v.Name,
            ["value"] = v.Value,
        }).ToList();
    }

    // ------------------------------------------------------------------
    // Wire helpers — moved from runtime FlagsClient so management owns
    // its own response mapping + body building.
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
                values.Add(new Dictionary<string, object?>
                {
                    ["name"] = v.Name,
                    ["value"] = NormalizeValue(v.Value),
                });
        }

        var environments = ExtractEnvironments(attrs.Environments);

        DateTime? createdAt = null;
        if (attrs.Created_at is DateTimeOffset createdDto) createdAt = createdDto.DateTime;

        DateTime? updatedAt = null;
        if (attrs.Updated_at is DateTimeOffset updatedDto) updatedAt = updatedDto.DateTime;

        return new Flag(
            evalClient: null,
            mgmtClient: this,
            id: resource.Id ?? string.Empty,
            name: attrs.Name ?? string.Empty,
            type: attrs.Type ?? "BOOLEAN",
            @default: NormalizeValue(attrs.Default),
            values: values,
            description: attrs.Description,
            environments: environments,
            createdAt: createdAt,
            updatedAt: updatedAt);
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

    private static object? NormalizeValue(object? value)
    {
        if (value is JsonElement je)
            return Smplkit.Config.Resolver.Normalize(je);
        return value;
    }

    private static GenFlags.FlagResponse BuildCreateFlagBody(
        string? id, string name, string type, object? @default,
        string? description, List<Dictionary<string, object?>>? values)
    {
        var flagValues = values?.Select(v => new GenFlags.FlagValue
        {
            Name = v.TryGetValue("name", out var n) ? n?.ToString() ?? "" : "",
            Value = v.TryGetValue("value", out var val) ? val! : new object(),
        }).ToList();

        return new GenFlags.FlagResponse
        {
            Data = new GenFlags.FlagResource
            {
                Type = "flag",
                Id = id,
                Attributes = new GenFlags.Flag
                {
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

    private static GenFlags.FlagResponse BuildUpdateFlagBody(
        string? id, string name, string type, object? @default,
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

        return new GenFlags.FlagResponse
        {
            Data = new GenFlags.FlagResource
            {
                Type = "flag",
                Id = id,
                Attributes = new GenFlags.Flag
                {
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
