using Smplkit;
using Smplkit.Flags;

namespace FlagsShowcase;

/// <summary>
/// Helper that creates and tears down demo flags for the showcase scripts.
///
/// Creates three flags:
///   - checkout-v2   (boolean)  — new checkout flow toggle
///   - banner-color  (string)   — configurable UI banner colour
///   - max-retries   (numeric)  — retry limit per environment
///
/// Each flag gets environment-specific defaults and at least one rule.
/// </summary>
public static class FlagsDemoSetup
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

        var checkoutV2 = await client.Flags.CreateAsync(
            key: "checkout-v2",
            name: "Checkout V2",
            type: FlagType.Boolean,
            @default: false,
            description: "Enables the redesigned checkout flow");

        // Enable in development, disable in production
        await checkoutV2.UpdateAsync(environments: new Dictionary<string, Dictionary<string, object?>>
        {
            ["development"] = new() { ["enabled"] = true, ["default"] = true },
            ["staging"] = new() { ["enabled"] = true, ["default"] = false },
            ["production"] = new() { ["enabled"] = true, ["default"] = false },
        });

        // Rule: enterprise users get checkout-v2 in staging
        await checkoutV2.AddRuleAsync(
            new Rule("Enable for enterprise users in staging")
                .When("user.plan", "==", "enterprise")
                .Serve(true)
                .Environment("staging")
                .Build());

        // Rule: beta users get checkout-v2 in production
        await checkoutV2.AddRuleAsync(
            new Rule("Enable for beta users in production")
                .When("user.beta", "==", true)
                .Serve(true)
                .Environment("production")
                .Build());

        Console.WriteLine($"     Created: id={checkoutV2.Id}");

        // ------------------------------------------------------------------
        // 2. banner-color (string) — configurable UI banner colour
        // ------------------------------------------------------------------
        Console.WriteLine("  -> Creating banner-color (string)...");

        var bannerColor = await client.Flags.CreateAsync(
            key: "banner-color",
            name: "Banner Color",
            type: FlagType.String,
            @default: "blue",
            description: "Hero banner background colour",
            values: new List<Dictionary<string, object?>>
            {
                new() { ["name"] = "Blue", ["value"] = "blue" },
                new() { ["name"] = "Green", ["value"] = "green" },
                new() { ["name"] = "Red", ["value"] = "red" },
            });

        await bannerColor.UpdateAsync(environments: new Dictionary<string, Dictionary<string, object?>>
        {
            ["development"] = new() { ["enabled"] = true, ["default"] = "green" },
            ["staging"] = new() { ["enabled"] = true, ["default"] = "blue" },
            ["production"] = new() { ["enabled"] = true, ["default"] = "blue" },
        });

        // Rule: premium users get red banner in production
        await bannerColor.AddRuleAsync(
            new Rule("Red banner for premium users")
                .When("user.plan", "==", "premium")
                .Serve("red")
                .Environment("production")
                .Build());

        Console.WriteLine($"     Created: id={bannerColor.Id}");

        // ------------------------------------------------------------------
        // 3. max-retries (numeric) — retry limit per environment
        // ------------------------------------------------------------------
        Console.WriteLine("  -> Creating max-retries (numeric)...");

        var maxRetries = await client.Flags.CreateAsync(
            key: "max-retries",
            name: "Max Retries",
            type: FlagType.Numeric,
            @default: 3.0,
            description: "Maximum API retry attempts");

        await maxRetries.UpdateAsync(environments: new Dictionary<string, Dictionary<string, object?>>
        {
            ["development"] = new() { ["enabled"] = true, ["default"] = 1.0 },
            ["staging"] = new() { ["enabled"] = true, ["default"] = 2.0 },
            ["production"] = new() { ["enabled"] = true, ["default"] = 5.0 },
        });

        // Rule: internal users get more retries in production
        await maxRetries.AddRuleAsync(
            new Rule("Extra retries for internal users")
                .When("user.internal", "==", true)
                .Serve(10.0)
                .Environment("production")
                .Build());

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

        await client.Flags.DeleteAsync(flags.CheckoutV2.Id);
        Console.WriteLine($"  -> Deleted checkout-v2 ({flags.CheckoutV2.Id})");

        await client.Flags.DeleteAsync(flags.BannerColor.Id);
        Console.WriteLine($"  -> Deleted banner-color ({flags.BannerColor.Id})");

        await client.Flags.DeleteAsync(flags.MaxRetries.Id);
        Console.WriteLine($"  -> Deleted max-retries ({flags.MaxRetries.Id})");

        Console.WriteLine("  Teardown complete.");
    }
}
