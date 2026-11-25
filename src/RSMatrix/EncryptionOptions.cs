namespace RSMatrix;

/// <summary>
/// Turns on end-to-end encryption. Pass it to <see cref="MatrixClient.ConnectAsync"/>. Without it, the client
/// ignores encrypted messages and refuses to send to encrypted rooms; nothing else changes.
/// </summary>
/// <remarks>
/// <para>
/// Encrypted rooms only work between devices that trust each other. Matrix does that with cross-signing: the account
/// has a set of signing keys, and a device it signed counts as the account's own. As the spec recommends, this client
/// only shares its keys with cross-signed devices and only shows encrypted messages from them, and other clients
/// increasingly do the same. So each device of the account must be cross-signed, including this one.
/// </para>
/// <para>
/// The server keeps the account's signing keys in "secret storage", encrypted with a recovery key that only you have.
/// While connecting, the client checks this device:
/// </para>
/// <list type="bullet">
/// <item>The device is cross-signed already: nothing to do. Neither callback is called.</item>
/// <item>The account has no signing keys yet: the client creates them together with a new recovery key,
/// and passes the key to <see cref="SaveRecoveryKeyAsync"/>.</item>
/// <item>The account has signing keys, and this device is new: the client asks <see cref="GetRecoveryKeyAsync"/>
/// for the recovery key, unlocks the signing keys with it and signs the device. This happens once per device.</item>
/// </list>
/// <para>
/// If the recovery key is lost, <see cref="MatrixClient.ResetCrossSigningAsync"/> replaces the signing keys
/// and creates a new recovery key.
/// </para>
/// </remarks>
public sealed class EncryptionOptions
{
    /// <summary>
    /// Directory for this device's private encryption keys. They are stored as plain JSON, so protect the directory
    /// like a password file. It belongs to one user and device ID and must stay across restarts: without it, the
    /// client can't read messages it received before, and the device ID can't be used again. Only one client at a
    /// time can use it. It can be the same directory as the session store.
    /// </summary>
    public required string StoreDirectory { get; init; }

    /// <summary>
    /// Asked while connecting, when this device is new and needs the account's recovery key to be cross-signed.
    /// Return the key, in the format other clients show it ("EsTc oGp8 ..."; spaces don't matter). A bot can read it
    /// from its configuration, an app can ask its user.
    /// </summary>
    /// <remarks>
    /// Return null if you don't have it. The client then connects without signing this device and reports
    /// <see cref="Models.ClientIssueKind.DeviceNotCrossSigned"/> in <see cref="MatrixClient.Issues"/>. Clients that follow
    /// the spec's recommendation won't exchange encrypted messages with it until it is signed.
    /// A key that doesn't belong to the account makes <see cref="MatrixClient.ConnectAsync"/> throw.
    /// </remarks>
    public required Func<CancellationToken, Task<string?>> GetRecoveryKeyAsync { get; init; }

    /// <summary>
    /// Called with a new recovery key when the client sets up cross-signing for the account: on the first connect of
    /// an account that has none, or from <see cref="MatrixClient.ResetCrossSigningAsync"/>. Save it somewhere safe, apart
    /// from <see cref="StoreDirectory"/>; every new device of the account needs it, and nobody can recover it for you.
    /// Together with the password it gives full control over the account's encryption identity.
    /// </summary>
    /// <remarks>
    /// The client waits for this callback before it changes anything on the server. If it throws, the setup stops,
    /// and the exception comes out of the call that started it.
    /// </remarks>
    public required Func<string, CancellationToken, Task> SaveRecoveryKeyAsync { get; init; }
}
