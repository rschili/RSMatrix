namespace RSMatrix.Models;

/// <summary>
/// Everything the client receives, as one of a few small records. Each change to a message carries the message as it is
/// after the change: when <see cref="MessageEdited"/> arrives, its message has the new text. Changes come out for
/// messages the client holds in memory, which are the recent ones and the ones read recently.
/// </summary>
public union Update(NewMessage, MessageEdited, MessageDeleted, ReactionAdded, ReactionRemoved, ClientIssue);

public sealed record NewMessage(Message Message);

public sealed record MessageEdited(Message Message);

public sealed record MessageDeleted(Message Message, RoomUser DeletedBy);

public sealed record ReactionAdded(Message Message, string Key, RoomUser User);

public sealed record ReactionRemoved(Message Message, string Key, RoomUser User);
