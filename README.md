# RSMatrix

## Scope

For now this is just a little sideproject to support the matrix.org chat protocol in .NET.
I tried several libraries and got none of them to work as they were all still in beta or abandoned.

Implementing this using the [Client spec version 1.17](https://spec.matrix.org/v1.17/client-server-api/)

The first goal was to have a simple text client which can retrieve and send text messages for Chatbot development.
It is slowly getting more comprehensive and moving towards a full client implementation.

I'm trying to keep it as simple as possible without sacrificing essential features.

RSMatrix is text-only. It does not support voice or video calls, and there are no plans to add them.

## Example

Check the github repository for a minimalistic [console example](https://github.com/rschili/RSMatrix/blob/main/src/RSMatrix.Console/Program.cs).
Here is the basic usage:

```cs
await using MatrixClient client = await MatrixClient.LoginAsync(userId, password, cancellationToken,
    deviceId: "MYBOT");
```

Without a `homeserver` URL, the client looks it up through the `.well-known` file of the user ID's domain.
The client immediately starts syncing after login or resume.

To handle messages:

```cs
await foreach (var message in client.Messages.ReadAllAsync(cancellationToken))
{
    Console.WriteLine(message);
    await message.Room.SendTypingNotificationAsync();
    if(message.Body.Equals("ping", StringComparison.OrdinalIgnoreCase))
        await message.SendResponseAsync("pong!");
}
```

The cancellation token cancels logging in or resuming.
To stop the sync, dispose the client. That stops sync, waits until it has ended and completes `client.Messages`.

## Reconnecting and resuming

Once the client has logged in, it retries network errors, unavailable servers and rate limits until it is disposed. Before the login, `LoginAsync` throws on these errors instead, because they more likely point to a wrong configuration.

Instead of logging in every time and replacine the earlier sessions, you probably want to resume instead.
To do that, a storage for the session data is needed:

```cs
const string store = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mybot", "matrix");
var session = MatrixClient.GetStoredSession(store, userId);
await using MatrixClient client = session != null
    ? await MatrixClient.ResumeAsync(session, cancellationToken)
    : await MatrixClient.LoginAsync(userId, password, cancellationToken, storeDirectory: store);
```

`ResumeAsync` continues at the stored sync position, so messages sent while the client was offline are delivered after resuming.
The store holds one session per user: the access token, the sync position and the device's encryption keys, as unencrypted files, protect the directory like a password file.

## Issues and health

`client.Issues` reports problems the client keeps running through: messages it could not decrypt, devices that can't read our encrypted messages, a device the account's identity hasn't signed, changed identities of other users, failed auto-joins and interrupted syncs. Reading it is optional. It buffers the latest 100 issues.

```cs
_ = Task.Run(async () =>
{
    await foreach (var issue in client.Issues.ReadAllAsync(cancellationToken))
        logger.LogWarning("{Issue}", issue);
});
```

For troubleshooting, set the environment variable `RSMATRIX_DIAGNOSTICS` before starting the client:

- `Unhandled`: logs a warning, once per run, for each kind of event, message type or field the client receives but doesn't handle. The first example goes to the debug level.
- `Verbose`: also logs every request and response body at the debug level. Passwords and tokens are removed.

## End-to-end encryption

Encryption is on when the client has a store directory. Without one, encrypted messages are ignored and sending to encrypted rooms throws. On its own, encryption only publishes keys for this device and never changes the account, so a store costs nothing for bots in unencrypted rooms.

Encrypted rooms only work between devices the account vouches for, called cross-signed devices. As the spec recommends, RSMatrix only shares keys with cross-signed devices and only shows encrypted messages from them, and other clients increasingly do the same. Logging in or resuming never creates or changes the account's cross-signing identity:

- If the account has an identity, pass its recovery key once per new device, and the client signs the device with it:

  ```cs
  await using MatrixClient client = await MatrixClient.LoginAsync(userId, password, cancellationToken,
      storeDirectory: store, recoveryKey: Environment.GetEnvironmentVariable("MATRIX_RECOVERY_KEY"));
  ```

- If the account has none, or its recovery key is lost, `SetUpCrossSigningAsync` creates a new identity, replacing the existing one, and returns its recovery key. Save the key somewhere safe, apart from the store; other devices of the account need it once.

  ```cs
  string recoveryKey = await client.SetUpCrossSigningAsync(password);
  ```

`client.EncryptionState` shows where the client stands: `Off` without a store, `NoIdentity`, `DeviceNotSigned` (also reported on `client.Issues` as `DeviceNotCrossSigned`, with the reason) or `Ready`.

> [!WARNING]
> The encryption code was written by AI, from the Matrix specification. It has been tested locally against a synapse server, but I have not done any real-world testing yet.

## Testing

`make test` runs the server-free unit tests used by CI.

For opt-in tests against a disposable local Synapse homeserver (requires Podman):

```sh
make test-integration-disposable  # start, test, and remove automatically
```

Or keep the server running while developing:

```sh
make matrix-up
make test-integration
make matrix-down               # removes the server and all its data
```

See [local homeserver notes](src/RSMatrix.IntegrationTests/Homeserver/README.md)
for prerequisites and lifecycle caveats. The integration project is built with
the solution, but CI still runs only the unit-test project. Live tests skip when
`RSMATRIX_INTEGRATION_URL` is unset; the Make targets above set it automatically.
