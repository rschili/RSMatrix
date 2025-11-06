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
        return MatrixHelper.PutTypingNotificationAsync(Client.HttpClientParameters, RoomId, Client.CurrentUser, timeoutMS);
    }

    public Task<string> SendTextMessageAsync(string body, string? inReplyTo, IList<MatrixId>? mentions) // TODO: threadId?
     => SendMessageInternalAsync(body, null, inReplyTo, mentions);

    public Task<string> SendHtmlMessageAsync(string body, string htmlBody, string? inReplyTo, IList<MatrixId>? mentions) // TODO: threadId?
        => SendMessageInternalAsync(body, (Format: "org.matrix.custom.html", FormattedBody: htmlBody), inReplyTo, mentions);
    
    private async Task<string> SendMessageInternalAsync(string body, (string Format, string FormattedBody)? formatted, string? inReplyTo, IList<MatrixId>? mentions)
    {
        //TODO: mentions and inReplyTo are generic and should be moved to a common location
        RoomMessageMention? roomMessageMention = null;
        if (mentions != null)
        {
            roomMessageMention = new RoomMessageMention { UserIds = mentions.Select(m => m.Full).ToList() };
        }
        RoomMessageRelatesTo? relatesTo = null;
        if (inReplyTo != null)
        {
            relatesTo = new RoomMessageRelatesTo { InReplyTo = new RoomMessageInReplyTo() { EventId = inReplyTo } };
        }

        var messageRequest = new MessageRequest { MsgType = "m.text", Body = body, Mentions = roomMessageMention, RelatesTo = relatesTo };
        if (formatted.HasValue)
        {
            messageRequest.Format = formatted.Value.Format;
            messageRequest.FormattedBody = formatted.Value.FormattedBody;
        }
        var response = await MatrixHelper.PutMessageAsync(Client.HttpClientParameters, RoomId, messageRequest).ConfigureAwait(false);
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