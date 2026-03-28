/// <summary>
/// Smpl Config SDK Showcase
/// ========================
///
/// Demonstrates the smplkit C# SDK for Smpl Config, covering:
///
/// - Client initialization (<see cref="Smplkit.SmplkitClient"/>)
/// - Management-plane CRUD: create, update, list, and delete configs
/// - Multi-level inheritance (auth_module → user_service → common)
///
/// This script is designed to be read top-to-bottom as a walkthrough of the
/// SDK's management-plane surface. It is runnable against a live smplkit
/// environment, but is *not* a test — it creates, modifies, and deletes
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

using var client = new SmplkitClient(new SmplkitClientOptions { ApiKey = apiKey });
Step("SmplkitClient initialized");

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
});
Step("Common config base values set");

// --- SKIPPED: Environment-specific overrides (production, staging) ---
// The C# SDK does not yet support setting environment overrides via
// CreateConfigOptions or a set_values/set_value method. The Python SDK
// uses common.set_values({...}, environment="production") for this.
// When environment override support is added to the C# SDK, uncomment
// and adapt the following:
//
// Production overrides:
//   max_retries: 5, request_timeout_ms: 10000,
//   credentials.scopes: ["read", "write", "admin"]
//
// Staging overrides:
//   max_retries: 2, credentials.scopes: ["read", "write"]
Step("SKIPPED: Environment overrides not yet supported in C# SDK");

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

// --- SKIPPED: Environment-specific overrides (production, staging, development) ---
// The C# SDK does not yet support setting environment overrides.
// The Python SDK uses user_service.set_values({...}, environment="production"):
//
// Production overrides:
//   database.host: "prod-users-rds.internal.acme.dev",
//   database.name: "users_prod", database.pool_size: 20,
//   database.ssl_mode: "require", cache_ttl_seconds: 600
//
// Staging overrides:
//   database.host: "staging-users-rds.internal.acme.dev",
//   database.name: "users_staging", database.pool_size: 10
//
// Development-only keys:
//   debug_sql: true, seed_test_data: true
//
// --- SKIPPED: set_value convenience method ---
// Python SDK: await user_service.set_value("enable_signup", False, environment="production")
Step("SKIPPED: Environment overrides not yet supported in C# SDK");

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

// --- SKIPPED: auth_module production environment overrides ---
// Python SDK: await auth_module.set_values({
//     "session_ttl_minutes": 30, "mfa_enabled": True, "max_failed_attempts": 3
// }, environment="production")
Step("SKIPPED: Environment overrides not yet supported in C# SDK");

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
// --- SKIPPED: The C# SDK does not yet implement the runtime plane. ---
//
// The Python SDK provides:
//   runtime = await config.connect("production", timeout=10)
//   runtime.get("key")           — read resolved value from local cache
//   runtime.get_str("key")       — typed string accessor
//   runtime.get_int("key")       — typed int accessor
//   runtime.get_bool("key")      — typed bool accessor
//   runtime.exists("key")        — check if key exists
//   runtime.get_all()            — get all resolved values
//   runtime.stats()              — cache diagnostics (fetch_count)
//
// When the C# SDK adds ConnectAsync() and ConfigRuntime, this section
// should exercise: connect, read resolved values (including inherited
// and deep-merged values), verify local caching, get_all, and
// multi-level inheritance resolution.
// ======================================================================

Section("3. Runtime Plane");
Step("SKIPPED: Runtime plane (ConnectAsync / ConfigRuntime) not yet implemented in C# SDK");

// ======================================================================
// 4. REAL-TIME UPDATES — WebSocket-driven cache invalidation
// ======================================================================
//
// --- SKIPPED: The C# SDK does not yet implement WebSocket support. ---
//
// The Python SDK provides:
//   runtime.on_change(callback)              — global change listener
//   runtime.on_change(callback, key="key")   — key-specific listener
//   runtime.connection_status()              — WebSocket status
//   runtime.refresh()                        — manual cache refresh
//
// When the C# SDK adds WebSocket support, this section should exercise:
// registering change listeners, simulating a config change via the
// management API, verifying the runtime cache updates automatically,
// and connection lifecycle (status, manual refresh).
// ======================================================================

Section("4. Real-Time Updates");
Step("SKIPPED: WebSocket / real-time updates not yet implemented in C# SDK");

// ======================================================================
// 5. ENVIRONMENT COMPARISON
// ======================================================================
//
// --- SKIPPED: Requires runtime plane (ConnectAsync). ---
//
// The Python SDK connects to user_service in development, staging, and
// production and prints the resolved db.host and max_retries for each.
// ======================================================================

Section("5. Environment Comparison");
Step("SKIPPED: Requires runtime plane (ConnectAsync) not yet implemented in C# SDK");

// ======================================================================
// 6. CLEANUP
// ======================================================================
Section("6. Cleanup");

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
});
Step("Common config reset to empty");

// SmplkitClient is disposed via the using declaration above.
Step("SmplkitClient will be disposed on exit");

// ======================================================================
// DONE
// ======================================================================
Section("ALL DONE");
Console.WriteLine("  The Config SDK showcase completed successfully.");
Console.WriteLine("  If you got here, Smpl Config management plane is ready to ship.");
Console.WriteLine();

return 0;
