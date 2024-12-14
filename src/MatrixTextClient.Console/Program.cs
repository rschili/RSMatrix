using MatrixTextClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// load environment variables or a .env file
DotNetEnv.Env.TraversePath().Load();
var userid = Environment.GetEnvironmentVariable("MATRIX_USER_ID");
var password = Environment.GetEnvironmentVariable("MATRIX_PASSWORD");
var device = Environment.GetEnvironmentVariable("MATRIX_DEVICE_ID");
if (string.IsNullOrWhiteSpace(userid) || string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(device))
{
    throw new ArgumentException("Please provide the required environment variables: MATRIX_USER_ID, MATRIX_PASSWORD, MATRIX_DEVICE_ID");
}

//set up dependency injection
var host = new HostBuilder()
    .ConfigureServices(services =>
    {
        services.AddHttpClient();
        services.AddLogging(services => services.AddSimpleConsole());
    })
    .Build();

//Using CancellationToken as a shutdown mechanism
var cancellationTokenSource = new CancellationTokenSource();
Console.CancelKeyPress += (sender, e) =>
{ // allows shutting down the app using Ctrl+C
    e.Cancel = true;
    cancellationTokenSource.Cancel();
};

try
{
    using var client = await MatrixClient.ConnectAsync(userid, password, device, host.Services.GetRequiredService<IHttpClientFactory>(), host.Services.GetRequiredService<ILogger<MatrixClient>>());
    client.BeginSyncLoop();
    await Task.Delay(Timeout.Infinite, cancellationTokenSource.Token); // Keep the console open
    //Alternatives would be using a SemaphoreSlim or ManualResetEventSlim, but this seems most intuitive
}
catch (OperationCanceledException)
{
    Console.WriteLine("Shutdown requested...");
}
catch (Exception ex)
{
    Console.WriteLine(ex.Message);
}
finally
{
    if (host is IAsyncDisposable asyncDisposable)
    {
        await asyncDisposable.DisposeAsync();
    }
    else if (host is IDisposable disposable)
    {
        disposable.Dispose();
    }
}