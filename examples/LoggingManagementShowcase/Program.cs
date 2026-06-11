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
using var client = new SmplClient(new SmplClientOptions { Service = "showcase-service" });
await LoggingManagementSetup.SetupManagementShowcaseAsync(client);

// create a parent logger with a default level
var root = client.Logging.Loggers.New("showcase");
root.SetLevel(LogLevel.Info);
await root.SaveAsync();
Console.WriteLine($"Created: {root.Id} (level={root.Level})");
System.Diagnostics.Debug.Assert(root.Level == LogLevel.Info);

// child logger with no level (inherits from parent)
var db = client.Logging.Loggers.New("showcase.db");
await db.SaveAsync();
Console.WriteLine($"Created: {db.Id} (inherits)");
System.Diagnostics.Debug.Assert(db.Level is null);

// child logger with explicit level (overrides parent)
var payments = client.Logging.Loggers.New("showcase.payments");
payments.SetLevel(LogLevel.Warn);
await payments.SaveAsync();
Console.WriteLine($"Created: {payments.Id} (level={payments.Level})");
System.Diagnostics.Debug.Assert(payments.Level == LogLevel.Warn);

// override log level for the production environment
root.SetLevel(LogLevel.Error, environment: "production");
await root.SaveAsync();
Console.WriteLine($"Set environment overrides: {root.Environments.Count} environments");
System.Diagnostics.Debug.Assert(root.Environments.ContainsKey("production"));

// clear environment override (inherits from the default level again)
root.ClearLevel(environment: "production");
await root.SaveAsync();
Console.WriteLine($"Cleared production override: {root.Environments.Count} environments");
System.Diagnostics.Debug.Assert(!root.Environments.ContainsKey("production"));

// fetch a logger by id
var fetched = await client.Logging.Loggers.GetAsync("showcase");
System.Diagnostics.Debug.Assert(fetched.Level == LogLevel.Info);

await LoggingManagementSetup.CleanupManagementShowcaseAsync(client);
Console.WriteLine("Done!");
