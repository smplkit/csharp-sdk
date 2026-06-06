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
    private const string UserAgent = "smplkit-dotnet-sdk/0.0.0";

    /// <summary>Gets the generated Config API client.</summary>
    internal GenConfig.ConfigClient Config { get; }

    /// <summary>Gets the generated Flags API client.</summary>
    internal GenFlags.FlagsClient Flags { get; }

    /// <summary>Gets the generated App/Platform API client.</summary>
    internal GenApp.AppClient App { get; }

    /// <summary>Gets the generated Logging API client.</summary>
    internal GenLogging.LoggingClient Logging { get; }

    /// <summary>Gets the generated Audit API client used by the management plane
    /// (SIEM forwarder CRUD). Account-scoped — does not carry the runtime
    /// <c>X-Smplkit-Environment</c> header.</summary>
    internal GenAudit.AuditClient Audit { get; }

    /// <summary>Gets the generated Audit API client used by the runtime plane
    /// (event record / list / get / search and discovery). Carries the
    /// <c>X-Smplkit-Environment</c> header resolved from the SDK's configured
    /// environment (ADR-055) when one is available; otherwise behaves like
    /// <see cref="Audit"/>.</summary>
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

        if (!httpClient.DefaultRequestHeaders.Contains("User-Agent"))
            httpClient.DefaultRequestHeaders.Add("User-Agent", UserAgent);

        if (!httpClient.DefaultRequestHeaders.Contains("Accept"))
            httpClient.DefaultRequestHeaders.Add("Accept", JsonApiMediaType);

        Auth.ApplyBearerToken(httpClient, options.ApiKey!);

        if (options.ExtraHeaders is { } extra)
        {
            var sdkOwned = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "Authorization", "Accept", "Content-Type", "User-Agent" };
            foreach (var (k, v) in extra)
                if (!sdkOwned.Contains(k))
                    httpClient.DefaultRequestHeaders.TryAddWithoutValidation(k, v);
        }

        var scheme = options.Scheme ?? "https";
        var domain = options.BaseDomain ?? "smplkit.com";
        Config = new GenConfig.ConfigClient($"{scheme}://config.{domain}", httpClient) { ReadResponseAsString = true };
        Flags = new GenFlags.FlagsClient($"{scheme}://flags.{domain}", httpClient) { ReadResponseAsString = true };
        App = new GenApp.AppClient($"{scheme}://app.{domain}", httpClient) { ReadResponseAsString = true };
        Logging = new GenLogging.LoggingClient($"{scheme}://logging.{domain}", httpClient) { ReadResponseAsString = true };
        var auditBaseUrl = $"{scheme}://audit.{domain}";
        Audit = new GenAudit.AuditClient(auditBaseUrl, httpClient) { ReadResponseAsString = true };
        // Runtime audit ops are environment-scoped (ADR-055): a dedicated
        // generated-client instance stamps X-Smplkit-Environment from the
        // configured environment on every request. It shares the HttpClient
        // with the management-plane Audit client; the header is added
        // per-request, so management's account-scoped CRUD stays env-free.
        AuditRuntime = new GenAudit.AuditClient(auditBaseUrl, httpClient)
        {
            ReadResponseAsString = true,
            RuntimeEnvironment = options.Environment,
        };
        Jobs = new GenJobs.JobsClient($"{scheme}://jobs.{domain}", httpClient) { ReadResponseAsString = true };
    }
}
