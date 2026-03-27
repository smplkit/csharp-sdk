namespace Smplkit.Errors;

/// <summary>
/// Raised when a requested resource does not exist (HTTP 404).
/// </summary>
public class SmplNotFoundException : SmplException
{
    /// <summary>
    /// Initializes a new instance of <see cref="SmplNotFoundException"/>.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="responseBody">The raw response body, if available.</param>
    public SmplNotFoundException(string message, string? responseBody = null)
        : base(message, statusCode: 404, responseBody: responseBody)
    {
    }
}
