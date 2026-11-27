using System.Net;
using RSMatrix.Http;
using RSMatrix.Models;

namespace RSMatrix.Tests;

public class RequestTests
{
    private const string InviteRoom = "!invite:example.org";

    private static string Stripped(string type, string stateKey, string content, string sender = "@alice:example.org")
        => $$"""{"type":"{{type}}","state_key":"{{stateKey}}","sender":"{{sender}}","content":{{content}}}""";

    private static string OwnInvite(bool isDirect = false)
        => Stripped("m.room.member", "@bot:example.org", isDirect ? """{"membership":"invite","is_direct":true}""" : """{"membership":"invite"}""");

    private static string InviteSync(params string[] events)
        => $$"""{"next_batch":"next","rooms":{"invite":{"{{InviteRoom}}":{"invite_state":{"events":[{{string.Join(",", events)}}]""" + "}}}}}";

    private static List<RoomInvite> ReadInvites(MatrixTestContext context)
        => context.ReadUpdates().Select(u => u.Value).OfType<Request>().Select(r => r.Value).OfType<RoomInvite>().ToList();

    [Test]
    public async Task Invite_ComesAsRequest_WithPreview()
    {
        using var context = new MatrixTestContext();
        await context.Process(InviteSync(
            OwnInvite(isDirect: true),
            Stripped("m.room.member", "@alice:example.org", """{"membership":"join","displayname":"Alice","avatar_url":"mxc://example.org/alice"}"""),
            Stripped("m.room.name", "", """{"name":"Workshop"}"""),
            Stripped("m.room.topic", "", """{"topic":"Tools"}"""),
            Stripped("m.room.avatar", "", """{"url":"mxc://example.org/room"}"""),
            Stripped("m.room.encryption", "", """{"algorithm":"m.megolm.v1.aes-sha2"}"""),
            Stripped("m.room.create", "", """{"room_version":"11"}""")));

        var updates = context.ReadUpdates();
        var request = (Request)updates.Single().Value!;
        await Assert.That(request.From.User.UserId.Full).IsEqualTo("@alice:example.org");
        await Assert.That(request.From.DisplayName).IsEqualTo("Alice");
        await Assert.That(request.From.AvatarUrl).IsEqualTo("mxc://example.org/alice");

        var invite = (RoomInvite)request.Value!;
        await Assert.That(invite.RoomId.Full).IsEqualTo(InviteRoom);
        await Assert.That(invite.DisplayName).IsEqualTo("Workshop");
        await Assert.That(invite.RoomName).IsEqualTo("Workshop");
        await Assert.That(invite.Topic).IsEqualTo("Tools");
        await Assert.That(invite.AvatarUrl).IsEqualTo("mxc://example.org/room");
        await Assert.That(invite.IsDirect).IsTrue();
        await Assert.That(invite.IsEncrypted).IsTrue();
        await Assert.That(context.Client.Rooms.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Invite_WithoutName_IsNamedByAlias_ElseByInviter()
    {
        using var context = new MatrixTestContext();
        await context.Process(InviteSync(OwnInvite(), Stripped("m.room.canonical_alias", "", """{"alias":"#tools:example.org"}""")));
        await Assert.That(ReadInvites(context).Single().DisplayName).IsEqualTo("#tools:example.org");

        await context.Process(InviteSync(OwnInvite(),
            Stripped("m.room.member", "@alice:example.org", """{"membership":"join","displayname":"Alice"}""")));
        var direct = ReadInvites(context).Single();
        await Assert.That(direct.RoomName).IsNull();
        await Assert.That(direct.DisplayName).IsEqualTo("Alice");

        await context.Process(InviteSync(OwnInvite()));
        await Assert.That(ReadInvites(context).Single().DisplayName).IsEqualTo("alice");
    }

    [Test]
    public async Task Invite_WithoutOwnMemberEvent_IsIgnored()
    {
        using var context = new MatrixTestContext();
        await context.Process(InviteSync(Stripped("m.room.name", "", """{"name":"Workshop"}""")));
        await Assert.That(context.ReadUpdates().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Accept_Joins_AndDecline_LeavesWithReason()
    {
        using var context = new MatrixTestContext();
        await context.Process(InviteSync(OwnInvite(isDirect: true)));
        var request = (Request)context.ReadUpdates().Single().Value!;

        await request.AcceptAsync();
        await request.DeclineAsync("Not today");
        await request.DeclineAsync();

        await Assert.That(string.Join(" ", context.Requests.Select(r => r.Path.Split('/')[^1]))).IsEqualTo("join leave leave");
        await Assert.That(Uri.UnescapeDataString(context.Requests[0].Path)).IsEqualTo("/_matrix/client/v3/rooms/!invite:example.org/join");
        await Assert.That(context.Requests[0].Body).IsEqualTo("{}");
        await Assert.That(context.Requests[1].Body).IsEqualTo("""{"reason":"Not today"}""");
        await Assert.That(context.Requests[2].Body).IsEqualTo("{}");
        await Assert.That(context.Room(InviteRoom).IsDirect).IsTrue();
    }

    [Test]
    public async Task Accept_ThrowsWhatTheServerAnswers()
    {
        using var context = new MatrixTestContext();
        context.Send = (_, _) => Task.FromResult(MatrixTestContext.Json("""{"errcode":"M_FORBIDDEN","error":"Not invited"}""", HttpStatusCode.Forbidden));
        await context.Process(InviteSync(OwnInvite()));
        var request = (Request)context.ReadUpdates().Single().Value!;

        var error = await Assert.ThrowsAsync<MatrixResponseException>(() => request.AcceptAsync());
        await Assert.That(error!.ErrorCode).IsEqualTo("M_FORBIDDEN");
    }
}
