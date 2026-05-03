namespace Smplkit.Logging;

/// <summary>
/// Per-environment configuration on a <see cref="Logger"/> or <see cref="LogGroup"/>.
/// </summary>
/// <remarks>
/// Lives at <c>logger.Environments[envName]</c>. Immutable — mutate via
/// <see cref="Logger.SetLevel"/> / <see cref="Logger.ClearLevel"/> /
/// <see cref="Logger.ClearAllEnvironmentLevels"/> with <c>environment</c>.
/// </remarks>
/// <param name="Level">Per-environment level override (null means no override).</param>
public sealed record LoggerEnvironment(LogLevel? Level = null);
