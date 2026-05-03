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

    /// <summary>
    /// Serializer options for typed config-model deserialization. Config
    /// item keys come back snake_cased on the wire (e.g. <c>max_retries</c>),
    /// so the property naming policy must match — otherwise the deserializer
    /// can't map JSON keys to PascalCase model properties and silently
    /// leaves them at their default value (typically 0 / null / false).
    /// </summary>
    internal static readonly JsonSerializerOptions SnakeCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
