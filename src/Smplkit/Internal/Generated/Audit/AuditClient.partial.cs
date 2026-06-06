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
    /// <summary>
    /// HTTP header that scopes a runtime audit request to an environment
    /// (ADR-055). Stamped from <see cref="RuntimeEnvironment"/> when set.
    /// </summary>
    private const string EnvironmentHeader = "X-Smplkit-Environment";

    /// <summary>
    /// The runtime environment to scope audit requests to. When non-null,
    /// every request issued through this client carries the
    /// <c>X-Smplkit-Environment</c> header so record / list / get / search /
    /// discovery resolve against that environment (ADR-055).
    ///
    /// <para>Left <c>null</c> on the management-plane client instance — SIEM
    /// forwarder CRUD is account-scoped and must not carry the header. The
    /// runtime <see cref="Smplkit.Audit.AuditClient"/> wires it from the SDK's
    /// configured environment via a dedicated generated-client instance.</para>
    ///
    /// <para>A caller-supplied <c>X-Smplkit-Environment</c> header (e.g. via
    /// <see cref="Smplkit.SmplClientOptions.ExtraHeaders"/>, which lands on the
    /// shared <see cref="System.Net.Http.HttpClient"/>'s default headers) wins:
    /// the stamp below only fires when the request does not already carry one.</para>
    /// </summary>
    internal string? RuntimeEnvironment { get; set; }

    static partial void UpdateJsonSerializerSettings(System.Text.Json.JsonSerializerOptions settings)
    {
        settings.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        settings.Converters.Add(new JsonStringEnumConverter());
    }

    partial void PrepareRequest(System.Net.Http.HttpClient client, System.Net.Http.HttpRequestMessage request, string url)
        => StampEnvironment(client, request);

    partial void PrepareRequest(System.Net.Http.HttpClient client, System.Net.Http.HttpRequestMessage request, System.Text.StringBuilder urlBuilder)
        => StampEnvironment(client, request);

    private void StampEnvironment(System.Net.Http.HttpClient client, System.Net.Http.HttpRequestMessage request)
    {
        if (RuntimeEnvironment is null)
            return;
        // Explicit override wins: skip if the caller already set the header on
        // the request or as a default on the shared HttpClient.
        if (request.Headers.Contains(EnvironmentHeader) || client.DefaultRequestHeaders.Contains(EnvironmentHeader))
            return;
        request.Headers.TryAddWithoutValidation(EnvironmentHeader, RuntimeEnvironment);
    }
}
