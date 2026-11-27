using RSMatrix.Desktop.Content;

namespace RSMatrix.Desktop.Chat;

/// <summary>
/// What the UI needs from a Matrix connection. <see cref="Matrix.MatrixChatSession"/> talks to a homeserver
/// through RSMatrix, <see cref="Sample.SampleChatSession"/> serves built-in sample data.
/// </summary>
public interface IChatSession : IAsyncDisposable
{
    ChatUser CurrentUser { get; }

    string ServerName { get; }

    /// <summary>Single reader. Completes when the session ends.</summary>
    IAsyncEnumerable<ChatEvent> ReadEventsAsync(CancellationToken cancellationToken);

    /// <summary>Returns the event ID of the sent message.</summary>
    /// <param name="replyToEventId">The message this one replies to, or null.</param>
    /// <param name="threadRootId">The thread to send to, or null for the room's main timeline.</param>
    Task<string> SendTextAsync(string roomId, string text, string? replyToEventId, string? threadRootId,
        CancellationToken cancellationToken);

    /// <summary>Reacts to a message. The reaction comes back as a <see cref="ReactionReceived"/> event.</summary>
    Task SendReactionAsync(string roomId, string targetEventId, string key, CancellationToken cancellationToken);

    /// <summary>Takes back the current user's reaction. Comes back as a <see cref="ReactionRemoved"/> event.</summary>
    Task RemoveReactionAsync(string roomId, string targetEventId, string key, CancellationToken cancellationToken);

    /// <param name="beforeEventId">The oldest message loaded so far, or null to start at the newest message.</param>
    Task<HistoryPage> LoadHistoryAsync(string roomId, string? beforeEventId, int limit, CancellationToken cancellationToken);

    /// <summary>All reactions to a message, or null when the message can't be loaded.</summary>
    Task<IReadOnlyList<ChatReaction>?> GetReactionsAsync(string roomId, string eventId, CancellationToken cancellationToken);

    /// <summary>Joined and invited members of the room, in no particular order.</summary>
    Task<IReadOnlyList<ChatMember>> GetMembersAsync(string roomId, CancellationToken cancellationToken);

    /// <summary>Returns the media content, or null when it is not available.</summary>
    Task<byte[]?> DownloadMediaAsync(string mxcUri, CancellationToken cancellationToken);
}

public sealed record ChatUser(string UserId, string DisplayName);

public sealed record ChatMember(ChatUser User, bool IsInvited);

/// <param name="Key">Usually an emoji. A user reacts at most once with the same key.</param>
public sealed record ChatReaction(string Key, IReadOnlyList<ChatUser> Users);

public sealed record ChatRoomInfo(string RoomId, string Name, bool IsEncrypted, bool IsDirect);

public enum ChatMessageKind
{
    Text,
    Notice,
    Emote,
    Image,
}

/// <param name="Body">Plain text, used for previews and copying.</param>
/// <param name="ThreadRootId">Event ID of the thread's first message when this is a reply in a thread.</param>
/// <param name="ReplyToEventId">The message this one replies to.</param>
/// <param name="Source">The event as JSON, when the session has it.</param>
/// <param name="Reactions">The reactions when the session delivered the message. Later ones come as events.</param>
public sealed record ChatMessage(
    string RoomId,
    string EventId,
    ChatUser Sender,
    DateTimeOffset Timestamp,
    ChatMessageKind Kind,
    string Body,
    MessageDocument Content,
    bool IsEncrypted,
    string? ThreadRootId = null,
    string? ReplyToEventId = null,
    string? Source = null,
    IReadOnlyList<ChatReaction>? Reactions = null);

/// <param name="Messages">Oldest first.</param>
/// <param name="HasMore">False when the page reached the start of the room.</param>
public sealed record HistoryPage(IReadOnlyList<ChatMessage> Messages, bool HasMore);

public enum ConnectionState
{
    Connecting,
    Online,
    Reconnecting,
    Stopped,
}

public abstract record ChatEvent;

/// <summary>The user is in this room: at the start of the session, or after joining.</summary>
/// <param name="LatestMessage">The room's newest message, for the room list, or null when the session holds none.</param>
public sealed record RoomJoined(ChatRoomInfo Room, ChatMessage? LatestMessage) : ChatEvent;

public sealed record RoomUpdated(ChatRoomInfo Room) : ChatEvent;

/// <summary>The user left the room, or was kicked or banned.</summary>
public sealed record RoomLeft(string RoomId) : ChatEvent;

public sealed record MessageReceived(ChatRoomInfo Room, ChatMessage Message) : ChatEvent;

/// <param name="Key">Usually an emoji. A user reacts at most once with the same key.</param>
public sealed record ReactionReceived(string RoomId, string TargetEventId, string Key, ChatUser Sender) : ChatEvent;

public sealed record ReactionRemoved(string RoomId, string TargetEventId, string Key, ChatUser Sender) : ChatEvent;

/// <summary>The reactions of a message changed.</summary>
/// <param name="Reactions">All reactions the message has now, or null when the session doesn't hold the message.</param>
public sealed record ReactionsChanged(string RoomId, string TargetEventId, IReadOnlyList<ChatReaction>? Reactions) : ChatEvent;

/// <summary>The user has read the room up to this event. Replaces the user's earlier receipt in the room.</summary>
public sealed record ReceiptReceived(string RoomId, string EventId, ChatUser User) : ChatEvent;

public sealed record ConnectionChanged(ConnectionState State, string? Detail) : ChatEvent;

/// <summary>A problem the session keeps running through, like a message that could not be decrypted.</summary>
public sealed record IssueReported(string Message, string? RoomId) : ChatEvent;
