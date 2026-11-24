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
    Task<string> SendTextAsync(string roomId, string text, CancellationToken cancellationToken);

    /// <param name="from">Token from the previous page, or null to start at the newest message.</param>
    Task<HistoryPage> LoadHistoryAsync(string roomId, string? from, int limit, CancellationToken cancellationToken);

    /// <summary>Returns the media content, or null when it is not available.</summary>
    Task<byte[]?> DownloadMediaAsync(string mxcUri, CancellationToken cancellationToken);
}

public sealed record ChatUser(string UserId, string DisplayName);

public sealed record ChatRoomInfo(string RoomId, string Name, bool IsEncrypted, bool IsDirect);

public enum ChatMessageKind
{
    Text,
    Notice,
    Emote,
    Image,
}

/// <param name="Body">Plain text, used for previews and copying.</param>
public sealed record ChatMessage(
    string RoomId,
    string EventId,
    ChatUser Sender,
    DateTimeOffset Timestamp,
    ChatMessageKind Kind,
    string Body,
    MessageDocument Content,
    bool IsEncrypted);

/// <param name="Messages">Oldest first.</param>
/// <param name="NextToken">Null when there is no older history.</param>
public sealed record HistoryPage(IReadOnlyList<ChatMessage> Messages, string? NextToken);

public enum ConnectionState
{
    Connecting,
    Online,
    Reconnecting,
    Stopped,
}

public abstract record ChatEvent;

public sealed record MessageReceived(ChatRoomInfo Room, ChatMessage Message) : ChatEvent;

public sealed record ConnectionChanged(ConnectionState State, string? Detail) : ChatEvent;

/// <summary>A problem the session keeps running through, like a message that could not be decrypted.</summary>
public sealed record IssueReported(string Message, string? RoomId) : ChatEvent;
