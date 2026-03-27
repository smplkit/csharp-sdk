namespace Smplkit.Errors;

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
    /// Initializes a new instance of <see cref="SmplException"/>.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="statusCode">The HTTP status code, if applicable.</param>
    /// <param name="responseBody">The raw response body, if applicable.</param>
    /// <param name="innerException">The inner exception, if any.</param>
    public SmplException(
        string message,
        int? statusCode = null,
        string? responseBody = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }
}
