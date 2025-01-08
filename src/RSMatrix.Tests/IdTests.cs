using RSMatrix.Models;

namespace RSMatrix.Tests;

public class IdTests
{
    [Test]
    [Arguments("@user:server", true, "user", "server")]
    [Arguments("@user:server:extra", true, "user", "server:extra")]
    [Arguments("@user", false, "", "")]
    [Arguments("user:server", false, "", "")]
    [Arguments("", false, "", "")]
    [Arguments("user", false, "", "")]
    [Arguments("user:", false, "", "")]
    [Arguments(":server", false, "", "")]
    [Arguments("!user:server", false, "", "")]
    public async Task TryParseUserId(string input, bool expectedResult, string expectedUser, string expectedServer)
    {
        var result = UserId.TryParse(input, out var userId);
        await Assert.That(result).IsEqualTo(expectedResult);
        if(expectedResult)
        {
            await Assert.That(userId).IsNotEqualTo(null);
            await Assert.That(userId!.Localpart.ToString()).IsEqualTo(expectedUser);
            await Assert.That(userId!.Domain.ToString()).IsEqualTo(expectedServer);
            await Assert.That(userId!.Full).IsEqualTo(input);
        }
    }

    [Test]
    [Arguments("!room:server", true, "room", "server")]
    [Arguments("!room:server:extra", true, "room", "server:extra")]
    [Arguments("!room", false, "", "")]
    [Arguments("room:server", false, "", "")]
    [Arguments("", false, "", "")]
    [Arguments("room", false, "", "")]
    [Arguments("room:", false, "", "")]
    [Arguments(":server", false, "", "")]
    [Arguments("@room:server", false, "", "")]
    public async Task TryParseRoomId(string input, bool expectedResult, string expectedRoom, string expectedServer)
    {
        var result = RoomId.TryParse(input, out var roomId);
        await Assert.That(result).IsEqualTo(expectedResult);
        if(expectedResult)
        {
            await Assert.That(roomId).IsNotEqualTo(null);
            await Assert.That(roomId!.Localpart.ToString()).IsEqualTo(expectedRoom);
            await Assert.That(roomId!.Domain.ToString()).IsEqualTo(expectedServer);
            await Assert.That(roomId!.Full).IsEqualTo(input);
        }
    }

    [Test]
    [Arguments("#alias:server", true, "alias", "server")]
    [Arguments("#alias:server:extra", true, "alias", "server:extra")]
    [Arguments("#alias", false, "", "")]
    [Arguments("alias:server", false, "", "")]
    [Arguments("", false, "", "")]
    [Arguments("alias", false, "", "")]
    [Arguments("alias:", false, "", "")]
    [Arguments(":server", false, "", "")]
    [Arguments("!alias:server", false, "", "")]
    public async Task TryParseRoomAlias(string input, bool expectedResult, string expectedAlias, string expectedServer)
    {
        var result = RoomAlias.TryParse(input, out var roomAlias);
        await Assert.That(result).IsEqualTo(expectedResult);
        if(expectedResult)
        {
            await Assert.That(roomAlias).IsNotEqualTo(null);
            await Assert.That(roomAlias!.Localpart.ToString()).IsEqualTo(expectedAlias);
            await Assert.That(roomAlias!.Domain.ToString()).IsEqualTo(expectedServer);
            await Assert.That(roomAlias!.Full).IsEqualTo(input);
        }
    }
}