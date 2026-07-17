// The Smpl Account client — account-level settings on client.Account.
//
// AccountClient exposes the authenticated account's own configuration,
// mirroring the product UI's Account area:
//
// - account.Settings — get/save the account settings object
//
// The settings endpoint isn't JSON:API — its body is a raw JSON object — so the
// settings sub-client uses HttpClient directly rather than going through a
// generated client.
//
// The client supports two construction shapes:
//
// * Wired into SmplClient — built from the app base URL and api key the
//   top-level client has already resolved. This is the common path.
// * Standalone — AccountClient(apiKey: ..., baseUrl: ..., ...) resolves the app
//   base URL itself. There are no pooled transports to tear down (each settings
//   call opens and closes its own short-lived HttpClient), so Dispose() is a
//   no-op kept for interface symmetry.

using System.Text;
using System.Text.Json;
using Smplkit.Errors;
using Smplkit.Internal;
using DebugLog = Smplkit.Internal.Debug;

namespace Smplkit.Account;

/// <summary>
/// Account-settings get/save (<c>client.Account.Settings</c>).
/// </summary>
/// <remarks>
/// The endpoint isn't JSON:API — body is a raw JSON object — so we use
/// <see cref="HttpClient"/> directly rather than going through a generated client.
/// </remarks>
public sealed class SettingsClient
{
    private readonly string _baseUrl;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly HttpMessageHandler? _handler;

    internal SettingsClient(
        string appBaseUrl,
        string apiKey,
        IReadOnlyDictionary<string, string>? extraHeaders = null,
        HttpMessageHandler? handler = null)
    {
        _baseUrl = appBaseUrl.TrimEnd('/');
        var headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {apiKey}" };
        if (extraHeaders is not null)
            foreach (var kv in extraHeaders)
                headers[kv.Key] = kv.Value;
        // This endpoint bypasses the shared generated-client transport, so the
        // SDK's default User-Agent is stamped here too — the platform edge
        // rejects UA-less requests — unless the caller supplied their own
        // (any casing) via extraHeaders.
        if (!headers.Keys.Any(k => string.Equals(k, "User-Agent", StringComparison.OrdinalIgnoreCase)))
            headers["User-Agent"] = SdkVersion.UserAgent;
        _headers = headers;
        _handler = handler;
    }

    /// <summary>Fetches the current account settings.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="AccountSettings"/> active record. Mutate its fields and call <c>SaveAsync()</c> to persist the changes.</returns>
    public async Task<AccountSettings> GetAsync(CancellationToken ct = default)
    {
        using var http = NewHttpClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{_baseUrl}/api/v1/accounts/current/settings");
        ApplyHeaders(request);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        CheckStatus((int)response.StatusCode, body);
        return new AccountSettings(this, ParseSettings(body));
    }

    internal async Task<AccountSettings> SaveInternalAsync(
        Dictionary<string, object?> data, CancellationToken ct = default)
    {
        using var http = NewHttpClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Put, $"{_baseUrl}/api/v1/accounts/current/settings")
        {
            Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json"),
        };
        ApplyHeaders(request);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        CheckStatus((int)response.StatusCode, body);
        return new AccountSettings(this, ParseSettings(body));
    }

    private HttpClient NewHttpClient()
        => _handler is null ? new HttpClient() : new HttpClient(_handler, disposeHandler: false);

    private void ApplyHeaders(HttpRequestMessage request)
    {
        foreach (var kv in _headers)
            request.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
    }

    private static void CheckStatus(int statusCode, string body)
    {
        if (statusCode < 200 || statusCode >= 300)
            throw ApiErrorParser.CreateException(statusCode, body);
    }

    private static Dictionary<string, object?> ParseSettings(string body)
    {
        if (string.IsNullOrEmpty(body))
            return new Dictionary<string, object?>();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, object?>();
        return doc.RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => Smplkit.Config.Resolver.Normalize(p.Value));
    }
}

/// <summary>
/// The Smpl Account client.
/// </summary>
/// <remarks>
/// <para>Exposes the authenticated account's own configuration, reachable as
/// <c>client.Account</c> (<see cref="Smplkit.SmplClient"/>) or constructed directly:</para>
/// <code>
/// using var account = new AccountClient(apiKey: "sk_...");
/// var settings = await account.Settings.GetAsync();
/// settings.EnvironmentOrder = new List&lt;string&gt; { "production", "staging" };
/// await settings.SaveAsync();
/// </code>
/// <para>Sub-client: <see cref="Settings"/> (get/save). Pure CRUD — no <c>Install()</c> required.</para>
/// </remarks>
public sealed class AccountClient : IDisposable
{
    /// <summary>The <c>settings</c> sub-client — account-settings get/save.</summary>
    public SettingsClient Settings { get; }

    /// <summary>
    /// Initializes a new <see cref="AccountClient"/>.
    /// </summary>
    /// <param name="apiKey">API key. When omitted, resolved from <c>SMPLKIT_API_KEY</c> or <c>~/.smplkit</c>.</param>
    /// <param name="baseUrl">Full app-service base URL. Usually resolved from
    /// <paramref name="baseDomain"/>/<paramref name="scheme"/>; supplied directly by the
    /// top-level clients which have already computed it.</param>
    /// <param name="profile">Named <c>~/.smplkit</c> profile section.</param>
    /// <param name="baseDomain">Base domain for API requests (default <c>smplkit.com</c>).</param>
    /// <param name="scheme">URL scheme (default <c>https</c>).</param>
    /// <param name="debug">Enable SDK debug logging.</param>
    /// <param name="extraHeaders">Extra headers attached to every request.</param>
    public AccountClient(
        string? apiKey = null,
        string? baseUrl = null,
        string? profile = null,
        string? baseDomain = null,
        string? scheme = null,
        bool? debug = null,
        IReadOnlyDictionary<string, string>? extraHeaders = null)
        : this(apiKey, baseUrl, profile, baseDomain, scheme, debug, extraHeaders, handler: null)
    {
    }

    internal AccountClient(
        string? apiKey,
        string? baseUrl,
        string? profile,
        string? baseDomain,
        string? scheme,
        bool? debug,
        IReadOnlyDictionary<string, string>? extraHeaders,
        HttpMessageHandler? handler)
    {
        var (appUrl, resolvedKey) = ResolveTarget(apiKey, baseUrl, profile, baseDomain, scheme, debug);
        Settings = new SettingsClient(appUrl, resolvedKey, extraHeaders, handler);
    }

    /// <summary>
    /// Resolve the (app base URL, api key) for the settings client.
    /// </summary>
    /// <remarks>
    /// <paramref name="baseUrl"/>/<paramref name="apiKey"/> are used directly when both are
    /// supplied (the path the top-level client takes after it has already resolved them);
    /// otherwise the account-global config resolver fills in whatever is missing.
    /// </remarks>
    private static (string AppUrl, string ApiKey) ResolveTarget(
        string? apiKey, string? baseUrl, string? profile, string? baseDomain, string? scheme, bool? debug)
    {
        var resolved = ConfigResolver.ResolveAccountGlobal(new SmplClientOptions
        {
            ApiKey = apiKey,
            Profile = profile,
            BaseDomain = baseDomain,
            Scheme = scheme,
            Debug = debug,
        });
        if (resolved.Debug)
            DebugLog.Enabled = true;
        var resolvedKey = apiKey ?? resolved.ApiKey;
        var appUrl = baseUrl ?? ConfigResolver.ServiceUrl(resolved.Scheme, "app", resolved.BaseDomain);
        return (appUrl.TrimEnd('/'), resolvedKey);
    }

    /// <summary>No-op — the settings client opens a short-lived <see cref="HttpClient"/> per call.</summary>
    public void Dispose()
    {
    }
}
