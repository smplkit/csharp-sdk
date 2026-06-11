using System.Text.Json;

namespace Smplkit.Account;

/// <summary>
/// Active-record account-settings model.
/// </summary>
/// <remarks>
/// The wire format is opaque JSON. Documented keys are exposed as typed
/// properties; unknown keys live in <see cref="Raw"/>. <see cref="SaveAsync"/>
/// writes the full settings object back.
/// </remarks>
public sealed class AccountSettings
{
    private readonly SettingsClient? _client;
    private Dictionary<string, object?> _data;

    internal AccountSettings(SettingsClient? client = null, Dictionary<string, object?>? data = null)
    {
        _client = client;
        _data = data is not null ? new Dictionary<string, object?>(data) : new Dictionary<string, object?>();
    }

    /// <summary>The full settings dict. Mutations are persisted on <see cref="SaveAsync"/>.</summary>
    public Dictionary<string, object?> Raw
    {
        get => _data;
        set => _data = new Dictionary<string, object?>(value);
    }

    /// <summary>Canonical ordering of STANDARD environments. Empty list if unset.</summary>
    public List<string> EnvironmentOrder
    {
        get
        {
            if (!_data.TryGetValue("environment_order", out var raw) || raw is null)
                return new List<string>();
            return raw switch
            {
                List<string> typed => new List<string>(typed),
                IEnumerable<object?> items => items.Select(x => x?.ToString() ?? string.Empty).ToList(),
                JsonElement je when je.ValueKind == JsonValueKind.Array =>
                    je.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList(),
                _ => new List<string>(),
            };
        }
        set => _data["environment_order"] = value.Cast<object?>().ToList();
    }

    /// <summary>Persists the full settings object back to the server.</summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        if (_client is null)
            throw new InvalidOperationException(
                "AccountSettings was constructed without a client; cannot save");
        var other = await _client.SaveInternalAsync(_data, ct).ConfigureAwait(false);
        Apply(other);
    }

    private void Apply(AccountSettings other) => _data = new Dictionary<string, object?>(other._data);

    /// <inheritdoc />
    public override string ToString() => $"AccountSettings({JsonSerializer.Serialize(_data)})";
}
