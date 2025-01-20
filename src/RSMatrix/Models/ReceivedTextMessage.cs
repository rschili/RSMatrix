
using RSMatrix.Http;

namespace RSMatrix.Models;

public class ReceivedTextMessage
{
    public string? Body { get; private set; }
    public Room Room { get; private set; }
    public RoomUser Sender { get; private set; }
    public MatrixTextClient Client { get; private set; }

    public List<RoomUser>? Mentions { get; internal set; }

    public string EventId { get; set; }

    public string? ThreadId { get; set; }

    public DateTimeOffset Timestamp { get; internal set; }

    internal ReceivedTextMessage(string? body, Room room, RoomUser sender, string eventId, DateTimeOffset timestamp, string? threadId, MatrixTextClient client)
    {
        Body = body;
        Room = room;
        Sender = sender;
        Client = client;
        EventId = eventId;
        ThreadId = threadId;
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

    /// <summary>
    /// Sends a response to the room.
    /// </summary>
    /// <param name="body"></param>
    /// <param name="isReply">True, to set the relation to this message</param>
    /// <returns></returns>
    public Task SendResponseAsync(string body, bool isReply = false, IList<MatrixId>? mentions = null)
    {
        return Room.SendTextMessageAsync(body, isReply ? EventId : null, mentions);
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