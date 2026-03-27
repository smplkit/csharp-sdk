namespace Smplkit.Internal;

/// <summary>
/// Helper for configuring authentication on HTTP requests.
/// </summary>
internal static class Auth
{
    /// <summary>
    /// Applies Bearer token authentication headers to the given <see cref="HttpClient"/>.
    /// </summary>
    /// <param name="httpClient">The HTTP client to configure.</param>
    /// <param name="apiKey">The API key to use as a Bearer token.</param>
    internal static void ApplyBearerToken(HttpClient httpClient, string apiKey)
    {
        httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
    }
}
