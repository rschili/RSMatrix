using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Narrensicher.Matrix.Models;
using Narrensicher.Matrix.Http;
using System.Reflection.Metadata;

namespace Narrensicher.Matrix;

/// <summary>
/// The low level class for interacting with the Matrix server.
/// </summary>
public sealed class MatrixTextClient
{
    internal MatrixClientCore Core { get; private init; }
    private ILogger Logger => Core.Logger;
    public MatrixId CurrentUser => Core.User;

    public delegate Task MessageHandler(MatrixTextMessage message);

    private MessageHandler? _messageHandler = null; // We use this to track if the sync has been started

    private MatrixTextClient(MatrixClientCore core)
    {
        Core = core ?? throw new ArgumentNullException(nameof(core));
    }

    public static async Task<MatrixTextClient> ConnectAsync(string userId, string password, string deviceId, IHttpClientFactory httpClientFactory, CancellationToken cancellationToken, ILogger? logger = null)
    {
        var core = await MatrixClientCore.ConnectAsync(userId, password, deviceId, httpClientFactory, cancellationToken, logger).ConfigureAwait(false);
        var client = new MatrixTextClient(core);
        return client;
    }

    public async Task SyncAsync(MessageHandler handler)
    {
        if(_messageHandler != null)
            throw new InvalidOperationException("Sync can only be started once.");

        _messageHandler = handler ?? throw new ArgumentNullException(nameof(handler));
        await Core.SyncAsync(HandleSyncResponse).ConfigureAwait(false);
    }

    private async Task HandleSyncResponse(SyncResponse response)
    {
        MatrixTextMessage message = new();
        try
        {
            await _messageHandler!(message).ConfigureAwait(false);
        }
        catch(TaskCanceledException)
        {
            Logger.LogInformation("Sync was cancelled.");
            throw;
        }
        catch(Exception ex)
        { // we only allow TaskCanceledException to bubble up
            Logger.LogError(ex, "Error during handling of message.");
        }
    }
}

