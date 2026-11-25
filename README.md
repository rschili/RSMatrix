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
MatrixClient client = await MatrixClient.ConnectAsync(userid, password, device,
    httpClientFactory, cancellationToken, logger);
```

and to handle messages:

```cs
await foreach (var message in client.Messages.ReadAllAsync(cancellationToken))
{
    Console.WriteLine(message);
    await message.Room.SendTypingNotificationAsync();
    if(message.Body.Equals("ping", StringComparison.OrdinalIgnoreCase))
        await message.SendResponseAsync("pong!");
}
```

Room and user information is cached and updated on the client object. Associated rooms and users are included in the message given to the handler.

## Reconnecting and resuming

Once the client has logged in, it retries network errors, unavailable servers and rate limits until the cancellation token is cancelled. Before the first login, `ConnectAsync` throws on these errors instead, because they more likely point to a wrong configuration.

Pass a directory for sessions to resume after a restart instead of logging in and syncing from scratch:

```cs
MatrixClient client = await MatrixClient.ConnectAsync(userid, password, device,
    httpClientFactory, cancellationToken, logger,
    sessionStoreDirectory: "/var/lib/mybot/matrix-session");
```

The directory keeps one session per user and device ID, with the access token and the sync position.
The files contain the access token unencrypted, so protect the directory as necessary. It can be the same directory as the encryption store.

## Issues and health

`client.Issues` reports problems the client keeps running through: messages it could not decrypt, devices that can't read our encrypted messages, a device that could not be cross-signed, changed identities of other users, failed auto-joins and interrupted syncs. Reading it is optional. It buffers the latest 100 issues.

```cs
_ = Task.Run(async () =>
{
    await foreach (var issue in client.Issues.ReadAllAsync(cancellationToken))
        logger.LogWarning("{Issue}", issue);
});
```

## End-to-end encryption

Pass `EncryptionOptions` to turn on encryption. Without them, nothing changes: encrypted messages are ignored and sending to encrypted rooms throws.

```cs
MatrixClient client = await MatrixClient.ConnectAsync(userid, password, device,
    httpClientFactory, cancellationToken, logger,
    encryption: new EncryptionOptions
    {
        StoreDirectory = "/var/lib/mybot/matrix-encryption",
        // Asked once per new device, when the account already has cross-signing keys.
        GetRecoveryKeyAsync = _ => Task.FromResult(Environment.GetEnvironmentVariable("MATRIX_RECOVERY_KEY")),
        // Called once with a new key, when RSMatrix sets up cross-signing for the account.
        SaveRecoveryKeyAsync = (key, _) => File.WriteAllTextAsync("/var/lib/mybot/matrix-recovery-key", key),
    });
```

The store directory holds this device's private keys and must survive restarts.

Encrypted rooms only work between devices the account vouches for, called cross-signed devices. As the spec recommends, RSMatrix only shares keys with cross-signed devices and only shows encrypted messages from them, and other clients increasingly do the same. RSMatrix cross-signs its own device while connecting:

- On an account without cross-signing keys, it creates them and a recovery key, which goes to `SaveRecoveryKeyAsync`. The keys are kept on the server, encrypted with the recovery key.
- On a new device of an account that has them, it asks `GetRecoveryKeyAsync` for the recovery key, once. Return null if you don't have it: the device then works unsigned, and `client.Issues` reports `DeviceNotCrossSigned`.
- If the recovery key is lost, `client.ResetCrossSigningAsync()` creates new keys and a new recovery key. Other devices of the account then need the new key once.

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
