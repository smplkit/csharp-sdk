namespace Smplkit.Errors;

/// <summary>
/// Raised when an operation exceeds its timeout.
/// </summary>
public class SmplTimeoutException : SmplException
{
    /// <summary>
    /// Initializes a new instance of <see cref="SmplTimeoutException"/>.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception, if any.</param>
    public SmplTimeoutException(string message, Exception? innerException = null)
        : base(message, statusCode: null, responseBody: null, innerException: innerException)
    {
    }
}
