using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MatrixTextClient.Models;
using MatrixTextClient.Http;

namespace MatrixTextClient;

/// <summary>
/// The low level class for interacting with the Matrix server.
/// </summary>
public sealed class MatrixClient
{
    public MatrixClientCore Core { get; private init; }
    public ILogger Logger => Core.Logger;
    public MatrixId User => Core.User;

    private MatrixClient(MatrixClientCore core)
    {
        Core = core ?? throw new ArgumentNullException(nameof(core));
    }

    public static async Task<MatrixClient> ConnectAsync(string userId, string password, string deviceId, IHttpClientFactory httpClientFactory, CancellationToken cancellationToken, ILogger? logger = null)
    {
        var core = await MatrixClientCore.ConnectAsync(userId, password, deviceId, httpClientFactory, cancellationToken, logger).ConfigureAwait(false);
        var client = new MatrixClient(core);
        return client;
    }

    public async Task SyncAsync(int? millisecondsBetweenRequests = 1000, MatrixClientCore.SyncReceivedHandler? handler = null)
    {
        if (handler == null)
            handler = DefaultSyncReceivedHandler;

        var request = new SyncParameters
        {
            FullState = false,
            SetPresence = Presence.Online,
            Timeout = 60000
        };

        while (!Core.HttpClientParameters.CancellationToken.IsCancellationRequested)
        {
            var response = await MatrixHelper.GetSyncAsync(Core.HttpClientParameters, request).ConfigureAwait(false);
            if (response != null)
            {
                await handler(Core, response);
                request.Since = response.NextBatch;
            }
            //Throttle the requests
            if (millisecondsBetweenRequests != null)
                await Task.Delay(millisecondsBetweenRequests.Value, Core.HttpClientParameters.CancellationToken).ConfigureAwait(false);
        }
    }

    public static Task DefaultSyncReceivedHandler(MatrixClientCore client, SyncResponse response)
    {
        client.Logger.LogInformation("Received sync response");
        return Task.CompletedTask;
    }
}

