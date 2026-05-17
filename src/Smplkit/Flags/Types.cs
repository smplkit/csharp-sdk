namespace Smplkit.Flags;

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
