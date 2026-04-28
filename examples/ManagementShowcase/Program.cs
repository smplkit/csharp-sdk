/// <summary>
/// Smpl SDK Management Showcase
/// ==============================
///
/// Demonstrates <c>client.Management.*</c> — the management plane for
/// app-service-owned resources that aren't tied to a specific microservice:
/// environments, context types, contexts, and per-account settings.
///
/// For runtime experiences (resolving configs, evaluating flags, controlling
/// log levels) see the per-service runtime showcases.
///
/// Prerequisites:
///     - .NET 8.0 SDK
///     - A valid smplkit API key (SMPLKIT_API_KEY env var or ~/.smplkit config file)
///     - The smplkit app service running and reachable
///
/// Usage:
///     dotnet run --project examples/ManagementShowcase
/// </summary>

using Smplkit;

using var client = new SmplClient(new SmplClientOptions
{
    Environment = "production",
    Service = "management-showcase",
});

return await ManagementShowcase.RunAsync(client);
