namespace Smplkit.Internal;

/// <summary>
/// Internal SMPLKIT_DEBUG diagnostic facility.
/// <para>
/// Set the environment variable <c>SMPLKIT_DEBUG=1</c> (or <c>true</c> / <c>yes</c>,
/// case-insensitive) to enable verbose output to stderr. All other values (including
/// unset) disable output. The variable is read once at class-load time and cached.
/// </para>
/// <para>Output bypasses the managed logging framework and writes directly to stderr.</para>
/// </summary>
internal static class Debug
{
    internal static bool Enabled = ParseDebugEnv(System.Environment.GetEnvironmentVariable("SMPLKIT_DEBUG"));

    // Per-async-context override of the output writer. Tests substitute a StringWriter
    // here to capture output without colliding with concurrently-running test classes
    // whose production code may also call Debug.Log. AsyncLocal scopes the override to
    // the test's ExecutionContext, so other tests' Debug.Log calls fall through to the
    // Console.Error default rather than writing into a foreign StringWriter.
    private static readonly System.Threading.AsyncLocal<System.IO.TextWriter?> _outOverride = new();

    /// <summary>Output writer — settable per-async-context so tests can capture in isolation.</summary>
    internal static System.IO.TextWriter Out
    {
        get => _outOverride.Value ?? Console.Error;
        set => _outOverride.Value = value;
    }

    private static readonly object _writeLock = new();

    internal static bool ParseDebugEnv(string? value)
    {
        if (value is null) return false;
        var v = value.Trim().ToLowerInvariant();
        return v == "1" || v == "true" || v == "yes";
    }

    /// <summary>
    /// Writes a single diagnostic line to stderr when debug is enabled.
    /// <para>Output format: <c>[smplkit:{subsystem}] {ISO-8601 timestamp} {message}\n</c></para>
    /// </summary>
    /// <param name="subsystem">Short tag identifying the calling subsystem.</param>
    /// <param name="message">Human-readable diagnostic message.</param>
    internal static void Log(string subsystem, string message)
    {
        if (!Enabled) return;
        var ts = DateTime.UtcNow.ToString("O");
        lock (_writeLock)
        {
            Out.WriteLine($"[smplkit:{subsystem}] {ts} {message}");
        }
    }
}
