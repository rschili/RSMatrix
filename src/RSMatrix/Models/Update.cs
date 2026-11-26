namespace RSMatrix.Models;

/// <summary>Everything the client receives, as one of a few small records.</summary>
public union Update(NewMessage, MessageChanged, ClientIssue);

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

public enum MessageChange
{
    Edited,
    Deleted,
    Reactions,
    /// <summary>A thread reply to this message was added, edited or deleted.</summary>
    Thread,
}
