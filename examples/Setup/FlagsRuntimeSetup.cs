// Setup / cleanup helpers for FlagsRuntimeShowcase.

using Smplkit;
using Smplkit.Errors;
using Smplkit.Flags;

namespace Smplkit.Examples.Setup;

public static class FlagsRuntimeSetup
{
    private static readonly string[] DemoEnvironments = { "staging", "production" };
    private static readonly string[] DemoFlagIds = { "checkout-v2", "banner-color", "max-retries" };

    public static async Task SetupRuntimeShowcaseAsync(SmplManagementClient mgmt)
    {
        var existing = (await mgmt.Environments.ListAsync()).Select(e => e.Id).ToHashSet();
        foreach (var envId in DemoEnvironments)
        {
            if (!existing.Contains(envId))
                await mgmt.Environments.New(envId, char.ToUpper(envId[0]) + envId[1..]).SaveAsync();
        }
        await CleanupRuntimeShowcaseAsync(mgmt);

        var checkout = mgmt.Flags.NewBooleanFlag(
            "checkout-v2", defaultValue: false,
            description: "Controls rollout of the new checkout experience.");
        checkout.EnableRules(environment: "staging");
        checkout.AddRule(new Rule("Enable for enterprise users in US region")
            .Environment("staging")
            .When("user.plan", "==", "enterprise")
            .When("account.region", "==", "us")
            .Serve(true)
            .Build());
        checkout.AddRule(new Rule("Enable for beta testers")
            .Environment("staging")
            .When("user.beta_tester", "==", true)
            .Serve(true)
            .Build());
        checkout.DisableRules(environment: "production");
        checkout.SetDefault(false, environment: "production");
        await checkout.SaveAsync();

        var banner = mgmt.Flags.NewStringFlag(
            "banner-color", defaultValue: "red",
            name: "Banner Color",
            description: "Controls the banner color shown to users.",
            values: new[]
            {
                new FlagValue("Red", "red"),
                new FlagValue("Green", "green"),
                new FlagValue("Blue", "blue"),
            });
        banner.EnableRules(environment: "staging");
        banner.AddRule(new Rule("Blue for enterprise users")
            .Environment("staging")
            .When("user.plan", "==", "enterprise")
            .Serve("blue")
            .Build());
        banner.AddRule(new Rule("Green for technology companies")
            .Environment("staging")
            .When("account.industry", "==", "technology")
            .Serve("green")
            .Build());
        banner.EnableRules(environment: "production");
        banner.SetDefault("blue", environment: "production");
        await banner.SaveAsync();

        var retries = mgmt.Flags.NewNumberFlag(
            "max-retries", defaultValue: 3,
            description: "Maximum number of API retries before failing.");
        retries.EnableRules(environment: "staging");
        retries.AddRule(new Rule("High retries for large accounts")
            .Environment("staging")
            .When("account.employee_count", ">", 100)
            .Serve(5)
            .Build());
        retries.EnableRules(environment: "production");
        await retries.SaveAsync();
    }

    public static async Task CleanupRuntimeShowcaseAsync(SmplManagementClient mgmt)
    {
        foreach (var flagId in DemoFlagIds)
        {
            try { await mgmt.Flags.DeleteAsync(flagId); }
            catch (NotFoundException) { /* not present — that's fine */ }
        }
    }
}
