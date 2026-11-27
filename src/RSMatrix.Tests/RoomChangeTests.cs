using RSMatrix.Models;
using static RSMatrix.Tests.MessageUpdateTests;

namespace RSMatrix.Tests;

public class RoomChangeTests
{
    private const string RoomId = "!room:example.org";

    private static string State(string id, string type, string stateKey, string content, string sender = "@alice:example.org")
        => Event(id, type, content, sender, extra: $$""","state_key":"{{stateKey}}" """);

    private static string Member(string id, string userId, string membership, string? displayName = null, string? sender = null)
        => State(id, "m.room.member", userId,
            displayName == null ? $$"""{"membership":"{{membership}}"}""" : $$"""{"membership":"{{membership}}","displayname":"{{displayName}}"}""",
            sender ?? userId);

    private static string RoomSync(string[]? state = null, string[]? timeline = null, string summary = "{}", string roomId = RoomId)
        => $$"""{"next_batch":"next","rooms":{"join":{"{{roomId}}":{"summary":{{summary}},"state":{"events":[{{string.Join(",", state ?? [])}}]},"timeline":{"events":[{{string.Join(",", timeline ?? [])}}]"""
            + "}}}}}";

    private static async Task<MatrixTestContext> SyncedAsync(params string[] state)
    {
        var context = new MatrixTestContext();
        await context.Process(RoomSync(state: state, summary: """{"m.joined_member_count":2,"m.invited_member_count":0}"""));
        context.ReadUpdates();
        return context;
    }

    private static List<RoomChange> Changes(List<Update> updates)
        => updates.Select(u => u.Value).OfType<RoomChanged>().Select(c => c.Change).ToList();

    [Test]
    public async Task FirstSync_FillsRooms_WithoutUpdatesOrRequests()
    {
        using var context = new MatrixTestContext();
        await context.Process(RoomSync(
            state: [State("$n", "m.room.name", "", """{"name":"Lounge"}"""), State("$t", "m.room.topic", "", """{"topic":"Chat"}"""),
                State("$a", "m.room.avatar", "", """{"url":"mxc://example.org/a"}"""), Member("$m", "@alice:example.org", "join", "Alice")],
            summary: """{"m.joined_member_count":5,"m.invited_member_count":1}"""));

        await Assert.That(context.ReadUpdates()).IsEmpty();
        await Assert.That(context.Requests).IsEmpty();
        var room = context.Client.Rooms.Single();
        await Assert.That(room.Name).IsEqualTo("Lounge");
        await Assert.That(room.Topic).IsEqualTo("Chat");
        await Assert.That(room.AvatarUrl).IsEqualTo("mxc://example.org/a");
        await Assert.That(room.JoinedMemberCount).IsEqualTo(5);
        await Assert.That(room.InvitedMemberCount).IsEqualTo(1);
        await Assert.That(room.IsJoined).IsTrue();
    }

    [Test]
    public async Task TimelineState_ReportsEachChange_BeforeTheRoomsMessages()
    {
        using var context = await SyncedAsync();
        await context.Process(RoomSync(timeline: [
            State("$n", "m.room.name", "", """{"name":"Lounge"}"""),
            Text("$1", "hello"),
            State("$t", "m.room.topic", "", """{"topic":"Chat"}"""),
            State("$a", "m.room.avatar", "", """{"url":"mxc://example.org/a"}"""),
            State("$e", "m.room.encryption", "", """{"algorithm":"m.megolm.v1.aes-sha2"}"""),
        ]));

        var updates = context.ReadUpdates();
        await Assert.That(Changes(updates)).IsEquivalentTo([RoomChange.Name, RoomChange.Topic, RoomChange.Avatar, RoomChange.Encryption, RoomChange.Unread]);
        await Assert.That(updates[^1].Value is NewMessage).IsTrue();
        await Assert.That(context.Room().DisplayName).IsEqualTo("Lounge");
    }

    [Test]
    public async Task StateThatDidNotChange_ReportsNothing()
    {
        using var context = await SyncedAsync(State("$n", "m.room.name", "", """{"name":"Lounge"}"""));
        await context.Process(RoomSync(state: [State("$n2", "m.room.name", "", """{"name":"Lounge"}""")]));

        await Assert.That(context.ReadUpdates()).IsEmpty();
    }

    [Test]
    public async Task RemovedName_FallsBackToTheAlias()
    {
        using var context = await SyncedAsync(State("$n", "m.room.name", "", """{"name":"Lounge"}"""),
            State("$c", "m.room.canonical_alias", "", """{"alias":"#lounge:example.org"}"""));
        await context.Process(RoomSync(timeline: [State("$n2", "m.room.name", "", "{}")]));

        await Assert.That(Changes(context.ReadUpdates())).IsEquivalentTo([RoomChange.Name]);
        await Assert.That(context.Room().Name).IsNull();
        await Assert.That(context.Room().DisplayName).IsEqualTo("#lounge:example.org");
    }

    [Test]
    public async Task MembershipInTimeline_IsAChange_LazyLoadedMemberIsNot()
    {
        using var context = await SyncedAsync();

        // The state section brings members the client didn't know yet, like the sender of a new message.
        await context.Process(RoomSync(state: [Member("$m1", "@carol:example.org", "join", "Carol")], timeline: [Text("$1", "hi", "@carol:example.org")]));
        await Assert.That(Changes(context.ReadUpdates())).IsEquivalentTo([RoomChange.Unread]);

        await context.Process(RoomSync(timeline: [Member("$m2", "@dave:example.org", "join", "Dave")]));
        await Assert.That(Changes(context.ReadUpdates())).IsEquivalentTo([RoomChange.Members]);
        await Assert.That(context.Room().Users["@dave:example.org"].Membership).IsEqualTo(Membership.Join);
    }

    [Test]
    public async Task KnownMemberChangedInStateSection_IsAChange()
    {
        using var context = await SyncedAsync(Member("$m1", "@carol:example.org", "join", "Carol"));
        await context.Process(RoomSync(state: [Member("$m2", "@carol:example.org", "join", "Caroline")]));

        await Assert.That(Changes(context.ReadUpdates())).IsEquivalentTo([RoomChange.Members]);
        await Assert.That(context.Room().Users["@carol:example.org"].RoomName).IsEqualTo("Caroline");
    }

    [Test]
    public async Task ChangedMemberCounts_AreAChange()
    {
        using var context = await SyncedAsync();
        await context.Process(RoomSync(summary: """{"m.joined_member_count":3}"""));

        await Assert.That(Changes(context.ReadUpdates())).IsEquivalentTo([RoomChange.Members]);
        await Assert.That(context.Room().JoinedMemberCount).IsEqualTo(3);
        await Assert.That(context.Room().InvitedMemberCount).IsEqualTo(0);
    }

    [Test]
    public async Task HeroRename_ChangesTheComposedName()
    {
        using var context = new MatrixTestContext();
        await context.Process(RoomSync(state: [Member("$m1", "@carol:example.org", "join", "Carol")],
            summary: """{"m.heroes":["@carol:example.org"],"m.joined_member_count":2,"m.invited_member_count":0}"""));
        context.ReadUpdates();
        await Assert.That(context.Room().DisplayName).IsEqualTo("Carol");

        await context.Process(RoomSync(timeline: [Member("$m2", "@carol:example.org", "join", "Caroline")]));
        await Assert.That(Changes(context.ReadUpdates())).IsEquivalentTo([RoomChange.Name, RoomChange.Members]);
        await Assert.That(context.Room().DisplayName).IsEqualTo("Caroline");
    }

    [Test]
    public async Task JoiningAndLeaving_ReportMembers_AndUpdateTheRoomList()
    {
        using var context = await SyncedAsync();
        await context.Process(RoomSync(state: [State("$n", "m.room.name", "", """{"name":"New"}""")],
            timeline: [Member("$m", "@bot:example.org", "join")], roomId: "!new:example.org"));

        await Assert.That(Changes(context.ReadUpdates())).IsEquivalentTo([RoomChange.Members]);
        await Assert.That(context.Client.Rooms.Count).IsEqualTo(2);

        await context.Process("""{"next_batch":"next","rooms":{"leave":{"!new:example.org":{},"!never-joined:example.org":{}}}}""");
        var updates = context.ReadUpdates();
        await Assert.That(Changes(updates)).IsEquivalentTo([RoomChange.Members]);
        await Assert.That(((RoomChanged)updates[0].Value!).Room.RoomId.Full).IsEqualTo("!new:example.org");
        await Assert.That(context.Room("!new:example.org").IsJoined).IsFalse();
        await Assert.That(context.Client.Rooms.Single().RoomId.Full).IsEqualTo(RoomId);
    }

    [Test]
    [Arguments("""{"m.heroes":["@a:example.org","@b:example.org"],"m.joined_member_count":3,"m.invited_member_count":0}""", "Alice and Bob")]
    [Arguments("""{"m.heroes":["@a:example.org","@b:example.org"],"m.joined_member_count":4,"m.invited_member_count":1}""", "Alice, Bob and 2 others")]
    [Arguments("""{"m.heroes":["@a:example.org"],"m.joined_member_count":2,"m.invited_member_count":1}""", "Alice and 1 other")]
    [Arguments("""{"m.heroes":["@a:example.org","@b:example.org"],"m.joined_member_count":1,"m.invited_member_count":0}""", "Empty room (was Alice and Bob)")]
    [Arguments("""{"m.heroes":[],"m.joined_member_count":1,"m.invited_member_count":0}""", "Empty room")]
    [Arguments("""{"m.heroes":["@unknown:example.org"],"m.joined_member_count":2,"m.invited_member_count":0}""", "@unknown:example.org")]
    public async Task DisplayName_WithoutNameOrAlias_ComesFromTheHeroes(string summary, string expected)
    {
        using var context = new MatrixTestContext();
        await context.Process(RoomSync(state: [Member("$a", "@a:example.org", "join", "Alice"), Member("$b", "@b:example.org", "leave", "Bob")],
            summary: summary));

        await Assert.That(context.Room().DisplayName).IsEqualTo(expected);
    }

    [Test]
    public async Task GetMembersAsync_LoadsOnce_AndKeepsWhatTheSyncDelivered()
    {
        using var context = await SyncedAsync(Member("$m1", "@carol:example.org", "join", "Carol"));
        context.Send = (_, _) => Task.FromResult(MatrixTestContext.Json($$"""
            {"chunk":[{{Member("$old", "@carol:example.org", "join", "Old Carol")}},{{Member("$d", "@dave:example.org", "invite", "Dave", "@carol:example.org")}},
             {{Member("$e", "@erin:example.org", "ban", "Erin", "@carol:example.org")}}]}
            """));

        var members = await context.Room().GetMembersAsync();
        await context.Room().GetMembersAsync();

        await Assert.That(context.Requests.Count(r => r.Path.Contains("/members"))).IsEqualTo(1);
        await Assert.That(context.Requests.Single().Path).IsEqualTo("/_matrix/client/v3/rooms/%21room%3Aexample.org/members?not_membership=leave");
        await Assert.That(members.Select(m => m.DisplayName)).IsEquivalentTo(["Carol", "Dave"]);
        await Assert.That(context.Room().Users["@erin:example.org"].Membership).IsEqualTo(Membership.Ban);
    }
}
