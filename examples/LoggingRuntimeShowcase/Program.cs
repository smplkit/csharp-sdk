// Demonstrates the smplkit runtime SDK for Smpl Logging.
//
// Prerequisites:
//     - dotnet add package Smplkit.Sdk
//     - A valid smplkit API key
//
// Usage:
//     dotnet run --project examples/LoggingRuntimeShowcase

using Smplkit;

// create the client
using var client = new SmplClient(new SmplClientOptions
{
    Environment = "production",
    Service = "showcase-service",
});
await client.Logging.InstallAsync();
Console.WriteLine("All loggers are now controlled by smplkit");
