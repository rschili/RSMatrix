using RSMatrix.Http;
using RSMatrix.Models;

namespace RSMatrix.Tests;

public class IdentifierIntegrationTests
{
    [Test]
    public async Task CachesKeepCaseDistinctRoomsAndUsersSeparate()
    {
        using var context = new MatrixTestContext();
        var upperRoom = context.Room("!ABC:example.org");
        var lowerRoom = context.Room("!abc:example.org");
        await Assert.That(ReferenceEquals(upperRoom, lowerRoom)).IsFalse();
        UserId.TryParse("@Alice:example.org", out var upperId);
        UserId.TryParse("@alice:example.org", out var lowerId);
        var upperUser = context.Client.GetOrAddUser(upperId!);
        var lowerUser = context.Client.GetOrAddUser(lowerId!);
        await Assert.That(ReferenceEquals(upperUser, lowerUser)).IsFalse();
        var first = context.Client.GetOrAddUser(upperUser, lowerRoom);
        var second = context.Client.GetOrAddUser(lowerUser, lowerRoom);
        await Assert.That(ReferenceEquals(first, second)).IsFalse();
        await Assert.That(lowerRoom.Users.Count).IsEqualTo(2);
    }

    [Test]
    public async Task DomainlessRoom_ReceivesMessagesAndCanReply()
    {
        using var context = new MatrixTestContext();
        await context.Process("""
        {"next_batch":"next","rooms":{"join":{"!abcdef_-123":{"timeline":{"events":[
         {"type":"m.room.message","sender":"@a:b","event_id":"$event","origin_server_ts":1700000000000,
          "content":{"msgtype":"m.text","body":"hello"}}]}}}}}
        """);
        await Assert.That(context.Client.Messages.TryRead(out var message)).IsTrue();
        await Assert.That(message!.Room.RoomId.Full).IsEqualTo("!abcdef_-123");
        await message.SendResponseAsync("reply");
        await Assert.That(context.Requests.Any(r => r.Path.Contains("/rooms/%21abcdef_-123/send/"))).IsTrue();
    }

    [Test]
    public async Task PathSegmentsRoundTripUnicodeAndReservedCharacters()
    {
        using var context = new MatrixTestContext();
        const string roomId = "!a /?#%é:example.org";
        const string eventId = "$event /?#%é";
        await context.Room(roomId).SendTextMessageAsync("hello", null, null);
        await MatrixHelper.PostReceiptAsync(context.Client.HttpClientParameters, context.Room(roomId).RoomId, eventId, null);
        await Assert.That(context.Requests[0].Path.Split('/')[5]).IsEqualTo(Uri.EscapeDataString(roomId));
        await Assert.That(context.Requests[1].Path).IsEqualTo(
            $"/_matrix/client/v3/rooms/{Uri.EscapeDataString(roomId)}/receipt/m.read/{Uri.EscapeDataString(eventId)}");
        await Assert.That(context.Requests.Any(r => r.Path.Contains('?'))).IsFalse();
    }
}
