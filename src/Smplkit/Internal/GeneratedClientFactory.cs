using GenApp = Smplkit.Internal.Generated.App;
using GenConfig = Smplkit.Internal.Generated.Config;
using GenFlags = Smplkit.Internal.Generated.Flags;
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

        var scheme = options.Scheme;
        var domain = options.BaseDomain;
        Config = new GenConfig.ConfigClient($"{scheme}://config.{domain}", httpClient) { ReadResponseAsString = true };
        Flags = new GenFlags.FlagsClient($"{scheme}://flags.{domain}", httpClient) { ReadResponseAsString = true };
        App = new GenApp.AppClient($"{scheme}://app.{domain}", httpClient) { ReadResponseAsString = true };
        Logging = new GenLogging.LoggingClient($"{scheme}://logging.{domain}", httpClient) { ReadResponseAsString = true };
    }
}
