/// <summary>
/// Smpl Config SDK Showcase
/// ========================
///
/// Demonstrates the prescriptive programming model:
///     var client = new SmplClient(options);
///     await client.ConnectAsync();
///     var value = client.Config.GetString("my_config", "key");
///
/// - Client initialization (<see cref="Smplkit.SmplClient"/>)
/// - Management-plane CRUD: create, update, list, and delete configs
/// - Environment-specific overrides (SetValuesAsync / SetValueAsync)
/// - Multi-level inheritance: auth_module → user_service → common
/// - Prescriptive value access: GetString, GetInt, GetBool
/// - Real-time updates via OnChange listeners
/// - Manual refresh
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

var apiKey = Environment.GetEnvironmentVariable("SMPLKIT_API_KEY") ?? "";

if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.WriteLine("ERROR: Set the SMPLKIT_API_KEY environment variable before running.");
    Console.WriteLine("  export SMPLKIT_API_KEY='sk_api_...'");
    return 1;
}

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

using var client = new SmplClient(new SmplClientOptions
{
    ApiKey = apiKey,
    Environment = "production",
});
Step("SmplClient initialized with environment=production");

// ======================================================================
// 2. MANAGEMENT PLANE — Set up the configuration hierarchy
// ======================================================================

Section("2a. Update the Common Config");

var common = await client.Config.GetByKeyAsync("common");
Step($"Fetched common config: id={common.Id}, key={common.Key}");

common = await client.Config.UpdateAsync(common.Id, new CreateConfigOptions
{
    Name = "Common",
    Description = "Organization-wide shared configuration",
    Items = new Dictionary<string, object?>
    {
        ["app_name"] = "Acme SaaS Platform",
        ["support_email"] = "support@acme.dev",
        ["max_retries"] = 3,
        ["request_timeout_ms"] = 5000,
    },
    Environments = new Dictionary<string, object?>(),
});
Step("Common config base values set");

common = await client.Config.SetValuesAsync(common.Id,
    new Dictionary<string, object?>
    {
        ["max_retries"] = 5,
        ["request_timeout_ms"] = 10000,
    },
    environment: "production");
Step("Common config production overrides set");

Section("2b. Create the User Service Config");

var userService = await client.Config.CreateAsync(new CreateConfigOptions
{
    Name = "User Service",
    Key = "user_service",
    Items = new Dictionary<string, object?>
    {
        ["cache_ttl_seconds"] = 300,
        ["enable_signup"] = true,
        ["pagination_default_page_size"] = 50,
    },
});
Step($"Created user_service config: id={userService.Id}");

userService = await client.Config.SetValuesAsync(userService.Id,
    new Dictionary<string, object?>
    {
        ["cache_ttl_seconds"] = 600,
        ["enable_signup"] = false,
    },
    environment: "production");
Step("User service production overrides set");

// ======================================================================
// 3. PRESCRIPTIVE ACCESS — Connect once, read everywhere
// ======================================================================

Section("3. Prescriptive Access");

await client.ConnectAsync();
Step("client.ConnectAsync() — all configs loaded and resolved");

// --- Typed accessors ---
var appName = client.Config.GetString("common", "app_name", "Unknown");
Step($"common/app_name (string) = {appName}");

var retries = client.Config.GetInt("common", "max_retries", 1);
Step($"common/max_retries (int) = {retries}");
// Expected: 5 (production override)

var signup = client.Config.GetBool("user_service", "enable_signup", true);
Step($"user_service/enable_signup (bool) = {signup}");
// Expected: false (production override)

// --- Raw access ---
var allValues = client.Config.GetValue("user_service") as Dictionary<string, object?>;
Step($"user_service total keys: {allValues?.Count}");

var missing = client.Config.GetValue("user_service", "nonexistent_item");
Step($"nonexistent item = {missing ?? "null"}");

// ======================================================================
// 4. REAL-TIME UPDATES — OnChange + RefreshAsync
// ======================================================================

Section("4. OnChange + Refresh");

var changes = new List<ConfigChangeEvent>();
client.Config.OnChange(evt =>
{
    changes.Add(evt);
    Console.WriteLine($"    [CHANGE] {evt.ConfigKey}/{evt.ItemKey}: {evt.OldValue} → {evt.NewValue}");
});
Step("Global change listener registered");

var retryChanges = new List<ConfigChangeEvent>();
client.Config.OnChange(
    evt => retryChanges.Add(evt),
    configKey: "common",
    itemKey: "max_retries");
Step("Key-specific listener registered for common/max_retries");

// Update via management API, then refresh
common = await client.Config.SetValueAsync(
    common.Id, "max_retries", 7, environment: "production");
Step("Updated max_retries to 7 via management API");

await client.Config.RefreshAsync();
Step("Manual refresh completed");

var newRetries = client.Config.GetInt("common", "max_retries", 1);
Step($"max_retries after refresh = {newRetries}");
// Expected: 7

Step($"Global changes: {changes.Count}, retry-specific: {retryChanges.Count}");

// ======================================================================
// 5. CLEANUP
// ======================================================================
Section("5. Cleanup");

await client.Config.DeleteAsync(userService.Id);
Step($"Deleted user_service ({userService.Id})");

await client.Config.UpdateAsync(common.Id, new CreateConfigOptions
{
    Name = common.Name,
    Items = new Dictionary<string, object?>(),
    Environments = new Dictionary<string, object?>(),
});
Step("Common config reset to empty");

Section("ALL DONE");
Console.WriteLine("  The Config SDK showcase completed successfully.\n");

return 0;
