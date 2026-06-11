using System.Reflection;
using Smplkit;
using Smplkit.Account;
using Smplkit.Audit;
using Smplkit.Config;
using Smplkit.Errors;
using Smplkit.Flags;
using Smplkit.Internal;
using Smplkit.Jobs;
using Smplkit.Logging;
using Smplkit.Platform;
using Xunit;
using AuditHttpConfiguration = Smplkit.Audit.HttpConfiguration;
using ConfigClient = Smplkit.Config.ConfigClient;
using FlagsClient = Smplkit.Flags.FlagsClient;
using LoggingClient = Smplkit.Logging.LoggingClient;
using PlatformClient = Smplkit.Platform.PlatformClient;

namespace Smplkit.Tests;

/// <summary>
/// Coverage for the standalone (direct-construction) entry points of every
/// product client — <c>new ConfigClient(apiKey: ...)</c>, <c>new FlagsClient(...)</c>,
/// and so on — plus the standalone-only WebSocket / Dispose teardown paths.
/// </summary>
/// <remarks>
/// Standalone construction resolves config via <see cref="ConfigResolver"/> and
/// builds an owned <see cref="HttpClient"/>; passing an explicit <c>apiKey</c> and
/// <c>baseDomain</c> keeps resolution fully offline (no <c>~/.smplkit</c> or env
/// vars). No network call happens at construction — the live surfaces connect
/// lazily — so these tests stay deterministic.
/// </remarks>
public class StandaloneConstructorsTests
{
    private const string Key = "sk_standalone_test";
    private const string Domain = "example.test";

    // ------------------------------------------------------------------
    // Config
    // ------------------------------------------------------------------

    [Fact]
    public void ConfigClient_Standalone_Constructs()
    {
        using var config = new ConfigClient(
            apiKey: Key, environment: "production", service: "svc",
            baseDomain: Domain, scheme: "https", debug: true, telemetry: false,
            extraHeaders: new Dictionary<string, string> { ["X-Tenant"] = "acme" });
        Assert.Equal(0, config.PendingCount);
    }

    [Fact]
    public void ConfigClient_Standalone_TelemetryEnabled_NoEnvironment()
    {
        // telemetry left default (enabled) + no environment/service exercises the
        // NullIfEmpty + owned-metrics branches.
        using var config = new ConfigClient(apiKey: Key, baseDomain: Domain);
        Assert.Equal(0, config.PendingCount);
    }

    [Fact]
    public void ConfigClient_Standalone_OwnedWebSocket_OpenedAndDisposed()
    {
        var config = new ConfigClient(apiKey: Key, baseDomain: Domain, telemetry: false);
        // Force the standalone EnsureWs path (builds + starts an owned socket).
        var ensureWs = typeof(ConfigClient).GetMethod("EnsureWs",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var ws = ensureWs.Invoke(config, null);
        Assert.NotNull(ws);
        // Dispose tears down the owned socket + transport (the _ownsWs branch).
        config.Dispose();
    }

    // ------------------------------------------------------------------
    // Flags
    // ------------------------------------------------------------------

    [Fact]
    public void FlagsClient_Standalone_Constructs_AndBuffers()
    {
        using var flags = new FlagsClient(
            apiKey: Key, environment: "production", service: "svc",
            baseDomain: Domain, debug: true, telemetry: false,
            extraHeaders: new Dictionary<string, string> { ["X-Tenant"] = "acme" });
        flags.Register(new FlagDeclaration("f", "BOOLEAN", true));
        Assert.Equal(1, flags.PendingCount);
    }

    [Fact]
    public void FlagsClient_Standalone_TelemetryEnabled()
    {
        using var flags = new FlagsClient(apiKey: Key, baseDomain: Domain);
        Assert.Equal(0, flags.PendingCount);
    }

    [Fact]
    public void FlagsClient_Standalone_OwnedWebSocket_OpenedAndDisposed()
    {
        var flags = new FlagsClient(apiKey: Key, baseDomain: Domain, telemetry: false);
        var ensureWs = typeof(FlagsClient).GetMethod("EnsureOwnedWebSocket",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var ws = ensureWs.Invoke(flags, null);
        Assert.NotNull(ws);
        // Calling again returns the same instance (the early-return branch).
        Assert.Same(ws, ensureWs.Invoke(flags, null));
        flags.Dispose();
    }

    [Fact]
    public void FlagsClient_FlushSync_EmptyBuffer_NoThrow()
    {
        using var flags = new FlagsClient(apiKey: Key, baseDomain: Domain, telemetry: false);
        flags.FlushSync(); // empty buffer → returns without a POST
    }

    // ------------------------------------------------------------------
    // Logging
    // ------------------------------------------------------------------

    [Fact]
    public void LoggingClient_Standalone_Constructs()
    {
        using var logging = new LoggingClient(
            apiKey: Key, environment: "production", service: "svc",
            baseDomain: Domain, debug: true, telemetry: false,
            extraHeaders: new Dictionary<string, string> { ["X-Tenant"] = "acme" });
        Assert.NotNull(logging.Loggers);
        Assert.NotNull(logging.LogGroups);
    }

    [Fact]
    public void LoggingClient_Standalone_OwnedWebSocket_OpenedAndDisposed()
    {
        var logging = new LoggingClient(apiKey: Key, baseDomain: Domain);
        var ensureWs = typeof(LoggingClient).GetMethod("EnsureWs",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var ws = ensureWs.Invoke(logging, null);
        Assert.NotNull(ws);
        Assert.Same(ws, ensureWs.Invoke(logging, null));
        // Mark started so Close() runs the WS-teardown branch, then Dispose.
        typeof(LoggingClient).GetField("_started",
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(logging, true);
        logging.Dispose();
    }

    [Fact]
    public void LoggingClient_OnChange_BeforeInstall_Throws()
    {
        using var logging = new LoggingClient(apiKey: Key, baseDomain: Domain);
        Assert.Throws<NotInstalledException>(() => logging.OnChange(_ => { }));
    }

    [Fact]
    public async Task LoggingClient_RefreshAsync_BeforeInstall_Throws()
    {
        using var logging = new LoggingClient(apiKey: Key, baseDomain: Domain);
        await Assert.ThrowsAsync<NotInstalledException>(() => logging.RefreshAsync());
    }

    // ------------------------------------------------------------------
    // Audit
    // ------------------------------------------------------------------

    [Fact]
    public async Task AuditClient_Standalone_ResolvesAndDisposes()
    {
        // apiKey present but baseUrl absent → the resolver branch (167-180) runs.
        await using var audit = new AuditClient(
            apiKey: Key, environment: "production", baseDomain: Domain,
            debug: true, extraHeaders: new Dictionary<string, string> { ["X-Tenant"] = "acme" });
        Assert.NotNull(audit.Events);
        Assert.NotNull(audit.ResourceTypes);
        Assert.NotNull(audit.EventTypes);
        Assert.NotNull(audit.Categories);
        Assert.NotNull(audit.Forwarders);
    }

    [Fact]
    public async Task AuditClient_Standalone_WithExplicitBaseUrl()
    {
        // apiKey + baseUrl supplied → the direct branch (no resolver).
        await using var audit = new AuditClient(
            apiKey: Key, baseUrl: "https://audit.example.test");
        Assert.NotNull(audit.Forwarders);
    }

    // ------------------------------------------------------------------
    // Jobs
    // ------------------------------------------------------------------

    [Fact]
    public void JobsClient_Standalone_Constructs()
    {
        using var jobs = new JobsClient(
            apiKey: Key, baseDomain: Domain, scheme: "https", debug: true,
            extraHeaders: new Dictionary<string, string> { ["X-Tenant"] = "acme" });
        Assert.NotNull(jobs.Runs);
    }

    // ------------------------------------------------------------------
    // Platform
    // ------------------------------------------------------------------

    [Fact]
    public void PlatformClient_Standalone_Constructs()
    {
        using var platform = new PlatformClient(
            apiKey: Key, baseDomain: Domain, scheme: "https", debug: true,
            extraHeaders: new Dictionary<string, string> { ["X-Tenant"] = "acme" });
        Assert.NotNull(platform.Environments);
        Assert.NotNull(platform.Services);
        Assert.NotNull(platform.Contexts);
        Assert.NotNull(platform.ContextTypes);
    }

    [Fact]
    public void PlatformClient_Standalone_WithExplicitBaseUrl()
    {
        // baseUrl supplied → the explicit GenApp.AppClient branch (line 113).
        using var platform = new PlatformClient(
            apiKey: Key, baseUrl: "https://app.example.test", baseDomain: Domain);
        Assert.NotNull(platform.Environments);
        platform.Dispose(); // owned-transport teardown
    }

    // ------------------------------------------------------------------
    // Account
    // ------------------------------------------------------------------

    [Fact]
    public void AccountClient_Standalone_Constructs_AndDisposes()
    {
        var account = new AccountClient(
            apiKey: Key, baseDomain: Domain, scheme: "https", debug: true,
            extraHeaders: new Dictionary<string, string> { ["X-Tenant"] = "acme" });
        Assert.NotNull(account.Settings);
        account.Dispose();   // no-op
        account.Dispose();   // idempotent
    }

    [Fact]
    public void AccountClient_Standalone_ResolvesAppUrl_WhenNoBaseUrl()
    {
        // No baseUrl → ResolveTarget composes the app URL from baseDomain/scheme.
        using var account = new AccountClient(apiKey: Key, baseDomain: Domain);
        Assert.NotNull(account.Settings);
    }

    // ------------------------------------------------------------------
    // Audit Forwarder per-environment mutators + ForwardSmplkitEvents
    // ------------------------------------------------------------------

    [Fact]
    public void Forwarder_SetConfiguration_Base_And_PerEnvironment()
    {
        var fwd = MakeForwarder();

        var baseCfg = new AuditHttpConfiguration { Url = "https://base" };
        fwd.SetConfiguration(baseCfg);
        Assert.Same(baseCfg, fwd.Configuration);

        var prodCfg = new AuditHttpConfiguration { Url = "https://prod" };
        fwd.SetConfiguration(prodCfg, environment: "production");
        Assert.Same(prodCfg, fwd.Environments["production"].Configuration);
        // The override entry is created on demand.
        Assert.False(fwd.Environments["production"].Enabled);
    }

    [Fact]
    public void Forwarder_SetEnabled_Base_And_PerEnvironment_PreservesOtherField()
    {
        var fwd = MakeForwarder();

        // Per-environment configuration first, then enablement — the override is
        // reused so the configuration survives (EnvironmentOverride get path).
        var cfg = new AuditHttpConfiguration { Url = "https://prod" };
        fwd.SetConfiguration(cfg, environment: "production");
        fwd.SetEnabled(true, environment: "production");
        Assert.True(fwd.Environments["production"].Enabled);
        Assert.Same(cfg, fwd.Environments["production"].Configuration);

        // Base enablement (server-pinned false, but the setter still assigns).
        fwd.SetEnabled(true);
        Assert.True(fwd.Enabled);

        // New environment via SetEnabled alone creates the override.
        fwd.SetEnabled(true, environment: "staging");
        Assert.True(fwd.Environments["staging"].Enabled);
    }

    [Fact]
    public void Forwarder_ForwardSmplkitEvents_Settable()
    {
        var fwd = MakeForwarder();
        Assert.False(fwd.ForwardSmplkitEvents);
        fwd.ForwardSmplkitEvents = true;
        Assert.True(fwd.ForwardSmplkitEvents);
    }

    private static Forwarder MakeForwarder()
    {
        var fwds = new ForwardersClient(
            new Smplkit.Internal.Generated.Audit.AuditClient(
                "https://audit.example.test", new HttpClient())
            { ReadResponseAsString = true });
        return fwds.New("k", ForwarderType.Http, new AuditHttpConfiguration { Url = "u" }, name: "n");
    }

    // ------------------------------------------------------------------
    // Small model / exception gaps
    // ------------------------------------------------------------------

    [Fact]
    public void NotInstalledException_Constructs_WithMessage()
    {
        var ex = new NotInstalledException("install first");
        Assert.Equal("install first", ex.Message);
        Assert.IsAssignableFrom<SmplkitException>(ex);
    }

    [Fact]
    public void AccountSettings_RawGetter_ReturnsData()
    {
        var settings = new AccountSettings();
        settings.Raw = new Dictionary<string, object?> { ["a"] = 1 };
        Assert.Equal(1, settings.Raw["a"]);
    }
}
