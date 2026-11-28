using RSMatrix.Models;

namespace RSMatrix.Tests;

public class PresenceTests
{
    private static string Presence(string userId, string content)
        => $$$"""{"next_batch":"next","presence":{"events":[{"type":"m.presence","sender":"{{{userId}}}","content":{{{content}}}}]}}""";

    [Test]
    public async Task PresenceChanged_ComesAfterTheFirstSync_ForStateStatusAndActivity()
    {
        using var context = new MatrixTestContext();
        await context.Process(Presence("@alice:example.org", """{"presence":"online","status_msg":"lunch","last_active_ago":60000}"""));
        await Assert.That(context.ReadUpdates()).IsEmpty();
        UserId.TryParse("@alice:example.org", out var aliceId);
        var alice = context.Client.GetOrAddUser(aliceId!);
        await Assert.That(alice.StatusMessage).IsEqualTo("lunch");
        await Assert.That(alice.LastActiveAt!.Value).IsLessThan(DateTimeOffset.UtcNow.AddSeconds(-59));
        await Assert.That(alice.LastActiveAt!.Value).IsGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-2));

        // A new last-active time alone isn't a change.
        await context.Process(Presence("@alice:example.org", """{"presence":"online","status_msg":"lunch","last_active_ago":10}"""));
        await Assert.That(context.ReadUpdates()).IsEmpty();
        await Assert.That(alice.LastActiveAt!.Value).IsGreaterThan(DateTimeOffset.UtcNow.AddSeconds(-30));

        // A missing status text clears it.
        await context.Process(Presence("@alice:example.org", """{"presence":"unavailable"}"""));
        var change = context.ReadUpdates().Select(u => u.Value).OfType<PresenceChanged>().Single();
        await Assert.That(change.User).IsSameReferenceAs(alice);
        await Assert.That(alice.Presence).IsEqualTo(Models.Presence.Unavailable);
        await Assert.That(alice.StatusMessage).IsNull();

        await context.Process(Presence("@alice:example.org", """{"presence":"unavailable","currently_active":true}"""));
        await Assert.That(context.ReadUpdates().Select(u => u.Value).OfType<PresenceChanged>().Count()).IsEqualTo(1);
    }

    [Test]
    public async Task OwnPresence_LandsOnCurrentUser()
    {
        using var context = new MatrixTestContext();
        await context.Process(Presence("@bot:example.org", """{"presence":"online","status_msg":"lunch"}"""));
        await Assert.That(context.Client.CurrentUser.Presence).IsEqualTo(Models.Presence.Online);
        await Assert.That(context.Client.CurrentUser.StatusMessage).IsEqualTo("lunch");
    }

    [Test]
    public async Task SetPresence_KeepsTheStatusText_AndSetStatus_KeepsThePresence()
    {
        using var context = new MatrixTestContext();
        await context.Client.SetStatusMessageAsync("lunch");
        await context.Client.SetPresenceAsync(Models.Presence.Unavailable);
        await context.Client.SetStatusMessageAsync(null);

        await Assert.That(context.Requests.Select(r => r.Path).Distinct()).IsEquivalentTo(["/_matrix/client/v3/presence/%40bot%3Aexample.org/status"]);
        await Assert.That(string.Join("\n", context.Requests.Select(r => r.Body))).IsEqualTo("""
            {"presence":"online","status_msg":"lunch"}
            {"presence":"unavailable","status_msg":"lunch"}
            {"presence":"unavailable"}
            """);
    }

    [Test]
    public async Task StatusTextFromAnotherDevice_IsKept()
    {
        using var context = new MatrixTestContext();
        await context.Client.SetStatusMessageAsync("from desktop");
        await context.Process(Presence("@bot:example.org", """{"presence":"online","status_msg":"from phone"}"""));
        await context.Client.SetPresenceAsync(Models.Presence.Unavailable);

        await Assert.That(context.Requests[^1].Body).IsEqualTo("""{"presence":"unavailable","status_msg":"from phone"}""");
    }
}
