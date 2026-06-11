// The Smpl Platform client — cross-cutting CRUD on client.Platform.
//
// PlatformClient groups the account-wide configuration resources that aren't
// owned by a single product, mirroring the product UI's Platform area:
//
// - Platform.Environments — environment CRUD
// - Platform.Services — service CRUD
// - Platform.Contexts — evaluation-context registration + read/delete
// - Platform.ContextTypes — context-type CRUD
//
// All four are pure CRUD — no Install() gate. Every sub-client speaks to the
// app service, so the client needs exactly one app transport (plus the
// context-registration buffer that Contexts drains).
//
// The client supports two construction shapes:
//
// * Wired into SmplClient — borrows the parent's app transport and an
//   externally-supplied context buffer. This is the common path; client.Flags
//   borrows client.Platform.Contexts as its evaluation-context registration
//   seam.
// * Standalone — PlatformClient(apiKey: ..., baseUrl: ..., ...) builds and owns
//   its own app transport and buffer. Dispose() tears down only the owned
//   transport.

using Smplkit.Internal;
using ContextRegistrationBuffer = Smplkit.Flags.ContextRegistrationBuffer;
using DebugLog = Smplkit.Internal.Debug;
using GenApp = Smplkit.Internal.Generated.App;

namespace Smplkit.Platform;

/// <summary>
/// The Smpl Platform client.
/// </summary>
/// <remarks>
/// <para>Groups the account-wide CRUD resources that aren't owned by a single
/// product, reachable as <c>client.Platform</c> (<see cref="Smplkit.SmplClient"/>)
/// or constructed directly:</para>
/// <code>
/// using var platform = new PlatformClient(apiKey: "sk_...");
/// var prod = platform.Environments.New("production", name: "Production");
/// await prod.SaveAsync();
/// foreach (var svc in await platform.Services.ListAsync())
/// {
///     // ...
/// }
/// </code>
/// <para>Sub-clients: <see cref="Environments"/>, <see cref="Services"/>,
/// <see cref="Contexts"/>, <see cref="ContextTypes"/>. Pure CRUD — no
/// <c>Install()</c> required.</para>
/// </remarks>
public sealed class PlatformClient : IDisposable
{
    private readonly HttpClient? _ownedHttpClient;

    /// <summary>The <c>environments</c> sub-client — environment CRUD.</summary>
    public EnvironmentsClient Environments { get; }

    /// <summary>The <c>services</c> sub-client — service CRUD.</summary>
    public ServicesClient Services { get; }

    /// <summary>The <c>contexts</c> sub-client — evaluation-context registration + read/delete.</summary>
    public ContextsClient Contexts { get; }

    /// <summary>The <c>context_types</c> sub-client — context-type CRUD.</summary>
    public ContextTypesClient ContextTypes { get; }

    /// <summary>
    /// Initializes a new <see cref="PlatformClient"/>.
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
    public PlatformClient(
        string? apiKey = null,
        string? baseUrl = null,
        string? profile = null,
        string? baseDomain = null,
        string? scheme = null,
        bool? debug = null,
        IReadOnlyDictionary<string, string>? extraHeaders = null)
    {
        var resolved = ConfigResolver.ResolveForManagement(new SmplClientOptions
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
        var appUrl = (baseUrl ?? ConfigResolver.ServiceUrl(resolved.Scheme, "app", resolved.BaseDomain)).TrimEnd('/');

        _ownedHttpClient = new HttpClient();
        var factory = new GeneratedClientFactory(_ownedHttpClient, new SmplClientOptions
        {
            ApiKey = resolvedKey,
            BaseDomain = resolved.BaseDomain,
            Scheme = resolved.Scheme,
            ExtraHeaders = extraHeaders is null ? null : new Dictionary<string, string>(extraHeaders),
        });

        // When base_url is supplied directly, point the generated app client at it.
        var appClient = baseUrl is null ? factory.App : new GenApp.AppClient(appUrl, _ownedHttpClient) { ReadResponseAsString = true };

        var buffer = new ContextRegistrationBuffer(lruSize: 10_000, flushSize: 100);

        Environments = new EnvironmentsClient(appClient);
        Services = new ServicesClient(appClient);
        Contexts = new ContextsClient(appClient, buffer);
        ContextTypes = new ContextTypesClient(appClient);
    }

    /// <summary>
    /// Wired constructor used by <see cref="Smplkit.SmplClient"/> to borrow the parent's
    /// app transport and an externally-supplied context registration buffer. This is the
    /// common path; <c>client.Flags</c> borrows <c>client.Platform.Contexts</c> as its
    /// evaluation-context registration seam.
    /// </summary>
    internal PlatformClient(GenApp.AppClient appClient, ContextRegistrationBuffer buffer)
    {
        _ownedHttpClient = null;
        Environments = new EnvironmentsClient(appClient);
        Services = new ServicesClient(appClient);
        Contexts = new ContextsClient(appClient, buffer);
        ContextTypes = new ContextTypesClient(appClient);
    }

    /// <summary>
    /// Closes the app transport — only when this client owns it.
    /// </summary>
    /// <remarks>
    /// A wired client borrows the parent's app transport and closes nothing.
    /// </remarks>
    public void Dispose()
    {
        _ownedHttpClient?.Dispose();
    }
}
