/// <summary>
/// Smpl Flags SDK Showcase
/// ========================
///
/// Demonstrates the smplkit C# SDK for Smpl Flags, covering:
///
/// - Management API: create, update, list, and delete flags and context types
/// - Runtime evaluation: typed handles, context providers, caching, real-time updates
///
/// Usage:
///     export SMPLKIT_API_KEY="sk_api_..."
///     dotnet run --project examples/FlagsShowcase           # runtime showcase (default)
///     dotnet run --project examples/FlagsShowcase runtime    # runtime showcase
///     dotnet run --project examples/FlagsShowcase management # management showcase
/// </summary>

using Smplkit;
using FlagsShowcase;

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

using var client = new SmplClient(new SmplClientOptions { ApiKey = apiKey });

if (args.Length > 0 && args[0] == "management")
{
    return await FlagsManagementShowcase.RunAsync(client);
}
else
{
    return await FlagsRuntimeShowcase.RunAsync(client);
}
