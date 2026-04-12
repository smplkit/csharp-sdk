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
///   5. Cleanup
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
        // Clean up any leftover flags from a previous failed run
        foreach (var key in new[] { "maintenance-mode", "ui-theme", "rate-limit-rps", "experiment-config" })
        {
            try { await client.Flags.Management.DeleteAsync(key); } catch { /* not found is fine */ }
        }

        // ==============================================================
        // 1. CREATE FLAGS OF EVERY TYPE
        // ==============================================================
        Section("1. Create Flags of Every Type");

        // Boolean flag — factory + SaveAsync pattern
        var maintenanceMode = client.Flags.Management.NewBooleanFlag(
            id: "maintenance-mode",
            defaultValue: false,
            name: "Maintenance Mode",
            description: "Kill switch to put the application into maintenance mode");
        await maintenanceMode.SaveAsync();
        Step($"Boolean flag created: id={maintenanceMode.Id}");

        // String flag
        // The values parameter defines a closed set — this flag can only
        // serve "light", "dark", or "auto". This makes it a constrained
        // flag. The Console UI shows dropdowns for value selection.
        var theme = client.Flags.Management.NewStringFlag(
            id: "ui-theme",
            defaultValue: "light",
            name: "UI Theme",
            description: "Active colour theme for the web UI",
            values: new List<Dictionary<string, object?>>
            {
                new() { ["name"] = "Light", ["value"] = "light" },
                new() { ["name"] = "Dark", ["value"] = "dark" },
                new() { ["name"] = "Auto", ["value"] = "auto" },
            });
        await theme.SaveAsync();
        Step($"String flag created: id={theme.Id}");

        // Numeric flag — Unconstrained
        // Unlike ui-theme above, this flag has no predefined values.
        // Any number is valid as a default or rule serve-value. This is
        // useful for tunables like thresholds, retry counts, and timeouts
        // where the value space is open-ended.
        //
        // Omitting the values parameter creates an unconstrained flag.
        var rateLimit = client.Flags.Management.NewNumberFlag(
            id: "rate-limit-rps",
            defaultValue: 100.0,
            name: "Rate Limit (RPS)",
            description: "Per-user rate limit in requests per second");
        await rateLimit.SaveAsync();
        Step($"Numeric flag created: id={rateLimit.Id}");

        // JSON flag
        // Like ui-theme, this JSON flag is constrained — only the
        // declared configuration objects can be served.
        var experimentConfig = client.Flags.Management.NewJsonFlag(
            id: "experiment-config",
            defaultValue: new Dictionary<string, object?>
            {
                ["variant"] = "control",
                ["sample_rate"] = 0.0,
                ["enabled"] = false,
            },
            name: "Experiment Config",
            description: "A/B experiment configuration blob",
            values: new List<Dictionary<string, object?>>
            {
                new() { ["name"] = "Control", ["value"] = new Dictionary<string, object?> { ["variant"] = "control", ["sample_rate"] = 0.0, ["enabled"] = false } },
                new() { ["name"] = "Treatment A", ["value"] = new Dictionary<string, object?> { ["variant"] = "treatment_a", ["sample_rate"] = 0.5, ["enabled"] = true } },
            });
        await experimentConfig.SaveAsync();
        Step($"JSON flag created: id={experimentConfig.Id}");

        // ==============================================================
        // 2. CONFIGURE ENVIRONMENTS WITH RULES
        // ==============================================================
        Section("2. Configure Environments with Rules");

        // maintenance-mode: enable the flag in all environments but leave it off
        maintenanceMode.SetEnvironmentEnabled("development", true);
        maintenanceMode.SetEnvironmentDefault("development", false);
        maintenanceMode.SetEnvironmentEnabled("staging", true);
        maintenanceMode.SetEnvironmentDefault("staging", false);
        maintenanceMode.SetEnvironmentEnabled("production", true);
        maintenanceMode.SetEnvironmentDefault("production", false);
        await maintenanceMode.SaveAsync();
        Step("maintenance-mode environments configured (all off by default)");

        // ui-theme: dark in development, auto in staging, light in production
        theme.SetEnvironmentEnabled("development", true);
        theme.SetEnvironmentDefault("development", "dark");
        theme.SetEnvironmentEnabled("staging", true);
        theme.SetEnvironmentDefault("staging", "auto");
        theme.SetEnvironmentEnabled("production", true);
        theme.SetEnvironmentDefault("production", "light");
        await theme.SaveAsync();
        Step("ui-theme environments configured");

        // Rule: beta users get dark theme in production
        theme.AddRule(
            new Rule("Dark theme for beta users")
                .When("user.beta", "==", true)
                .Serve("dark")
                .Environment("production")
                .Build());
        await theme.SaveAsync();
        Step("ui-theme rule added: dark for beta users in production");

        // rate-limit-rps: lower in dev, higher in prod
        rateLimit.SetEnvironmentEnabled("development", true);
        rateLimit.SetEnvironmentDefault("development", 10.0);
        rateLimit.SetEnvironmentEnabled("staging", true);
        rateLimit.SetEnvironmentDefault("staging", 50.0);
        rateLimit.SetEnvironmentEnabled("production", true);
        rateLimit.SetEnvironmentDefault("production", 100.0);
        await rateLimit.SaveAsync();
        Step("rate-limit-rps environments configured");

        // Rule: enterprise customers get higher rate limit in production
        rateLimit.AddRule(
            new Rule("Higher rate limit for enterprise")
                .When("account.plan", "==", "enterprise")
                .Serve(500.0)
                .Environment("production")
                .Build());
        await rateLimit.SaveAsync();
        Step("rate-limit-rps rule added: 500 RPS for enterprise in production");

        // experiment-config: active experiment in staging only
        experimentConfig.SetEnvironmentEnabled("development", true);
        experimentConfig.SetEnvironmentDefault("development", new Dictionary<string, object?>
        {
            ["variant"] = "control",
            ["sample_rate"] = 0.0,
            ["enabled"] = false,
        });
        experimentConfig.SetEnvironmentEnabled("staging", true);
        experimentConfig.SetEnvironmentDefault("staging", new Dictionary<string, object?>
        {
            ["variant"] = "treatment_a",
            ["sample_rate"] = 0.5,
            ["enabled"] = true,
        });
        experimentConfig.SetEnvironmentEnabled("production", true);
        experimentConfig.SetEnvironmentDefault("production", new Dictionary<string, object?>
        {
            ["variant"] = "control",
            ["sample_rate"] = 0.0,
            ["enabled"] = false,
        });
        await experimentConfig.SaveAsync();
        Step("experiment-config environments configured");

        // ==============================================================
        // 3. LIST AND INSPECT FLAGS
        // ==============================================================
        Section("3. List and Inspect Flags");

        var allFlags = await client.Flags.Management.ListAsync();
        Step($"Total flags: {allFlags.Count}");

        foreach (var f in allFlags)
        {
            Console.WriteLine($"     - {f.Id} (type={f.Type}, default={f.Default})");
        }

        // Fetch a single flag by id to show the full payload
        var fetched = await client.Flags.Management.GetAsync("ui-theme");
        Step($"Fetched ui-theme by id: id={fetched.Id}, type={fetched.Type}");
        Step($"  Description: {fetched.Description}");
        Step($"  Default: {fetched.Default}");
        Step($"  Values: [{(fetched.Values is not null ? string.Join(", ", fetched.Values.Select(v => v.TryGetValue("value", out var val) ? val?.ToString() ?? "null" : "?")) : "unconstrained")}]");
        Step($"  Environments: [{string.Join(", ", fetched.Environments.Keys)}]");

        // ==============================================================
        // 4. UPDATE FLAG PROPERTIES
        // ==============================================================
        Section("4. Update Flag Properties");

        // Update the description and default of rate-limit-rps — mutate + SaveAsync
        rateLimit.Description = "Per-user rate limit (requests per second) — updated via SDK";
        rateLimit.Default = 50.0;
        await rateLimit.SaveAsync();
        Step($"rate-limit-rps updated: default={rateLimit.Default}, desc=\"{rateLimit.Description}\"");

        // Update maintenance-mode name
        maintenanceMode.Name = "Maintenance Mode (Global)";
        await maintenanceMode.SaveAsync();
        Step($"maintenance-mode renamed to: {maintenanceMode.Name}");

        // ==============================================================
        // 5. CLEANUP
        // ==============================================================
        Section("5. Cleanup");

        // Delete flags by key
        await client.Flags.Management.DeleteAsync("maintenance-mode");
        Step("Deleted maintenance-mode");

        await client.Flags.Management.DeleteAsync("ui-theme");
        Step("Deleted ui-theme");

        await client.Flags.Management.DeleteAsync("rate-limit-rps");
        Step("Deleted rate-limit-rps");

        await client.Flags.Management.DeleteAsync("experiment-config");
        Step("Deleted experiment-config");

        // ==============================================================
        // DONE
        // ==============================================================
        Section("ALL DONE");
        Console.WriteLine("  The Flags Management SDK showcase completed successfully.");
        Console.WriteLine("  If you got here, Smpl Flags management is ready to ship.\n");

        return 0;
    }
}
