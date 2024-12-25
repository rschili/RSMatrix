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

        // We only want to receive text messages, filter out spam we're not interested in
        Filter filter = new()
        {
            AccountData = new()
            {
                NotTypes = new() { "*" }
            },
            Room = new()
            {
                AccountData = new()
                {
                    NotTypes = new() { "*" }
                },
                Ephemeral = new()
                {
                    NotTypes = new() { "m.typing", "m.receipt" },
                    LazyLoadMembers = true
                },
                Timeline = new()
                {
                    LazyLoadMembers = true,
                },
                State = new()
                {
                    NotTypes = new() { "m.room.join_rules", "m.room.guest_access", "m.room.avatar", "m.room.history_visibility", "m.room.power_levels" },
                    LazyLoadMembers = true
                },
            }
        };
        filter = await Core.SetFilterAsync(filter).ConfigureAwait(false);
        if(filter.FilterId == null)
            Logger.LogWarning("No filter ID was returned after setting a filter. This should not happen. It won't break the client, but unnecessary events will be received.");
        
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

