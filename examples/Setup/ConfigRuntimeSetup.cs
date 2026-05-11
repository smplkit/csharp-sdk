// Setup / cleanup helpers for ConfigRuntimeShowcase.

using Smplkit;
using Smplkit.Errors;

namespace Smplkit.Examples.Setup;

public static class ConfigRuntimeSetup
{
    private static readonly string[] DemoEnvironments = { "staging", "production" };
    private static readonly string[] DemoConfigIds = {
        "showcase-user-service", "showcase-auth-module", "showcase-common"
    };

    public static async Task SetupRuntimeShowcaseAsync(SmplManagementClient mgmt)
    {
        var existing = (await mgmt.Environments.ListAsync()).Select(e => e.Id).ToHashSet();
        foreach (var envId in DemoEnvironments)
        {
            if (!existing.Contains(envId))
                await mgmt.Environments.New(envId, char.ToUpper(envId[0]) + envId[1..]).SaveAsync();
        }
        await CleanupRuntimeShowcaseAsync(mgmt);

        var shared = mgmt.Config.New(
            "showcase-common",
            name: "Showcase Common",
            description: "Showcase-only shared configuration.");
        shared.SetString("app_name", "Acme SaaS Platform");
        shared.SetString("support_email", "support@acme.dev");
        shared.SetNumber("max_retries", 3);
        shared.SetNumber("request_timeout_ms", 5000);
        shared.SetNumber("pagination_default_page_size", 25);
        shared.SetNumber("max_retries", 5, environment: "production");
        shared.SetNumber("request_timeout_ms", 10000, environment: "production");
        shared.SetNumber("max_retries", 2, environment: "staging");
        await shared.SaveAsync();

        var userService = mgmt.Config.New(
            "showcase-user-service",
            name: "Showcase User Service",
            description: "Configuration for the user microservice.",
            parent: shared);
        userService.SetString("database.host", "localhost");
        userService.SetNumber("database.port", 5432);
        userService.SetString("database.name", "users_dev");
        userService.SetNumber("database.pool_size", 5);
        userService.SetNumber("cache_ttl_seconds", 300);
        userService.SetBoolean("enable_signup", true);
        userService.SetNumber("pagination_default_page_size", 50);
        userService.SetString("database.host", "prod-users-rds.internal.acme.dev",
            environment: "production");
        userService.SetString("database.name", "users_prod", environment: "production");
        userService.SetNumber("database.pool_size", 20, environment: "production");
        userService.SetNumber("cache_ttl_seconds", 600, environment: "production");
        userService.SetBoolean("enable_signup", false, environment: "production");
        await userService.SaveAsync();

        var authModule = mgmt.Config.New(
            "showcase-auth-module",
            name: "Showcase Auth Module",
            description: "Authentication module within the user service.",
            parent: shared);
        authModule.SetNumber("session_ttl_minutes", 60);
        authModule.SetBoolean("mfa_enabled", false);
        authModule.SetNumber("session_ttl_minutes", 30, environment: "production");
        authModule.SetBoolean("mfa_enabled", true, environment: "production");
        await authModule.SaveAsync();
    }

    public static async Task CleanupRuntimeShowcaseAsync(SmplManagementClient mgmt)
    {
        // Delete any configs using showcase-common as parent before deleting it — previous
        // runs (including from other SDK showcases) may have left extra children.
        var allConfigs = await mgmt.Config.ListAsync();
        foreach (var cfg in allConfigs.Where(c => c.Parent == "showcase-common"))
        {
            try { await mgmt.Config.DeleteAsync(cfg.Id); }
            catch (NotFoundException) { }
        }
        foreach (var configId in DemoConfigIds)
        {
            try { await mgmt.Config.DeleteAsync(configId); }
            catch (NotFoundException) { /* not present — that's fine */ }
        }
    }
}
