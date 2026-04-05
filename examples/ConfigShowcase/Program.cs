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
///     dotnet run --project examples/ConfigShowcase           # runtime showcase (default)
///     dotnet run --project examples/ConfigShowcase runtime    # runtime showcase
///     dotnet run --project examples/ConfigShowcase management # management showcase
/// </summary>

using Smplkit;
using ConfigShowcase;

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------

// The SmplClient constructor resolves three required parameters:
//
//   ApiKey       — not passed here; resolved automatically from the
//                  SMPLKIT_API_KEY environment variable or the
//                  ~/.smplkit configuration file.
//
//   Environment  — the target environment. Can also be resolved from
//                  SMPLKIT_ENVIRONMENT if not passed.
//
//   Service      — identifies this SDK instance. Can also be resolved
//                  from SMPLKIT_SERVICE if not passed.
//
// To pass the API key explicitly:
//
//   var client = new SmplClient(new SmplClientOptions
//   {
//       ApiKey = "sk_api_...",
//       Environment = "production",
//       Service = "showcase-service",
//   });
//
using var client = new SmplClient(new SmplClientOptions
{
    Environment = "production",
    Service = "showcase-service",
});

if (args.Length > 0 && args[0] == "management")
{
    return await ConfigManagementShowcase.RunAsync(client);
}
else
{
    return await ConfigRuntimeShowcase.RunAsync(client);
}
