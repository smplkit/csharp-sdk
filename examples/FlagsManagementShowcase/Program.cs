// Demonstrates the smplkit management SDK for Smpl Flags.
//
// Prerequisites:
//     - dotnet add package Smplkit.Sdk
//     - A valid smplkit API key
//
// Usage:
//     dotnet run --project examples/FlagsManagementShowcase

using Smplkit;
using Smplkit.Examples.Setup;
using Smplkit.Flags;

// create the client
using var mgmt = new SmplManagementClient();
await FlagsManagementSetup.SetupManagementShowcaseAsync(mgmt);

// create a boolean flag
var checkoutFlag = mgmt.Flags.NewBooleanFlag(
    "checkout-v2", defaultValue: false,
    description: "Controls rollout of the new checkout experience.");
await checkoutFlag.SaveAsync();
Console.WriteLine($"Created flag: {checkoutFlag.Id}");

// create a string flag (constrained)
var bannerFlag = mgmt.Flags.NewStringFlag(
    "banner-color", defaultValue: "red",
    name: "Banner Color",
    description: "Controls the banner color shown to users.",
    values: new[]
    {
        new FlagValue("Red", "red"),
        new FlagValue("Green", "green"),
        new FlagValue("Blue", "blue"),
    });
await bannerFlag.SaveAsync();
Console.WriteLine($"Created flag: {bannerFlag.Id}");

// create a numeric flag (unconstrained)
var retryFlag = mgmt.Flags.NewNumberFlag(
    "max-retries", defaultValue: 3,
    description: "Maximum number of API retries before failing.");
await retryFlag.SaveAsync();
Console.WriteLine($"Created flag: {retryFlag.Id}");

// create a JSON flag (constrained)
var themeFlag = mgmt.Flags.NewJsonFlag(
    "ui-theme",
    defaultValue: new Dictionary<string, object?> { ["mode"] = "light", ["accent"] = "#0066cc" },
    description: "Controls the UI theme configuration.",
    values: new[]
    {
        new FlagValue("Light", new Dictionary<string, object?> { ["mode"] = "light", ["accent"] = "#0066cc" }),
        new FlagValue("Dark", new Dictionary<string, object?> { ["mode"] = "dark", ["accent"] = "#66ccff" }),
        new FlagValue("High Contrast", new Dictionary<string, object?> { ["mode"] = "dark", ["accent"] = "#ffffff" }),
    });
await themeFlag.SaveAsync();
Console.WriteLine($"Created flag: {themeFlag.Id}");

// checkoutFlag (serve true in staging to enterprise US users)
checkoutFlag.EnableRules(environment: "staging");
checkoutFlag.AddRule(new Rule("Enable for enterprise users in US region")
    .Environment("staging")
    .When("user.plan", "==", "enterprise")
    .When("account.region", "==", "us")
    .Serve(true)
    .Build());

// checkoutFlag (serve true in staging for beta testers)
checkoutFlag.AddRule(new Rule("Enable for beta testers")
    .Environment("staging")
    .When("user.beta_tester", "==", true)
    .Serve(true)
    .Build());

// checkoutFlag (disabled rules; serve false in production)
checkoutFlag.DisableRules(environment: "production");
checkoutFlag.SetDefault(false, environment: "production");
await checkoutFlag.SaveAsync();
Console.WriteLine($"Updated flag: {checkoutFlag.Id}");

// list flags
var flags = await mgmt.Flags.ListAsync();
Console.WriteLine($"Total flags: {flags.Count}");
foreach (var f in flags)
{
    var envs = string.Join(", ", f.Environments.Keys);
    Console.WriteLine($"  {f.Id} ({f.Type}) — default={f.Default}, environments=[{envs}]");
}

// get a flag
var fetched = await mgmt.Flags.GetAsync("checkout-v2");
Console.WriteLine($"\nFetched by id: {fetched.Id}");

// update a flag
bannerFlag.AddValue("Purple", "purple");
bannerFlag.Default = "blue";
bannerFlag.Description = "Controls the banner color — updated";
bannerFlag.AddRule(new Rule("Purple for enterprise users")
    .Environment("production")
    .When("user.plan", "==", "enterprise")
    .Serve("purple")
    .Build());
await bannerFlag.SaveAsync();
Console.WriteLine($"Updated flag: {bannerFlag.Id}");

// delete all the rules of a flag
checkoutFlag.ClearRules(environment: "staging");
await checkoutFlag.SaveAsync();

// revert production's default value back to the flag default
checkoutFlag.ClearDefault(environment: "production");
await checkoutFlag.SaveAsync();
Console.WriteLine($"Updated flag: {checkoutFlag.Id}");

// clear values (flag becomes unconstrained)
bannerFlag.ClearValues();
await bannerFlag.SaveAsync();
Console.WriteLine($"Updated flag: {bannerFlag.Id}");

// delete flags
await mgmt.Flags.DeleteAsync("checkout-v2");
await bannerFlag.DeleteAsync();
Console.WriteLine("Deleted flags");

// cleanup
await FlagsManagementSetup.CleanupManagementShowcaseAsync(mgmt);
Console.WriteLine("Done!");
