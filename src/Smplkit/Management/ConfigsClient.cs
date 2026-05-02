using Smplkit.Errors;

namespace Smplkit.Management;

/// <summary>
/// Provides config CRUD operations on the management plane.
/// Accessible via <see cref="SmplManagementClient.Config"/>.
/// </summary>
public sealed class ConfigsClient
{
    private readonly Smplkit.Config.ConfigClient _runtime;

    internal ConfigsClient(Smplkit.Config.ConfigClient runtime)
    {
        _runtime = runtime;
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
        return _runtime.New(id, name, description, parentId);
    }

    /// <summary>Lists all configs.</summary>
    public Task<List<Smplkit.Config.Config>> ListAsync(CancellationToken ct = default) => _runtime.ListAsync(ct);

    /// <summary>Fetches a config by id.</summary>
    /// <exception cref="NotFoundException">If no matching config exists.</exception>
    public Task<Smplkit.Config.Config> GetAsync(string id, CancellationToken ct = default) => _runtime.GetAsync(id, ct);

    /// <summary>Deletes a config by id.</summary>
    public Task DeleteAsync(string id, CancellationToken ct = default) => _runtime.DeleteAsync(id, ct);
}
