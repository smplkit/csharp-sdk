using Smplkit;
using Smplkit.Flags;

namespace FlagsShowcase;

/// <summary>
/// Smpl Flags SDK — Runtime Evaluation Showcase
/// ===============================================
///
/// Demonstrates the runtime (evaluation) plane of the smplkit Flags C# SDK:
///
///   1. Typed flag declarations (bool, string, number)
///   2. Context provider setup
///   3. Explicit context registration
///   4. Connect to an environment
///   5. Evaluate flags for different users
///   6. Explicit context override
///   7. Caching statistics
///   8. Context registration flush
///   9. Real-time updates (change listeners + management API change)
///  10. Environment comparison
///  11. Tier 1 explicit evaluate
///  12. Cleanup
///
/// This script creates, modifies, and deletes real flags.
///
/// Prerequisites:
///     - .NET 8.0 SDK
///     - A valid smplkit API key (set via SMPLKIT_API_KEY env var)
///
/// Usage:
///     export SMPLKIT_API_KEY="sk_api_..."
///     dotnet run --project examples/FlagsShowcase
/// </summary>
public static class FlagsRuntimeShowcase
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
        // 0. SET UP DEMO FLAGS
        // ==============================================================
        var demoFlags = await FlagsDemoSetup.SetupDemoFlagsAsync(client);

        // ==============================================================
        // 1. TYPED FLAG DECLARATIONS
        // ==============================================================
        Section("1. Typed Flag Declarations");

        // Declare typed handles with code-level defaults. These are the
        // compile-time contract between your app and the flag service.
        var checkoutV2 = client.Flags.BoolFlag("checkout-v2", defaultValue: false);
        Step($"BoolFlag declared: key={checkoutV2.Key}, default={checkoutV2.Default}");

        var bannerColor = client.Flags.StringFlag("banner-color", defaultValue: "blue");
        Step($"StringFlag declared: key={bannerColor.Key}, default={bannerColor.Default}");

        var maxRetries = client.Flags.NumberFlag("max-retries", defaultValue: 3);
        Step($"NumberFlag declared: key={maxRetries.Key}, default={maxRetries.Default}");

        // ==============================================================
        // 2. CONTEXT PROVIDER
        // ==============================================================
        Section("2. Context Provider");

        // The context provider is called on every evaluation to supply
        // the current request context. In a real app this would read from
        // the HTTP request, session, or DI container.
        var currentUser = new Context(
            "user", "user-42",
            new Dictionary<string, object?>
            {
                ["plan"] = "enterprise",
                ["beta"] = false,
                ["country"] = "US",
            },
            name: "Alice");

        client.Flags.SetContextProvider(() => new List<Context> { currentUser });
        Step($"Context provider set: returns {currentUser}");

        // ==============================================================
        // 3. EXPLICIT CONTEXT REGISTRATION
        // ==============================================================
        Section("3. Explicit Context Registration");

        // Register contexts explicitly for analytics / audience tracking.
        // This is fire-and-forget and batched automatically.
        client.Flags.Register(currentUser);
        Step("Registered current user context");

        var secondUser = new Context(
            "user", "user-99",
            new Dictionary<string, object?>
            {
                ["plan"] = "free",
                ["beta"] = true,
                ["country"] = "GB",
            },
            name: "Bob");

        client.Flags.Register(secondUser);
        Step($"Registered second user: {secondUser}");

        // ==============================================================
        // 4. CONNECT TO AN ENVIRONMENT
        // ==============================================================
        Section("4. Connect to Environment");

        await client.Flags.ConnectAsync("production", timeout: 10);
        Step($"Connected to production — status: {client.Flags.ConnectionStatus}");

        // ==============================================================
        // 5. EVALUATE FLAGS FOR DIFFERENT USERS
        // ==============================================================
        Section("5. Evaluate Flags for Different Users");

        // Evaluate with provider context (Alice — enterprise, not beta)
        var aliceCheckout = checkoutV2.Get();
        var aliceBanner = bannerColor.Get();
        var aliceRetries = maxRetries.Get();
        Step($"Alice (enterprise, no beta):");
        Step($"  checkout-v2  = {aliceCheckout}");
        Step($"  banner-color = {aliceBanner}");
        Step($"  max-retries  = {aliceRetries}");

        // Switch context provider to Bob (free plan, beta user)
        client.Flags.SetContextProvider(() => new List<Context> { secondUser });
        Step("Switched context provider to Bob");

        var bobCheckout = checkoutV2.Get();
        var bobBanner = bannerColor.Get();
        var bobRetries = maxRetries.Get();
        Step($"Bob (free, beta):");
        Step($"  checkout-v2  = {bobCheckout}");
        Step($"  banner-color = {bobBanner}");
        Step($"  max-retries  = {bobRetries}");

        // ==============================================================
        // 6. EXPLICIT CONTEXT OVERRIDE
        // ==============================================================
        Section("6. Explicit Context Override");

        // Override the provider with an explicit context for a single call.
        var premiumUser = new Context(
            "user", "user-premium-1",
            new Dictionary<string, object?>
            {
                ["plan"] = "premium",
                ["beta"] = false,
                ["country"] = "DE",
            },
            name: "Charlie");

        var charlieCheckout = checkoutV2.Get(context: new List<Context> { premiumUser });
        var charlieBanner = bannerColor.Get(context: new List<Context> { premiumUser });
        var charlieRetries = maxRetries.Get(context: new List<Context> { premiumUser });
        Step($"Charlie (premium, explicit override):");
        Step($"  checkout-v2  = {charlieCheckout}");
        Step($"  banner-color = {charlieBanner}");
        Step($"  max-retries  = {charlieRetries}");

        // ==============================================================
        // 7. CACHING STATISTICS
        // ==============================================================
        Section("7. Caching Statistics");

        var stats = client.Flags.Stats;
        Step($"Cache hits:   {stats.CacheHits}");
        Step($"Cache misses: {stats.CacheMisses}");

        // Read the same flags a few more times to demonstrate cache hits
        for (int i = 0; i < 50; i++)
        {
            checkoutV2.Get();
            bannerColor.Get();
            maxRetries.Get();
        }

        var statsAfter = client.Flags.Stats;
        Step($"After 150 extra reads:");
        Step($"  Cache hits:   {statsAfter.CacheHits}");
        Step($"  Cache misses: {statsAfter.CacheMisses}");
        Step("All repeated evaluations served from local cache");

        // ==============================================================
        // 8. CONTEXT REGISTRATION FLUSH
        // ==============================================================
        Section("8. Context Registration Flush");

        // Flush any pending context registrations to the server.
        await client.Flags.FlushContextsAsync();
        Step("Pending context registrations flushed to server");

        // List registered contexts for the user type
        var registeredContexts = await client.Flags.ListContextsAsync("user");
        Step($"Registered user contexts: {registeredContexts.Count}");
        foreach (var ctx in registeredContexts)
        {
            var key = ctx.TryGetValue("key", out var k) ? k?.ToString() : "?";
            Console.WriteLine($"     - {key}");
        }

        // ==============================================================
        // 9. REAL-TIME UPDATES
        // ==============================================================
        Section("9. Real-Time Updates");

        // 9a. Register change listeners
        var globalChanges = new List<FlagChangeEvent>();
        client.Flags.OnChange(evt =>
        {
            globalChanges.Add(evt);
            Console.WriteLine($"    [CHANGE] flag={evt.Key}, source={evt.Source}");
        });
        Step("Global change listener registered");

        var retryChanges = new List<FlagChangeEvent>();
        maxRetries.OnChange(evt => retryChanges.Add(evt));
        Step("Flag-specific listener registered for max-retries");

        // 9b. Trigger a change via the management API
        Step("Updating max-retries default in production via management API...");
        await demoFlags.MaxRetries.UpdateAsync(environments: new Dictionary<string, Dictionary<string, object?>>
        {
            ["production"] = new() { ["enabled"] = true, ["default"] = 7.0 },
        });

        // Give the WebSocket a moment to deliver the update
        await Task.Delay(2000);

        var newRetries = maxRetries.Get();
        Step($"max-retries after live update = {newRetries}");
        Step($"Global changes received: {globalChanges.Count}");
        Step($"max-retries specific changes: {retryChanges.Count}");

        // 9c. Manual refresh
        await client.Flags.RefreshAsync();
        Step("Manual refresh completed");
        Step($"Connection status: {client.Flags.ConnectionStatus}");

        // ==============================================================
        // 10. ENVIRONMENT COMPARISON
        // ==============================================================
        Section("10. Environment Comparison");

        // Disconnect from production to compare environments via Tier 1
        await client.Flags.DisconnectAsync();
        Step("Disconnected from production");

        foreach (var env in new[] { "development", "staging", "production" })
        {
            await client.Flags.ConnectAsync(env, timeout: 10);

            // Set context provider back to Alice for consistent comparison
            client.Flags.SetContextProvider(() => new List<Context> { currentUser });

            var envCheckout = checkoutV2.Get();
            var envBanner = bannerColor.Get();
            var envRetries = maxRetries.Get();

            Step($"[{env,-12}] checkout-v2={envCheckout}, banner-color={envBanner}, max-retries={envRetries}");

            await client.Flags.DisconnectAsync();
        }

        // ==============================================================
        // 11. TIER 1 EXPLICIT EVALUATE
        // ==============================================================
        Section("11. Tier 1 Explicit Evaluate");

        // Tier 1 evaluate: stateless, no provider or cache. Useful for
        // one-off server-side evaluations or edge functions.
        var t1Context = new List<Context>
        {
            new("user", "user-42", new Dictionary<string, object?>
            {
                ["plan"] = "enterprise",
                ["beta"] = false,
            }),
        };

        var t1Checkout = await client.Flags.EvaluateAsync("checkout-v2", "staging", t1Context);
        Step($"Tier 1 evaluate checkout-v2 (staging, enterprise): {t1Checkout}");

        var t1Retries = await client.Flags.EvaluateAsync("max-retries", "production", t1Context);
        Step($"Tier 1 evaluate max-retries (production, enterprise): {t1Retries}");

        var t1Banner = await client.Flags.EvaluateAsync("banner-color", "production",
            new List<Context>
            {
                new("user", "user-premium", new Dictionary<string, object?> { ["plan"] = "premium" }),
            });
        Step($"Tier 1 evaluate banner-color (production, premium): {t1Banner}");

        // ==============================================================
        // 12. CLEANUP
        // ==============================================================
        await FlagsDemoSetup.TeardownDemoFlagsAsync(client, demoFlags);

        // ==============================================================
        // DONE
        // ==============================================================
        Section("ALL DONE");
        Console.WriteLine("  The Flags Runtime SDK showcase completed successfully.");
        Console.WriteLine("  If you got here, Smpl Flags runtime is ready to ship.\n");

        return 0;
    }
}
