using System.Text.Json;

namespace Smplkit.Internal;

/// <summary>
/// Shared JSON serializer options used across the SDK.
/// </summary>
internal static class JsonOptions
{
    /// <summary>
    /// Default serializer options with camelCase naming.
    /// </summary>
    internal static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
