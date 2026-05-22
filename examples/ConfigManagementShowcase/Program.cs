// Demonstrates the smplkit management SDK for Smpl Config.
//
// Prerequisites:
//     - dotnet add package Smplkit.Sdk
//     - A valid smplkit API key, provided via one of:
//         - SMPLKIT_API_KEY environment variable
//         - ~/.smplkit configuration file (see SDK docs)
//     - The smplkit Config service running and reachable
//
// Usage:
//     dotnet run --project examples/ConfigManagementShowcase

using Smplkit;
using Smplkit.Examples.Setup;
using Smplkit.Flags;

// create the client
using var mgmt = new SmplManagementClient();
await ConfigManagementSetup.SetupManagementShowcaseAsync(mgmt);

// create a "parent" configuration that all other configs inherit from
var shared = mgmt.Config.New(
    "showcase-common",
    name: "Showcase Common",
    description: "Showcase-only shared configuration.");
shared.SetString("app_name", "Acme SaaS Platform");
shared.SetString("support_email", "support@acme.dev");
shared.SetNumber("max_retries", 3);
shared.SetNumber("request_timeout_ms", 5000);
shared.SetNumber("pagination_default_page_size", 25);
shared.SetNumber("max_retries", 5, environment: "production");
shared.SetNumber("request_timeout_ms", 10000, environment: "production");
await shared.SaveAsync();
Console.WriteLine($"Created config: {shared.Id}");

// create a config (inherits from showcase-common)
var userService = mgmt.Config.New(
    "showcase-user-service",
    name: "Showcase User Service",
    description: "Configuration for the user microservice.",
    parent: shared);
userService.SetString("database.host", "localhost");
userService.SetNumber("database.port", 5432);
userService.SetString("database.name", "users_dev");
userService.SetNumber("database.pool_size", 5);
userService.SetNumber("cache_ttl_seconds", 300);
userService.SetBoolean("enable_signup", true);
userService.SetNumber("pagination_default_page_size", 50);
await userService.SaveAsync();

// update a config
userService.SetString("database.host", "prod-users-rds.internal.acme.dev",
    environment: "production");
userService.SetString("database.name", "users_prod", environment: "production");
userService.SetNumber("database.pool_size", 20, environment: "production");
userService.SetNumber("cache_ttl_seconds", 600, environment: "production");
userService.SetBoolean("enable_signup", false, environment: "production");
await userService.SaveAsync();
Console.WriteLine($"Updated config: {userService.Id}");

// list configs
var configs = await mgmt.Config.ListAsync();
foreach (var cfg in configs)
{
    var parentInfo = cfg.Parent is not null ? $" (parent: {cfg.Parent})" : " (root)";
    Console.WriteLine($"  {cfg.Id}{parentInfo}");
}

// get a config
var fetched = await mgmt.Config.GetAsync("showcase-user-service");
Console.WriteLine($"Fetched: id={fetched.Id}, name={fetched.Name}");
Console.WriteLine($"  description={fetched.Description}");
Console.WriteLine($"  parent={fetched.Parent ?? "(none)"}");
Console.WriteLine($"  items: [{string.Join(", ", fetched.Items.Keys)}]");

// delete configs
await userService.DeleteAsync();
await shared.DeleteAsync();
Console.WriteLine("Deleted configs");

// cleanup
await ConfigManagementSetup.CleanupManagementShowcaseAsync(mgmt);
Console.WriteLine("Done!");
