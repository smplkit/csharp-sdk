using Smplkit.Errors;
using Smplkit.Logging;

namespace Smplkit.Management;

/// <summary>
/// Provides log-group CRUD operations on the management plane.
/// Accessible via <see cref="SmplManagementClient.LogGroups"/>.
/// </summary>
public sealed class LogGroupsClient
{
    private readonly LoggingClient _runtime;

    internal LogGroupsClient(LoggingClient runtime)
    {
        _runtime = runtime;
    }

    /// <summary>Creates an unsaved log group.</summary>
    /// <param name="id">The group identifier (slug).</param>
    /// <param name="name">Display name. Auto-generated from id if null.</param>
    /// <param name="group">Optional parent group identifier.</param>
    public LogGroup New(string id, string? name = null, string? group = null)
        => _runtime.NewGroup(id, name, group);

    /// <summary>Lists all log groups.</summary>
    public Task<List<LogGroup>> ListAsync(CancellationToken ct = default) => _runtime.ListGroupsAsync(ct);

    /// <summary>Fetches a log group by id.</summary>
    /// <exception cref="NotFoundException">If no matching group exists.</exception>
    public Task<LogGroup> GetAsync(string id, CancellationToken ct = default) => _runtime.GetGroupAsync(id, ct);

    /// <summary>Deletes a log group by id.</summary>
    public Task DeleteAsync(string id, CancellationToken ct = default) => _runtime.DeleteGroupAsync(id, ct);
}
