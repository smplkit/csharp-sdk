namespace Smplkit.Errors;

/// <summary>
/// Raised when the server rejects a request due to validation errors (HTTP 422).
/// </summary>
public class SmplValidationException : SmplException
{
    /// <summary>
    /// Initializes a new instance of <see cref="SmplValidationException"/>.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="responseBody">The raw response body, if available.</param>
    public SmplValidationException(string message, string? responseBody = null)
        : base(message, statusCode: 422, responseBody: responseBody)
    {
    }
}
