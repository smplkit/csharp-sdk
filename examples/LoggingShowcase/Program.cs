/// <summary>
/// Smpl Logging SDK Showcase
/// ==========================
///
/// Demonstrates the smplkit C# SDK for Smpl Logging, covering:
///
/// - Management API: create, update, list, and delete loggers and groups
/// - Runtime: start the logging runtime, register change listeners
///
/// Usage:
///     dotnet run --project examples/LoggingShowcase           # runtime showcase (default)
///     dotnet run --project examples/LoggingShowcase runtime    # runtime showcase
///     dotnet run --project examples/LoggingShowcase management # management showcase
/// </summary>

if (args.Length > 0 && args[0] == "management")
{
    await LoggingManagementShowcase.RunAsync();
}
else
{
    await LoggingRuntimeShowcase.RunAsync();
}
