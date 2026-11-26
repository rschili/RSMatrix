namespace RSMatrix.Models;

/// <summary>
/// Everything the client receives, as one of a few small records. The objects they point to already show the change:
/// when <see cref="MessageEdited"/> arrives, its message has the new text.
/// </summary>
public union Update(NewMessage, MessageEdited, MessageDeleted, ReactionAdded, ReactionRemoved, ClientIssue);

public sealed record NewMessage(Message Message);

public sealed record MessageEdited(Message Message);

public sealed record MessageDeleted(Message Message, RoomUser DeletedBy);

public sealed record ReactionAdded(Message Message, string Key, RoomUser User);

public sealed record ReactionRemoved(Message Message, string Key, RoomUser User);
