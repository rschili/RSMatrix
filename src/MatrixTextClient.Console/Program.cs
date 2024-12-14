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
        services.AddTransient<MatrixService>();
    })
    .Build();

try
{
    var matrix = host.Services.GetRequiredService<MatrixService>();
    var connectionParameters = new ConnectionParameters(userid, password, device);
    var connection = await matrix.ConnectAsync(connectionParameters);
    Console.WriteLine(connection.Content);
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