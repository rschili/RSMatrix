namespace RSMatrix;

/// <summary>
/// The server logged out this device and deleted it, for example after a password change or when the device was
/// removed in another client. The stored session and the device's encryption keys are gone. Log in again with
/// <see cref="MatrixClient.LoginAsync"/>, which creates a new device.
/// </summary>
public sealed class SessionEndedException : InvalidOperationException
{
    public SessionEndedException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
