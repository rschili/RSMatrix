using RSMatrix.Models;

namespace RSMatrix.Tests;

public class TypingTests
{
    private static string Typing(params string[] userIds)
        => """{"next_batch":"next","rooms":{"join":{"!room:example.org":{"ephemeral":{"events":[{"type":"m.typing","content":{"user_ids":["""
            + string.Join(",", userIds.Select(id => $"\"{id}\"")) + "]}}]}}}}}";

    [Test]
    public async Task TypingUsers_AreReplacedByEachEvent_WithoutUs()
    {
        using var context = new MatrixTestContext();
        await context.Process(Typing("@alice:example.org", "@bot:example.org"));
        var room = context.Room();
        await Assert.That(room.TypingUsers.Select(u => u.User.UserId.Full)).IsEquivalentTo(["@alice:example.org"]);
        context.ReadUpdates();

        await context.Process(Typing("@bob:example.org"));
        await Assert.That(room.TypingUsers.Single().User.UserId.Full).IsEqualTo("@bob:example.org");
        var change = context.ReadUpdates().Select(u => u.Value).OfType<RoomChanged>().Single();
        await Assert.That(change.Change).IsEqualTo(RoomChange.Typing);

        await context.Process(Typing("@bob:example.org"));
        await Assert.That(context.ReadUpdates()).IsEmpty();

        await context.Process(Typing());
        await Assert.That(room.TypingUsers).IsEmpty();
    }

    [Test]
    public async Task SetTyping_SendsStartWithTimeout_AndStopWithout()
    {
        using var context = new MatrixTestContext();
        await context.Room().SetTypingAsync(true);
        await context.Room().SetTypingAsync(false);

        await Assert.That(context.Requests.Select(r => r.Path)).IsEquivalentTo([
            "/_matrix/client/v3/rooms/%21room%3Aexample.org/typing/%40bot%3Aexample.org",
            "/_matrix/client/v3/rooms/%21room%3Aexample.org/typing/%40bot%3Aexample.org"]);
        await Assert.That(context.Requests[0].Body).IsEqualTo("""{"typing":true,"timeout":30000}""");
        await Assert.That(context.Requests[1].Body).IsEqualTo("""{"typing":false}""");
    }
}
