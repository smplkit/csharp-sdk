using System.Collections.Generic;
using GenApp = Smplkit.Internal.Generated.App;
using GenAudit = Smplkit.Internal.Generated.Audit;
using GenConfig = Smplkit.Internal.Generated.Config;
using GenFlags = Smplkit.Internal.Generated.Flags;
using GenJobs = Smplkit.Internal.Generated.Jobs;
using GenLogging = Smplkit.Internal.Generated.Logging;

namespace Smplkit.Internal;

/// <summary>
/// Constructs and holds NSwag-generated client instances, each configured
/// with the correct base URL and sharing the same <see cref="HttpClient"/>.
/// </summary>
internal sealed class GeneratedClientFactory
{
    private const string JsonApiMediaType = "application/vnd.api+json";

    /// <summary>Gets the User-Agent value riding every request from this transport —
    /// the caller-supplied value when one was provided (on the <see cref="HttpClient"/>
    /// or via <see cref="SmplClientOptions.ExtraHeaders"/>), else the SDK default.
    /// The WebSocket handshake reuses it so both channels present the same agent.</summary>
    internal string EffectiveUserAgent { get; }

    /// <summary>Gets the generated Config API client.</summary>
    internal GenConfig.ConfigClient Config { get; }

    /// <summary>Gets the generated Flags API client.</summary>
    internal GenFlags.FlagsClient Flags { get; }

    /// <summary>Gets the generated App/Platform API client.</summary>
    internal GenApp.AppClient App { get; }

    /// <summary>Gets the generated Logging API client.</summary>
    internal GenLogging.LoggingClient Logging { get; }

    /// <summary>Gets the generated Audit API client shared by every audit
    /// sub-client. Environment scoping rides the event request body and the
    /// <c>filter[environment]</c> query param (ADR-055), not this transport.</summary>
    internal GenAudit.AuditClient AuditRuntime { get; }

    /// <summary>Gets the generated Jobs API client.</summary>
    internal GenJobs.JobsClient Jobs { get; }

    /// <summary>
    /// Configures the shared <see cref="HttpClient"/> and creates generated client instances.
    /// </summary>
    /// <param name="httpClient">The underlying HTTP client (may be caller-owned).</param>
    /// <param name="options">Client options with resolved API key, timeout, etc.</param>
    internal GeneratedClientFactory(HttpClient httpClient, SmplClientOptions options)
    {
        httpClient.Timeout = options.Timeout;

        // User-Agent precedence: a caller-supplied value — already present on a
        // caller-owned HttpClient, or provided via ExtraHeaders under any casing —
        // always wins; otherwise the SDK stamps its own product token. The
        // platform edge rejects requests with no User-Agent at all, so some
        // value must always ride along. (HttpHeaders name lookups are
        // case-insensitive, so Contains covers any casing on the HttpClient.)
        var callerSuppliedUserAgent = options.ExtraHeaders is not null
            && options.ExtraHeaders.Keys.Any(
                k => string.Equals(k, "User-Agent", StringComparison.OrdinalIgnoreCase));
        if (!callerSuppliedUserAgent && !httpClient.DefaultRequestHeaders.Contains("User-Agent"))
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", SdkVersion.UserAgent);

        if (!httpClient.DefaultRequestHeaders.Contains("Accept"))
            httpClient.DefaultRequestHeaders.Add("Accept", JsonApiMediaType);

        Auth.ApplyBearerToken(httpClient, options.ApiKey!);

        if (options.ExtraHeaders is { } extra)
        {
            var sdkOwned = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "Authorization", "Accept", "Content-Type" };
            foreach (var (k, v) in extra)
                if (!sdkOwned.Contains(k))
                    httpClient.DefaultRequestHeaders.TryAddWithoutValidation(k, v);
        }

        EffectiveUserAgent = httpClient.DefaultRequestHeaders.TryGetValues("User-Agent", out var uaValues)
            ? string.Join(" ", uaValues)
            : SdkVersion.UserAgent;

        var scheme = options.Scheme ?? "https";
        var domain = options.BaseDomain ?? "smplkit.com";
        Config = new GenConfig.ConfigClient($"{scheme}://config.{domain}", httpClient) { ReadResponseAsString = true };
        Flags = new GenFlags.FlagsClient($"{scheme}://flags.{domain}", httpClient) { ReadResponseAsString = true };
        App = new GenApp.AppClient($"{scheme}://app.{domain}", httpClient) { ReadResponseAsString = true };
        Logging = new GenLogging.LoggingClient($"{scheme}://logging.{domain}", httpClient) { ReadResponseAsString = true };
        // Audit env scoping rides the event body and filter[environment]
        // (ADR-055), not the transport, so the generated audit client carries
        // only the shared auth/headers configured above.
        var auditBaseUrl = $"{scheme}://audit.{domain}";
        AuditRuntime = new GenAudit.AuditClient(auditBaseUrl, httpClient) { ReadResponseAsString = true };
        Jobs = new GenJobs.JobsClient($"{scheme}://jobs.{domain}", httpClient) { ReadResponseAsString = true };
    }
}
