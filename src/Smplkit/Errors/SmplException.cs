using System.Text.Json;

namespace Smplkit.Errors;

/// <summary>
/// Represents a single error from a JSON:API error response.
/// </summary>
public sealed class ApiErrorDetail
{
    /// <summary>Gets the HTTP status code string from the error object.</summary>
    public string? Status { get; }

    /// <summary>Gets the short title of the error.</summary>
    public string? Title { get; }

    /// <summary>Gets the detailed human-readable description of the error.</summary>
    public string? Detail { get; }

    /// <summary>Gets the source location that caused the error.</summary>
    public ApiErrorSource? Source { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="ApiErrorDetail"/>.
    /// </summary>
    public ApiErrorDetail(string? status, string? title, string? detail, ApiErrorSource? source)
    {
        Status = status;
        Title = title;
        Detail = detail;
        Source = source;
    }

    /// <summary>
    /// Serializes this error detail to a JSON string for debugging.
    /// </summary>
    public string ToJsonString()
    {
        var parts = new Dictionary<string, object?>();
        if (Status is not null) parts["status"] = Status;
        if (Title is not null) parts["title"] = Title;
        if (Detail is not null) parts["detail"] = Detail;
        if (Source is not null)
        {
            var src = new Dictionary<string, object?>();
            if (Source.Pointer is not null) src["pointer"] = Source.Pointer;
            if (src.Count > 0) parts["source"] = src;
        }
        return JsonSerializer.Serialize(parts);
    }
}

/// <summary>
/// Represents the source of a JSON:API error.
/// </summary>
public sealed class ApiErrorSource
{
    /// <summary>Gets the JSON pointer to the field that caused the error.</summary>
    public string? Pointer { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="ApiErrorSource"/>.
    /// </summary>
    public ApiErrorSource(string? pointer)
    {
        Pointer = pointer;
    }
}

/// <summary>
/// Base exception for all smplkit SDK errors.
/// </summary>
public class SmplException : Exception
{
    /// <summary>
    /// Gets the HTTP status code from the response, if available.
    /// </summary>
    public int? StatusCode { get; }

    /// <summary>
    /// Gets the raw response body, if available.
    /// </summary>
    public string? ResponseBody { get; }

    /// <summary>
    /// Gets the parsed JSON:API error details from the response, if available.
    /// </summary>
    public IReadOnlyList<ApiErrorDetail> Errors { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="SmplException"/>.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="statusCode">The HTTP status code, if applicable.</param>
    /// <param name="responseBody">The raw response body, if applicable.</param>
    /// <param name="innerException">The inner exception, if any.</param>
    /// <param name="errors">Parsed JSON:API error details, if available.</param>
    public SmplException(
        string message,
        int? statusCode = null,
        string? responseBody = null,
        Exception? innerException = null,
        IReadOnlyList<ApiErrorDetail>? errors = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
        Errors = errors ?? Array.Empty<ApiErrorDetail>();
    }

    /// <summary>
    /// Returns a string representation including all JSON:API error details.
    /// </summary>
    public override string ToString()
    {
        if (Errors.Count == 0)
            return base.ToString();

        var typeName = GetType().Name;
        if (Errors.Count == 1)
        {
            return $"{typeName}: {Message}\nError: {Errors[0].ToJsonString()}\n{base.ToString()}";
        }

        var lines = new System.Text.StringBuilder();
        lines.Append($"{typeName}: {Message}\nErrors:");
        for (var i = 0; i < Errors.Count; i++)
        {
            lines.Append($"\n  [{i}] {Errors[i].ToJsonString()}");
        }
        lines.Append($"\n{base.ToString()}");
        return lines.ToString();
    }

    /// <summary>
    /// Derives a human-readable message from parsed JSON:API errors.
    /// </summary>
    /// <param name="errors">The parsed error details.</param>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <returns>A derived message string.</returns>
    internal static string DeriveMessage(IReadOnlyList<ApiErrorDetail> errors, int statusCode)
    {
        if (errors.Count == 0)
            return $"HTTP {statusCode}";

        var first = errors[0];
        var msg = first.Detail ?? first.Title ?? first.Status ?? "An API error occurred";

        if (errors.Count > 1)
            msg += $" (and {errors.Count - 1} more error{(errors.Count - 1 == 1 ? "" : "s")})";

        return msg;
    }
}
