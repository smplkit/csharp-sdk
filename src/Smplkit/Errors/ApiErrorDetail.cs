using System.Text.Json;

namespace Smplkit.Errors;

/// <summary>
/// Represents a single error from an API error response.
/// </summary>
public sealed class ApiErrorDetail
{
    /// <summary>Gets the status code string from the error object.</summary>
    public string? Status { get; }

    /// <summary>Gets the short title of the error.</summary>
    public string? Title { get; }

    /// <summary>Gets the detailed human-readable description of the error.</summary>
    public string? Detail { get; }

    /// <summary>Gets the source field that caused the error, if available.</summary>
    public ApiErrorSource? Source { get; }

    /// <summary>Initializes a new instance of <see cref="ApiErrorDetail"/>.</summary>
    public ApiErrorDetail(string? status, string? title, string? detail, ApiErrorSource? source)
    {
        Status = status;
        Title = title;
        Detail = detail;
        Source = source;
    }

    /// <summary>Returns this error detail as a JSON string.</summary>
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
