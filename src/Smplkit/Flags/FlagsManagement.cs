using Smplkit.Errors;

namespace Smplkit.Flags;

/// <summary>
/// Provides management (CRUD) operations for the smplkit Flags service.
/// Accessible via <see cref="FlagsClient.Management"/>.
/// </summary>
public sealed class FlagsManagement
{
    private readonly FlagsClient _client;

    internal FlagsManagement(FlagsClient client) => _client = client;

    /// <summary>
    /// Create an unsaved boolean flag. Call <see cref="Flag.SaveAsync"/> to persist.
    /// </summary>
    /// <param name="id">The flag identifier (slug).</param>
    /// <param name="defaultValue">Default boolean value.</param>
    /// <param name="name">Display name. Auto-generated from id if null.</param>
    /// <param name="description">Optional description.</param>
    /// <returns>An unsaved <see cref="BooleanFlag"/>.</returns>
    public BooleanFlag NewBooleanFlag(string id, bool defaultValue, string? name = null, string? description = null)
        => _client.NewBooleanFlag(id, defaultValue, name, description);

    /// <summary>
    /// Create an unsaved string flag. Call <see cref="Flag.SaveAsync"/> to persist.
    /// </summary>
    /// <param name="id">The flag identifier (slug).</param>
    /// <param name="defaultValue">Default string value.</param>
    /// <param name="name">Display name. Auto-generated from id if null.</param>
    /// <param name="description">Optional description.</param>
    /// <param name="values">Optional closed set of allowed values.</param>
    /// <returns>An unsaved <see cref="StringFlag"/>.</returns>
    public StringFlag NewStringFlag(string id, string defaultValue, string? name = null, string? description = null, List<Dictionary<string, object?>>? values = null)
        => _client.NewStringFlag(id, defaultValue, name, description, values);

    /// <summary>
    /// Create an unsaved number flag. Call <see cref="Flag.SaveAsync"/> to persist.
    /// </summary>
    /// <param name="id">The flag identifier (slug).</param>
    /// <param name="defaultValue">Default numeric value.</param>
    /// <param name="name">Display name. Auto-generated from id if null.</param>
    /// <param name="description">Optional description.</param>
    /// <param name="values">Optional closed set of allowed values.</param>
    /// <returns>An unsaved <see cref="NumberFlag"/>.</returns>
    public NumberFlag NewNumberFlag(string id, double defaultValue, string? name = null, string? description = null, List<Dictionary<string, object?>>? values = null)
        => _client.NewNumberFlag(id, defaultValue, name, description, values);

    /// <summary>
    /// Create an unsaved JSON flag. Call <see cref="Flag.SaveAsync"/> to persist.
    /// </summary>
    /// <param name="id">The flag identifier (slug).</param>
    /// <param name="defaultValue">Default JSON value.</param>
    /// <param name="name">Display name. Auto-generated from id if null.</param>
    /// <param name="description">Optional description.</param>
    /// <param name="values">Optional closed set of allowed values.</param>
    /// <returns>An unsaved <see cref="JsonFlag"/>.</returns>
    public JsonFlag NewJsonFlag(string id, Dictionary<string, object?> defaultValue, string? name = null, string? description = null, List<Dictionary<string, object?>>? values = null)
        => _client.NewJsonFlag(id, defaultValue, name, description, values);

    /// <summary>
    /// Fetches a flag by its identifier.
    /// </summary>
    /// <param name="id">The flag identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching <see cref="Flag"/>.</returns>
    /// <exception cref="SmplNotFoundException">If no matching flag exists.</exception>
    public Task<Flag> GetAsync(string id, CancellationToken ct = default)
        => _client.GetAsync(id, ct);

    /// <summary>
    /// Lists all flags.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of <see cref="Flag"/> objects.</returns>
    public Task<List<Flag>> ListAsync(CancellationToken ct = default)
        => _client.ListAsync(ct);

    /// <summary>
    /// Deletes a flag by its identifier.
    /// </summary>
    /// <param name="id">The flag identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="SmplNotFoundException">If no matching flag exists.</exception>
    public Task DeleteAsync(string id, CancellationToken ct = default)
        => _client.DeleteAsync(id, ct);
}
