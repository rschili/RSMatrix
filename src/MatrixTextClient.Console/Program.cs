// See https://aka.ms/new-console-template for more information
using MatrixTextClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureServices(services =>
    {
        services.AddHttpClient();
        services.AddTransient<MatrixService>();
    })
    .Build();

try
{
    var matrix = host.Services.GetRequiredService<MatrixService>();
    var connection = matrix.GetContentAsync("https://www.microsoft.com");
    Console.WriteLine(connection);
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