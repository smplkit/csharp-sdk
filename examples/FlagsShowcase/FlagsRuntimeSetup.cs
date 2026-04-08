using Smplkit;
using Smplkit.Flags;

namespace FlagsShowcase;

/// <summary>
/// Helper that creates and tears down demo flags for the runtime showcase.
///
/// Creates three flags:
///   - checkout-v2   (boolean)  — new checkout flow toggle
///   - banner-color  (string)   — configurable UI banner colour
///   - max-retries   (numeric)  — retry limit per environment
///
/// Each flag gets environment-specific defaults and at least one rule.
/// </summary>
public static class FlagsRuntimeSetup
{
    public record DemoFlags(Flag CheckoutV2, Flag BannerColor, Flag MaxRetries);

    public static async Task<DemoFlags> SetupDemoFlagsAsync(SmplClient client)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 60));
        Console.WriteLine("  Demo Flag Setup");
        Console.WriteLine(new string('=', 60));
        Console.WriteLine();

        // ------------------------------------------------------------------
        // 1. checkout-v2 (boolean) — new checkout flow toggle
        // ------------------------------------------------------------------
        Console.WriteLine("  -> Creating checkout-v2 (boolean)...");

        var checkoutV2 = client.Flags.NewBooleanFlag(
            key: "checkout-v2",
            defaultValue: false,
            name: "Checkout V2",
            description: "Enables the redesigned checkout flow");

        // Enable in development, disable in production
        checkoutV2.SetEnvironmentEnabled("development", true);
        checkoutV2.SetEnvironmentDefault("development", true);
        checkoutV2.SetEnvironmentEnabled("staging", true);
        checkoutV2.SetEnvironmentDefault("staging", false);
        checkoutV2.SetEnvironmentEnabled("production", true);
        checkoutV2.SetEnvironmentDefault("production", false);

        // Rule: enterprise users get checkout-v2 in staging
        checkoutV2.AddRule(
            new Rule("Enable for enterprise users in staging")
                .When("user.plan", "==", "enterprise")
                .Serve(true)
                .Environment("staging")
                .Build());

        // Rule: beta users get checkout-v2 in production
        checkoutV2.AddRule(
            new Rule("Enable for beta users in production")
                .When("user.beta", "==", true)
                .Serve(true)
                .Environment("production")
                .Build());

        await checkoutV2.SaveAsync();
        Console.WriteLine($"     Created: id={checkoutV2.Id}");

        // ------------------------------------------------------------------
        // 2. banner-color (string) — configurable UI banner colour
        // ------------------------------------------------------------------
        Console.WriteLine("  -> Creating banner-color (string)...");

        var bannerColor = client.Flags.NewStringFlag(
            key: "banner-color",
            defaultValue: "blue",
            name: "Banner Color",
            description: "Hero banner background colour",
            values: new List<Dictionary<string, object?>>
            {
                new() { ["name"] = "Blue", ["value"] = "blue" },
                new() { ["name"] = "Green", ["value"] = "green" },
                new() { ["name"] = "Red", ["value"] = "red" },
            });

        bannerColor.SetEnvironmentEnabled("development", true);
        bannerColor.SetEnvironmentDefault("development", "green");
        bannerColor.SetEnvironmentEnabled("staging", true);
        bannerColor.SetEnvironmentDefault("staging", "blue");
        bannerColor.SetEnvironmentEnabled("production", true);
        bannerColor.SetEnvironmentDefault("production", "blue");

        // Rule: premium users get red banner in production
        bannerColor.AddRule(
            new Rule("Red banner for premium users")
                .When("user.plan", "==", "premium")
                .Serve("red")
                .Environment("production")
                .Build());

        await bannerColor.SaveAsync();
        Console.WriteLine($"     Created: id={bannerColor.Id}");

        // ------------------------------------------------------------------
        // 3. max-retries (numeric) — retry limit per environment
        // ------------------------------------------------------------------
        Console.WriteLine("  -> Creating max-retries (numeric)...");

        var maxRetries = client.Flags.NewNumberFlag(
            key: "max-retries",
            defaultValue: 3.0,
            name: "Max Retries",
            description: "Maximum API retry attempts");

        maxRetries.SetEnvironmentEnabled("development", true);
        maxRetries.SetEnvironmentDefault("development", 1.0);
        maxRetries.SetEnvironmentEnabled("staging", true);
        maxRetries.SetEnvironmentDefault("staging", 2.0);
        maxRetries.SetEnvironmentEnabled("production", true);
        maxRetries.SetEnvironmentDefault("production", 5.0);

        // Rule: internal users get more retries in production
        maxRetries.AddRule(
            new Rule("Extra retries for internal users")
                .When("user.internal", "==", true)
                .Serve(10.0)
                .Environment("production")
                .Build());

        await maxRetries.SaveAsync();
        Console.WriteLine($"     Created: id={maxRetries.Id}");

        Console.WriteLine();
        Console.WriteLine("  Demo flags created successfully.");
        return new DemoFlags(checkoutV2, bannerColor, maxRetries);
    }

    public static async Task TeardownDemoFlagsAsync(SmplClient client, DemoFlags flags)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 60));
        Console.WriteLine("  Demo Flag Teardown");
        Console.WriteLine(new string('=', 60));
        Console.WriteLine();

        await client.Flags.DeleteAsync("checkout-v2");
        Console.WriteLine("  -> Deleted checkout-v2");

        await client.Flags.DeleteAsync("banner-color");
        Console.WriteLine("  -> Deleted banner-color");

        await client.Flags.DeleteAsync("max-retries");
        Console.WriteLine("  -> Deleted max-retries");

        Console.WriteLine("  Teardown complete.");
    }
}
