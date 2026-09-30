using System.Text.Json;

namespace RSMatrix.Models;

/// <summary>
/// An immutable snapshot of an observed room state event. Content is untrusted JSON;
/// callers must validate fields before using them, including for known event types.
/// </summary>
public sealed class RoomStateEvent
{
    public string Type { get; }
    public string StateKey { get; }
    public string EventId { get; }
    public string Sender { get; }
    public long OriginServerTs { get; }
    public JsonElement Content { get; }

    internal RoomStateEvent(ClientEventWithoutRoomID source)
    {
        Type = source.Type;
        StateKey = source.StateKey!;
        EventId = source.EventId;
        Sender = source.Sender;
        OriginServerTs = source.OriginServerTs;
        // Retain the content independently of the response's JSON document lifetime.
        Content = source.Content!.Value.Clone();
    }
}
