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

        // ------------------------------------------------------------------
        // 1. common — organisation-wide shared configuration
        // ------------------------------------------------------------------
        Console.WriteLine("  -> Setting up common config...");

        var common = await client.Config.GetByKeyAsync("common");

        common = await client.Config.UpdateAsync(common.Id, new CreateConfigOptions
        {
            Name = "Common",
            Description = "Organization-wide shared configuration",
            Items = new Dictionary<string, object?>
            {
                ["app_name"] = "Acme SaaS Platform",
                ["support_email"] = "support@acme.dev",
                ["max_retries"] = 3,
                ["request_timeout_ms"] = 5000,
            },
            Environments = new Dictionary<string, object?>(),
        });

        common = await client.Config.SetValuesAsync(common.Id,
            new Dictionary<string, object?>
            {
                ["max_retries"] = 5,
                ["request_timeout_ms"] = 10000,
            },
            environment: "production");

        Console.WriteLine($"     Updated: id={common.Id}, key={common.Key}");

        // ------------------------------------------------------------------
        // 2. user_service — per-service config with production overrides
        // ------------------------------------------------------------------
        Console.WriteLine("  -> Creating user_service config...");

        var userService = await client.Config.CreateAsync(new CreateConfigOptions
        {
            Name = "User Service",
            Key = "user_service",
            Items = new Dictionary<string, object?>
            {
                ["cache_ttl_seconds"] = 300,
                ["enable_signup"] = true,
                ["pagination_default_page_size"] = 50,
            },
        });

        userService = await client.Config.SetValuesAsync(userService.Id,
            new Dictionary<string, object?>
            {
                ["cache_ttl_seconds"] = 600,
                ["enable_signup"] = false,
            },
            environment: "production");

        Console.WriteLine($"     Created: id={userService.Id}");

        // ------------------------------------------------------------------
        // 3. auth_module — child config inheriting from user_service
        // ------------------------------------------------------------------
        Console.WriteLine("  -> Creating auth_module config (child of user_service)...");

        var authModule = await client.Config.CreateAsync(new CreateConfigOptions
        {
            Name = "Auth Module",
            Key = "auth_module",
            Parent = userService.Id,
            Items = new Dictionary<string, object?>
            {
                ["token_ttl_seconds"] = 3600,
                ["mfa_enabled"] = false,
                ["session_max_age_hours"] = 24,
            },
        });

        authModule = await client.Config.SetValuesAsync(authModule.Id,
            new Dictionary<string, object?>
            {
                ["mfa_enabled"] = true,
                ["session_max_age_hours"] = 8,
            },
            environment: "production");

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
        await client.Config.DeleteAsync(configs.AuthModule.Id);
        Console.WriteLine($"  -> Deleted auth_module ({configs.AuthModule.Id})");

        await client.Config.DeleteAsync(configs.UserService.Id);
        Console.WriteLine($"  -> Deleted user_service ({configs.UserService.Id})");

        // Reset common to empty (it's a built-in, not deletable)
        await client.Config.UpdateAsync(configs.Common.Id, new CreateConfigOptions
        {
            Name = configs.Common.Name,
            Items = new Dictionary<string, object?>(),
            Environments = new Dictionary<string, object?>(),
        });
        Console.WriteLine($"  -> Common config reset to empty ({configs.Common.Id})");

        Console.WriteLine("  Teardown complete.");
    }
}
