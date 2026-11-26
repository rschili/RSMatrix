namespace RSMatrix.Models;

/// <summary>A message the client knows by ID, without necessarily holding it.</summary>
public sealed class MessageReference
{
    internal MessageReference(Room room, string id)
    {
        Room = room;
        Id = id;
    }

    public string Id { get; }
    public Room Room { get; }

    /// <summary>
    /// Returns the message from memory, or loads it from the server. A loaded message may miss reactions, see
    /// <see cref="Message.HasAllReactions"/>. Returns null if the server doesn't return the event or the event isn't a message.
    /// </summary>
    public Task<Message?> GetAsync(CancellationToken cancellationToken = default)
        => Room.GetMessageAsync(Id, cancellationToken);
}
