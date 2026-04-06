using System.Text.Json;

namespace Smplkit.Errors;

/// <summary>
/// Parses JSON:API error response bodies and throws the appropriate
/// typed SDK exception.
/// </summary>
internal static class ApiErrorParser
{
    /// <summary>
    /// Parses a JSON:API error response body and creates the appropriate exception.
    /// </summary>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <param name="body">The raw response body string.</param>
    /// <returns>A typed <see cref="SmplException"/> subclass for the given status code.</returns>
    internal static SmplException CreateException(int statusCode, string body)
    {
        var errors = ParseErrors(body);
        var message = SmplException.DeriveMessage(errors, statusCode);

        return statusCode switch
        {
            400 or 422 => new SmplValidationException(message, responseBody: body, statusCode: statusCode, errors: errors),
            404 => new SmplNotFoundException(message, body, errors),
            409 => new SmplConflictException(message, body, errors),
            _ => new SmplException(message, statusCode: statusCode, responseBody: body, errors: errors),
        };
    }

    /// <summary>
    /// Attempts to parse JSON:API error details from a response body.
    /// Returns an empty list for non-JSON or malformed bodies.
    /// </summary>
    private static IReadOnlyList<ApiErrorDetail> ParseErrors(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return Array.Empty<ApiErrorDetail>();

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("errors", out var errorsElement)
                || errorsElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<ApiErrorDetail>();

            var result = new List<ApiErrorDetail>();
            foreach (var errorElement in errorsElement.EnumerateArray())
            {
                var status = errorElement.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String
                    ? s.GetString()
                    : null;
                var title = errorElement.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString()
                    : null;
                var detail = errorElement.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String
                    ? d.GetString()
                    : null;

                ApiErrorSource? source = null;
                if (errorElement.TryGetProperty("source", out var sourceElement)
                    && sourceElement.ValueKind == JsonValueKind.Object)
                {
                    var pointer = sourceElement.TryGetProperty("pointer", out var p) && p.ValueKind == JsonValueKind.String
                        ? p.GetString()
                        : null;
                    if (pointer is not null)
                        source = new ApiErrorSource(pointer);
                }

                result.Add(new ApiErrorDetail(status, title, detail, source));
            }

            return result;
        }
        catch (JsonException)
        {
            return Array.Empty<ApiErrorDetail>();
        }
    }
}
