namespace Smplkit.Management;

/// <summary>
/// Whether an environment participates in the canonical ordering.
/// </summary>
/// <remarks>
/// <see cref="Standard"/> environments (production, staging, development) appear in
/// <c>AccountSettings.EnvironmentOrder</c> and the standard console columns.
/// <see cref="AdHoc"/> environments are transient (preview branches, dev sandboxes)
/// and are excluded from the standard ordering.
/// </remarks>
public enum EnvironmentClassification
{
    /// <summary>A customer deploy target (production, staging, development).</summary>
    Standard,

    /// <summary>A transient target (preview branch, individual sandbox).</summary>
    AdHoc,
}

/// <summary>Extension methods for <see cref="EnvironmentClassification"/>.</summary>
public static class EnvironmentClassificationExtensions
{
    /// <summary>Returns the wire-format string ("STANDARD" or "AD_HOC").</summary>
    public static string ToWireString(this EnvironmentClassification classification) => classification switch
    {
        EnvironmentClassification.Standard => "STANDARD",
        EnvironmentClassification.AdHoc => "AD_HOC",
        _ => throw new ArgumentOutOfRangeException(nameof(classification)),
    };

    /// <summary>Parses the wire-format string. Unknown values default to <see cref="EnvironmentClassification.Standard"/>.</summary>
    public static EnvironmentClassification ParseClassification(string? wire) => wire switch
    {
        "AD_HOC" => EnvironmentClassification.AdHoc,
        _ => EnvironmentClassification.Standard,
    };
}
