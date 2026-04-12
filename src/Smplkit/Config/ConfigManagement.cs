using Smplkit.Errors;

namespace Smplkit.Config;

/// <summary>
/// Provides management (CRUD) operations for the smplkit Config service.
/// Accessible via <see cref="ConfigClient.Management"/>.
/// </summary>
public sealed class ConfigManagement
{
    private readonly ConfigClient _client;

    internal ConfigManagement(ConfigClient client) => _client = client;

    /// <summary>
    /// Create an unsaved config. Call <see cref="Config.SaveAsync"/> to persist.
    /// </summary>
    /// <param name="id">The config identifier (slug).</param>
    /// <param name="name">Display name. Auto-generated from id if null.</param>
    /// <param name="description">Optional description.</param>
    /// <param name="parent">Optional parent config identifier.</param>
    /// <returns>An unsaved <see cref="Config"/>.</returns>
    public Config New(string id, string? name = null, string? description = null, string? parent = null)
        => _client.New(id, name, description, parent);

    /// <summary>
    /// Fetches a single config by its identifier.
    /// </summary>
    /// <param name="id">The config identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching <see cref="Config"/>.</returns>
    /// <exception cref="SmplNotFoundException">If no matching config exists.</exception>
    public Task<Config> GetAsync(string id, CancellationToken ct = default)
        => _client.GetAsync(id, ct);

    /// <summary>
    /// Lists all configs for the account.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of <see cref="Config"/> objects.</returns>
    public Task<List<Config>> ListAsync(CancellationToken ct = default)
        => _client.ListAsync(ct);

    /// <summary>
    /// Deletes a config by its identifier.
    /// </summary>
    /// <param name="id">The config identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="SmplNotFoundException">If no matching config exists.</exception>
    public Task DeleteAsync(string id, CancellationToken ct = default)
        => _client.DeleteAsync(id, ct);
}
