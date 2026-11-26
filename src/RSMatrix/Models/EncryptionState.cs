namespace RSMatrix.Models;

/// <summary>How ready the client is for encrypted rooms. See <see cref="MatrixClient.EncryptionState"/>.</summary>
public enum EncryptionState
{
    /// <summary>The client was logged in without a store directory. Encrypted messages are ignored and sending to encrypted rooms throws.</summary>
    Off,

    /// <summary>
    /// The account has no cross-signing identity. Clients that follow the spec won't share keys with this device.
    /// <see cref="MatrixClient.SetUpCrossSigningAsync"/> creates one.
    /// </summary>
    NoIdentity,

    /// <summary>
    /// The account has a cross-signing identity, but it hasn't signed this device. Resume or log in once with the recovery key.
    /// <see cref="MatrixClient.Issues"/> reports why as <see cref="ClientIssueKind.DeviceNotCrossSigned"/>.
    /// </summary>
    DeviceNotSigned,

    /// <summary>This device is cross-signed.</summary>
    Ready
}
