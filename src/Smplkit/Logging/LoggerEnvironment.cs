namespace Smplkit.Logging;

/// <summary>
/// Per-environment configuration on a <see cref="Logger"/> or <see cref="LogGroup"/>.
/// </summary>
/// <remarks>
/// Lives at <c>logger.Environments[envName]</c> (an
/// <see cref="System.Collections.Generic.IReadOnlyDictionary{TKey,TValue}"/> of
/// environment name to <see cref="LoggerEnvironment"/>). Immutable — mutate the
/// override via <see cref="Logger.SetLevel"/> / <see cref="Logger.ClearLevel"/> /
/// <see cref="Logger.ClearAllEnvironmentLevels"/> with an <c>environment</c> argument.
/// </remarks>
/// <param name="Level">Per-environment level override (null means no override).</param>
public sealed record LoggerEnvironment(LogLevel? Level = null)
{
    /// <summary>Build a <see cref="LoggerEnvironment"/> from the raw wire-shaped
    /// per-environment dict (<c>{"level": "ERROR"}</c>). An entry with no level
    /// override yields <see cref="Level"/> = <c>null</c>.</summary>
    internal static LoggerEnvironment FromRaw(Dictionary<string, object?> raw)
    {
        LogLevel? level = null;
        if (raw.TryGetValue("level", out var v) && v is string s)
            level = LogLevelExtensions.TryParseLogLevel(s);
        return new LoggerEnvironment(level);
    }
}
