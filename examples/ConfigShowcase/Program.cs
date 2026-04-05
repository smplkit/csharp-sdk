/// <summary>
/// Smpl Config SDK Showcase
/// ========================
///
/// Demonstrates the smplkit C# SDK for Smpl Config, covering:
///
/// - Runtime access: prescriptive typed accessors, inheritance, change listeners
/// - Management API: create, update, list, and delete configs with environment overrides
///
/// Usage:
///     export SMPLKIT_API_KEY="sk_api_..."
///     dotnet run --project examples/ConfigShowcase           # runtime showcase (default)
///     dotnet run --project examples/ConfigShowcase runtime    # runtime showcase
///     dotnet run --project examples/ConfigShowcase management # management showcase
/// </summary>

using Smplkit;
using ConfigShowcase;

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------

var apiKey = Environment.GetEnvironmentVariable("SMPLKIT_API_KEY") ?? "";

if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.WriteLine("ERROR: Set the SMPLKIT_API_KEY environment variable before running.");
    Console.WriteLine("  export SMPLKIT_API_KEY='sk_api_...'");
    return 1;
}

// ---------------------------------------------------------------------------
// Dispatch
// ---------------------------------------------------------------------------

using var client = new SmplClient(new SmplClientOptions { ApiKey = apiKey, Environment = "production" });

if (args.Length > 0 && args[0] == "management")
{
    return await ConfigManagementShowcase.RunAsync(client);
}
else
{
    return await ConfigRuntimeShowcase.RunAsync(client);
}
