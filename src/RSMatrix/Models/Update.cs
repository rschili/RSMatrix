namespace RSMatrix.Models;

/// <summary>Everything the client receives, as one of a few small records.</summary>
public union Update(NewMessage, MessageChanged, RoomChanged, Request, ConnectionChanged, ClientIssue);

public sealed record NewMessage(Message Message);

/// <summary>
/// Something changed on a message. This comes for every message, also ones the client doesn't hold. The exception is a
/// deletion of a reaction or an edit the client never saw: the deletion doesn't say which message it belonged to.
/// </summary>
/// <param name="Message">
/// The message right after this change, when the client holds it. Otherwise null, and <see cref="MessageReference.GetAsync"/>
/// loads it.
/// </param>
/// <param name="Change">What changed. Most consumers replace the message they show and don't need it.</param>
public sealed record MessageChanged(MessageReference Reference, Message? Message, MessageChange Change);

/// <summary>
/// The connection to the server was lost or came back. The client starts online and keeps retrying while offline.
/// <see cref="MatrixClient.IsOnline"/> holds the current state.
/// </summary>
/// <param name="Error">Why the connection was lost; null when it came back.</param>
public sealed record ConnectionChanged(bool IsOnline, Exception? Error);

public enum MessageChange
{
    Edited,
    Deleted,
    Reactions,
    /// <summary>A thread reply to this message was added, edited or deleted.</summary>
    Thread,
}

/// <summary>
/// Something changed on a room the client is in. <see cref="Room"/> already holds the new values. The first sync of a
/// session fills in all rooms without reporting changes; <see cref="MatrixClient.Rooms"/> lists them.
/// </summary>
/// <param name="Change">What changed. Most consumers redraw the room and don't need it.</param>
public sealed record RoomChanged(Room Room, RoomChange Change);

public enum RoomChange
{
    /// <summary>The result of <see cref="Room.DisplayName"/> changed.</summary>
    Name,
    Topic,
    Avatar,
    /// <summary>
    /// Someone joined, left, was invited or changed their name or avatar in the room. Also comes when the current
    /// user joined or left the room, see <see cref="Room.IsJoined"/>.
    /// </summary>
    Members,
    /// <summary>The room was encrypted.</summary>
    Encryption,
    /// <summary><see cref="Room.NotificationCount"/>, <see cref="Room.MentionCount"/> or <see cref="Room.HasUnread"/> changed.</summary>
    Unread,
    /// <summary><see cref="Room.TypingUsers"/> changed.</summary>
    Typing,
}
