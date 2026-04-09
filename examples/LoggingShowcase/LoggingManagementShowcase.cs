using Smplkit;
using Smplkit.Logging;

/// <summary>
/// Smpl Logging SDK -- Management API Showcase
/// ==============================================
///
/// Demonstrates the management plane of the smplkit Logging C# SDK:
///
///   1. Create a log group
///   2. Create a logger
///   3. Set a log level on the logger
///   4. Set an environment-specific level
///   5. List loggers and groups
///   6. Get a logger by key
///   7. Delete the logger and group
///
/// This script creates, modifies, and deletes real loggers and groups.
///
/// Prerequisites:
///     - .NET 8.0 SDK
///     - A valid smplkit API key, provided via SMPLKIT_API_KEY env var or ~/.smplkit config file
///
/// Usage:
///     dotnet run --project examples/LoggingShowcase
/// </summary>
public static class LoggingManagementShowcase
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

        // Clean up any leftover resources from a previous failed run
        try { await client.Logging.DeleteAsync("showcase-logger"); } catch { /* not found is fine */ }
        try { await client.Logging.DeleteGroupAsync("showcase-group"); } catch { /* not found is fine */ }

        // ==============================================================
        // 1. CREATE A LOG GROUP
        // ==============================================================
        Section("1. Create a Log Group");

        var group = client.Logging.NewGroup("showcase-group", name: "Showcase Group");
        Step($"NewGroup created locally: key={group.Key}, name={group.Name}");

        await group.SaveAsync();
        Step($"Group saved: id={group.Id}, key={group.Key}");

        // ==============================================================
        // 2. CREATE A LOGGER
        // ==============================================================
        Section("2. Create a Logger");

        var logger = client.Logging.New("showcase-logger", name: "Showcase Logger", managed: true);
        Step($"New logger created locally: key={logger.Key}, name={logger.Name}");

        await logger.SaveAsync();
        Step($"Logger saved: id={logger.Id}, key={logger.Key}");

        // ==============================================================
        // 3. SET LOG LEVEL ON THE LOGGER
        // ==============================================================
        Section("3. Set Log Level");

        logger.SetLevel(LogLevel.Info);
        Step($"Level set locally: {logger.Level}");

        await logger.SaveAsync();
        Step($"Logger saved with level: {logger.Level}");

        // ==============================================================
        // 4. SET ENVIRONMENT-SPECIFIC LEVEL
        // ==============================================================
        Section("4. Set Environment Level");

        logger.SetEnvironmentLevel("production", LogLevel.Error);
        Step("Environment level set locally: production=Error");

        await logger.SaveAsync();
        Step($"Logger saved with environment override");
        Step($"  Environments: [{string.Join(", ", logger.Environments.Keys)}]");

        // ==============================================================
        // 5. LIST LOGGERS AND GROUPS
        // ==============================================================
        Section("5. List Loggers and Groups");

        var allLoggers = await client.Logging.ListAsync();
        Step($"Total loggers: {allLoggers.Count}");
        foreach (var l in allLoggers)
        {
            Console.WriteLine($"     - {l.Key} (id={l.Id}, level={l.Level})");
        }

        var allGroups = await client.Logging.ListGroupsAsync();
        Step($"Total groups: {allGroups.Count}");
        foreach (var g in allGroups)
        {
            Console.WriteLine($"     - {g.Key} (id={g.Id}, level={g.Level})");
        }

        // ==============================================================
        // 6. GET A LOGGER BY KEY
        // ==============================================================
        Section("6. Get Logger by Key");

        try
        {
            var fetched = await client.Logging.GetAsync("showcase-logger");
            Step($"Fetched logger: key={fetched.Key}, level={fetched.Level}");
            Step($"  Name: {fetched.Name}");
            Step($"  Created: {fetched.CreatedAt}");
            Step($"  Updated: {fetched.UpdatedAt}");
        }
        catch (Exception ex)
        {
            Step($"Error fetching logger: {ex.Message}");
        }

        // ==============================================================
        // 7. DELETE LOGGER AND GROUP
        // ==============================================================
        Section("7. Cleanup");

        try
        {
            await client.Logging.DeleteAsync("showcase-logger");
            Step("Deleted showcase-logger");
        }
        catch (Exception ex)
        {
            Step($"Error deleting logger: {ex.Message}");
        }

        try
        {
            await client.Logging.DeleteGroupAsync("showcase-group");
            Step("Deleted showcase-group");
        }
        catch (Exception ex)
        {
            Step($"Error deleting group: {ex.Message}");
        }

        // ==============================================================
        // DONE
        // ==============================================================
        Section("MANAGEMENT SHOWCASE COMPLETE");
        Console.WriteLine("  The Logging Management showcase completed successfully.");
    }
}
