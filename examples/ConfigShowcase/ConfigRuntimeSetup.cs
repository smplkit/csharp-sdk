using Smplkit;
using Smplkit.Config;

namespace ConfigShowcase;

/// <summary>
/// Helper that creates and tears down demo configs for the runtime showcase.
///
/// Creates three configs with a parent-child hierarchy:
///   - common         (root)    — organisation-wide shared configuration
///   - user_service   (root)    — per-service config with production overrides
///   - auth_module    (child)   — inherits from user_service
///
/// Each config gets environment-specific overrides.
/// </summary>
public static class ConfigRuntimeSetup
{
    public record DemoConfigs(Config Common, Config UserService, Config AuthModule);

    public static async Task<DemoConfigs> SetupDemoConfigsAsync(SmplClient client)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 60));
        Console.WriteLine("  Demo Config Setup");
        Console.WriteLine(new string('=', 60));
        Console.WriteLine();

        // Pre-cleanup: delete any configs left over from a previous run.
        // Children must be deleted before parents.
        foreach (var id in new[] { "auth_module", "user_service" })
        {
            try { await client.Config.Management.DeleteAsync(id); Console.WriteLine($"  Pre-cleanup: deleted leftover config {id}"); }
            catch { /* not present — ignore */ }
        }

        // ------------------------------------------------------------------
        // 1. common — organisation-wide shared configuration
        // ------------------------------------------------------------------
        Console.WriteLine("  -> Setting up common config...");

        var common = await client.Config.Management.GetAsync("common");

        // Mutate properties directly + SaveAsync
        common.Name = "Common";
        common.Description = "Organization-wide shared configuration";
        common.Items = new Dictionary<string, object?>
        {
            ["app_name"] = "Acme SaaS Platform",
            ["support_email"] = "support@acme.dev",
            ["max_retries"] = 3,
            ["request_timeout_ms"] = 5000,
        };
        common.Environments = new Dictionary<string, Dictionary<string, object?>>();
        await common.SaveAsync();

        // Add production overrides
        common.Environments["production"] = new Dictionary<string, object?>
        {
            ["max_retries"] = 5,
            ["request_timeout_ms"] = 10000,
        };
        await common.SaveAsync();

        Console.WriteLine($"     Updated: id={common.Id}");

        // ------------------------------------------------------------------
        // 2. user_service — per-service config with production overrides
        // ------------------------------------------------------------------
        Console.WriteLine("  -> Creating user_service config...");

        var userService = client.Config.Management.New(
            id: "user_service",
            name: "User Service");
        userService.Items = new Dictionary<string, object?>
        {
            ["cache_ttl_seconds"] = 300,
            ["enable_signup"] = true,
            ["pagination_default_page_size"] = 50,
        };
        await userService.SaveAsync();

        // Add production overrides
        userService.Environments["production"] = new Dictionary<string, object?>
        {
            ["cache_ttl_seconds"] = 600,
            ["enable_signup"] = false,
        };
        await userService.SaveAsync();

        Console.WriteLine($"     Created: id={userService.Id}");

        // ------------------------------------------------------------------
        // 3. auth_module — child config inheriting from user_service
        // ------------------------------------------------------------------
        Console.WriteLine("  -> Creating auth_module config (child of user_service)...");

        var authModule = client.Config.Management.New(
            id: "auth_module",
            name: "Auth Module",
            parent: userService.Id);
        authModule.Items = new Dictionary<string, object?>
        {
            ["token_ttl_seconds"] = 3600,
            ["mfa_enabled"] = false,
            ["session_max_age_hours"] = 24,
        };
        await authModule.SaveAsync();

        // Add production overrides
        authModule.Environments["production"] = new Dictionary<string, object?>
        {
            ["mfa_enabled"] = true,
            ["session_max_age_hours"] = 8,
        };
        await authModule.SaveAsync();

        Console.WriteLine($"     Created: id={authModule.Id}, parent={authModule.Parent}");

        Console.WriteLine();
        Console.WriteLine("  Demo configs created successfully.");
        return new DemoConfigs(common, userService, authModule);
    }

    public static async Task TeardownDemoConfigsAsync(SmplClient client, DemoConfigs configs)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 60));
        Console.WriteLine("  Demo Config Teardown");
        Console.WriteLine(new string('=', 60));
        Console.WriteLine();

        // Delete child first (auth_module), then parent (user_service)
        await client.Config.Management.DeleteAsync("auth_module");
        Console.WriteLine("  -> Deleted auth_module");

        await client.Config.Management.DeleteAsync("user_service");
        Console.WriteLine("  -> Deleted user_service");

        // Reset common to empty (it's a built-in, not deletable)
        configs.Common.Items = new Dictionary<string, object?>();
        configs.Common.Environments = new Dictionary<string, Dictionary<string, object?>>();
        await configs.Common.SaveAsync();
        Console.WriteLine($"  -> Common config reset to empty ({configs.Common.Id})");

        Console.WriteLine("  Teardown complete.");
    }
}
