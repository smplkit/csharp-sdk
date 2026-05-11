// Demonstrates the smplkit runtime SDK for Smpl Flags.
//
// Prerequisites:
//     - dotnet add package Smplkit.Sdk
//     - A valid smplkit API key
//
// Usage:
//     dotnet run --project examples/FlagsRuntimeShowcase

// ---------------------------------------------------------------------------
// Note: this showcase calls client.SetContext(...) inline to demonstrate
// context-driven flag evaluation.  In a real app (ASP.NET Core, etc.),
// SetContext is called once per request from middleware — not scattered
// through your handlers.
// ---------------------------------------------------------------------------

using Smplkit;
using Smplkit.Examples.Setup;
using Smplkit.Flags;

var alice = new Dictionary<string, object?>
{
    ["beta_tester"] = true,
    ["email"] = "alice.adams@acme.com",
    ["first_name"] = "Alice",
    ["last_name"] = "Adams",
    ["plan"] = "enterprise",
};

var bob = new Dictionary<string, object?>
{
    ["beta_tester"] = false,
    ["email"] = "bob.jones@acme.com",
    ["first_name"] = "Bob",
    ["last_name"] = "Jones",
    ["plan"] = "free",
};

var largeTechAccount = new Dictionary<string, object?>
{
    ["employee_count"] = 500,
    ["id"] = 1234,
    ["industry"] = "technology",
    ["region"] = "us",
};

var smallRetailAccount = new Dictionary<string, object?>
{
    ["employee_count"] = 10,
    ["id"] = 5678,
    ["industry"] = "retail",
    ["region"] = "eu",
};

// create the client
using var client = new SmplClient(new SmplClientOptions
{
    Environment = "staging",
    Service = "showcase-service",
});
await FlagsRuntimeSetup.SetupRuntimeShowcaseAsync(client.Manage);
await client.WaitUntilReadyAsync();

// declare flags - default values will be used if the flag does not exist
// or smplkit is unreachable
var checkoutV2 = client.Flags.BooleanFlag("checkout-v2", defaultValue: false);
var bannerColor = client.Flags.StringFlag("banner-color", defaultValue: "red");
var maxRetries = client.Flags.NumberFlag("max-retries", defaultValue: 3);

var allChanges = new List<FlagChangeEvent>();
var bannerChanges = new List<FlagChangeEvent>();

// global listener — fires when ANY flag definition changes
client.Flags.OnChange(evt =>
{
    allChanges.Add(evt);
    Console.WriteLine($"    Global flag listener: '{evt.Id}' updated via {evt.Source}");
});

// flag listener — fires only when a specific flag changes
client.Flags.OnChange("banner-color", evt =>
{
    bannerChanges.Add(evt);
    Console.WriteLine("    banner-color flag changed!");
});

// request 1 — Alice from a large tech account
using (client.SetContext(CreateContext(alice, largeTechAccount)))
{
    var checkoutResult = checkoutV2.Get();
    Console.WriteLine($"checkout-v2 = {checkoutResult}");
    System.Diagnostics.Debug.Assert(checkoutResult);

    var bannerResult = bannerColor.Get();
    Console.WriteLine($"banner-color = {bannerResult}");
    System.Diagnostics.Debug.Assert(bannerResult == "blue");

    var retriesResult = maxRetries.Get();
    Console.WriteLine($"max-retries = {retriesResult}");
    System.Diagnostics.Debug.Assert(retriesResult == 5);
}

// request 2 — Bob from a small retail account
using (client.SetContext(CreateContext(bob, smallRetailAccount)))
{
    var checkoutResult2 = checkoutV2.Get();
    Console.WriteLine($"checkout-v2 = {checkoutResult2}");
    System.Diagnostics.Debug.Assert(!checkoutResult2);

    var bannerResult2 = bannerColor.Get();
    Console.WriteLine($"banner-color = {bannerResult2}");
    System.Diagnostics.Debug.Assert(bannerResult2 == "red");

    var retriesResult2 = maxRetries.Get();
    Console.WriteLine($"max-retries = {retriesResult2}");
    System.Diagnostics.Debug.Assert(retriesResult2 == 3);

    // nested scoped override — temporarily impersonate Alice without
    // disturbing the surrounding request's context.
    using (client.SetContext(CreateContext(alice, largeTechAccount)))
    {
        var scopedResult = checkoutV2.Get();
        Console.WriteLine($"checkout-v2 (scoped: Alice) = {scopedResult}");
        System.Diagnostics.Debug.Assert(scopedResult);
    }

    // context auto-reverted to Bob/small retail here
    System.Diagnostics.Debug.Assert(!checkoutV2.Get());
}

// get a flag's value (explicitly pass context)
var explicitResult = checkoutV2.Get(context: new[]
{
    new Smplkit.Context("user", "john.smith@acme.com", new Dictionary<string, object?>
    {
        ["plan"] = "free",
        ["beta_tester"] = false,
    }),
    new Smplkit.Context("account", "1111", new Dictionary<string, object?>
    {
        ["region"] = "jp",
    }),
});
Console.WriteLine($"checkout-v2 (free, JP) = {explicitResult}");
System.Diagnostics.Debug.Assert(!explicitResult);

// simulate someone making changes to a flag to trigger listeners
await UpdateRulesAsync(client);

// wait a moment for the event to be delivered (typical WS round-trip
// is well under 200ms; 400ms is plenty of headroom and anything past
// that is a real signal, not noise to absorb).
await Task.Delay(400);

// verify both listeners fired
System.Diagnostics.Debug.Assert(allChanges.Count >= 1);
System.Diagnostics.Debug.Assert(bannerChanges.Count >= 1);

await FlagsRuntimeSetup.CleanupRuntimeShowcaseAsync(client.Manage);
Console.WriteLine("Done!");

// Create context within which flags will be evaluated.
static IEnumerable<Smplkit.Context> CreateContext(
    Dictionary<string, object?> user, Dictionary<string, object?> account)
{
    return new[]
    {
        new Smplkit.Context("user", (string)user["email"]!, new Dictionary<string, object?>
        {
            ["beta_tester"] = user["beta_tester"],
            ["first_name"] = user["first_name"],
            ["last_name"] = user["last_name"],
            ["plan"] = user["plan"],
        }),
        new Smplkit.Context("account", account["id"]!.ToString()!, new Dictionary<string, object?>
        {
            ["industry"] = account["industry"],
            ["region"] = account["region"],
            ["employee_count"] = account["employee_count"],
        }),
    };
}

static async Task UpdateRulesAsync(SmplClient client)
{
    var currentBanner = await client.Manage.Flags.GetAsync("banner-color");
    currentBanner.AddRule(new Rule("Red for small companies")
        .Environment("staging")
        .When("account.employee_count", "<", 50)
        .Serve("red")
        .Build());
    await currentBanner.SaveAsync();
}
