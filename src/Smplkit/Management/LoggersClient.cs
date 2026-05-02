using Smplkit.Errors;
using Smplkit.Logging;

namespace Smplkit.Management;

/// <summary>
/// Provides logger CRUD operations on the management plane.
/// Accessible via <see cref="SmplManagementClient.Loggers"/>.
/// </summary>
public sealed class LoggersClient
{
    private readonly LoggingClient _runtime;

    internal LoggersClient(LoggingClient runtime)
    {
        _runtime = runtime;
    }

    /// <summary>
    /// Creates an unsaved logger. The id doubles as the display name; <c>managed</c>
    /// defaults to <c>true</c> (every logger created via the management API is by
    /// definition managed).
    /// </summary>
    /// <param name="id">The logger identifier (slug). Also used as the display name.</param>
    /// <param name="managed">Whether this logger is managed. Defaults to <c>true</c>.</param>
    public Logger New(string id, bool managed = true) => _runtime.New(id, name: id, managed: managed);

    /// <summary>Lists all loggers.</summary>
    public Task<List<Logger>> ListAsync(CancellationToken ct = default) => _runtime.ListAsync(ct);

    /// <summary>Fetches a logger by id.</summary>
    /// <exception cref="NotFoundException">If no matching logger exists.</exception>
    public Task<Logger> GetAsync(string id, CancellationToken ct = default) => _runtime.GetAsync(id, ct);

    /// <summary>Deletes a logger by id.</summary>
    public Task DeleteAsync(string id, CancellationToken ct = default) => _runtime.DeleteAsync(id, ct);

    /// <summary>
    /// Registers explicit logger sources with per-source service and environment overrides.
    /// </summary>
    public Task RegisterAsync(IEnumerable<LoggerSource> sources, CancellationToken ct = default)
        => _runtime.RegisterSourcesAsync(sources, ct);
}
