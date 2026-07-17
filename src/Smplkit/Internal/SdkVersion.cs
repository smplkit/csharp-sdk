using System.Reflection;

namespace Smplkit.Internal;

/// <summary>
/// Resolves the SDK's own release version from assembly metadata and exposes
/// the default <c>User-Agent</c> product token stamped on every outbound
/// request (HTTP and the WebSocket upgrade) when the caller has not supplied
/// one. The platform edge (CloudFront + AWS managed WAF rules) rejects
/// requests that carry no User-Agent header at all, and .NET's
/// <see cref="HttpClient"/> sends none by default.
/// </summary>
internal static class SdkVersion
{
    /// <summary>
    /// The default User-Agent product token, e.g. <c>smplkit-sdk-csharp/1.2.3</c>.
    /// Computed once per process from the SDK assembly's metadata.
    /// </summary>
    internal static string UserAgent { get; } =
        "smplkit-sdk-csharp/" + Resolve(typeof(SdkVersion).Assembly);

    /// <summary>
    /// Resolve the version token for <paramref name="assembly"/>: the
    /// informational version (which carries the release version the CI pack
    /// step injects) with any <c>+build</c> metadata stripped, else the plain
    /// assembly version, else <c>0.0.0</c>.
    /// </summary>
    internal static string Resolve(Assembly assembly)
        => Resolve(
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            assembly.GetName().Version);

    /// <summary>
    /// Pure core of <see cref="Resolve(Assembly)"/>, split out so every
    /// fallback branch is directly testable.
    /// </summary>
    internal static string Resolve(string? informationalVersion, Version? assemblyVersion)
    {
        var stripped = StripBuildMetadata(informationalVersion);
        if (stripped is not null)
            return stripped;
        if (assemblyVersion is not null)
            return $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{Math.Max(assemblyVersion.Build, 0)}";
        return "0.0.0";
    }

    /// <summary>
    /// Strip SemVer <c>+build</c> metadata (e.g. the commit SHA SourceLink
    /// appends to the informational version) so the UA stays a clean product
    /// token; returns <c>null</c> when there is no usable version text.
    /// </summary>
    internal static string? StripBuildMetadata(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
            return null;
        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        var version = plus >= 0 ? informationalVersion[..plus] : informationalVersion;
        return string.IsNullOrWhiteSpace(version) ? null : version;
    }
}
