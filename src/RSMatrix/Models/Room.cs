using System.Collections.Immutable;
using RSMatrix.Http;

namespace RSMatrix.Models;

public class Room
{
    internal MatrixTextClient Client { get; }
    public Room(MatrixId roomId, MatrixTextClient client)
    {
        RoomId = roomId ?? throw new ArgumentNullException(nameof(roomId));
        Client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public MatrixId RoomId { get; }

    // ConcurrentDictionary is expensive, we only use it for the global things. Inside the room we use ImmutableDictionary instead as there is less data and less movement
    public ImmutableDictionary<string, RoomUser> Users { get; internal set; } = ImmutableDictionary<string, RoomUser>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);
    public string? DisplayName { get; internal set; }
    public MatrixId? CanonicalAlias { get; internal set; }
    public List<MatrixId>? AltAliases { get; internal set; }

    public ReceivedTextMessage? LastMessage { get; internal set; }

    public MatrixId? LastReceipt { get; internal set; }

    public Task SendTypingNotificationAsync(uint? timeoutMS = null)
    {
        return MatrixHelper.PutTypingNotificationAsync(Client.Core.HttpClientParameters, RoomId, Client.CurrentUser, timeoutMS);
    }

    public Task SendTextMessageAsync(string body) // TODO: threadId?
    {
        var messageRequest = new MessageRequest { MsgType = "m.Text", Body = body };
        return MatrixHelper.PutMessageAsync(Client.Core.HttpClientParameters, RoomId, messageRequest);
    }

    public override string ToString()
    {
        return DisplayName ?? RoomId.Localpart.ToString();
    }
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

    /// <summary>
    /// RoomUser and User may have display names. This takes care of choosing the right one
    /// </summary>
    public string GetDisplayName()
    {
        return DisplayName ?? User.DisplayName ?? User.UserId.Localpart.ToString();
    }

    public override string ToString()
    {
        return GetDisplayName();
    }
}