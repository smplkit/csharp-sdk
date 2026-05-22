using System.Linq;
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
    /// <returns>A typed <see cref="SmplkitException"/> subclass for the given status code.</returns>
    internal static SmplkitException CreateException(int statusCode, string body)
    {
        var errors = ParseErrors(body);
        var message = SmplkitException.DeriveMessage(errors, statusCode);

        return statusCode switch
        {
            400 or 422 => new ValidationException(message, responseBody: body, statusCode: statusCode, errors: errors),
            402 => new PaymentRequiredException(message, body, errors),
            404 => new NotFoundException(message, body, errors),
            409 => new ConflictException(message, body, errors),
            _ => new SmplkitException(message, statusCode: statusCode, responseBody: body, errors: errors),
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
                var code = errorElement.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString()
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

                IReadOnlyDictionary<string, object?>? meta = null;
                if (errorElement.TryGetProperty("meta", out var metaElement)
                    && metaElement.ValueKind == JsonValueKind.Object)
                {
                    var dict = new Dictionary<string, object?>();
                    foreach (var prop in metaElement.EnumerateObject())
                    {
                        dict[prop.Name] = JsonElementToObject(prop.Value);
                    }
                    meta = dict;
                }

                result.Add(new ApiErrorDetail(status, code, title, detail, source, meta));
            }

            return result;
        }
        catch (JsonException)
        {
            return Array.Empty<ApiErrorDetail>();
        }
    }

    /// <summary>
    /// Convert a JsonElement into a native .NET object — string, double,
    /// long, bool, null, list, or dictionary. Used to decode JSON:API
    /// <c>meta</c> values without requiring a fixed schema.
    /// </summary>
    private static object? JsonElementToObject(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.TryGetInt64(out var i) ? (object)i : el.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => el.EnumerateArray().Select(JsonElementToObject).ToList(),
            JsonValueKind.Object => el.EnumerateObject()
                .ToDictionary(p => p.Name, p => JsonElementToObject(p.Value)),
            _ => null,
        };
    }
}
