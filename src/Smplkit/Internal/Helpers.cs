using System.Globalization;
using System.Text.RegularExpressions;

namespace Smplkit.Internal;

/// <summary>
/// Shared helper utilities used across product clients.
/// </summary>
internal static class Helpers
{
    /// <summary>Default page size for runtime fetch-all loops (matches the server cap).</summary>
    internal const int RuntimePageSize = 1000;

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

    /// <summary>
    /// Drives an offset-paginated list endpoint until the server returns a short
    /// page. <paramref name="fetchPage"/> is invoked once per page with
    /// 1-based page numbers and <see cref="RuntimePageSize"/> as the page size;
    /// when it returns fewer than <see cref="RuntimePageSize"/> items, iteration
    /// stops. Termination is purely length-based — the server's
    /// <c>meta.total</c> is not consulted.
    /// </summary>
    /// <typeparam name="T">Element type returned by each page.</typeparam>
    /// <param name="fetchPage">Per-page fetch (pageNumber, pageSize, ct) → batch.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The concatenated list of all pages.</returns>
    internal static async Task<List<T>> FetchAllPagesAsync<T>(
        Func<int, int, CancellationToken, Task<List<T>>> fetchPage,
        CancellationToken ct = default)
    {
        var all = new List<T>();
        var page = 1;
        while (true)
        {
            var batch = await fetchPage(page, RuntimePageSize, ct).ConfigureAwait(false);
            all.AddRange(batch);
            if (batch.Count < RuntimePageSize) break;
            page++;
        }
        return all;
    }
}
