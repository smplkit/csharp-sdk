using Smplkit.Errors;

namespace Smplkit.Logging;

/// <summary>
/// Provides management (CRUD) operations for the smplkit Logging service.
/// Accessible via <see cref="LoggingClient.Management"/>.
/// </summary>
public sealed class LoggingManagement
{
    private readonly LoggingClient _client;

    internal LoggingManagement(LoggingClient client) => _client = client;

    // ------------------------------------------------------------------
    // Logger CRUD
    // ------------------------------------------------------------------

    /// <summary>
    /// Create an unsaved logger. Call <see cref="Logger.SaveAsync"/> to persist.
    /// </summary>
    /// <param name="id">The logger identifier (slug).</param>
    /// <param name="name">Display name. Auto-generated from id if null.</param>
    /// <param name="managed">Whether this logger is managed.</param>
    /// <returns>An unsaved <see cref="Logger"/>.</returns>
    public Logger New(string id, string? name = null, bool managed = false)
        => _client.New(id, name, managed);

    /// <summary>
    /// Fetches a logger by its identifier.
    /// </summary>
    /// <param name="id">The logger identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching <see cref="Logger"/>.</returns>
    /// <exception cref="SmplNotFoundException">If no matching logger exists.</exception>
    public Task<Logger> GetAsync(string id, CancellationToken ct = default)
        => _client.GetAsync(id, ct);

    /// <summary>
    /// Lists all loggers.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of <see cref="Logger"/> objects.</returns>
    public Task<List<Logger>> ListAsync(CancellationToken ct = default)
        => _client.ListAsync(ct);

    /// <summary>
    /// Deletes a logger by its identifier.
    /// </summary>
    /// <param name="id">The logger identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="SmplNotFoundException">If no matching logger exists.</exception>
    public Task DeleteAsync(string id, CancellationToken ct = default)
        => _client.DeleteAsync(id, ct);

    // ------------------------------------------------------------------
    // LogGroup CRUD
    // ------------------------------------------------------------------

    /// <summary>
    /// Create an unsaved log group. Call <see cref="LogGroup.SaveAsync"/> to persist.
    /// </summary>
    /// <param name="id">The group identifier (slug).</param>
    /// <param name="name">Display name. Auto-generated from id if null.</param>
    /// <param name="group">Optional parent group identifier.</param>
    /// <returns>An unsaved <see cref="LogGroup"/>.</returns>
    public LogGroup NewGroup(string id, string? name = null, string? group = null)
        => _client.NewGroup(id, name, group);

    /// <summary>
    /// Fetches a log group by its identifier.
    /// </summary>
    /// <param name="id">The group identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching <see cref="LogGroup"/>.</returns>
    /// <exception cref="SmplNotFoundException">If no matching group exists.</exception>
    public Task<LogGroup> GetGroupAsync(string id, CancellationToken ct = default)
        => _client.GetGroupAsync(id, ct);

    /// <summary>
    /// Lists all log groups.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of <see cref="LogGroup"/> objects.</returns>
    public Task<List<LogGroup>> ListGroupsAsync(CancellationToken ct = default)
        => _client.ListGroupsAsync(ct);

    /// <summary>
    /// Deletes a log group by its identifier.
    /// </summary>
    /// <param name="id">The group identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="SmplNotFoundException">If no matching group exists.</exception>
    public Task DeleteGroupAsync(string id, CancellationToken ct = default)
        => _client.DeleteGroupAsync(id, ct);

    // ------------------------------------------------------------------
    // Logger source registration
    // ------------------------------------------------------------------

    /// <summary>
    /// Registers explicit logger sources with per-source service and environment overrides.
    /// Useful for sample-data seeding, cross-service migration, and test fixtures.
    /// </summary>
    /// <remarks>
    /// Contrast with <see cref="LoggingClient.StartAsync"/>, which registers loggers
    /// discovered in the current process under the SmplClient's own service and environment.
    /// </remarks>
    /// <param name="sources">Logger sources to register.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task RegisterSourcesAsync(IEnumerable<LoggerSource> sources, CancellationToken ct = default)
        => _client.RegisterSourcesAsync(sources, ct);
}
