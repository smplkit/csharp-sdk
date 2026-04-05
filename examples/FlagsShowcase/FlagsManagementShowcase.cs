using Smplkit;
using Smplkit.Flags;

namespace FlagsShowcase;

/// <summary>
/// Smpl Flags SDK — Management API Showcase
/// ==========================================
///
/// Demonstrates the management plane of the smplkit Flags C# SDK:
///
///   1. Create flags of every type (boolean, string, numeric, JSON)
///   2. Configure environments with rules
///   3. List and inspect flags
///   4. Update flag properties
///   5. Context type management (CRUD)
///   6. Cleanup
///
/// This script creates, modifies, and deletes real flags.
///
/// Prerequisites:
///     - .NET 8.0 SDK
///     - A valid smplkit API key, provided via SMPLKIT_API_KEY env var or ~/.smplkit config file
///
/// Usage:
///     dotnet run --project examples/FlagsShowcase management
/// </summary>
public static class FlagsManagementShowcase
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
        // 1. CREATE FLAGS OF EVERY TYPE
        // ==============================================================
        Section("1. Create Flags of Every Type");

        // Boolean flag
        var maintenanceMode = await client.Flags.CreateAsync(
            key: "maintenance-mode",
            name: "Maintenance Mode",
            type: FlagType.Boolean,
            @default: false,
            description: "Kill switch to put the application into maintenance mode");
        Step($"Boolean flag created: key={maintenanceMode.Key}, id={maintenanceMode.Id}");

        // String flag
        var theme = await client.Flags.CreateAsync(
            key: "ui-theme",
            name: "UI Theme",
            type: FlagType.String,
            @default: "light",
            description: "Active colour theme for the web UI",
            values: new List<Dictionary<string, object?>>
            {
                new() { ["name"] = "Light", ["value"] = "light" },
                new() { ["name"] = "Dark", ["value"] = "dark" },
                new() { ["name"] = "Auto", ["value"] = "auto" },
            });
        Step($"String flag created: key={theme.Key}, id={theme.Id}");

        // Numeric flag
        var rateLimit = await client.Flags.CreateAsync(
            key: "rate-limit-rps",
            name: "Rate Limit (RPS)",
            type: FlagType.Numeric,
            @default: 100.0,
            description: "Per-user rate limit in requests per second");
        Step($"Numeric flag created: key={rateLimit.Key}, id={rateLimit.Id}");

        // JSON flag
        var experimentConfig = await client.Flags.CreateAsync(
            key: "experiment-config",
            name: "Experiment Config",
            type: FlagType.Json,
            @default: new Dictionary<string, object?>
            {
                ["variant"] = "control",
                ["sample_rate"] = 0.0,
                ["enabled"] = false,
            },
            description: "A/B experiment configuration blob");
        Step($"JSON flag created: key={experimentConfig.Key}, id={experimentConfig.Id}");

        // ==============================================================
        // 2. CONFIGURE ENVIRONMENTS WITH RULES
        // ==============================================================
        Section("2. Configure Environments with Rules");

        // maintenance-mode: enable the flag in all environments but leave it off
        await maintenanceMode.UpdateAsync(environments: new Dictionary<string, Dictionary<string, object?>>
        {
            ["development"] = new() { ["enabled"] = true, ["default"] = false },
            ["staging"] = new() { ["enabled"] = true, ["default"] = false },
            ["production"] = new() { ["enabled"] = true, ["default"] = false },
        });
        Step("maintenance-mode environments configured (all off by default)");

        // ui-theme: dark in development, auto in staging, light in production
        await theme.UpdateAsync(environments: new Dictionary<string, Dictionary<string, object?>>
        {
            ["development"] = new() { ["enabled"] = true, ["default"] = "dark" },
            ["staging"] = new() { ["enabled"] = true, ["default"] = "auto" },
            ["production"] = new() { ["enabled"] = true, ["default"] = "light" },
        });
        Step("ui-theme environments configured");

        // Rule: beta users get dark theme in production
        await theme.AddRuleAsync(
            new Rule("Dark theme for beta users")
                .When("user.beta", "==", true)
                .Serve("dark")
                .Environment("production")
                .Build());
        Step("ui-theme rule added: dark for beta users in production");

        // rate-limit-rps: lower in dev, higher in prod
        await rateLimit.UpdateAsync(environments: new Dictionary<string, Dictionary<string, object?>>
        {
            ["development"] = new() { ["enabled"] = true, ["default"] = 10.0 },
            ["staging"] = new() { ["enabled"] = true, ["default"] = 50.0 },
            ["production"] = new() { ["enabled"] = true, ["default"] = 100.0 },
        });
        Step("rate-limit-rps environments configured");

        // Rule: enterprise customers get higher rate limit in production
        await rateLimit.AddRuleAsync(
            new Rule("Higher rate limit for enterprise")
                .When("account.plan", "==", "enterprise")
                .Serve(500.0)
                .Environment("production")
                .Build());
        Step("rate-limit-rps rule added: 500 RPS for enterprise in production");

        // experiment-config: active experiment in staging only
        await experimentConfig.UpdateAsync(environments: new Dictionary<string, Dictionary<string, object?>>
        {
            ["development"] = new()
            {
                ["enabled"] = true,
                ["default"] = new Dictionary<string, object?>
                {
                    ["variant"] = "control",
                    ["sample_rate"] = 0.0,
                    ["enabled"] = false,
                },
            },
            ["staging"] = new()
            {
                ["enabled"] = true,
                ["default"] = new Dictionary<string, object?>
                {
                    ["variant"] = "treatment_a",
                    ["sample_rate"] = 0.5,
                    ["enabled"] = true,
                },
            },
            ["production"] = new()
            {
                ["enabled"] = true,
                ["default"] = new Dictionary<string, object?>
                {
                    ["variant"] = "control",
                    ["sample_rate"] = 0.0,
                    ["enabled"] = false,
                },
            },
        });
        Step("experiment-config environments configured");

        // ==============================================================
        // 3. LIST AND INSPECT FLAGS
        // ==============================================================
        Section("3. List and Inspect Flags");

        var allFlags = await client.Flags.ListAsync();
        Step($"Total flags: {allFlags.Count}");

        foreach (var f in allFlags)
        {
            Console.WriteLine($"     - {f.Key} (type={f.Type}, default={f.Default})");
        }

        // Fetch a single flag by ID to show the full payload
        var fetched = await client.Flags.GetAsync(theme.Id);
        Step($"Fetched ui-theme by ID: key={fetched.Key}, type={fetched.Type}");
        Step($"  Description: {fetched.Description}");
        Step($"  Default: {fetched.Default}");
        Step($"  Values: [{string.Join(", ", fetched.Values.Select(v => v.TryGetValue("value", out var val) ? val?.ToString() ?? "null" : "?"))}]");
        Step($"  Environments: [{string.Join(", ", fetched.Environments.Keys)}]");

        // ==============================================================
        // 4. UPDATE FLAG PROPERTIES
        // ==============================================================
        Section("4. Update Flag Properties");

        // Update the description and default of rate-limit-rps
        await rateLimit.UpdateAsync(
            description: "Per-user rate limit (requests per second) — updated via SDK",
            @default: 150.0);
        Step($"rate-limit-rps updated: default={rateLimit.Default}, desc=\"{rateLimit.Description}\"");

        // Update maintenance-mode name
        await maintenanceMode.UpdateAsync(name: "Maintenance Mode (Global)");
        Step($"maintenance-mode renamed to: {maintenanceMode.Name}");

        // ==============================================================
        // 5. CONTEXT TYPE MANAGEMENT
        // ==============================================================
        Section("5. Context Type Management");

        // Create context types for targeting
        var userCtxType = await client.Flags.CreateContextTypeAsync("user", "User");
        Step($"Created context type: key={userCtxType.Key}, id={userCtxType.Id}");

        var accountCtxType = await client.Flags.CreateContextTypeAsync("account", "Account");
        Step($"Created context type: key={accountCtxType.Key}, id={accountCtxType.Id}");

        // Update with known attributes
        userCtxType = await client.Flags.UpdateContextTypeAsync(userCtxType.Id,
            new Dictionary<string, object?>
            {
                ["plan"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["beta"] = new Dictionary<string, object?> { ["type"] = "boolean" },
                ["country"] = new Dictionary<string, object?> { ["type"] = "string" },
            });
        Step($"Updated user context type with attributes: [{string.Join(", ", userCtxType.Attributes.Keys)}]");

        accountCtxType = await client.Flags.UpdateContextTypeAsync(accountCtxType.Id,
            new Dictionary<string, object?>
            {
                ["plan"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["seats"] = new Dictionary<string, object?> { ["type"] = "number" },
            });
        Step($"Updated account context type with attributes: [{string.Join(", ", accountCtxType.Attributes.Keys)}]");

        // List all context types
        var contextTypes = await client.Flags.ListContextTypesAsync();
        Step($"Total context types: {contextTypes.Count}");
        foreach (var ct in contextTypes)
            Console.WriteLine($"     - {ct.Key}: {ct.Name} (id={ct.Id})");

        // ==============================================================
        // 6. CLEANUP
        // ==============================================================
        Section("6. Cleanup");

        // Delete flags
        await client.Flags.DeleteAsync(maintenanceMode.Id);
        Step($"Deleted maintenance-mode ({maintenanceMode.Id})");

        await client.Flags.DeleteAsync(theme.Id);
        Step($"Deleted ui-theme ({theme.Id})");

        await client.Flags.DeleteAsync(rateLimit.Id);
        Step($"Deleted rate-limit-rps ({rateLimit.Id})");

        await client.Flags.DeleteAsync(experimentConfig.Id);
        Step($"Deleted experiment-config ({experimentConfig.Id})");

        // Delete context types
        await client.Flags.DeleteContextTypeAsync(userCtxType.Id);
        Step($"Deleted context type user ({userCtxType.Id})");

        await client.Flags.DeleteContextTypeAsync(accountCtxType.Id);
        Step($"Deleted context type account ({accountCtxType.Id})");

        // ==============================================================
        // DONE
        // ==============================================================
        Section("ALL DONE");
        Console.WriteLine("  The Flags Management SDK showcase completed successfully.");
        Console.WriteLine("  If you got here, Smpl Flags management is ready to ship.\n");

        return 0;
    }
}
