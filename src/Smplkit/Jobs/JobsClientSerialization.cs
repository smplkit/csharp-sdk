using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smplkit.Internal.Generated.Jobs;

/// <summary>
/// Hand-authored extension of the generated jobs client that omits null-valued
/// properties when serializing request bodies and corrects the wire format of the
/// retry-policy attributes object.
/// </summary>
/// <remarks>
/// <para>The base <c>enabled</c> and <c>kind</c> attributes (and the
/// server-managed timestamps / version) are read-only roll-ups the jobs service
/// derives. The canonical SDKs never write the read-only enablement roll-up;
/// leaving it out of the write body — rather than sending <c>"enabled": null</c>
/// — keeps this wrapper faithful to that contract.</para>
/// <para>The generated <see cref="RetryPolicy"/> attributes object cannot be
/// round-tripped by the default serializer: <c>System.Text.Json</c>'s
/// <c>JsonStringEnumConverter</c> emits the .NET enum member name
/// (<c>"Exponential"</c>) rather than the lowercase wire value
/// (<c>"exponential"</c>). A single <see cref="RetryPolicyJsonConverter"/> owns the
/// whole attributes object and emits / parses the exact wire shape (<c>backoff</c> as
/// its lowercase slug, the four discrete <c>retry_*</c> match fields), matching the
/// canonical SDKs.</para>
/// <para>Implemented via the generated client's <c>UpdateJsonSerializerSettings</c>
/// partial-method extension point, so no generated file is edited. Scoped to the
/// jobs generated client only.</para>
/// </remarks>
public partial class JobsClient
{
    static partial void UpdateJsonSerializerSettings(JsonSerializerOptions settings)
    {
        settings.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        settings.Converters.Add(new RetryPolicyJsonConverter());
    }
}

/// <summary>
/// JSON converter for the generated retry-policy attributes object
/// (<see cref="RetryPolicy"/>), reached as the <c>attributes</c> of a
/// <c>RetryPolicyResource</c> / <c>RetryPolicyCreateResource</c>. The property has
/// no converter attribute, so this options-level converter is honored on both
/// read and write.
/// </summary>
/// <remarks>
/// On write it emits only the caller-writable attributes — <c>name</c>,
/// <c>max_retries</c>, <c>backoff</c> (lowercase slug), <c>delay_seconds</c>,
/// <c>max_delay_seconds</c> when set, and the four discrete match fields
/// (<c>retry_on_timeout</c>, <c>retry_on_connection_error</c>, <c>retry_statuses</c>,
/// <c>retry_statuses_except</c>) — leaving the server-managed timestamps / version off
/// the body. On read it parses the full attributes object, tolerating <c>backoff</c> in
/// any case.
/// </remarks>
internal sealed class RetryPolicyJsonConverter : JsonConverter<RetryPolicy>
{
    public override RetryPolicy Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var el = doc.RootElement;

        var retryStatuses = new List<string>();
        if (el.TryGetProperty("retry_statuses", out var rs) && rs.ValueKind == JsonValueKind.Array)
            foreach (var s in rs.EnumerateArray())
                retryStatuses.Add(s.GetString()!);
        var retryStatusesExcept = new List<string>();
        if (el.TryGetProperty("retry_statuses_except", out var rse) && rse.ValueKind == JsonValueKind.Array)
            foreach (var s in rse.EnumerateArray())
                retryStatusesExcept.Add(s.GetString()!);

        var policy = new RetryPolicy
        {
            Name = el.GetProperty("name").GetString()!,
            Max_retries = el.GetProperty("max_retries").GetInt32(),
            Backoff = ParseBackoff(el.GetProperty("backoff").GetString()!),
            Delay_seconds = el.GetProperty("delay_seconds").GetInt32(),
            Retry_on_timeout = el.TryGetProperty("retry_on_timeout", out var rot) && rot.GetBoolean(),
            Retry_on_connection_error = el.TryGetProperty("retry_on_connection_error", out var roce) && roce.GetBoolean(),
            Retry_statuses = retryStatuses,
            Retry_statuses_except = retryStatusesExcept,
        };
        if (el.TryGetProperty("max_delay_seconds", out var mds) && mds.ValueKind != JsonValueKind.Null)
            policy.Max_delay_seconds = mds.GetInt32();
        if (el.TryGetProperty("created_at", out var ca) && ca.ValueKind != JsonValueKind.Null)
            policy.Created_at = ca.GetDateTimeOffset();
        if (el.TryGetProperty("updated_at", out var ua) && ua.ValueKind != JsonValueKind.Null)
            policy.Updated_at = ua.GetDateTimeOffset();
        if (el.TryGetProperty("deleted_at", out var da) && da.ValueKind != JsonValueKind.Null)
            policy.Deleted_at = da.GetDateTimeOffset();
        if (el.TryGetProperty("version", out var ver) && ver.ValueKind != JsonValueKind.Null)
            policy.Version = ver.GetInt32();
        return policy;
    }

    public override void Write(Utf8JsonWriter writer, RetryPolicy value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("name", value.Name);
        writer.WriteNumber("max_retries", value.Max_retries);
        // Lowercase wire slug — the generated JsonStringEnumConverter would emit
        // the .NET member name ("Exponential") instead.
        writer.WriteString("backoff", value.Backoff == RetryPolicyBackoff.Exponential ? "exponential" : "fixed");
        writer.WriteNumber("delay_seconds", value.Delay_seconds);
        // Only valid with exponential backoff; omitted when unset.
        if (value.Max_delay_seconds is int mds)
            writer.WriteNumber("max_delay_seconds", mds);
        writer.WriteBoolean("retry_on_timeout", value.Retry_on_timeout);
        writer.WriteBoolean("retry_on_connection_error", value.Retry_on_connection_error);
        writer.WritePropertyName("retry_statuses");
        writer.WriteStartArray();
        foreach (var status in value.Retry_statuses ?? new List<string>())
            writer.WriteStringValue(status);
        writer.WriteEndArray();
        writer.WritePropertyName("retry_statuses_except");
        writer.WriteStartArray();
        foreach (var status in value.Retry_statuses_except ?? new List<string>())
            writer.WriteStringValue(status);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static RetryPolicyBackoff ParseBackoff(string value) =>
        string.Equals(value, "exponential", StringComparison.OrdinalIgnoreCase)
            ? RetryPolicyBackoff.Exponential
            : RetryPolicyBackoff.Fixed;
}
