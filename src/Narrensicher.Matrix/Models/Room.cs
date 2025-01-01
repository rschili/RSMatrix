using System.Collections.Immutable;

namespace Narrensicher.Matrix.Models;

public class Room
{
    public Room(MatrixId roomId)
    {
        RoomId = roomId;
    }

    public MatrixId RoomId { get; }

    // ConcurrentDictionary is expensive, we only use it for the global things. Inside the room we use ImmutableDictionary instead as there is less data and less movement
    public ImmutableDictionary<string, RoomUser> Users { get; internal set; } = ImmutableDictionary<string, RoomUser>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);
    public string? DisplayName { get; internal set; }
    public MatrixId? CanonicalAlias { get; internal set; }
    public List<MatrixId>? AltAliases { get; internal set; }

    public MatrixTextMessage? LastMessage { get; internal set; }

    public MatrixId? LastReceipt { get; internal set; }
}

public class RoomUser
{
    public RoomUser(User user)
    {
        User = user;
    }

    public User User { get; }
    public string? DisplayName { get; internal set; }
    public Membership? Membership { get; internal set; }
}