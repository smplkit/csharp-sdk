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

    /// <summary>Output writer — field so tests can substitute it without Console.Error races.</summary>
    internal static System.IO.TextWriter Out = Console.Error;

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
        Out.WriteLine($"[smplkit:{subsystem}] {ts} {message}");
    }
}
