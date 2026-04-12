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
///   2. Prescriptive access via Get (lazy init on first call)
///   3. Typed deserialization via Get&lt;T&gt;
///   4. Multi-level inheritance
///   5. Real-time updates (OnChange + RefreshAsync)
///   6. Cleanup
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
        // 1. PRESCRIPTIVE ACCESS VIA RESOLVE
        // ==============================================================
        Section("1. Prescriptive Access via Get");

        // First Get() call triggers lazy init (fetches all configs,
        // resolves environment overrides, opens WebSocket). No ConnectAsync needed.
        var commonValues = client.Config.Get("common");
        Step($"common resolved keys: [{string.Join(", ", commonValues.Keys)}]");

        var appName = commonValues.TryGetValue("app_name", out var nameVal) ? nameVal?.ToString() : "Unknown";
        Step($"common/app_name = {appName}");

        var retries = commonValues.TryGetValue("max_retries", out var retriesVal) ? retriesVal : 1;
        Step($"common/max_retries = {retries}");
        // Expected: 5 (production override)

        var userServiceValues = client.Config.Get("user_service");
        var signup = userServiceValues.TryGetValue("enable_signup", out var signupVal) ? signupVal : true;
        Step($"user_service/enable_signup = {signup}");
        // Expected: false (production override)

        var cacheTtl = userServiceValues.TryGetValue("cache_ttl_seconds", out var ttlVal) ? ttlVal : 0;
        Step($"user_service/cache_ttl_seconds = {cacheTtl}");
        // Expected: 600 (production override)

        // ==============================================================
        // 2. TYPED DESERIALIZATION VIA RESOLVE<T>
        // ==============================================================
        Section("2. Typed Deserialization via Get<T>");

        var commonConfig = client.Config.Get<CommonConfig>("common");
        Step($"Get<CommonConfig>(\"common\"):");
        Step($"  app_name = {commonConfig.AppName}");
        Step($"  support_email = {commonConfig.SupportEmail}");
        Step($"  max_retries = {commonConfig.MaxRetries}");
        Step($"  request_timeout_ms = {commonConfig.RequestTimeoutMs}");

        // ==============================================================
        // 3. MULTI-LEVEL INHERITANCE
        // ==============================================================
        Section("3. Multi-Level Inheritance");

        // auth_module inherits from user_service.
        // Its own items override, but parent items are still accessible
        // through the resolved hierarchy.
        var authValues = client.Config.Get("auth_module");

        var tokenTtl = authValues.TryGetValue("token_ttl_seconds", out var tokenVal) ? tokenVal : 0;
        Step($"auth_module/token_ttl_seconds = {tokenTtl}");
        // Expected: 3600 (base value, no production override)

        var mfaEnabled = authValues.TryGetValue("mfa_enabled", out var mfaVal) ? mfaVal : false;
        Step($"auth_module/mfa_enabled = {mfaEnabled}");
        // Expected: true (production override)

        var sessionMaxAge = authValues.TryGetValue("session_max_age_hours", out var sessionVal) ? sessionVal : 0;
        Step($"auth_module/session_max_age_hours = {sessionMaxAge}");
        // Expected: 8 (production override)

        // ==============================================================
        // 4. RAW VALUE ACCESS
        // ==============================================================
        Section("4. Raw Value Access");

        var allUserServiceValues = client.Config.Get("user_service");
        Step($"user_service total keys: {allUserServiceValues.Count}");

        var hasMissing = allUserServiceValues.TryGetValue("nonexistent_item", out var missingVal);
        Step($"nonexistent item = {(hasMissing ? missingVal?.ToString() ?? "null" : "null")}");

        // ==============================================================
        // 5. REAL-TIME UPDATES — OnChange + RefreshAsync
        // ==============================================================
        Section("5. OnChange + Refresh");

        var globalChanges = new List<ConfigChangeEvent>();
        client.Config.OnChange(evt =>
        {
            globalChanges.Add(evt);
            Console.WriteLine($"    [CHANGE] {evt.ConfigId}/{evt.ItemKey}: {evt.OldValue} -> {evt.NewValue}");
        });
        Step("Global change listener registered");

        var retryChanges = new List<ConfigChangeEvent>();
        client.Config.OnChange("common", "max_retries", evt => retryChanges.Add(evt));
        Step("Item-specific listener registered for common/max_retries");

        // Update via management API (mutate + SaveAsync), then refresh
        demoConfigs.Common.Environments["production"]["max_retries"] = 7;
        await demoConfigs.Common.SaveAsync();
        Step("Updated max_retries to 7 via management API");

        await client.Config.RefreshAsync();
        Step("Manual refresh completed");

        var refreshedCommon = client.Config.Get("common");
        var newRetries = refreshedCommon.TryGetValue("max_retries", out var newRetriesVal) ? newRetriesVal : 1;
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

    // ------------------------------------------------------------------
    // Typed config model for Resolve<T> demo
    // ------------------------------------------------------------------

    private class CommonConfig
    {
        public string? AppName { get; set; }
        public string? SupportEmail { get; set; }
        public int MaxRetries { get; set; }
        public int RequestTimeoutMs { get; set; }
    }
}
