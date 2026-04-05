using Smplkit;
using Smplkit.Config;

namespace ConfigShowcase;

/// <summary>
/// Smpl Config SDK — Runtime Showcase
/// ====================================
///
/// Demonstrates the runtime (prescriptive access) plane of the smplkit Config C# SDK:
///
///   1. Demo config setup (common, user_service, auth_module hierarchy)
///   2. Connect and prescriptive access
///   3. Typed accessors (GetString, GetInt, GetBool)
///   4. Raw value access
///   5. Multi-level inheritance
///   6. Real-time updates (OnChange + RefreshAsync)
///   7. Cleanup
///
/// This script creates, modifies, and deletes real configs.
///
/// Prerequisites:
///     - .NET 8.0 SDK
///     - A valid smplkit API key, provided via SMPLKIT_API_KEY env var or ~/.smplkit config file
///
/// Usage:
///     dotnet run --project examples/ConfigShowcase
/// </summary>
public static class ConfigRuntimeShowcase
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
        // 0. SET UP DEMO CONFIGS
        // ==============================================================
        var demoConfigs = await ConfigRuntimeSetup.SetupDemoConfigsAsync(client);

        // ==============================================================
        // 1. CONNECT — PRESCRIPTIVE ACCESS
        // ==============================================================
        Section("1. Connect and Prescriptive Access");

        await client.ConnectAsync();
        Step("client.ConnectAsync() — all configs loaded and resolved");

        // ==============================================================
        // 2. TYPED ACCESSORS
        // ==============================================================
        Section("2. Typed Accessors");

        var appName = client.Config.GetString("common", "app_name", "Unknown");
        Step($"common/app_name (string) = {appName}");

        var retries = client.Config.GetInt("common", "max_retries", 1);
        Step($"common/max_retries (int) = {retries}");
        // Expected: 5 (production override)

        var signup = client.Config.GetBool("user_service", "enable_signup", true);
        Step($"user_service/enable_signup (bool) = {signup}");
        // Expected: false (production override)

        var cacheTtl = client.Config.GetInt("user_service", "cache_ttl_seconds", 0);
        Step($"user_service/cache_ttl_seconds (int) = {cacheTtl}");
        // Expected: 600 (production override)

        // ==============================================================
        // 3. RAW VALUE ACCESS
        // ==============================================================
        Section("3. Raw Value Access");

        var allValues = client.Config.GetValue("user_service") as Dictionary<string, object?>;
        Step($"user_service total keys: {allValues?.Count}");

        var missing = client.Config.GetValue("user_service", "nonexistent_item");
        Step($"nonexistent item = {missing ?? "null"}");

        // ==============================================================
        // 4. MULTI-LEVEL INHERITANCE
        // ==============================================================
        Section("4. Multi-Level Inheritance");

        // auth_module inherits from user_service.
        // Its own items override, but parent items are still accessible
        // through the resolved hierarchy.
        var tokenTtl = client.Config.GetInt("auth_module", "token_ttl_seconds", 0);
        Step($"auth_module/token_ttl_seconds (int) = {tokenTtl}");
        // Expected: 3600 (base value, no production override)

        var mfaEnabled = client.Config.GetBool("auth_module", "mfa_enabled", false);
        Step($"auth_module/mfa_enabled (bool) = {mfaEnabled}");
        // Expected: true (production override)

        var sessionMaxAge = client.Config.GetInt("auth_module", "session_max_age_hours", 0);
        Step($"auth_module/session_max_age_hours (int) = {sessionMaxAge}");
        // Expected: 8 (production override)

        // ==============================================================
        // 5. REAL-TIME UPDATES — OnChange + RefreshAsync
        // ==============================================================
        Section("5. OnChange + Refresh");

        var globalChanges = new List<ConfigChangeEvent>();
        client.Config.OnChange(evt =>
        {
            globalChanges.Add(evt);
            Console.WriteLine($"    [CHANGE] {evt.ConfigKey}/{evt.ItemKey}: {evt.OldValue} -> {evt.NewValue}");
        });
        Step("Global change listener registered");

        var retryChanges = new List<ConfigChangeEvent>();
        client.Config.OnChange(
            evt => retryChanges.Add(evt),
            configKey: "common",
            itemKey: "max_retries");
        Step("Key-specific listener registered for common/max_retries");

        // Update via management API, then refresh
        await client.Config.SetValueAsync(
            demoConfigs.Common.Id, "max_retries", 7, environment: "production");
        Step("Updated max_retries to 7 via management API");

        await client.Config.RefreshAsync();
        Step("Manual refresh completed");

        var newRetries = client.Config.GetInt("common", "max_retries", 1);
        Step($"max_retries after refresh = {newRetries}");
        // Expected: 7

        Step($"Global changes: {globalChanges.Count}, retry-specific: {retryChanges.Count}");

        // ==============================================================
        // 6. CLEANUP
        // ==============================================================
        await ConfigRuntimeSetup.TeardownDemoConfigsAsync(client, demoConfigs);

        // ==============================================================
        // DONE
        // ==============================================================
        Section("ALL DONE");
        Console.WriteLine("  The Config Runtime showcase completed successfully.");
        Console.WriteLine("  If you got here, Smpl Config runtime is ready to ship.\n");

        return 0;
    }
}
