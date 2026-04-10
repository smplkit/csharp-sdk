namespace Smplkit.Errors;

/// <summary>
/// Raised when the server rejects a request due to validation errors.
/// </summary>
public class SmplValidationException : SmplException
{
    /// <summary>
    /// Initializes a new instance of <see cref="SmplValidationException"/>.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="responseBody">The raw response body, if available.</param>
    /// <param name="statusCode">The status code. Defaults to 422.</param>
    /// <param name="errors">Parsed error details, if available.</param>
    public SmplValidationException(
        string message,
        string? responseBody = null,
        int statusCode = 422,
        IReadOnlyList<ApiErrorDetail>? errors = null)
        : base(message, statusCode: statusCode, responseBody: responseBody, errors: errors)
    {
    }
}
