// Demonstrates the smplkit runtime SDK for Smpl Config.
//
// Prerequisites:
//     - dotnet add package Smplkit.Sdk
//     - A valid smplkit API key
//     - The smplkit Config service running and reachable
//
// Usage:
//     dotnet run --project examples/ConfigRuntimeShowcase

using Smplkit;
using Smplkit.Examples.Setup;

// create the client
using var client = new SmplClient(new SmplClientOptions
{
    Environment = "production",
    Service = "showcase-service",
});
await ConfigRuntimeSetup.SetupRuntimeShowcaseAsync(client.Manage);
await client.WaitUntilReadyAsync();

// get a config as a plain dict
var userSvcConfigDict = client.Config.Get("showcase-user-service");
Console.WriteLine($"Total resolved keys: {userSvcConfigDict.Count}");
Console.WriteLine($"database.host = {userSvcConfigDict.GetOrDefault("database.host")}");
Console.WriteLine($"max_retries = {userSvcConfigDict.GetOrDefault("max_retries")}");
Console.WriteLine($"cache_ttl_seconds = {userSvcConfigDict.GetOrDefault("cache_ttl_seconds")}");
Console.WriteLine($"pagination_default_page_size = {userSvcConfigDict.GetOrDefault("pagination_default_page_size")}");
Console.WriteLine($"enable_signup = {userSvcConfigDict.GetOrDefault("enable_signup")}");
Console.WriteLine($"nonexistent_key = {userSvcConfigDict.GetOrDefault("nonexistent_key")}");

// production overrides resolve through the inheritance chain
System.Diagnostics.Debug.Assert((string?)userSvcConfigDict.GetOrDefault("database.host")
    == "prod-users-rds.internal.acme.dev");
System.Diagnostics.Debug.Assert(userSvcConfigDict.GetOrDefault("nonexistent_key") is null);

// get a config as a typed model
var userSvcConfig = client.Config.Get<UserServiceConfig>("showcase-user-service");
Console.WriteLine($"cfg.database.host = {userSvcConfig.Database.Host}");
Console.WriteLine($"cfg.database.pool_size = {userSvcConfig.Database.PoolSize}");
Console.WriteLine($"cfg.cache_ttl_seconds = {userSvcConfig.CacheTtlSeconds}");
Console.WriteLine($"cfg.enable_signup = {userSvcConfig.EnableSignup}");
Console.WriteLine($"cfg.max_retries = {userSvcConfig.MaxRetries}");
Console.WriteLine($"cfg.app_name = {userSvcConfig.AppName}");

System.Diagnostics.Debug.Assert(userSvcConfig.MaxRetries == 5);
System.Diagnostics.Debug.Assert(userSvcConfig.AppName == "Acme SaaS Platform");

var changes = new List<Smplkit.Config.ConfigChangeEvent>();
var retriesChanges = new List<Smplkit.Config.ConfigChangeEvent>();

// global listener — fires when ANY config item changes
client.Config.OnChange(evt =>
{
    changes.Add(evt);
    Console.WriteLine($"    [CHANGE] {evt.ConfigId}.{evt.ItemKey}: {evt.OldValue} -> {evt.NewValue}");
});

// item-scoped listener
client.Config.OnChange("showcase-common", "max_retries", evt => retriesChanges.Add(evt));

// simulate someone making a change to trigger listeners
await UpdateMaxRetriesAsync(client, 7);

// wait a moment for the event to be delivered (typical WS round-trip
// is well under 200ms; 400ms is plenty of headroom and anything past
// that is a real signal, not noise to absorb).
await Task.Delay(400);

Console.WriteLine($"Global changes received: {changes.Count}");
Console.WriteLine($"Retries-specific changes received: {retriesChanges.Count}");

System.Diagnostics.Debug.Assert(changes.Count >= 1);
System.Diagnostics.Debug.Assert(retriesChanges.Count >= 1);

await ConfigRuntimeSetup.CleanupRuntimeShowcaseAsync(client.Manage);
Console.WriteLine("Done!");

static async Task UpdateMaxRetriesAsync(SmplClient client, int maxRetries)
{
    var commonCfg = await client.Manage.Config.GetAsync("showcase-common");
    commonCfg.SetNumber("max_retries", maxRetries, environment: "production");
    await commonCfg.SaveAsync();
}

// Typed config model used with client.Config.Get<T>(...)
public sealed record Database(string Host, int Port, string Name, int PoolSize);

public sealed record UserServiceConfig(
    string AppName,
    string SupportEmail,
    int MaxRetries,
    int RequestTimeoutMs,
    Database Database,
    int CacheTtlSeconds,
    bool EnableSignup,
    int PaginationDefaultPageSize)
{
    public UserServiceConfig() : this(
        AppName: "",
        SupportEmail: "",
        MaxRetries: 0,
        RequestTimeoutMs: 0,
        Database: new Database("", 0, "", 0),
        CacheTtlSeconds: 0,
        EnableSignup: false,
        PaginationDefaultPageSize: 0)
    { }
}
