
using RSMatrix.Http;

namespace RSMatrix.Models;

public class ReceivedTextMessage
{
    public string? Body { get; private set; }
    public Room Room { get; private set; }
    public RoomUser Sender { get; private set; }
    public MatrixTextClient Client { get; private set; }

    public List<RoomUser>? Mentions { get; internal set; }

    internal string EventId { get; set; }

    internal string? ThreadId { get; set; }

    public DateTimeOffset Timestamp { get; internal set; }

    internal ReceivedTextMessage(string? body, Room room, RoomUser sender, string eventId, DateTimeOffset timestamp, MatrixTextClient client)
    {
        Body = body;
        Room = room;
        Sender = sender;
        Client = client;
        EventId = eventId;
        Timestamp = timestamp;
    }

    /// <summary>
    /// For now this is internal, the client will occasionally call this to send a read receipt.
    /// </summary>
    /// <returns></returns>
    internal async Task SendReceiptAsync()
    {
        //await MatrixHelper.PostReceiptAsync(Client.Core.HttpClientParameters, Room.RoomId, EventId, ThreadId ?? "main");
        // Set both, read marker and receipt at the same time
        await MatrixHelper.PostReadMarkersAsync(Client.Core.HttpClientParameters, Room.RoomId, EventId, true);
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
            $"Timestamp: {Timestamp}\n" +
            $"Mentions: {mentionsString}";
    }
}