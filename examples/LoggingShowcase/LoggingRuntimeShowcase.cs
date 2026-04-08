using Smplkit;
using Smplkit.Logging;

/// <summary>
/// Smpl Logging SDK -- Runtime Showcase
/// =======================================
///
/// Demonstrates the runtime plane of the smplkit Logging C# SDK:
///
///   1. Start the logging runtime
///   2. Register global change listeners
///   3. Register scoped (per-logger-key) change listeners
///
/// Prerequisites:
///     - .NET 8.0 SDK
///     - A valid smplkit API key, provided via SMPLKIT_API_KEY env var or ~/.smplkit config file
///
/// Usage:
///     dotnet run --project examples/LoggingShowcase
/// </summary>
public static class LoggingRuntimeShowcase
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

    public static async Task RunAsync()
    {
        using var client = new SmplClient(new SmplClientOptions
        {
            Environment = "production",
            Service = "logging-showcase",
        });

        // ==============================================================
        // 0. SET UP DEMO LOGGERS
        // ==============================================================
        var demoData = await LoggingRuntimeSetup.SetupAsync(client);

        // ==============================================================
        // 1. START THE LOGGING RUNTIME
        // ==============================================================
        Section("1. Start Logging Runtime");

        try
        {
            await client.Logging.StartAsync();
            Step("Logging runtime started (loggers loaded, WebSocket connected)");
        }
        catch (Exception ex)
        {
            Step($"StartAsync failed (expected if no server): {ex.Message}");
        }

        // ==============================================================
        // 2. REGISTER GLOBAL CHANGE LISTENER
        // ==============================================================
        Section("2. Global Change Listener");

        var globalChanges = new List<LoggerChangeEvent>();
        client.Logging.OnChange(evt =>
        {
            globalChanges.Add(evt);
            Console.WriteLine($"    [GLOBAL CHANGE] logger={evt.Key}, level={evt.Level}, source={evt.Source}");
        });
        Step("Global change listener registered");
        Step("Any logger level change will be logged above");

        // ==============================================================
        // 3. REGISTER SCOPED CHANGE LISTENER
        // ==============================================================
        Section("3. Scoped Change Listener");

        var scopedChanges = new List<LoggerChangeEvent>();
        client.Logging.OnChange("runtime-logger-1", evt =>
        {
            scopedChanges.Add(evt);
            Console.WriteLine($"    [SCOPED CHANGE] logger={evt.Key}, level={evt.Level}, source={evt.Source}");
        });
        Step("Scoped change listener registered for 'runtime-logger-1'");
        Step("Only changes to runtime-logger-1 will trigger this listener");

        Step($"Global changes captured so far: {globalChanges.Count}");
        Step($"Scoped changes captured so far: {scopedChanges.Count}");

        // ==============================================================
        // 4. CLEANUP
        // ==============================================================
        await LoggingRuntimeSetup.TeardownAsync(client, demoData);

        // ==============================================================
        // DONE
        // ==============================================================
        Section("RUNTIME SHOWCASE COMPLETE");
        Console.WriteLine("  The Logging Runtime showcase completed successfully.");
    }
}
