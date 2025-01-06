
using RSMatrix.Http;

namespace RSMatrix.Models;

public class ReceivedTextMessage
{
    public string? Body { get; private set; }
    public Room Room { get; private set; }
    public RoomUser Sender { get; private set; }
    public MatrixTextClient Client { get; private set; }

    public List<RoomUser>? Mentions { get; internal set; }

    internal MatrixId EventId { get; set; }

    internal string? ThreadId { get; set; }

    internal ReceivedTextMessage(string? body, Room room, RoomUser sender, MatrixId eventId, MatrixTextClient client)
    {
        Body = body;
        Room = room;
        Sender = sender;
        Client = client;
        EventId = eventId;
    }

    /// <summary>
    /// For now this is internal, the client will occasionally call this to send a read receipt.
    /// </summary>
    /// <returns></returns>
    internal async Task SendReceiptAsync()
    {
        await MatrixHelper.PostReceiptAsync(Client.Core.HttpClientParameters, Room.RoomId, EventId, ThreadId ?? "main");
        await MatrixHelper.PostReadMarkersAsync(Client.Core.HttpClientParameters, Room.RoomId, EventId);
    }

    public Task SendResponseAsync(string body)
    {
        return Room.SendTextMessageAsync(body);
    }

    public override string ToString()
    {
        var mentionsString = Mentions != null ? string.Join(", ", Mentions) : "None";
        return $"Body: {Body}\n" +
            $"Room: {Room}\n" +
            $"Sender: {Sender}\n" +
            $"EventId: {EventId}\n" +
            $"ThreadId: {ThreadId}\n" +
            $"Mentions: {mentionsString}";
    }
}