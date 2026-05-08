using System.Text.Json.Serialization;

namespace Smplkit.Internal.Generated.Audit;

/// <summary>
/// Partial-method implementation hook on the generated AuditClient.
///
/// <para>System.Text.Json's default behavior is to serialize null
/// values, which means the generated request DTOs (Event, Forwarder,
/// etc.) emit their nullable read-only properties (created_at,
/// actor_id, slug, version, ...) as <c>null</c> on POST/PUT bodies.
/// Those fields are documented as readOnly in the audit OpenAPI spec
/// and have no business in a write request — the server ignores them
/// today, but they're stale bytes that bloat traces and confuse log
/// readers.</para>
///
/// <para>Setting <see cref="JsonIgnoreCondition.WhenWritingNull"/> only
/// affects writes; deserialization of responses still populates null
/// fields normally.</para>
/// </summary>
public partial class AuditClient
{
    static partial void UpdateJsonSerializerSettings(System.Text.Json.JsonSerializerOptions settings)
    {
        settings.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    }
}
