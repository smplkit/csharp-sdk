using System.Text.Json;
using Smplkit.Errors;
using Smplkit.Internal;

namespace Smplkit.Config;

/// <summary>
/// Client for the smplkit Config service. Provides CRUD operations on
/// configuration resources.
/// </summary>
public sealed class ConfigClient
{
    private readonly Transport _transport;

    /// <summary>
    /// Initializes a new instance of <see cref="ConfigClient"/>.
    /// </summary>
    /// <param name="transport">The HTTP transport layer.</param>
    internal ConfigClient(Transport transport)
    {
        _transport = transport;
    }

    /// <summary>
    /// Fetches a single config by its UUID.
    /// </summary>
    /// <param name="id">The config UUID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching <see cref="Config"/>.</returns>
    /// <exception cref="SmplNotFoundException">If no matching config exists.</exception>
    public async Task<Config> GetAsync(string id, CancellationToken ct = default)
    {
        var json = await _transport.GetAsync($"/api/v1/configs/{id}", ct).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<JsonApiSingleResponse>(json, Transport.SerializerOptions);
        return MapResource(response?.Data)
            ?? throw new SmplNotFoundException($"Config {id} not found");
    }

    /// <summary>
    /// Fetches a single config by its human-readable key.
    /// Uses the list endpoint with a <c>filter[key]</c> query parameter.
    /// </summary>
    /// <param name="key">The config key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching <see cref="Config"/>.</returns>
    /// <exception cref="SmplNotFoundException">If no matching config exists.</exception>
    public async Task<Config> GetByKeyAsync(string key, CancellationToken ct = default)
    {
        var encodedKey = Uri.EscapeDataString(key);
        var json = await _transport.GetAsync($"/api/v1/configs?filter[key]={encodedKey}", ct).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<JsonApiListResponse>(json, Transport.SerializerOptions);

        if (response?.Data is null || response.Data.Count == 0)
            throw new SmplNotFoundException($"Config with key '{key}' not found");

        return MapResource(response.Data[0])
            ?? throw new SmplNotFoundException($"Config with key '{key}' not found");
    }

    /// <summary>
    /// Lists all configs for the account.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of <see cref="Config"/> objects.</returns>
    public async Task<List<Config>> ListAsync(CancellationToken ct = default)
    {
        var json = await _transport.GetAsync("/api/v1/configs", ct).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<JsonApiListResponse>(json, Transport.SerializerOptions);

        if (response?.Data is null)
            return new List<Config>();

        var results = new List<Config>(response.Data.Count);
        foreach (var resource in response.Data)
        {
            var config = MapResource(resource);
            if (config is not null)
                results.Add(config);
        }
        return results;
    }

    /// <summary>
    /// Creates a new config.
    /// </summary>
    /// <param name="options">The creation options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created <see cref="Config"/>.</returns>
    /// <exception cref="SmplValidationException">If the server rejects the request.</exception>
    public async Task<Config> CreateAsync(CreateConfigOptions options, CancellationToken ct = default)
    {
        var body = new JsonApiRequestBody
        {
            Data = new JsonApiRequestResource
            {
                Type = "config",
                Attributes = new JsonApiRequestAttributes
                {
                    Name = options.Name,
                    Key = options.Key,
                    Description = options.Description,
                    Parent = options.Parent,
                    Values = options.Values,
                },
            },
        };

        var json = await _transport.PostAsync("/api/v1/configs", body, ct).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<JsonApiSingleResponse>(json, Transport.SerializerOptions);
        return MapResource(response?.Data)
            ?? throw new SmplValidationException("Failed to create config");
    }

    /// <summary>
    /// Deletes a config by its UUID.
    /// </summary>
    /// <param name="id">The UUID of the config to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="SmplNotFoundException">If the config does not exist.</exception>
    /// <exception cref="SmplConflictException">If the config has children.</exception>
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await _transport.DeleteAsync($"/api/v1/configs/{id}", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Maps a JSON:API resource to a <see cref="Config"/> record.
    /// </summary>
    private static Config? MapResource(JsonApiResource? resource)
    {
        if (resource?.Attributes is null)
            return null;

        var attrs = resource.Attributes;
        return new Config(
            Id: resource.Id ?? string.Empty,
            Key: attrs.Key ?? string.Empty,
            Name: attrs.Name ?? string.Empty,
            Description: attrs.Description,
            Parent: attrs.Parent,
            Values: attrs.Values ?? new Dictionary<string, object?>(),
            Environments: attrs.Environments ?? new Dictionary<string, Dictionary<string, object?>>(),
            CreatedAt: attrs.CreatedAt,
            UpdatedAt: attrs.UpdatedAt
        );
    }
}
