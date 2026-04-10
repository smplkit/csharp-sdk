namespace Smplkit.Flags;

/// <summary>
/// The value type of a flag.
/// </summary>
public enum FlagType
{
    /// <summary>Boolean flag type.</summary>
    Boolean,

    /// <summary>String flag type.</summary>
    String,

    /// <summary>Numeric flag type.</summary>
    Numeric,

    /// <summary>JSON flag type.</summary>
    Json,
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
        FlagType.String => "STRING",
        FlagType.Numeric => "NUMERIC",
        FlagType.Json => "JSON",
        _ => throw new ArgumentOutOfRangeException(nameof(flagType)),
    };

    /// <summary>
    /// Parses a string representation to a <see cref="FlagType"/>.
    /// </summary>
    public static FlagType ParseFlagType(string wireString) => wireString switch
    {
        "BOOLEAN" => FlagType.Boolean,
        "STRING" => FlagType.String,
        "NUMERIC" => FlagType.Numeric,
        "JSON" => FlagType.Json,
        _ => throw new ArgumentException($"Unknown flag type: {wireString}", nameof(wireString)),
    };
}
