namespace RSMatrix;

/// <summary>
/// The server ended the session but allows logging in again with the same device (soft logout), and the client has
/// no password for that. Resume again with the password.
/// </summary>
public sealed class PasswordRequiredException : InvalidOperationException
{
    public PasswordRequiredException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
