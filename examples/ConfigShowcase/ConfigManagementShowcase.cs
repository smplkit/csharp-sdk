using Smplkit;
using Smplkit.Config;

namespace ConfigShowcase;

/// <summary>
/// Smpl Config SDK — Management API Showcase
/// ============================================
///
/// Demonstrates the management plane of the smplkit Config C# SDK:
///
///   1. Update the common config with base items and environment overrides
///   2. Create user_service config with environment overrides
///   3. Create auth_module as a child config (inheritance)
///   4. List and inspect configs
///   5. Update a config
///   6. Cleanup
///
/// This script creates, modifies, and deletes real configs.
///
/// Prerequisites:
///     - .NET 8.0 SDK
///     - A valid smplkit API key, provided via SMPLKIT_API_KEY env var or ~/.smplkit config file
///
/// Usage:
///     dotnet run --project examples/ConfigShowcase management
/// </summary>
public static class ConfigManagementShowcase
{
    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 60));
        Console.WriteLine($"  {title}");
        Console.WriteLine(new string('=', 60));
        Console.WriteLine();
    }

    private static void Step(string description)
    {
        Console.WriteLine($"  -> {description}");
    }

    // ------------------------------------------------------------------
    // Entry point
    // ------------------------------------------------------------------

    public static async Task<int> RunAsync(SmplClient client)
    {
        // ==============================================================
        // 1. UPDATE THE COMMON CONFIG
        // ==============================================================
        Section("1. Update the Common Config");

        var common = await client.Config.GetAsync("common");
        Step($"Fetched common config: id={common.Id}, key={common.Key}");

        // Mutate items directly + SaveAsync
        common.Name = "Common";
        common.Description = "Organization-wide shared configuration";
        common.Items = new Dictionary<string, object?>
        {
            ["app_name"] = "Acme SaaS Platform",
            ["support_email"] = "support@acme.dev",
            ["max_retries"] = 3,
            ["request_timeout_ms"] = 5000,
            ["log_level"] = "info",
        };
        common.Environments = new Dictionary<string, Dictionary<string, object?>>();
        await common.SaveAsync();
        Step("Common config base values set");

        // Add production overrides
        common.Environments["production"] = new Dictionary<string, object?>
        {
            ["max_retries"] = 5,
            ["request_timeout_ms"] = 10000,
            ["log_level"] = "warn",
        };
        await common.SaveAsync();
        Step("Common config production overrides set");

        // ==============================================================
        // 2. CREATE USER SERVICE CONFIG
        // ==============================================================
        Section("2. Create User Service Config");

        var userService = client.Config.New(
            key: "user_service",
            name: "User Service",
            description: "Configuration for the user management service");
        userService.Items = new Dictionary<string, object?>
        {
            ["cache_ttl_seconds"] = 300,
            ["enable_signup"] = true,
            ["pagination_default_page_size"] = 50,
            ["password_min_length"] = 8,
        };
        await userService.SaveAsync();
        Step($"Created user_service config: id={userService.Id}");

        // Add production overrides
        userService.Environments["production"] = new Dictionary<string, object?>
        {
            ["cache_ttl_seconds"] = 600,
            ["enable_signup"] = false,
            ["password_min_length"] = 12,
        };
        await userService.SaveAsync();
        Step("User service production overrides set");

        // ==============================================================
        // 3. CREATE AUTH MODULE (CHILD CONFIG)
        // ==============================================================
        Section("3. Create Auth Module (Child of User Service)");

        var authModule = client.Config.New(
            key: "auth_module",
            name: "Auth Module",
            description: "Authentication module config — inherits from user_service",
            parent: userService.Id);
        authModule.Items = new Dictionary<string, object?>
        {
            ["token_ttl_seconds"] = 3600,
            ["mfa_enabled"] = false,
            ["session_max_age_hours"] = 24,
            ["allowed_origins"] = "*",
        };
        await authModule.SaveAsync();
        Step($"Created auth_module config: id={authModule.Id}, parent={authModule.Parent}");

        // Add production overrides
        authModule.Environments["production"] = new Dictionary<string, object?>
        {
            ["mfa_enabled"] = true,
            ["session_max_age_hours"] = 8,
            ["allowed_origins"] = "https://app.acme.dev",
        };
        await authModule.SaveAsync();
        Step("Auth module production overrides set");

        // ==============================================================
        // 4. LIST AND INSPECT CONFIGS
        // ==============================================================
        Section("4. List and Inspect Configs");

        var allConfigs = await client.Config.ListAsync();
        Step($"Total configs: {allConfigs.Count}");

        foreach (var c in allConfigs)
        {
            var parentLabel = c.Parent != null ? $", parent={c.Parent}" : "";
            Console.WriteLine($"     - {c.Key} (id={c.Id}{parentLabel})");
        }

        // Fetch a single config by key to show the full payload
        var fetched = await client.Config.GetAsync("user_service");
        Step($"Fetched user_service by key: key={fetched.Key}");
        Step($"  Description: {fetched.Description}");
        Step($"  Items: [{string.Join(", ", fetched.Items.Keys)}]");
        Step($"  Environments: [{string.Join(", ", fetched.Environments.Keys)}]");

        // ==============================================================
        // 5. UPDATE A CONFIG
        // ==============================================================
        Section("5. Update a Config");

        // Mutate properties directly + SaveAsync
        userService.Description = "User management service config — updated via SDK";
        await userService.SaveAsync();
        Step($"user_service description updated: \"{userService.Description}\"");

        // Update a single item in an environment override
        common.Environments["production"]["max_retries"] = 7;
        await common.SaveAsync();
        Step("common/max_retries updated to 7 in production");

        // ==============================================================
        // 6. CLEANUP
        // ==============================================================
        Section("6. Cleanup");

        // Delete child first, then parent
        await client.Config.DeleteAsync("auth_module");
        Step("Deleted auth_module");

        await client.Config.DeleteAsync("user_service");
        Step("Deleted user_service");

        // Reset common to empty (it's a built-in, not deletable)
        common.Items = new Dictionary<string, object?>();
        common.Environments = new Dictionary<string, Dictionary<string, object?>>();
        await common.SaveAsync();
        Step("Common config reset to empty");

        // ==============================================================
        // DONE
        // ==============================================================
        Section("ALL DONE");
        Console.WriteLine("  The Config Management showcase completed successfully.");
        Console.WriteLine("  If you got here, Smpl Config management is ready to ship.\n");

        return 0;
    }
}
