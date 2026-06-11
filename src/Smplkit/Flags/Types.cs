namespace Smplkit.Flags;

/// <summary>
/// Describes a flag declaration for buffered registration.
/// </summary>
/// <remarks>
/// Used by <c>client.Flags.Register</c> to queue declarations for bulk
/// registration. <see cref="Service"/> and <see cref="Environment"/> default
/// to <c>null</c>; the runtime client fills them from the active
/// <see cref="Smplkit.SmplClient"/> when it forwards declarations.
/// </remarks>
/// <param name="Id">The flag identifier.</param>
/// <param name="Type">The flag type (<c>BOOLEAN</c>, <c>STRING</c>, <c>NUMERIC</c>, <c>JSON</c>).</param>
/// <param name="Default">The default value.</param>
/// <param name="Service">Owning service identifier (optional).</param>
/// <param name="Environment">Owning environment key (optional).</param>
public sealed record FlagDeclaration(
    string Id,
    string Type,
    object? Default,
    string? Service = null,
    string? Environment = null);

/// <summary>
/// The value type of a flag.
/// </summary>
public enum FlagType
{
    /// <summary>Boolean flag type.</summary>
    Boolean,

    /// <summary>JSON flag type.</summary>
    Json,

    /// <summary>Numeric flag type.</summary>
    Numeric,

    /// <summary>String flag type.</summary>
    String,
}

/// <summary>
/// Extension methods for <see cref="FlagType"/>.
/// </summary>
public static class FlagTypeExtensions
{
    /// <summary>
    /// Returns the string representation of a <see cref="FlagType"/>.
    /// </summary>
    public static string ToWireString(this FlagType flagType) => flagType switch
    {
        FlagType.Boolean => "BOOLEAN",
        FlagType.Json => "JSON",
        FlagType.Numeric => "NUMERIC",
        FlagType.String => "STRING",
        _ => throw new ArgumentOutOfRangeException(nameof(flagType)),
    };

    /// <summary>
    /// Parses a string representation to a <see cref="FlagType"/>.
    /// </summary>
    public static FlagType ParseFlagType(string wireString) => wireString switch
    {
        "BOOLEAN" => FlagType.Boolean,
        "JSON" => FlagType.Json,
        "NUMERIC" => FlagType.Numeric,
        "STRING" => FlagType.String,
        _ => throw new ArgumentException($"Unknown flag type: {wireString}", nameof(wireString)),
    };
}
