// Demonstrates the smplkit runtime SDK for Smpl Logging.
//
// Prerequisites:
//     - dotnet add package Smplkit.Sdk
//     - dotnet add package Microsoft.Extensions.Logging
//     - A valid smplkit API key
//
// Usage:
//     dotnet run --project examples/LoggingRuntimeShowcase

using Microsoft.Extensions.Logging;
using Smplkit;
using Smplkit.Logging.Adapters;

// Create the smplkit client.
using var client = new SmplClient(new SmplClientOptions
{
    Environment = "production",
    Service = "showcase-service",
});

// Wire smplkit into your logging pipeline. AddSmplkit registers the adapter as
// an ILoggerProvider (so every CreateLogger call is observable for auto-discovery)
// AND as an IConfigureOptions<LoggerFilterOptions> + IOptionsChangeTokenSource<...>
// (so server-managed levels gate output to every other registered provider —
// Console here, but the same applies to Debug, file sinks, Serilog-via-MEL, etc.).
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.AddSmplkit(client);
});

// Bulk-register all discovered logger categories with the server and apply
// current managed levels.
await client.Logging.InstallAsync();
Console.WriteLine("All loggers are now controlled by smplkit");

// Any logger created after this point is auto-discovered and level-managed.
var dbLogger = loggerFactory.CreateLogger("MyApp.Db");
dbLogger.LogInformation("Hello — server-managed level controls whether this is emitted");
