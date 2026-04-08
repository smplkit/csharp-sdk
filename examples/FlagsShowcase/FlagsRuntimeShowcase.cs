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
///   4. Evaluate flags for different users (lazy init on first .Get())
///   5. Explicit context override
///   6. Caching statistics
///   7. Context registration flush
///   8. Real-time updates (change listeners + management API change)
///   9. Environment comparison
///  10. Cleanup
///
/// This script creates, modifies, and deletes real flags.
///
/// Prerequisites:
///     - .NET 8.0 SDK
///     - A valid smplkit API key, provided via SMPLKIT_API_KEY env var or ~/.smplkit config file
///
/// Usage:
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
        var demoFlags = await FlagsRuntimeSetup.SetupDemoFlagsAsync(client);

        // ==============================================================
        // 1. TYPED FLAG DECLARATIONS
        // ==============================================================
        Section("1. Typed Flag Declarations");

        // Declare typed handles with code-level defaults. These are the
        // compile-time contract between your app and the flag service.
        // No ConnectAsync needed — first .Get() triggers lazy init.
        var checkoutV2 = client.Flags.BooleanFlag("checkout-v2", defaultValue: false);
        Step($"BooleanFlag declared: key={checkoutV2.Key}, default={checkoutV2.Default}");

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
        // 4. EVALUATE FLAGS FOR DIFFERENT USERS
        // ==============================================================
        Section("4. Evaluate Flags for Different Users");

        // First .Get() triggers lazy init (fetches flag definitions,
        // opens WebSocket). No ConnectAsync needed.
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
        // 5. EXPLICIT CONTEXT OVERRIDE
        // ==============================================================
        Section("5. Explicit Context Override");

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
        // 6. CACHING STATISTICS
        // ==============================================================
        Section("6. Caching Statistics");

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
        // 7. CONTEXT REGISTRATION FLUSH
        // ==============================================================
        Section("7. Context Registration Flush");

        // Flush any pending context registrations to the server.
        await client.Flags.FlushContextsAsync();
        Step("Pending context registrations flushed to server");

        // ==============================================================
        // 8. REAL-TIME UPDATES
        // ==============================================================
        Section("8. Real-Time Updates");

        // 8a. Register change listeners
        var globalChanges = new List<FlagChangeEvent>();
        client.Flags.OnChange(evt =>
        {
            globalChanges.Add(evt);
            Console.WriteLine($"    [CHANGE] flag={evt.Key}, source={evt.Source}");
        });
        Step("Global change listener registered");

        var retryChanges = new List<FlagChangeEvent>();
        client.Flags.OnChange("max-retries", evt => retryChanges.Add(evt));
        Step("Flag-specific listener registered for max-retries");

        // 8b. Trigger a change via the management API
        Step("Updating max-retries default in production via management API...");
        demoFlags.MaxRetries.SetEnvironmentDefault("production", 7.0);
        await demoFlags.MaxRetries.SaveAsync();

        // Give the WebSocket a moment to deliver the update
        await Task.Delay(2000);

        var newRetries = maxRetries.Get();
        Step($"max-retries after live update = {newRetries}");
        Step($"Global changes received: {globalChanges.Count}");
        Step($"max-retries specific changes: {retryChanges.Count}");

        // 8c. Manual refresh
        await client.Flags.RefreshAsync();
        Step("Manual refresh completed");
        Step($"Connection status: {client.Flags.ConnectionStatus}");

        // ==============================================================
        // 9. ENVIRONMENT COMPARISON
        // ==============================================================
        Section("9. Environment Comparison");

        foreach (var env in new[] { "development", "staging", "production" })
        {
            using var envClient = new SmplClient(new SmplClientOptions
            {
                Environment = env,
                Service = "showcase-service",
            });

            // Set context provider to Alice for consistent comparison
            envClient.Flags.SetContextProvider(() => new List<Context> { currentUser });

            // First .Get() triggers lazy init — no ConnectAsync needed
            var envCheckout = envClient.Flags.BooleanFlag("checkout-v2", defaultValue: false).Get();
            var envBanner = envClient.Flags.StringFlag("banner-color", defaultValue: "blue").Get();
            var envRetries = envClient.Flags.NumberFlag("max-retries", defaultValue: 3).Get();

            Step($"[{env,-12}] checkout-v2={envCheckout}, banner-color={envBanner}, max-retries={envRetries}");
        }

        // ==============================================================
        // 10. CLEANUP
        // ==============================================================
        await FlagsRuntimeSetup.TeardownDemoFlagsAsync(client, demoFlags);

        // ==============================================================
        // DONE
        // ==============================================================
        Section("ALL DONE");
        Console.WriteLine("  The Flags Runtime SDK showcase completed successfully.");
        Console.WriteLine("  If you got here, Smpl Flags runtime is ready to ship.\n");

        return 0;
    }
}
