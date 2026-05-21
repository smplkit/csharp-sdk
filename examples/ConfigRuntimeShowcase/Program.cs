// Demonstrates the smplkit runtime SDK for Smpl Config.
//
// Prerequisites:
//     - dotnet add package Smplkit.Sdk
//     - A valid smplkit API key
//
// Usage:
//     dotnet run --project examples/ConfigRuntimeShowcase

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
    await ConfigRuntimeSetup.SetupRuntimeShowcaseAsync(client.Manage);

    // declare a common/shared configuration
    var common = client.Config.GetOrCreate(
        "showcase-common",
        description: "Shared defaults for showcase services.");

    // declare a configuration that inherits from some parent
    var billing = client.Config.GetOrCreate(
        "showcase-billing",
        parent: common,
        description: "Plan-limit configuration discovered from code.");

    // get a configured value
    var appName = common.GetString("app.name", "Acme SaaS");
    var supportEmail = common.GetString("support.email", "support@acme.dev");
    var maxSeats = billing.GetInt("plan.max_seats", 5, description: "Maximum seats per organization.");
    var trialDays = billing.GetInt("plan.trial_days", 14);
    var tier = billing.GetString("plan.tier", "free");

    Console.WriteLine($"app.name = {appName}");
    Console.WriteLine($"support.email = {supportEmail}");
    Console.WriteLine($"plan.max_seats = {maxSeats}");
    Console.WriteLine($"plan.trial_days = {trialDays}");
    Console.WriteLine($"plan.tier = {tier}");

    // listen for changes
    var changes = new List<ConfigChangeEvent>();
    billing.OnChange("plan.max_seats", evt =>
    {
        changes.Add(evt);
        Console.WriteLine($"    [CHANGE] {evt.ConfigId}.{evt.ItemKey}: {evt.OldValue} -> {evt.NewValue}");
    });

    // simulate someone overriding a value in the console
    await ConfigRuntimeSetup.SimulateAdminOverrideAsync(client.Manage);

    // wait for the WebSocket push to deliver the change, then refetch
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
    while (DateTime.UtcNow < deadline && billing.GetInt("plan.max_seats", 5) != 25)
        await Task.Delay(100);

    // get the latest value
    var updatedSeats = billing.GetInt("plan.max_seats", 5);
    Console.WriteLine($"plan.max_seats after override = {updatedSeats}");
    System.Diagnostics.Debug.Assert(updatedSeats == 25, $"Expected 25, got {updatedSeats}");
    System.Diagnostics.Debug.Assert(changes.Count >= 1, "Expected at least one change event");
}
finally
{
    await ConfigRuntimeSetup.CleanupRuntimeShowcaseAsync(client.Manage);
}
Console.WriteLine("Done!");
