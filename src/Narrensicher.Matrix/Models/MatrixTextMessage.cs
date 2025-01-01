
using Narrensicher.Matrix.Http;

namespace Narrensicher.Matrix.Models;

public class MatrixTextMessage
{
    public string? Body { get; private set; }
    public Room Room { get; private set; }
    public RoomUser Sender { get; private set; }
    public MatrixTextClient Client { get; private set; }

    internal MatrixId EventId { get; set; }

    internal string? ThreadId { get; set; }

    internal MatrixTextMessage(string? body, Room room, RoomUser sender, MatrixId eventId, MatrixTextClient client)
    {
        Body = body;
        Room = room;
        Sender = sender;
        Client = client;
        EventId = eventId;
    }

    public async Task SendReceiptAsync()
    {
        await MatrixHelper.PostReceiptAsync(Client.Core.HttpClientParameters, Room.RoomId, EventId, ThreadId ?? "main");
        await MatrixHelper.PostReadMarkersAsync(Client.Core.HttpClientParameters, Room.RoomId, EventId);
    }
}