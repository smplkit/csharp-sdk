using System.Globalization;
using System.Text.RegularExpressions;

namespace Smplkit.Internal;

/// <summary>
/// Shared helper utilities used across product clients.
/// </summary>
internal static class Helpers
{
    /// <summary>
    /// Converts a kebab-case or dot-separated key to a human-readable display name.
    /// <c>"checkout-v2"</c> becomes <c>"Checkout V2"</c>;
    /// <c>"com.acme.payments"</c> becomes <c>"Com Acme Payments"</c>.
    /// </summary>
    /// <param name="key">The key to convert.</param>
    /// <returns>A title-cased display name.</returns>
    internal static string KeyToDisplayName(string key)
    {
        // Replace hyphens, underscores, and dots with spaces
        var spaced = Regex.Replace(key, @"[-_.]", " ");
        // Title-case each word
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(spaced);
    }
}
