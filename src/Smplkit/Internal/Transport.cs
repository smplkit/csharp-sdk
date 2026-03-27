using System.Net;
using System.Text;
using System.Text.Json;
using Smplkit.Errors;

namespace Smplkit.Internal;

/// <summary>
/// Internal HTTP transport layer. Wraps <see cref="HttpClient"/> and maps
/// HTTP errors to typed SDK exceptions.
/// </summary>
internal sealed class Transport
{
    private const string JsonApiMediaType = "application/vnd.api+json";
    private const string UserAgent = "smplkit-csharp-sdk/0.0.0";

    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;

    /// <summary>
    /// Initializes a new <see cref="Transport"/> with the given HTTP client and options.
    /// </summary>
    /// <param name="httpClient">The underlying HTTP client.</param>
    /// <param name="options">Client configuration options.</param>
    internal Transport(HttpClient httpClient, SmplkitClientOptions options)
    {
        _httpClient = httpClient;
        _baseUrl = options.BaseUrl.TrimEnd('/');

        _httpClient.Timeout = options.Timeout;
        _httpClient.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        _httpClient.DefaultRequestHeaders.Add("Accept", JsonApiMediaType);
        Auth.ApplyBearerToken(_httpClient, options.ApiKey);
    }

    /// <summary>
    /// Sends a GET request to the specified path.
    /// </summary>
    /// <param name="path">The relative URL path.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The response body as a string.</returns>
    internal async Task<string> GetAsync(string path, CancellationToken ct = default)
    {
        var url = _baseUrl + path;
        try
        {
            var response = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
            return await HandleResponseAsync(response, ct).ConfigureAwait(false);
        }
        catch (SmplException)
        {
            throw;
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new SmplTimeoutException($"Request to {path} timed out.", ex);
        }
        catch (TaskCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new SmplConnectionException($"Connection failed for {path}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Sends a POST request with a JSON body to the specified path.
    /// </summary>
    /// <param name="path">The relative URL path.</param>
    /// <param name="body">The object to serialize as JSON.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The response body as a string.</returns>
    internal async Task<string> PostAsync(string path, object body, CancellationToken ct = default)
    {
        var url = _baseUrl + path;
        var json = JsonSerializer.Serialize(body, SerializerOptions);
        using var content = new StringContent(json, Encoding.UTF8, JsonApiMediaType);
        try
        {
            var response = await _httpClient.PostAsync(url, content, ct).ConfigureAwait(false);
            return await HandleResponseAsync(response, ct).ConfigureAwait(false);
        }
        catch (SmplException)
        {
            throw;
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new SmplTimeoutException($"Request to {path} timed out.", ex);
        }
        catch (TaskCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new SmplConnectionException($"Connection failed for {path}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Sends a PUT request with a JSON body to the specified path.
    /// </summary>
    /// <param name="path">The relative URL path.</param>
    /// <param name="body">The object to serialize as JSON.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The response body as a string.</returns>
    internal async Task<string> PutAsync(string path, object body, CancellationToken ct = default)
    {
        var url = _baseUrl + path;
        var json = JsonSerializer.Serialize(body, SerializerOptions);
        using var content = new StringContent(json, Encoding.UTF8, JsonApiMediaType);
        try
        {
            var response = await _httpClient.PutAsync(url, content, ct).ConfigureAwait(false);
            return await HandleResponseAsync(response, ct).ConfigureAwait(false);
        }
        catch (SmplException)
        {
            throw;
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new SmplTimeoutException($"Request to {path} timed out.", ex);
        }
        catch (TaskCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new SmplConnectionException($"Connection failed for {path}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Sends a DELETE request to the specified path.
    /// </summary>
    /// <param name="path">The relative URL path.</param>
    /// <param name="ct">Cancellation token.</param>
    internal async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        var url = _baseUrl + path;
        try
        {
            var response = await _httpClient.DeleteAsync(url, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NoContent)
                return;
            await HandleResponseAsync(response, ct).ConfigureAwait(false);
        }
        catch (SmplException)
        {
            throw;
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new SmplTimeoutException($"Request to {path} timed out.", ex);
        }
        catch (TaskCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new SmplConnectionException($"Connection failed for {path}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Reads the response body and maps non-success status codes to typed exceptions.
    /// </summary>
    private static async Task<string> HandleResponseAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
            return body;

        var statusCode = (int)response.StatusCode;
        switch (statusCode)
        {
            case 404:
                throw new SmplNotFoundException($"Resource not found.", body);
            case 409:
                throw new SmplConflictException($"Conflict: {body}", body);
            case 422:
                throw new SmplValidationException($"Validation failed: {body}", body);
            default:
                throw new SmplException(
                    $"HTTP {statusCode}: {body}",
                    statusCode: statusCode,
                    responseBody: body);
        }
    }

    /// <summary>
    /// Default JSON serializer options with camelCase naming.
    /// </summary>
    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
