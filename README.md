# RSMatrix

## Scope

For now this is just a little sideproject to support the matrix.org chat protocol in .NET.
I tried several libraries and got none of them to work as they were all still in beta or abandoned.

Implementing this using the [Client spec version 1.17](https://spec.matrix.org/v1.17/client-server-api/)

The goal is to have a simple text client which can retrieve and send text messages for Chatbot development. It's not intended as a comprehensive library for building a client UI.

I will try to make it conform to all requirements. It sends automatic receipts and read notifications.

End-to-End Encryption is **NOT** supported. I looked into it a bit, but seemed too much work and I do not need it myself.

## Example

Check the github repository for a minimalistic [console example](https://github.com/rschili/RSMatrix/blob/main/src/RSMatrix.Console/Program.cs).
Here is the basic usage:

```cs
MatrixTextClient client = await MatrixTextClient.ConnectAsync(userid, password, device,
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
