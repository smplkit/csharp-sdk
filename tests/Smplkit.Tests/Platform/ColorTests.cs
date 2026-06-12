using Smplkit.Platform;
using Xunit;

namespace Smplkit.Tests.Platform;

/// <summary>
/// Tests for <see cref="Color"/> — fail-fast validation per PR #127 rule 6.
/// </summary>
public class ColorTests
{
    [Theory]
    [InlineData("#fff")]
    [InlineData("#FFF")]
    [InlineData("#abcdef")]
    [InlineData("#ABCDEF")]
    [InlineData("#abcdefab")]
    [InlineData("#000000")]
    public void Constructor_ValidHex_Accepts(string hex)
    {
        var color = new Color(hex);
        Assert.Equal(hex.ToLowerInvariant(), color.Hex);
    }

    [Theory]
    [InlineData("fff")]                  // missing #
    [InlineData("#xy")]                  // non-hex
    [InlineData("#")]                    // empty
    [InlineData("#abcd")]                // 4 digits
    [InlineData("#abcde")]               // 5 digits
    [InlineData("#abcdefabc")]           // 9 digits
    [InlineData("not a color")]
    [InlineData("")]
    public void Constructor_InvalidHex_Throws(string hex)
    {
        Assert.Throws<ArgumentException>(() => new Color(hex));
    }

    [Fact]
    public void Constructor_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new Color(null!));
    }

    [Fact]
    public void Hex_NormalizesToLowercase()
    {
        var color = new Color("#FF00CC");
        Assert.Equal("#ff00cc", color.Hex);
    }

    [Theory]
    [InlineData(0, 0, 0, "#000000")]
    [InlineData(255, 255, 255, "#ffffff")]
    [InlineData(239, 68, 68, "#ef4444")]
    [InlineData(16, 32, 48, "#102030")]
    public void Rgb_ValidComponents_BuildsHex(int r, int g, int b, string expected)
    {
        var color = Color.Rgb(r, g, b);
        Assert.Equal(expected, color.Hex);
    }

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(256, 0, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 256, 0)]
    [InlineData(0, 0, -1)]
    [InlineData(0, 0, 256)]
    public void Rgb_OutOfRange_Throws(int r, int g, int b)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Color.Rgb(r, g, b));
    }

    [Fact]
    public void Equality_IsCanonical()
    {
        Assert.Equal(new Color("#FFF"), new Color("#fff"));
        Assert.Equal(new Color("#ef4444"), Color.Rgb(239, 68, 68));
    }

    [Fact]
    public void ImplicitConversion_FromString_Works()
    {
        Color color = "#abc";
        Assert.Equal("#abc", color.Hex);
    }

    [Fact]
    public void ToString_ReturnsHex()
    {
        Assert.Equal("#ef4444", new Color("#EF4444").ToString());
    }
}
