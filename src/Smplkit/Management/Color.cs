using System.Text.RegularExpressions;

namespace Smplkit.Management;

/// <summary>
/// A color, expressed as a CSS hex string. Immutable — construct a new
/// <see cref="Color"/> to change the value.
/// </summary>
/// <example>
/// <code>
/// new Color("#ef4444");           // 6-digit hex
/// new Color("#fff");              // 3-digit shorthand
/// new Color("#ef4444aa");         // 8-digit with alpha
/// Color.Rgb(239, 68, 68);         // RGB components (0–255)
/// </code>
/// </example>
public sealed class Color : IEquatable<Color>
{
    private static readonly Regex HexRegex = new(
        @"^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$",
        RegexOptions.Compiled);

    /// <summary>Gets the normalized (lowercase) hex string.</summary>
    public string Hex { get; }

    /// <summary>
    /// Initializes a new <see cref="Color"/> from a CSS hex string.
    /// </summary>
    /// <param name="hex">A CSS hex string: "#RGB", "#RRGGBB", or "#RRGGBBAA".</param>
    /// <exception cref="ArgumentNullException">If <paramref name="hex"/> is null.</exception>
    /// <exception cref="ArgumentException">If <paramref name="hex"/> is not a valid hex format.</exception>
    public Color(string hex)
    {
        if (hex is null)
            throw new ArgumentNullException(nameof(hex), "Color hex must be a string.");
        if (!HexRegex.IsMatch(hex))
            throw new ArgumentException(
                $"Invalid color '{hex}': must be a CSS hex string like '#RGB', '#RRGGBB', or '#RRGGBBAA'",
                nameof(hex));
        Hex = hex.ToLowerInvariant();
    }

    /// <summary>
    /// Constructs a <see cref="Color"/> from 0–255 RGB components.
    /// </summary>
    /// <param name="r">Red component (0–255).</param>
    /// <param name="g">Green component (0–255).</param>
    /// <param name="b">Blue component (0–255).</param>
    /// <exception cref="ArgumentOutOfRangeException">If any component is out of range.</exception>
    public static Color Rgb(int r, int g, int b)
    {
        if (r < 0 || r > 255)
            throw new ArgumentOutOfRangeException(nameof(r),
                $"Color.Rgb r must be in range 0-255, got {r}");
        if (g < 0 || g > 255)
            throw new ArgumentOutOfRangeException(nameof(g),
                $"Color.Rgb g must be in range 0-255, got {g}");
        if (b < 0 || b > 255)
            throw new ArgumentOutOfRangeException(nameof(b),
                $"Color.Rgb b must be in range 0-255, got {b}");
        return new Color($"#{r:x2}{g:x2}{b:x2}");
    }

    /// <inheritdoc />
    public bool Equals(Color? other) => other is not null && Hex == other.Hex;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Color other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Hex.GetHashCode();

    /// <inheritdoc />
    public override string ToString() => Hex;

    /// <summary>Implicit conversion from string. Validates via <see cref="Color(string)"/>.</summary>
    public static implicit operator Color(string hex) => new(hex);
}
