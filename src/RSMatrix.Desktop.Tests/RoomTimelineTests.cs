using RSMatrix.Desktop.Chat;
using RSMatrix.Desktop.Content;
using RSMatrix.Desktop.ViewModels;

namespace RSMatrix.Desktop.Tests;

public class RoomTimelineTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly ChatUser Alice = new("@alice:example.org", "Alice");
    private static readonly ChatUser Bob = new("@bob:example.org", "Bob");
    private static readonly ChatUser Carol = new("@carol:example.org", "Carol");
    private static readonly ChatUser Dave = new("@dave:example.org", "Dave");
    private static readonly ChatUser Erin = new("@erin:example.org", "Erin");
    private static readonly ChatUser Me = new("@rob:example.org", "Rob");

    [Test]
    public async Task Messages_From_The_Same_Sender_Are_Grouped_Within_Five_Minutes()
    {
        var timeline = CreateTimeline();
        var first = Message("$1", Alice, Noon);
        var second = Message("$2", Alice, Noon.AddMinutes(4));
        var later = Message("$3", Alice, Noon.AddMinutes(10));
        var other = Message("$4", Bob, Noon.AddMinutes(11));

        foreach (var message in new[] { first, second, later, other })
            timeline.Append(message);

        await Assert.That(timeline.Items[0]).IsTypeOf<DaySeparatorViewModel>();
        await Assert.That(new[] { first.IsGroupStart, second.IsGroupStart, later.IsGroupStart, other.IsGroupStart })
            .IsEquivalentTo([true, false, true, true]);
    }

    [Test]
    public async Task Duplicates_Are_Ignored()
    {
        var timeline = CreateTimeline();

        await Assert.That(timeline.Append(Message("$1", Alice, Noon))).IsTrue();
        await Assert.That(timeline.Append(Message("$1", Alice, Noon))).IsFalse();
        await Assert.That(timeline.Prepend([Message("$1", Alice, Noon)])).IsEqualTo(0);
        await Assert.That(timeline.MessageCount).IsEqualTo(1);
    }

    [Test]
    public async Task History_From_The_Same_Day_Shares_The_Day_Separator()
    {
        var timeline = CreateTimeline();
        var existing = Message("$2", Alice, Noon.AddMinutes(1));
        timeline.Append(existing);

        var added = timeline.Prepend([Message("$0", Bob, Noon.AddDays(-1)), Message("$1", Alice, Noon)]);

        await Assert.That(added).IsEqualTo(2);
        await Assert.That(timeline.Items.OfType<DaySeparatorViewModel>().Count()).IsEqualTo(2);
        await Assert.That(timeline.Items.Last()).IsSameReferenceAs(existing);
        await Assert.That(existing.IsGroupStart).IsFalse();
    }

    [Test]
    public async Task A_Sent_Message_Whose_Echo_Arrived_First_Is_Removed()
    {
        var timeline = CreateTimeline();
        var pending = Message("", Alice, Noon);
        timeline.Append(pending);
        timeline.Append(Message("$sent", Alice, Noon));

        timeline.ConfirmSent(pending, "$sent");

        await Assert.That(timeline.MessageCount).IsEqualTo(1);
        await Assert.That(timeline.Items.Contains(pending)).IsFalse();
    }

    [Test]
    public async Task A_Sent_Message_Takes_Its_Event_Id_So_The_Echo_Is_Ignored()
    {
        var timeline = CreateTimeline();
        var pending = Message("", Alice, Noon);
        timeline.Append(pending);

        timeline.ConfirmSent(pending, "$sent");

        await Assert.That(timeline.Append(Message("$sent", Alice, Noon))).IsFalse();
        await Assert.That(pending.EventId).IsEqualTo("$sent");
    }

    [Test]
    public async Task Reactions_With_The_Same_Key_Are_Counted_Once_Per_Sender()
    {
        var timeline = CreateTimeline();
        var message = Message("$1", Alice, Noon);
        timeline.Append(message);

        timeline.Messages.AddReaction("$1", "🍪", Bob);
        timeline.Messages.AddReaction("$1", "🍪", Bob);
        timeline.Messages.AddReaction("$1", "🍪", Me);
        timeline.Messages.AddReaction("$1", "👍", Alice);

        await Assert.That(message.Reactions.Select(r => (r.Key, r.Count, r.IncludesMe)))
            .IsEquivalentTo([("🍪", 2, true), ("👍", 1, false)]);
        await Assert.That(message.Reactions[0].ToolTipText).IsEqualTo("Bob and Rob reacted with 🍪");
        await Assert.That(message.HasReacted("🍪")).IsTrue();
    }

    [Test]
    public async Task A_Removed_Reaction_Drops_Its_Pill_When_Nobody_Is_Left()
    {
        var timeline = CreateTimeline();
        var message = Message("$1", Alice, Noon);
        timeline.Append(message);
        timeline.Messages.AddReaction("$1", "🍪", Bob);
        timeline.Messages.AddReaction("$1", "🍪", Me);

        timeline.Messages.RemoveReaction("$1", "🍪", Me);
        await Assert.That(message.Reactions.Single().IncludesMe).IsFalse();

        timeline.Messages.RemoveReaction("$1", "🍪", Bob);
        await Assert.That(message.Reactions).IsEmpty();
    }

    [Test]
    public async Task Reactions_And_Receipts_Wait_For_Messages_From_History()
    {
        var timeline = CreateTimeline();
        timeline.Append(Message("$2", Alice, Noon));
        timeline.Messages.AddReaction("$1", "👍", Bob);
        timeline.Messages.ApplyReceipt("$1", Bob);

        var older = Message("$1", Bob, Noon.AddMinutes(-10));
        timeline.Prepend([older]);

        await Assert.That(older.Reactions.Single().Key).IsEqualTo("👍");
        await Assert.That(older.ReadBy.Single()).IsEqualTo(Bob);
    }

    [Test]
    public async Task A_Receipt_Moves_The_Reader_To_The_Newer_Message()
    {
        var timeline = CreateTimeline();
        var first = Message("$1", Alice, Noon);
        var second = Message("$2", Alice, Noon.AddMinutes(1));
        timeline.Append(first);
        timeline.Append(second);

        timeline.Messages.ApplyReceipt("$1", Bob);
        timeline.Messages.ApplyReceipt("$1", Carol);
        timeline.Messages.ApplyReceipt("$2", Bob);

        await Assert.That(first.ReadBy).IsEquivalentTo([Carol]);
        await Assert.That(second.ReadBy).IsEquivalentTo([Bob]);
        await Assert.That(first.ReadersToolTip).IsEqualTo("Seen by Carol");
    }

    [Test]
    public async Task Own_Receipts_Are_Not_Shown()
    {
        var timeline = CreateTimeline();
        var message = Message("$1", Alice, Noon);
        timeline.Append(message);

        timeline.Messages.ApplyReceipt("$1", Me);

        await Assert.That(message.HasReaders).IsFalse();
    }

    [Test]
    public async Task Many_Readers_Show_Three_Avatars_And_The_Rest_As_A_Number()
    {
        var timeline = CreateTimeline();
        var message = Message("$1", Alice, Noon);
        timeline.Append(message);

        foreach (var reader in new[] { Bob, Carol, Dave, Erin })
            timeline.Messages.ApplyReceipt("$1", reader);

        await Assert.That(message.ReaderAvatars.Count).IsEqualTo(3);
        await Assert.That(message.ReaderOverflowText).IsEqualTo("+1");
        await Assert.That(message.ReadersToolTip).IsEqualTo("Seen by 4 people\nBob, Carol, Dave and Erin");
    }

    [Test]
    public async Task A_Sent_Message_Picks_Up_Reactions_That_Arrived_Before_Its_Event_Id()
    {
        var timeline = CreateTimeline();
        var pending = Message("", Me, Noon);
        timeline.Append(pending);
        timeline.Messages.AddReaction("$sent", "👍", Bob);

        timeline.ConfirmSent(pending, "$sent");

        await Assert.That(pending.Reactions.Single().Key).IsEqualTo("👍");
    }

    [Test]
    public async Task A_Reply_Links_To_Its_Target_Even_When_The_Target_Loads_Later()
    {
        var timeline = CreateTimeline();
        var reply = Message("$2", Bob, Noon, replyTo: "$1");
        timeline.Append(reply);
        await Assert.That(reply.ReplyToPreview).IsEqualTo("Original message isn't loaded");

        var target = Message("$1", Alice, Noon.AddMinutes(-1));
        timeline.Prepend([target]);

        await Assert.That(reply.ReplyTo).IsSameReferenceAs(target);
        await Assert.That(reply.ReplyToName).IsEqualTo("Alice");
    }

    private static RoomTimeline CreateTimeline()
        => new(new RoomMessages(Me.UserId), () => DateOnly.FromDateTime(Noon.LocalDateTime));

    private static MessageViewModel Message(string eventId, ChatUser sender, DateTimeOffset timestamp, string? replyTo = null)
        => new(new ChatMessage("!room:example.org", eventId, sender, timestamp, ChatMessageKind.Text, "text",
            PlainTextParser.Parse("text"), IsEncrypted: false, ReplyToEventId: replyTo), isOwn: false);
}
