using System.Text;
using RSMatrix.Models;

namespace RSMatrix.Tests;

public class IdTests
{
    [Test]
    [Arguments("@user:server", true, "user", "server")]
    [Arguments("@a:b", true, "a", "b")]
    [Arguments("@:server", true, "", "server")]
    [Arguments("@USER:SERVER", true, "USER", "SERVER")]
    [Arguments("@historical sender:server", true, "historical sender", "server")]
    [Arguments("@é日本語😀:server", true, "é日本語😀", "server")]
    [Arguments("@\t\n\u0001:server", true, "\t\n\u0001", "server")]
    [Arguments("@a/#?%&=+_:server", true, "a/#?%&=+_", "server")]
    [Arguments("@user:server:extra", false, "", "")]
    [Arguments("@user:", false, "", "")]
    [Arguments("@user\0:server", false, "", "")]
    [Arguments(null, false, "", "")]
    [Arguments("@user", false, "", "")]
    [Arguments("user:server", false, "", "")]
    [Arguments("", false, "", "")]
    [Arguments("user", false, "", "")]
    [Arguments("user:", false, "", "")]
    [Arguments(":server", false, "", "")]
    [Arguments("!user:server", false, "", "")]
    public async Task TryParseUserId(string? input, bool expectedResult, string expectedUser, string expectedServer)
    {
        var result = UserId.TryParse(input, out var userId);
        await Assert.That(result).IsEqualTo(expectedResult);
        if(expectedResult)
        {
            await Assert.That(userId).IsNotNull();
            await Assert.That(userId!.Localpart.ToString()).IsEqualTo(expectedUser);
            await Assert.That(userId!.Domain.ToString()).IsEqualTo(expectedServer);
            await Assert.That(userId!.Full).IsEqualTo(input!);
            await Assert.That(userId.Kind).IsEqualTo(IdKind.User);
        }
        else
        {
            await Assert.That(userId).IsNull();
        }
    }

    [Test]
    [Arguments("!room:server", true, "room", "server")]
    [Arguments("!r:s", true, "r", "s")]
    [Arguments("!:server", true, "", "server")]
    [Arguments("!é日本語😀 /?#:server", true, "é日本語😀 /?#", "server")]
    [Arguments("!room:server:extra", false, "", "")]
    [Arguments("!room", true, "room", "")]
    [Arguments("!r", true, "r", "")]
    [Arguments("!opaque-_/+=", true, "opaque-_/+=", "")]
    [Arguments("!é日本語😀", true, "é日本語😀", "")]
    [Arguments("!", false, "", "")]
    [Arguments("!room:", false, "", "")]
    [Arguments("!opaque\0", false, "", "")]
    [Arguments("!room\0:server", false, "", "")]
    [Arguments(null, false, "", "")]
    [Arguments("room:server", false, "", "")]
    [Arguments("", false, "", "")]
    [Arguments("room", false, "", "")]
    [Arguments("room:", false, "", "")]
    [Arguments(":server", false, "", "")]
    [Arguments("@room:server", false, "", "")]
    public async Task TryParseRoomId(string? input, bool expectedResult, string expectedRoom, string expectedServer)
    {
        var result = RoomId.TryParse(input, out var roomId);
        await Assert.That(result).IsEqualTo(expectedResult);
        if(expectedResult)
        {
            await Assert.That(roomId).IsNotNull();
            await Assert.That(roomId!.Localpart.ToString()).IsEqualTo(expectedRoom);
            await Assert.That(roomId!.Domain.ToString()).IsEqualTo(expectedServer);
            await Assert.That(roomId!.Full).IsEqualTo(input!);
            await Assert.That(roomId.Kind).IsEqualTo(IdKind.Room);
        }
        else
        {
            await Assert.That(roomId).IsNull();
        }
    }

    [Test]
    [Arguments("#alias:server", true, "alias", "server")]
    [Arguments("#a:b", true, "a", "b")]
    [Arguments("#:server", true, "", "server")]
    [Arguments("#é日本語😀 /?#:server", true, "é日本語😀 /?#", "server")]
    [Arguments("#alias:server:extra", false, "", "")]
    [Arguments("#alias:", false, "", "")]
    [Arguments("#alias\0:server", false, "", "")]
    [Arguments(null, false, "", "")]
    [Arguments("#alias", false, "", "")]
    [Arguments("alias:server", false, "", "")]
    [Arguments("", false, "", "")]
    [Arguments("alias", false, "", "")]
    [Arguments("alias:", false, "", "")]
    [Arguments(":server", false, "", "")]
    [Arguments("!alias:server", false, "", "")]
    public async Task TryParseRoomAlias(string? input, bool expectedResult, string expectedAlias, string expectedServer)
    {
        var result = RoomAlias.TryParse(input, out var roomAlias);
        await Assert.That(result).IsEqualTo(expectedResult);
        if(expectedResult)
        {
            await Assert.That(roomAlias).IsNotNull();
            await Assert.That(roomAlias!.Localpart.ToString()).IsEqualTo(expectedAlias);
            await Assert.That(roomAlias!.Domain.ToString()).IsEqualTo(expectedServer);
            await Assert.That(roomAlias!.Full).IsEqualTo(input!);
            await Assert.That(roomAlias.Kind).IsEqualTo(IdKind.RoomAlias);
        }
        else
        {
            await Assert.That(roomAlias).IsNull();
        }
    }

    [Test]
    [Arguments("matrix.org", true)]
    [Arguments("MATRIX.ORG", true)]
    [Arguments("my-server.example:8448", true)]
    [Arguments("server:0", true)]
    [Arguments("server:99999", true)]
    [Arguments("server:00001", true)]
    [Arguments("1.2.3.4", true)]
    [Arguments("255.255.255.255:1234", true)]
    [Arguments("[::]", true)]
    [Arguments("[::1]:8448", true)]
    [Arguments("[1234:5678::ABCD]", true)]
    [Arguments("[::ffff:192.0.2.1]:1234", true)]
    [Arguments("", false)]
    [Arguments(":8448", false)]
    [Arguments("server:", false)]
    [Arguments("server:extra", false)]
    [Arguments("server:123456", false)]
    [Arguments("server:-1", false)]
    [Arguments("server:+1", false)]
    [Arguments("server:１２", false)]
    [Arguments("server:12:34", false)]
    [Arguments("256.2.3.4", false)]
    [Arguments("0000.2.3.4", false)]
    [Arguments("[::1", false)]
    [Arguments("::1", false)]
    [Arguments("[]", false)]
    [Arguments("[1]", false)]
    [Arguments("[1.2.3.4]", false)]
    [Arguments("[1::2::3]", false)]
    [Arguments("[::g]", false)]
    [Arguments("[::1%eth0]", false)]
    [Arguments("[::1]suffix", false)]
    [Arguments("[::1]:", false)]
    [Arguments("[::1]:extra", false)]
    [Arguments("[::1]:123456", false)]
    [Arguments("[::1]/path", false)]
    [Arguments("server/path", false)]
    [Arguments("server\\path", false)]
    [Arguments("server?query", false)]
    [Arguments("server#fragment", false)]
    [Arguments("server%2fpath", false)]
    [Arguments("server@other", false)]
    [Arguments("server_name", false)]
    [Arguments("server=other", false)]
    [Arguments("server+other", false)]
    [Arguments("server name", false)]
    [Arguments("server\n", false)]
    [Arguments("server\0", false)]
    [Arguments("sérver", false)]
    public async Task ServerNameIsValidatedIndependently(string server, bool expectedResult)
    {
        foreach (var sigil in new[] { '@', '!', '#' })
        {
            var input = $"{sigil}localpart:{server}";
            await Assert.That(MatrixId.TryParse(input, out var id)).IsEqualTo(expectedResult);
            if (expectedResult)
            {
                await Assert.That(id!.Domain.ToString()).IsEqualTo(server);
                await Assert.That(id.Full).IsEqualTo(input);
            }
            else
            {
                await Assert.That(id).IsNull();
            }
        }
    }

    [Test]
    [Arguments(0x0000, false)]
    [Arguments(0x0001, true)]
    [Arguments(0x0020, true)]
    [Arguments(0x003A, false)]
    [Arguments(0x007F, true)]
    [Arguments(0x00E9, true)]
    [Arguments(0xD7FF, true)]
    [Arguments(0xD800, false)]
    [Arguments(0xDBFF, false)]
    [Arguments(0xDC00, false)]
    [Arguments(0xDFFF, false)]
    [Arguments(0xE000, true)]
    [Arguments(0xFFFD, true)]
    [Arguments(0x1F600, true)]
    [Arguments(0x10FFFF, true)]
    public async Task LocalpartsRequireValidUnicode(int codePoint, bool expectedResult)
    {
        // Construct surrogates at runtime: custom-attribute strings are encoded as UTF-8.
        var character = codePoint <= char.MaxValue ? ((char)codePoint).ToString() : char.ConvertFromUtf32(codePoint);
        foreach (var input in new[] { $"@{character}:s", $"!{character}:s", $"#{character}:s", $"!{character}" })
        {
            await Assert.That(MatrixId.TryParse(input, out var id)).IsEqualTo(expectedResult);
            if (expectedResult)
                await Assert.That(id!.Localpart.ToString()).IsEqualTo(character);
            else
                await Assert.That(id).IsNull();
        }

        // A valid localpart must not allow invalid Unicode to slip into the server name.
        if (codePoint is >= 0xD800 and <= 0xDFFF)
        {
            await Assert.That(MatrixId.TryParse($"@user:server{character}", out var id)).IsFalse();
            await Assert.That(id).IsNull();
        }
    }

    [Test]
    [Arguments(0xD800, 0xDC00, true)]
    [Arguments(0xDBFF, 0xDFFF, true)]
    [Arguments(0xDC00, 0xD800, false)]
    [Arguments(0xD800, 0xD800, false)]
    [Arguments(0xDC00, 0xDC00, false)]
    [Arguments(0xD800, 0x0061, false)]
    [Arguments(0x0061, 0xDC00, false)]
    [Arguments(0x0061, 0xD800, false)]
    public async Task SurrogatePairsMustBeWellFormed(int first, int second, bool expectedResult)
    {
        var localpart = new string(new[] { (char)first, (char)second });
        foreach (var input in new[] { $"@{localpart}:s", $"!{localpart}:s", $"#{localpart}:s", $"!{localpart}" })
        {
            await Assert.That(MatrixId.TryParse(input, out _)).IsEqualTo(expectedResult);
        }
    }

    [Test]
    [Arguments("@", ":s")]
    [Arguments("!", ":s")]
    [Arguments("#", ":s")]
    [Arguments("!", "")]
    [Arguments("@", ":[::1]:8448")]
    public async Task IdentifierLengthIsLimitedTo255Utf8Bytes(string sigil, string suffix)
    {
        var localpartBudget = 255 - Encoding.UTF8.GetByteCount(sigil + suffix);
        foreach (var character in new[] { "a", "é", "日", "😀" })
        {
            var characterBytes = Encoding.UTF8.GetByteCount(character);
            var localpart = string.Concat(Enumerable.Repeat(character, localpartBudget / characterBytes))
                + new string('a', localpartBudget % characterBytes);
            var input = sigil + localpart + suffix;
            await Assert.That(Encoding.UTF8.GetByteCount(input)).IsEqualTo(255);
            await Assert.That(MatrixId.TryParse(input, out var id)).IsTrue();
            await Assert.That(id!.Full).IsEqualTo(input);

            var tooLong = sigil + localpart + "a" + suffix;
            await Assert.That(Encoding.UTF8.GetByteCount(tooLong)).IsEqualTo(256);
            await Assert.That(MatrixId.TryParse(tooLong, out var rejected)).IsFalse();
            await Assert.That(rejected).IsNull();
        }
    }

    [Test]
    [Arguments("@user:server", "@user:server", true)]
    [Arguments("!opaque", "!opaque", true)]
    [Arguments("#alias:[::1]:8448", "#alias:[::1]:8448", true)]
    [Arguments("@é:server", "@é:server", true)]
    [Arguments("@user:server", "@User:server", false)]
    [Arguments("@user:server", "@user:SERVER", false)]
    [Arguments("!opaque", "!Opaque", false)]
    [Arguments("!room:server", "!room:SERVER", false)]
    [Arguments("#alias:server", "#alias:SERVER", false)]
    [Arguments("@user:[::abcd]", "@user:[::ABCD]", false)]
    [Arguments("@user:server:1", "@user:server:01", false)]
    [Arguments("@user:server", "!user:server", false)]
    [Arguments("!room:server", "#room:server", false)]
    [Arguments("@é:server", "@e\u0301:server", false)]
    public async Task EqualityUsesTheExactFullIdentifier(string first, string second, bool expectedResult)
    {
        await Assert.That(MatrixId.TryParse(first, out var left)).IsTrue();
        await Assert.That(MatrixId.TryParse(second, out var right)).IsTrue();
        await Assert.That(left!.Equals(right)).IsEqualTo(expectedResult);
        await Assert.That(left.Equals((object?)right)).IsEqualTo(expectedResult);
        await Assert.That(((IEquatable<MatrixId>)left).Equals(right)).IsEqualTo(expectedResult);
        await Assert.That(left.Equals((MatrixId?)null)).IsFalse();
        await Assert.That(left!.Equals((object?)null)).IsFalse();
        await Assert.That(left!.Equals(first)).IsFalse();
        await Assert.That(left.ToString()).IsEqualTo(first);
        await Assert.That(new HashSet<MatrixId> { left }.Contains(right!)).IsEqualTo(expectedResult);
        if (expectedResult)
            await Assert.That(left.GetHashCode()).IsEqualTo(right!.GetHashCode());
    }
}