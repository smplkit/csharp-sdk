/// <summary>
/// Smpl Config SDK Showcase
/// ========================
///
/// Demonstrates the smplkit C# SDK for Smpl Config, covering:
///
/// - Client initialization (<see cref="Smplkit.SmplClient"/>)
/// - Management-plane CRUD: create, update, list, and delete configs
/// - Environment-specific overrides (SetValuesAsync / SetValueAsync)
/// - Multi-level inheritance: auth_module → user_service → common
/// - Runtime value resolution: ConnectAsync, Get, typed accessors
/// - Local cache verification: Stats()
/// - Real-time updates via WebSocket and change listeners
/// - Manual refresh and connection lifecycle
/// - Environment comparison across development / staging / production
///
/// This script is designed to be read top-to-bottom as a walkthrough of the
/// SDK's full capability surface. It is runnable against a live smplkit
/// environment, but is NOT a test — it creates, modifies, and deletes
/// real configs.
///
/// Prerequisites:
///     - .NET 8.0 SDK
///     - A valid smplkit API key (set via SMPLKIT_API_KEY env var)
///     - The smplkit Config service running and reachable
///
/// Usage:
///     export SMPLKIT_API_KEY="sk_api_..."
///     dotnet run --project examples/ConfigShowcase
/// </summary>

using Smplkit;
using Smplkit.Config;

// ---------------------------------------------------------------------------
// Configuration — set your API key via the SMPLKIT_API_KEY env var
// You can also omit the API key entirely — the SDK will resolve it from
// the SMPLKIT_API_KEY environment variable or ~/.smplkit config file.
// See the SDK README for details.
// ---------------------------------------------------------------------------

var apiKey = Environment.GetEnvironmentVariable("SMPLKIT_API_KEY") ?? "";

if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.WriteLine("ERROR: Set the SMPLKIT_API_KEY environment variable before running.");
    Console.WriteLine("  export SMPLKIT_API_KEY='sk_api_...'");
    return 1;
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

static void Section(string title)
{
    Console.WriteLine();
    Console.WriteLine(new string('=', 60));
    Console.WriteLine($"  {title}");
    Console.WriteLine(new string('=', 60));
    Console.WriteLine();
}

static void Step(string description)
{
    Console.WriteLine($"  → {description}");
}

// ======================================================================
// 1. SDK INITIALIZATION
// ======================================================================
Section("1. SDK Initialization");

using var client = new SmplClient(new SmplClientOptions { ApiKey = apiKey });
Step("SmplClient initialized");

// ======================================================================
// 2. MANAGEMENT PLANE — Set up the configuration hierarchy
// ======================================================================
//
// This section uses the management API to create and populate configs.
// In real life, a customer might do this via the console UI, Terraform,
// or a setup script. The SDK supports all of it programmatically.
// ======================================================================

// ------------------------------------------------------------------
// 2a. Update the built-in common config
// ------------------------------------------------------------------
Section("2a. Update the Common Config");

// Every account has a 'common' config auto-created at provisioning.
// It serves as the default parent for all other configs. Let's populate
// it with shared baseline values that every service in our org needs.

var common = await client.Config.GetByKeyAsync("common");
Step($"Fetched common config: id={common.Id}, key={common.Key}");

// Set base values — these apply to ALL environments by default.
common = await client.Config.UpdateAsync(common.Id, new CreateConfigOptions
{
    Name = "Common",
    Description = "Organization-wide shared configuration",
    Values = new Dictionary<string, object?>
    {
        ["app_name"] = "Acme SaaS Platform",
        ["support_email"] = "support@acme.dev",
        ["max_retries"] = 3,
        ["request_timeout_ms"] = 5000,
        ["pagination_default_page_size"] = 25,
        ["credentials"] = new Dictionary<string, object?>
        {
            ["oauth_provider"] = "https://auth.acme.dev",
            ["client_id"] = "acme_default_client",
            ["scopes"] = new[] { "read" },
        },
        ["feature_flags"] = new Dictionary<string, object?>
        {
            ["provider"] = "smplkit",
            ["endpoint"] = "https://flags.smplkit.com",
            ["refresh_interval_seconds"] = 30,
        },
    },
    Environments = new Dictionary<string, object?>(),
});
Step("Common config base values set");

// Override specific values for production — these flow through to every
// config that inherits from common, unless overridden further down.
common = await client.Config.SetValuesAsync(common.Id,
    new Dictionary<string, object?>
    {
        ["max_retries"] = 5,
        ["request_timeout_ms"] = 10000,
        ["credentials"] = new Dictionary<string, object?>
        {
            ["scopes"] = new[] { "read", "write", "admin" },
        },
    },
    environment: "production");
Step("Common config production overrides set");

// Staging gets its own tweaks.
common = await client.Config.SetValuesAsync(common.Id,
    new Dictionary<string, object?>
    {
        ["max_retries"] = 2,
        ["credentials"] = new Dictionary<string, object?>
        {
            ["scopes"] = new[] { "read", "write" },
        },
    },
    environment: "staging");
Step("Common config staging overrides set");

// ------------------------------------------------------------------
// 2b. Create a service-specific config (inherits from common)
// ------------------------------------------------------------------
Section("2b. Create the User Service Config");

// When we don't specify a parent, the API defaults to common.
// This config adds service-specific keys and overrides a few common ones.
var userService = await client.Config.CreateAsync(new CreateConfigOptions
{
    Name = "User Service",
    Key = "user_service",
    Description = "Configuration for the user microservice and its dependencies.",
    Values = new Dictionary<string, object?>
    {
        ["database"] = new Dictionary<string, object?>
        {
            ["host"] = "localhost",
            ["port"] = 5432,
            ["name"] = "users_dev",
            ["pool_size"] = 5,
            ["ssl_mode"] = "prefer",
        },
        ["cache_ttl_seconds"] = 300,
        ["enable_signup"] = true,
        ["allowed_email_domains"] = new[] { "acme.dev", "acme.com" },
        // Override the common pagination default for this service
        ["pagination_default_page_size"] = 50,
    },
});
Step($"Created user_service config: id={userService.Id}");

// Production overrides for the user service.
userService = await client.Config.SetValuesAsync(userService.Id,
    new Dictionary<string, object?>
    {
        ["database"] = new Dictionary<string, object?>
        {
            ["host"] = "prod-users-rds.internal.acme.dev",
            ["name"] = "users_prod",
            ["pool_size"] = 20,
            ["ssl_mode"] = "require",
        },
        ["cache_ttl_seconds"] = 600,
    },
    environment: "production");
Step("User service production overrides set");

// Staging overrides.
userService = await client.Config.SetValuesAsync(userService.Id,
    new Dictionary<string, object?>
    {
        ["database"] = new Dictionary<string, object?>
        {
            ["host"] = "staging-users-rds.internal.acme.dev",
            ["name"] = "users_staging",
            ["pool_size"] = 10,
        },
    },
    environment: "staging");
Step("User service staging overrides set");

// Add keys that only exist in the development environment.
userService = await client.Config.SetValuesAsync(userService.Id,
    new Dictionary<string, object?>
    {
        ["debug_sql"] = true,
        ["seed_test_data"] = true,
    },
    environment: "development");
Step("User service development-only keys set");

// Set a single value using the convenience method.
userService = await client.Config.SetValueAsync(
    userService.Id, "enable_signup", false, environment: "production");
Step("Disabled signup in production via SetValueAsync");

// ------------------------------------------------------------------
// 2c. Create a second config to show multi-level inheritance
// ------------------------------------------------------------------
Section("2c. Create the Auth Module Config (child of User Service)");

// This config's parent is user_service (not common), demonstrating
// multi-level inheritance: auth_module → user_service → common.
var authModule = await client.Config.CreateAsync(new CreateConfigOptions
{
    Name = "Auth Module",
    Key = "auth_module",
    Description = "Authentication module within the user service.",
    Parent = userService.Id,
    Values = new Dictionary<string, object?>
    {
        ["session_ttl_minutes"] = 60,
        ["max_failed_attempts"] = 5,
        ["lockout_duration_minutes"] = 15,
        ["mfa_enabled"] = false,
    },
});
Step($"Created auth_module config: id={authModule.Id}, parent={userService.Id}");

authModule = await client.Config.SetValuesAsync(authModule.Id,
    new Dictionary<string, object?>
    {
        ["session_ttl_minutes"] = 30,
        ["mfa_enabled"] = true,
        ["max_failed_attempts"] = 3,
    },
    environment: "production");
Step("Auth module production overrides set");

// ------------------------------------------------------------------
// 2d. List all configs — verify hierarchy
// ------------------------------------------------------------------
Section("2d. List All Configs");

var configs = await client.Config.ListAsync();
foreach (var cfg in configs)
{
    var parentInfo = cfg.Parent is not null ? $" (parent: {cfg.Parent})" : " (root)";
    Step($"{cfg.Key}{parentInfo}");
}

// ======================================================================
// 3. RUNTIME PLANE — Resolve configuration in a running application
// ======================================================================
//
// ConnectAsync eagerly fetches the config and its full parent chain,
// resolves values for the given environment via deep merge, caches
// everything in-process, and starts a background WebSocket for
// real-time updates.
//
// Get() and all value-access methods are SYNCHRONOUS — they read from
// a local dictionary with zero network overhead.
// ======================================================================

// ------------------------------------------------------------------
// 3a. Connect to a config for runtime use
// ------------------------------------------------------------------
Section("3a. Connect to Runtime Config");

var runtime = await client.Config.ConnectAsync(userService.Id, "production", timeout: 10);
Step("Runtime config connected and fully loaded");

// ------------------------------------------------------------------
// 3b. Read resolved values — all synchronous, all from local cache
// ------------------------------------------------------------------
Section("3b. Read Resolved Values");

var dbConfig = runtime.Get("database");
Step($"database = {FormatValue(dbConfig)}");
// Expected (deep-merged): user_service prod override + user_service base
// host: "prod-users-rds.internal.acme.dev", port: 5432, name: "users_prod",
// pool_size: 20, ssl_mode: "require"

var retries = runtime.Get("max_retries");
Step($"max_retries = {retries}");
// Expected: 5 (from common's production override — inherited through)

var creds = runtime.Get("credentials");
Step($"credentials = {FormatValue(creds)}");

var cacheTtl = runtime.Get("cache_ttl_seconds");
Step($"cache_ttl_seconds = {cacheTtl}");
// Expected: 600 (user_service production override)

var pageSize = runtime.Get("pagination_default_page_size");
Step($"pagination_default_page_size = {pageSize}");
// Expected: 50 (user_service base overrides common's 25)

var support = runtime.Get("support_email");
Step($"support_email = {support}");
// Expected: "support@acme.dev" (inherited all the way from common base)

var missing = runtime.Get("this_key_does_not_exist");
Step($"nonexistent key = {missing ?? "null"}");
// Expected: null

var withDefault = runtime.Get("this_key_does_not_exist", @default: "fallback");
Step($"nonexistent key with default = {withDefault}");
// Expected: "fallback"

// Typed convenience accessors
var signupEnabled = runtime.GetBool("enable_signup", @default: false);
Step($"enable_signup (bool) = {signupEnabled}");
// Expected: false (user_service production override via SetValueAsync)

var timeoutMs = runtime.GetInt("request_timeout_ms", @default: 3000);
Step($"request_timeout_ms (int) = {timeoutMs}");
// Expected: 10000 (common production override)

var appName = runtime.GetString("app_name", @default: "Unknown");
Step($"app_name (str) = {appName}");
// Expected: "Acme SaaS Platform" (common base)

// Check whether a key exists (regardless of its value).
Step($"'database' exists = {runtime.Exists("database")}");
// Expected: true
Step($"'ghost_key' exists = {runtime.Exists("ghost_key")}");
// Expected: false

// ------------------------------------------------------------------
// 3c. Verify local caching — no network requests on repeated reads
// ------------------------------------------------------------------
Section("3c. Verify Local Caching");

// ConnectAsync fetched everything eagerly. All Get() calls are pure
// local dict reads with zero network overhead. Stats() lets us verify.

var statsBefore = runtime.Stats();
Step($"Network fetches so far: {statsBefore.FetchCount}");
// Expected: 2 (user_service + common, fetched during connect)

// Read a bunch of keys — none should trigger a network fetch.
for (int i = 0; i < 100; i++)
{
    runtime.Get("max_retries");
    runtime.Get("database");
    runtime.Get("credentials");
}

var statsAfter = runtime.Stats();
Step($"Network fetches after 300 reads: {statsAfter.FetchCount}");
// Expected: still 2

if (statsAfter.FetchCount != statsBefore.FetchCount)
    throw new InvalidOperationException(
        $"SDK made unexpected network calls! Before: {statsBefore.FetchCount}, After: {statsAfter.FetchCount}");

Step("PASSED — all reads served from local cache");

// ------------------------------------------------------------------
// 3d. Get ALL resolved values as a dictionary
// ------------------------------------------------------------------
Section("3d. Get Full Resolved Configuration");

// Sometimes you want the entire resolved config as a dict — for
// logging at startup, passing to a framework, or debugging.
var allValues = runtime.GetAll();
Step($"Total resolved keys: {allValues.Count}");
foreach (var kvp in allValues.OrderBy(k => k.Key))
    Step($"  {kvp.Key} = {FormatValue(kvp.Value)}");

// ------------------------------------------------------------------
// 3e. Multi-level inheritance — connect to auth_module in production
// ------------------------------------------------------------------
Section("3e. Multi-Level Inheritance (auth_module)");

await using var authRuntime = await client.Config.ConnectAsync(
    authModule.Id, "production", timeout: 10);

var sessionTtl = authRuntime.Get("session_ttl_minutes");
Step($"session_ttl_minutes = {sessionTtl}");
// Expected: 30 (auth_module production override)

var mfa = authRuntime.Get("mfa_enabled");
Step($"mfa_enabled = {mfa}");
// Expected: true (auth_module production override)

// Keys inherited from user_service:
var authDb = authRuntime.Get("database");
Step($"database (inherited from user_service) = {FormatValue(authDb)}");

// Keys inherited all the way from common:
var authApp = authRuntime.GetString("app_name");
Step($"app_name (inherited from common) = {authApp}");

Step("auth_runtime closed via await using");

// ======================================================================
// 4. REAL-TIME UPDATES — WebSocket-driven cache invalidation
// ======================================================================
//
// The SDK maintains a WebSocket connection to the config service. When
// a value changes (via the console, API, or another SDK instance), the
// server pushes an update and the SDK refreshes its local cache. The
// application can register listeners to react to changes without polling.
// ======================================================================

Section("4. Real-Time Updates via WebSocket");

// ------------------------------------------------------------------
// 4a. Register change listeners
// ------------------------------------------------------------------

var changesReceived = new List<ConfigChangeEvent>();

runtime.OnChange(evt =>
{
    changesReceived.Add(evt);
    Console.WriteLine($"    [CHANGE] {evt.Key}: {FormatValue(evt.OldValue)!} → {FormatValue(evt.NewValue)}");
});
Step("Global change listener registered");

var retryChanges = new List<ConfigChangeEvent>();
runtime.OnChange(evt => retryChanges.Add(evt), key: "max_retries");
Step("Key-specific listener registered for 'max_retries'");

// ------------------------------------------------------------------
// 4b. Simulate a config change via the management API
// ------------------------------------------------------------------
Step("Updating max_retries on common (production) via management API...");

common = await client.Config.SetValueAsync(
    common.Id, "max_retries", 7, environment: "production");

// Give the WebSocket a moment to deliver the update.
await Task.Delay(2000);

// The runtime cache should now reflect the new value WITHOUT us
// having to do anything — the WebSocket pushed the update.
var newRetries = runtime.Get("max_retries");
Step($"max_retries after live update = {newRetries}");
// Expected: 7

Step($"Changes received by global listener: {changesReceived.Count}");
Step($"Retry-specific changes received: {retryChanges.Count}");

// ------------------------------------------------------------------
// 4c. Connection lifecycle
// ------------------------------------------------------------------
Section("4c. WebSocket Connection Lifecycle");

var wsStatus = runtime.ConnectionStatus();
Step($"WebSocket status: {wsStatus}");
// Expected: "connected"

// Force a manual refresh — re-fetches the chain via HTTP, re-resolves,
// fires listeners for any changes with source="manual".
await runtime.RefreshAsync();
Step("Manual refresh completed");

// ======================================================================
// 5. ENVIRONMENT COMPARISON
// ======================================================================
//
// A developer might want to see how the same config resolves across
// environments — useful for debugging "works in staging but not prod."
// ======================================================================

Section("5. Environment Comparison");

foreach (var env in new[] { "development", "staging", "production" })
{
    await using var envRuntime = await client.Config.ConnectAsync(
        userService.Id, env, timeout: 10);

    var db = envRuntime.Get("database") as Dictionary<string, object?>;
    var dbHost = db?.TryGetValue("host", out var h) == true ? h?.ToString() : "N/A";
    var envRetries = envRuntime.Get("max_retries");
    Step($"[{env,-12}] db.host={dbHost}, retries={envRetries}");
}

// ======================================================================
// 6. CLEANUP
// ======================================================================
Section("6. Cleanup");

// Close the runtime connection (WebSocket teardown).
await runtime.CloseAsync();
Step("Runtime connection closed");

// Delete configs in dependency order (children first).
await client.Config.DeleteAsync(authModule.Id);
Step($"Deleted auth_module ({authModule.Id})");

await client.Config.DeleteAsync(userService.Id);
Step($"Deleted user_service ({userService.Id})");

// Restore common to empty state (can't delete, but can clear values).
await client.Config.UpdateAsync(common.Id, new CreateConfigOptions
{
    Name = common.Name,
    Values = new Dictionary<string, object?>(),
    Environments = new Dictionary<string, object?>(),
});
Step("Common config reset to empty");

// SmplClient is disposed via the 'using' declaration at the top.
Step("SmplClient will be disposed on exit");

// ======================================================================
// DONE
// ======================================================================
Section("ALL DONE");
Console.WriteLine("  The Config SDK showcase completed successfully.");
Console.WriteLine("  If you got here, Smpl Config is ready to ship.\n");

return 0;

// ---------------------------------------------------------------------------
// Local helper for printing arbitrary resolved values
// ---------------------------------------------------------------------------

static string FormatValue(object? value) => value switch
{
    null => "null",
    Dictionary<string, object?> d => "{" + string.Join(", ", d.Select(kvp => $"{kvp.Key}: {FormatValue(kvp.Value)}")) + "}",
    object?[] arr => "[" + string.Join(", ", arr.Select(FormatValue)) + "]",
    _ => value.ToString() ?? "null",
};
