using Smplkit;
using Smplkit.Logging;

/// <summary>
/// Helper that creates and tears down demo loggers and groups for the runtime showcase.
///
/// Creates:
///   - runtime-group       — a log group for organizing demo loggers
///   - runtime-logger-1    — a logger with Info level
///   - runtime-logger-2    — a logger with Warn level and production override
/// </summary>
public static class LoggingRuntimeSetup
{
    public record DemoData(LogGroup Group, Logger Logger1, Logger Logger2);

    public static async Task<DemoData> SetupAsync(SmplClient client)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 60));
        Console.WriteLine("  Demo Logging Setup");
        Console.WriteLine(new string('=', 60));
        Console.WriteLine();

        // Clean up any leftover resources from a previous failed run
        foreach (var key in new[] { "runtime-logger-1", "runtime-logger-2" })
        {
            try { await client.Logging.DeleteAsync(key); } catch { /* not found is fine */ }
        }
        try { await client.Logging.DeleteGroupAsync("runtime-group"); } catch { /* not found is fine */ }

        // ------------------------------------------------------------------
        // 1. Create a log group
        // ------------------------------------------------------------------
        Console.WriteLine("  -> Creating runtime-group...");

        var group = client.Logging.NewGroup("runtime-group", name: "Runtime Group");
        await group.SaveAsync();
        Console.WriteLine($"     Created: id={group.Id}, id={group.Id}");

        // ------------------------------------------------------------------
        // 2. Create runtime-logger-1 with Info level
        // ------------------------------------------------------------------
        Console.WriteLine("  -> Creating runtime-logger-1...");

        var logger1 = client.Logging.New("runtime-logger-1", name: "Runtime Logger 1", managed: true);
        logger1.SetLevel(LogLevel.Info);
        await logger1.SaveAsync();
        Console.WriteLine($"     Created: id={logger1.Id}, id={logger1.Id}, level={logger1.Level}");

        // ------------------------------------------------------------------
        // 3. Create runtime-logger-2 with Warn level and production override
        // ------------------------------------------------------------------
        Console.WriteLine("  -> Creating runtime-logger-2...");

        var logger2 = client.Logging.New("runtime-logger-2", name: "Runtime Logger 2", managed: true);
        logger2.SetLevel(LogLevel.Warn);
        logger2.SetEnvironmentLevel("production", LogLevel.Error);
        await logger2.SaveAsync();
        Console.WriteLine($"     Created: id={logger2.Id}, id={logger2.Id}, level={logger2.Level}");

        Console.WriteLine();
        Console.WriteLine("  Demo loggers created successfully.");
        return new DemoData(group, logger1, logger2);
    }

    public static async Task TeardownAsync(SmplClient client, DemoData data)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 60));
        Console.WriteLine("  Demo Logging Teardown");
        Console.WriteLine(new string('=', 60));
        Console.WriteLine();

        try
        {
            await client.Logging.DeleteAsync("runtime-logger-1");
            Console.WriteLine($"  -> Deleted runtime-logger-1 ({data.Logger1.Id})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  -> Error deleting runtime-logger-1: {ex.Message}");
        }

        try
        {
            await client.Logging.DeleteAsync("runtime-logger-2");
            Console.WriteLine($"  -> Deleted runtime-logger-2 ({data.Logger2.Id})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  -> Error deleting runtime-logger-2: {ex.Message}");
        }

        try
        {
            await client.Logging.DeleteGroupAsync("runtime-group");
            Console.WriteLine($"  -> Deleted runtime-group ({data.Group.Id})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  -> Error deleting runtime-group: {ex.Message}");
        }

        Console.WriteLine("  Teardown complete.");
    }
}
