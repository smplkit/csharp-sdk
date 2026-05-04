using GenApp = Smplkit.Internal.Generated.App;

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
    /// <summary>Returns the generated wire enum used by the app client.</summary>
    public static GenApp.EnvironmentClassification ToWireString(this EnvironmentClassification classification) => classification switch
    {
        EnvironmentClassification.Standard => GenApp.EnvironmentClassification.STANDARD,
        EnvironmentClassification.AdHoc => GenApp.EnvironmentClassification.AD_HOC,
        _ => throw new ArgumentOutOfRangeException(nameof(classification)),
    };

    /// <summary>Maps the generated wire enum to the public SDK enum. Unknown / null values default to <see cref="EnvironmentClassification.Standard"/>.</summary>
    public static EnvironmentClassification ParseClassification(GenApp.EnvironmentClassification? wire) => wire switch
    {
        GenApp.EnvironmentClassification.AD_HOC => EnvironmentClassification.AdHoc,
        _ => EnvironmentClassification.Standard,
    };
}
