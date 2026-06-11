// Demonstrates the smplkit runtime SDK for Smpl Config.
//
// Prerequisites:
//   - dotnet add package Smplkit.Sdk
//   - A valid smplkit API key, provided via one of:
//       - SMPLKIT_API_KEY environment variable
//       - ~/.smplkit configuration file (see SDK docs)
//
// Usage:
//   dotnet run --project examples/ConfigRuntimeShowcase

using System.Diagnostics;
using Smplkit;
using Smplkit.Config;
using Smplkit.Examples.Setup;

// create the client
using var client = new SmplClient(new SmplClientOptions
{
    Environment = "production",
    Service = "showcase-billing",
});
try
{
    await ConfigRuntimeSetup.CleanupRuntimeShowcaseAsync(client);

    // bind POCOs
    var common = client.Config.Bind("showcase-common", new Common());
    var billing = client.Config.Bind(
        "showcase-billing",
        new Billing { Plan = new Plan { MaxSeats = 5, TrialDays = 14, Tier = "free" } },
        parent: common);

    Console.WriteLine($"common.App.Name = {common.App.Name}");
    Console.WriteLine($"billing.App.Name = {billing.App.Name}  // inherited from common");
    Console.WriteLine($"billing.Plan.MaxSeats = {billing.Plan.MaxSeats}");
    Debug.Assert(common.App.Name == "Acme SaaS");
    Debug.Assert(billing.Plan.MaxSeats == 5);

    // add listeners if desired
    var changes = new List<ConfigChangeEvent>();
    client.Config.OnChange("showcase-billing", "plan.max_seats", evt =>
    {
        changes.Add(evt);
        Console.WriteLine(
            $"    [CHANGE] {evt.ConfigId}.{evt.ItemKey}: {evt.OldValue} -> {evt.NewValue}");
    });

    await client.WaitUntilReadyAsync();

    // simulate someone making a change in the smplkit console
    await ConfigRuntimeSetup.SimulateAdminOverrideAsync(client);
    await Task.Delay(400);

    // observe changes are automatically reflected in bound objects
    Console.WriteLine($"billing.Plan.MaxSeats after override = {billing.Plan.MaxSeats}");
    Debug.Assert(billing.Plan.MaxSeats == 25, $"Expected 25, got {billing.Plan.MaxSeats}");
    Debug.Assert(changes.Count >= 1);

    // you can also bind plain-old dictionaries
    var db = client.Config.Bind("showcase-database", new Dictionary<string, object?>
    {
        ["primary"] = new Dictionary<string, object?>
        {
            ["host"] = "db.acme.example",
            ["port"] = 5432,
        },
        ["pool_size"] = 10,
        ["statement_timeout_ms"] = 30000,
    });
    var primary = (IDictionary<string, object?>)db["primary"]!;
    Console.WriteLine($"db[\"primary\"][\"host\"] = {primary["host"]}");
    Console.WriteLine($"db[\"pool_size\"] = {db["pool_size"]}");
    Debug.Assert((string)primary["host"]! == "db.acme.example");
    Debug.Assert((int)db["pool_size"]! == 10);

    // or read live values via Subscribe(id)
    var commonView = client.Config.Subscribe("showcase-common");
    Console.WriteLine("showcase-common (via Subscribe):");
    foreach (var (k, v) in commonView)
        Console.WriteLine($"    {k} = {v}");
    Debug.Assert((string)commonView["app.name"]! == "Acme SaaS");

    // or skip the POCO/dict and just fetch specific keys directly
    var slowQueryMs = client.Config.GetValueOr(
        "showcase-database", "slow_query_threshold_ms", 500);
    Console.WriteLine(
        $"showcase-database.slow_query_threshold_ms = {slowQueryMs}  " +
        "// default used; now registered for visibility");
    Debug.Assert(slowQueryMs == 500);

    Console.WriteLine("Done!");
}
finally
{
    await ConfigRuntimeSetup.CleanupRuntimeShowcaseAsync(client);
}

// Example POCO configuration classes to showcase how "code-first"
// configuration management works

public class App
{
    public string Name { get; set; } = "Acme SaaS";
}

public class Support
{
    public string Email { get; set; } = "support@acme.dev";
}

public class Plan
{
    public int MaxSeats { get; set; } = 5;
    public int TrialDays { get; set; } = 14;
    public string Tier { get; set; } = "free";
}

public class Common
{
    public App App { get; set; } = new();
    public Support Support { get; set; } = new();
}

public class Billing
{
    public App App { get; set; } = new();
    public Support Support { get; set; } = new();
    public Plan Plan { get; set; } = new();
}
