using RSMatrix;
using Microsoft.Extensions.Logging;
using RSMatrix.Models;

// load environment variables or a .env file
DotNetEnv.Env.TraversePath().Load();
var userid = Environment.GetEnvironmentVariable("MATRIX_USER_ID");
// Optional with a store, where the server creates the device ID and the store keeps it.
var device = Environment.GetEnvironmentVariable("MATRIX_DEVICE_ID");
// Needed to log in. With a store, later runs resume the session and only need it if the server ended the session.
var password = Environment.GetEnvironmentVariable("MATRIX_PASSWORD");
// Optional. Keeps the session and the encryption keys between runs and enables end-to-end encryption.
var store = Environment.GetEnvironmentVariable("MATRIX_STORE");
// Optional. Skips the homeserver lookup through the user ID's domain.
var homeserver = Environment.GetEnvironmentVariable("MATRIX_HOMESERVER");
// Needed once per new device if the account already has cross-signing keys.
var recoveryKey = Environment.GetEnvironmentVariable("MATRIX_RECOVERY_KEY");
if (string.IsNullOrWhiteSpace(userid))
    throw new ArgumentException("Please provide the required environment variable MATRIX_USER_ID.");
if (string.IsNullOrWhiteSpace(store) && string.IsNullOrWhiteSpace(device))
    throw new ArgumentException("Please provide MATRIX_DEVICE_ID, or MATRIX_STORE to keep the session between runs.");

using var loggerFactory = LoggerFactory.Create(logging =>
{
    logging.AddSimpleConsole(options =>
    {
        options.IncludeScopes = true;
        options.SingleLine = true;
        options.TimestampFormat = "hh:mm:ss ";
    });
    logging.SetMinimumLevel(LogLevel.Information);
});

//Using CancellationToken as a shutdown mechanism
var cancellationTokenSource = new CancellationTokenSource();
Console.CancelKeyPress += (sender, e) =>
{ // allows shutting down the app using Ctrl+C
    e.Cancel = true;
    cancellationTokenSource.Cancel();
};

try
{
    var logger = loggerFactory.CreateLogger<MatrixClient>();
    var session = string.IsNullOrWhiteSpace(store) ? null : MatrixClient.GetStoredSession(store, userid);
    if (session == null && string.IsNullOrWhiteSpace(password))
        throw new ArgumentException("Please provide MATRIX_PASSWORD to log in.");
    await using var client = session != null
        ? await MatrixClient.ResumeAsync(session, cancellationTokenSource.Token,
            password: string.IsNullOrWhiteSpace(password) ? null : password,
            logger: logger,
            recoveryKey: string.IsNullOrWhiteSpace(recoveryKey) ? null : recoveryKey)
        : await MatrixClient.LoginAsync(userid, password!, cancellationTokenSource.Token,
            deviceId: string.IsNullOrWhiteSpace(device) ? null : device,
            storeDirectory: string.IsNullOrWhiteSpace(store) ? null : store,
            homeserver: string.IsNullOrWhiteSpace(homeserver) ? null : new Uri(homeserver),
            logger: logger,
            recoveryKey: string.IsNullOrWhiteSpace(recoveryKey) ? null : recoveryKey);

    await foreach (var update in client.Updates.ReadAllAsync(cancellationTokenSource.Token))
    {
        switch (update.Value)
        {
            case NewMessage { Message: var message } when !message.IsOwn:
                await MessageReceivedAsync(message);
                break;
            case ClientIssue issue:
                Console.WriteLine($"{issue.Kind}: {issue.Message}");
                break;
        }
    }
    Console.WriteLine("Sync has ended.");
}
catch (OperationCanceledException)
{
    Console.WriteLine("Shutdown requested...");
}
catch (Exception ex)
{
    Console.WriteLine(ex.Message);
}
Console.WriteLine("Goodbye!");


async Task MessageReceivedAsync(Message message)
{
    Console.WriteLine(message);
    var age = DateTimeOffset.Now - message.Timestamp;
    if(message.Body.Contains("ping") && age.TotalSeconds < 10)
    {
        await message.Room.SendTypingNotificationAsync();
        await Task.Delay(2000);
        await message.ReplyAsync("pong!");
    }
}