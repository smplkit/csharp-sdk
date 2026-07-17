using System.Reflection;
using Smplkit.Internal;
using Xunit;

namespace Smplkit.Tests.Internal;

/// <summary>
/// Tests for <see cref="SdkVersion"/>: the default User-Agent product token
/// and the assembly-metadata version resolution behind it.
/// </summary>
public class SdkVersionTests
{
    /// <summary>The version the SDK assembly actually carries, resolved the
    /// same way production code does: informational version stripped of
    /// <c>+build</c> metadata, falling back to the assembly version.</summary>
    private static string ExpectedAssemblyVersion()
    {
        var assembly = typeof(SmplClient).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus >= 0 ? informational[..plus] : informational;
        }
        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    [Fact]
    public void UserAgent_HasSdkProductTokenAndAssemblyVersion()
    {
        Assert.StartsWith("smplkit-sdk-csharp/", SdkVersion.UserAgent, StringComparison.Ordinal);
        Assert.Equal($"smplkit-sdk-csharp/{ExpectedAssemblyVersion()}", SdkVersion.UserAgent);
    }

    [Fact]
    public void UserAgent_VersionPart_ContainsNoBuildMetadata()
    {
        Assert.DoesNotContain("+", SdkVersion.UserAgent, StringComparison.Ordinal);
        var version = SdkVersion.UserAgent["smplkit-sdk-csharp/".Length..];
        Assert.Matches(@"^\d+\.\d+\.\d+", version);
    }

    [Fact]
    public void Resolve_FromAssembly_MatchesPureResolution()
    {
        var assembly = typeof(SmplClient).Assembly;
        Assert.Equal(ExpectedAssemblyVersion(), SdkVersion.Resolve(assembly));
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3+abc1234", "1.2.3")]
    [InlineData("2.0.0-rc.1+deadbeef", "2.0.0-rc.1")]
    public void Resolve_PrefersInformationalVersion_StrippingBuildMetadata(
        string informational, string expected)
    {
        Assert.Equal(expected, SdkVersion.Resolve(informational, new Version(9, 9, 9, 9)));
    }

    [Fact]
    public void Resolve_NoInformationalVersion_FallsBackToAssemblyVersion()
    {
        Assert.Equal("4.5.6", SdkVersion.Resolve(null, new Version(4, 5, 6, 0)));
    }

    [Fact]
    public void Resolve_TwoPartAssemblyVersion_NormalizesToThreeParts()
    {
        Assert.Equal("4.5.0", SdkVersion.Resolve("   ", new Version(4, 5)));
    }

    [Fact]
    public void Resolve_NoVersionInformationAtAll_YieldsZeroVersion()
    {
        Assert.Equal("0.0.0", SdkVersion.Resolve(null, null));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("+abc1234", null)]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3+abc1234", "1.2.3")]
    public void StripBuildMetadata_HandlesEmptyAndSuffixedInputs(string? input, string? expected)
    {
        Assert.Equal(expected, SdkVersion.StripBuildMetadata(input));
    }
}
