using System.Text.Json;

namespace Smplkit.Errors;

/// <summary>
/// Represents a single error from an API error response.
/// </summary>
public sealed class ApiErrorDetail
{
    /// <summary>Gets the status code string from the error object.</summary>
    public string? Status { get; }

    /// <summary>
    /// Gets the application-specific machine-readable error code
    /// (e.g. <c>environment_unmanaged</c>). Per JSON:API §7 and
    /// ADR-014, smplkit sets this on every error so callers can branch
    /// without string-matching the human <see cref="Detail"/> field.
    /// </summary>
    public string? Code { get; }

    /// <summary>Gets the short title of the error.</summary>
    public string? Title { get; }

    /// <summary>Gets the detailed human-readable description of the error.</summary>
    public string? Detail { get; }

    /// <summary>Gets the source field that caused the error, if available.</summary>
    public ApiErrorSource? Source { get; }

    /// <summary>
    /// Gets additional structured context for the error (e.g.
    /// <c>{"environment": "staging"}</c>). Values are JSON-decoded
    /// natively (string, double, bool, list, dictionary, or null).
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Meta { get; }

    /// <summary>Initializes a new instance of <see cref="ApiErrorDetail"/>.</summary>
    public ApiErrorDetail(
        string? status,
        string? code,
        string? title,
        string? detail,
        ApiErrorSource? source,
        IReadOnlyDictionary<string, object?>? meta)
    {
        Status = status;
        Code = code;
        Title = title;
        Detail = detail;
        Source = source;
        Meta = meta;
    }

    /// <summary>
    /// Backwards-compatible constructor for callers built against the
    /// older 4-arg shape. <see cref="Code"/> and <see cref="Meta"/>
    /// default to <c>null</c>.
    /// </summary>
    public ApiErrorDetail(string? status, string? title, string? detail, ApiErrorSource? source)
        : this(status, null, title, detail, source, null)
    {
    }

    /// <summary>Returns this error detail as a JSON string.</summary>
    public string ToJsonString()
    {
        var parts = new Dictionary<string, object?>();
        if (Status is not null) parts["status"] = Status;
        if (Code is not null) parts["code"] = Code;
        if (Title is not null) parts["title"] = Title;
        if (Detail is not null) parts["detail"] = Detail;
        if (Source is not null)
        {
            var src = new Dictionary<string, object?>();
            if (Source.Pointer is not null) src["pointer"] = Source.Pointer;
            if (src.Count > 0) parts["source"] = src;
        }
        if (Meta is not null && Meta.Count > 0) parts["meta"] = Meta;
        return JsonSerializer.Serialize(parts);
    }
}

/// <summary>
/// Represents the source of an API error.
/// </summary>
public sealed class ApiErrorSource
{
    /// <summary>Gets the pointer to the field that caused the error.</summary>
    public string? Pointer { get; }

    /// <summary>Initializes a new instance of <see cref="ApiErrorSource"/>.</summary>
    public ApiErrorSource(string? pointer)
    {
        Pointer = pointer;
    }
}
