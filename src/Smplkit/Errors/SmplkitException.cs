namespace Smplkit.Errors;

/// <summary>
/// Base exception for all smplkit SDK errors.
/// </summary>
/// <remarks>
/// Maps to the Python SDK's <c>Error</c> base class. Subclasses match the Python
/// hierarchy with a <c>Exception</c> suffix per C# convention:
/// <see cref="NotFoundException"/>, <see cref="ConflictException"/>,
/// <see cref="ConnectionException"/>, <see cref="TimeoutException"/>,
/// <see cref="ValidationException"/>.
/// </remarks>
public class SmplkitException : Exception
{
    /// <summary>Gets the HTTP status code from the response, if available.</summary>
    public int? StatusCode { get; }

    /// <summary>Gets the raw response body, if available.</summary>
    public string? ResponseBody { get; }

    /// <summary>Gets the parsed error details from the response, if available.</summary>
    public IReadOnlyList<ApiErrorDetail> Errors { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="SmplkitException"/>.
    /// </summary>
    public SmplkitException(
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

    /// <inheritdoc />
    public override string ToString()
    {
        if (Errors.Count == 0)
            return base.ToString();

        var typeName = GetType().Name;
        if (Errors.Count == 1)
            return $"{typeName}: {Message}\nError: {Errors[0].ToJsonString()}\n{base.ToString()}";

        var lines = new System.Text.StringBuilder();
        lines.Append($"{typeName}: {Message}\nErrors:");
        for (var i = 0; i < Errors.Count; i++)
            lines.Append($"\n  [{i}] {Errors[i].ToJsonString()}");
        lines.Append($"\n{base.ToString()}");
        return lines.ToString();
    }

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

/// <summary>Raised when a requested resource does not exist.</summary>
public class NotFoundException : SmplkitException
{
    /// <summary>Initializes a new instance of <see cref="NotFoundException"/>.</summary>
    public NotFoundException(
        string message,
        string? responseBody = null,
        IReadOnlyList<ApiErrorDetail>? errors = null)
        : base(message, statusCode: 404, responseBody: responseBody, errors: errors)
    {
    }
}

/// <summary>Raised when an operation conflicts with current state.</summary>
public class ConflictException : SmplkitException
{
    /// <summary>Initializes a new instance of <see cref="ConflictException"/>.</summary>
    public ConflictException(
        string message,
        string? responseBody = null,
        IReadOnlyList<ApiErrorDetail>? errors = null)
        : base(message, statusCode: 409, responseBody: responseBody, errors: errors)
    {
    }
}

/// <summary>Raised when the server rejects a request due to validation errors.</summary>
public class ValidationException : SmplkitException
{
    /// <summary>Initializes a new instance of <see cref="ValidationException"/>.</summary>
    public ValidationException(
        string message,
        string? responseBody = null,
        int statusCode = 422,
        IReadOnlyList<ApiErrorDetail>? errors = null)
        : base(message, statusCode: statusCode, responseBody: responseBody, errors: errors)
    {
    }
}

/// <summary>Raised when a network request fails.</summary>
public class ConnectionException : SmplkitException
{
    /// <summary>Initializes a new instance of <see cref="ConnectionException"/>.</summary>
    public ConnectionException(string message, Exception? innerException = null)
        : base(message, statusCode: null, responseBody: null, innerException: innerException)
    {
    }
}

/// <summary>Raised when the server returns 402 — the requested operation requires an upgraded plan.</summary>
public class PaymentRequiredException : SmplkitException
{
    /// <summary>Initializes a new instance of <see cref="PaymentRequiredException"/>.</summary>
    public PaymentRequiredException(
        string message,
        string? responseBody = null,
        IReadOnlyList<ApiErrorDetail>? errors = null)
        : base(message, statusCode: 402, responseBody: responseBody, errors: errors)
    {
    }
}

/// <summary>Raised when an operation exceeds its timeout.</summary>
public class TimeoutException : SmplkitException
{
    /// <summary>Initializes a new instance of <see cref="TimeoutException"/>.</summary>
    public TimeoutException(string message, Exception? innerException = null)
        : base(message, statusCode: null, responseBody: null, innerException: innerException)
    {
    }
}
