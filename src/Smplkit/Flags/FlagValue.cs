namespace Smplkit.Flags;

/// <summary>
/// A constrained value entry on a <see cref="Flag"/>.
/// </summary>
/// <remarks>
/// Lives in <see cref="Flag.Values"/>. Immutable — author values via
/// <see cref="Flag.AddValue"/> / <see cref="Flag.RemoveValue"/> /
/// <see cref="Flag.ClearValues"/>.
/// </remarks>
public sealed record FlagValue(string Name, object? Value);

/// <summary>
/// A single targeting rule on a <see cref="Flag"/>.
/// </summary>
/// <remarks>
/// Lives in <see cref="FlagEnvironment.Rules"/>. Immutable — author rules via
/// the <see cref="Smplkit.Rule"/> fluent builder and pass through
/// <see cref="Flag.AddRule"/>.
/// </remarks>
/// <param name="Logic">JSON Logic predicate. Empty dict means "always match".</param>
/// <param name="Value">Value to serve when <paramref name="Logic"/> evaluates truthy.</param>
/// <param name="Description">Human-readable label (optional).</param>
public sealed record FlagRule(
    IReadOnlyDictionary<string, object?> Logic,
    object? Value = null,
    string? Description = null);

/// <summary>
/// Per-environment configuration on a <see cref="Flag"/>.
/// </summary>
/// <remarks>
/// Lives at <c>flag.Environments[envName]</c>. Immutable — mutate via
/// <see cref="Flag.AddRule"/> / <see cref="Flag.EnableRules"/> /
/// <see cref="Flag.DisableRules"/> / <see cref="Flag.SetDefault"/> /
/// <see cref="Flag.ClearRules"/> with <c>environment</c>.
/// </remarks>
/// <param name="Enabled">Whether the flag is active in this environment.</param>
/// <param name="Default">Environment-specific default override (null means no override).</param>
/// <param name="Rules">Targeting rules to evaluate, in order.</param>
public sealed record FlagEnvironment(
    bool Enabled = true,
    object? Default = null,
    IReadOnlyList<FlagRule>? Rules = null)
{
    /// <summary>The targeting rules, in evaluation order.</summary>
    public IReadOnlyList<FlagRule> Rules { get; init; } = Rules ?? Array.Empty<FlagRule>();
}
