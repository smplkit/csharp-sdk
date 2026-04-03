namespace Smplkit.Errors;

/// <summary>
/// Raised when a method requiring ConnectAsync() is called before connecting.
/// </summary>
public class SmplNotConnectedException : SmplException
{
    /// <summary>
    /// Initializes a new instance of <see cref="SmplNotConnectedException"/>
    /// with a default message.
    /// </summary>
    public SmplNotConnectedException()
        : base("SmplClient is not connected. Call ConnectAsync() first.")
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="SmplNotConnectedException"/>
    /// with a custom message.
    /// </summary>
    /// <param name="message">The error message.</param>
    public SmplNotConnectedException(string message)
        : base(message)
    {
    }
}
