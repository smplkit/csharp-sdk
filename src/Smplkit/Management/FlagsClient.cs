using Smplkit.Errors;
using Smplkit.Flags;

namespace Smplkit.Management;

/// <summary>
/// Provides flag CRUD operations on the management plane.
/// Accessible via <see cref="SmplManagementClient.Flags"/> or
/// <see cref="SmplClient.Manage"/>.<see cref="SmplManagementClient.Flags"/>.
/// </summary>
public sealed class FlagsClient
{
    private readonly Smplkit.Flags.FlagsClient _runtime;

    internal FlagsClient(Smplkit.Flags.FlagsClient runtime)
    {
        _runtime = runtime;
    }

    /// <summary>Creates an unsaved boolean flag.</summary>
    public BooleanFlag NewBooleanFlag(string id, bool defaultValue, string? name = null, string? description = null)
        => _runtime.NewBooleanFlag(id, defaultValue, name, description);

    /// <summary>Creates an unsaved string flag.</summary>
    public StringFlag NewStringFlag(string id, string defaultValue, string? name = null, string? description = null, IEnumerable<FlagValue>? values = null)
        => _runtime.NewStringFlag(id, defaultValue, name, description, FlagValuesToInternal(values));

    /// <summary>Creates an unsaved number flag.</summary>
    public NumberFlag NewNumberFlag(string id, double defaultValue, string? name = null, string? description = null, IEnumerable<FlagValue>? values = null)
        => _runtime.NewNumberFlag(id, defaultValue, name, description, FlagValuesToInternal(values));

    /// <summary>Creates an unsaved JSON flag.</summary>
    public JsonFlag NewJsonFlag(string id, Dictionary<string, object?> defaultValue, string? name = null, string? description = null, IEnumerable<FlagValue>? values = null)
        => _runtime.NewJsonFlag(id, defaultValue, name, description, FlagValuesToInternal(values));

    /// <summary>Lists all flags.</summary>
    public Task<List<Flag>> ListAsync(CancellationToken ct = default) => _runtime.ListAsync(ct);

    /// <summary>Fetches a flag by id.</summary>
    /// <exception cref="NotFoundException">If no matching flag exists.</exception>
    public Task<Flag> GetAsync(string id, CancellationToken ct = default) => _runtime.GetAsync(id, ct);

    /// <summary>Deletes a flag by id.</summary>
    public Task DeleteAsync(string id, CancellationToken ct = default) => _runtime.DeleteAsync(id, ct);

    private static List<Dictionary<string, object?>>? FlagValuesToInternal(IEnumerable<FlagValue>? values)
    {
        if (values is null) return null;
        return values.Select(v => new Dictionary<string, object?>
        {
            ["name"] = v.Name,
            ["value"] = v.Value,
        }).ToList();
    }
}
