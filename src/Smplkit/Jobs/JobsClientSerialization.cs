using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smplkit.Internal.Generated.Jobs;

/// <summary>
/// Hand-authored extension of the generated jobs client that omits null-valued
/// properties when serializing request bodies.
/// </summary>
/// <remarks>
/// <para>The base <c>enabled</c> and <c>recurring</c> attributes (and the
/// server-managed timestamps / version) are read-only roll-ups the jobs service
/// derives. The canonical SDKs never write the read-only enablement roll-up;
/// leaving it out of the write body — rather than sending <c>"enabled": null</c>
/// — keeps this wrapper faithful to that contract.</para>
/// <para>Implemented via the generated client's <c>UpdateJsonSerializerSettings</c>
/// partial-method extension point, so no generated file is edited. Scoped to the
/// jobs generated client only.</para>
/// </remarks>
public partial class JobsClient
{
    static partial void UpdateJsonSerializerSettings(JsonSerializerOptions settings)
    {
        settings.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    }
}
