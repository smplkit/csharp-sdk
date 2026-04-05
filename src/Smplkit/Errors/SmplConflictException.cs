namespace Smplkit.Errors;

/// <summary>
/// Raised when an operation conflicts with current state (HTTP 409).
/// For example, deleting a config that has children.
/// </summary>
public class SmplConflictException : SmplException
{
    /// <summary>
    /// Initializes a new instance of <see cref="SmplConflictException"/>.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="responseBody">The raw response body, if available.</param>
    /// <param name="errors">Parsed JSON:API error details, if available.</param>
    public SmplConflictException(
        string message,
        string? responseBody = null,
        IReadOnlyList<ApiErrorDetail>? errors = null)
        : base(message, statusCode: 409, responseBody: responseBody, errors: errors)
    {
    }
}
