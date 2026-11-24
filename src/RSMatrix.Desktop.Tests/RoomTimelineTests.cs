using RSMatrix.Desktop.Chat;
using RSMatrix.Desktop.Content;
using RSMatrix.Desktop.ViewModels;

namespace RSMatrix.Desktop.Tests;

public class RoomTimelineTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly ChatUser Alice = new("@alice:example.org", "Alice");
    private static readonly ChatUser Bob = new("@bob:example.org", "Bob");

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

    private static RoomTimeline CreateTimeline() => new(() => DateOnly.FromDateTime(Noon.LocalDateTime));

    private static MessageViewModel Message(string eventId, ChatUser sender, DateTimeOffset timestamp)
        => new(new ChatMessage("!room:example.org", eventId, sender, timestamp, ChatMessageKind.Text, "text",
            PlainTextParser.Parse("text"), IsEncrypted: false), isOwn: false);
}
