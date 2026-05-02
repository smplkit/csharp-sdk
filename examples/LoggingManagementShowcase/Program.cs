// Demonstrates the smplkit management SDK for Smpl Logging.
//
// Prerequisites:
//     - dotnet add package Smplkit.Sdk
//     - A valid smplkit API key
//
// Usage:
//     dotnet run --project examples/LoggingManagementShowcase

using Smplkit;
using Smplkit.Examples.Setup;

// create the client
using var mgmt = new SmplManagementClient();
await LoggingManagementSetup.SetupManagementShowcaseAsync(mgmt);

// create a parent logger with a default level
var root = mgmt.Loggers.New("showcase");
root.SetLevel(LogLevel.Info);
await root.SaveAsync();
Console.WriteLine($"Created: {root.Id} (level={root.Level})");
System.Diagnostics.Debug.Assert(root.Level == LogLevel.Info);

// child logger with no level (inherits from parent)
var db = mgmt.Loggers.New("showcase.db");
await db.SaveAsync();
Console.WriteLine($"Created: {db.Id} (inherits)");
System.Diagnostics.Debug.Assert(db.Level is null);

// child logger with explicit level (overrides parent)
var payments = mgmt.Loggers.New("showcase.payments");
payments.SetLevel(LogLevel.Warn);
await payments.SaveAsync();
Console.WriteLine($"Created: {payments.Id} (level={payments.Level})");
System.Diagnostics.Debug.Assert(payments.Level == LogLevel.Warn);

// override log level for different environments
root.SetLevel(LogLevel.Error, environment: "production");
root.SetLevel(LogLevel.Debug, environment: "staging");
await root.SaveAsync();
Console.WriteLine($"Set environment overrides: {root.Environments.Count} environments");
System.Diagnostics.Debug.Assert(root.Environments.ContainsKey("production"));
System.Diagnostics.Debug.Assert(root.Environments.ContainsKey("staging"));

// clear environment override (inherits from the default level again)
root.ClearLevel(environment: "staging");
await root.SaveAsync();
Console.WriteLine($"Cleared staging override: {root.Environments.Count} environments");
System.Diagnostics.Debug.Assert(!root.Environments.ContainsKey("staging"));

// fetch a logger by id
var fetched = await mgmt.Loggers.GetAsync("showcase");
System.Diagnostics.Debug.Assert(fetched.Level == LogLevel.Info);

await LoggingManagementSetup.CleanupManagementShowcaseAsync(mgmt);
Console.WriteLine("Done!");
