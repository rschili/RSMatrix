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

    public string? LastReceiptEventId { get; internal set; }
    public RoomEncryption? Encryption { get; internal set; }
    public bool IsEncrypted => Encryption != null;

    public Task SendTypingNotificationAsync(uint? timeoutMS = null)
    {
        return MatrixHelper.PutTypingNotificationAsync(Client.Core.HttpClientParameters, RoomId, Client.CurrentUser, timeoutMS);
    }

    public async Task<string> SendTextMessageAsync(string body) // TODO: threadId?
    {
        var messageRequest = new MessageRequest { MsgType = "m.text", Body = body };
        var response = await MatrixHelper.PutMessageAsync(Client.Core.HttpClientParameters, RoomId, messageRequest).ConfigureAwait(false);
        return response.EventId ?? "";
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

public class RoomEncryption
{
    public string Algorithm { get; }
    internal RoomEncryption(string algorithm)
    {
        Algorithm = algorithm ?? throw new ArgumentNullException(nameof(algorithm));
    }
}