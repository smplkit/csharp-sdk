namespace Smplkit.Errors;

/// <summary>
/// Raised when a network request fails.
/// </summary>
public class SmplConnectionException : SmplException
{
    /// <summary>
    /// Initializes a new instance of <see cref="SmplConnectionException"/>.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception, if any.</param>
    public SmplConnectionException(string message, Exception? innerException = null)
        : base(message, statusCode: null, responseBody: null, innerException: innerException)
    {
    }
}
