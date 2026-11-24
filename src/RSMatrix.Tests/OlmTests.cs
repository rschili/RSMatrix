using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RSMatrix.Crypto;

namespace RSMatrix.Tests;

public class OlmTests
{
    private static (OlmAccount Alice, OlmAccount Bob, OlmSession AliceSession) CreateOutbound()
    {
        var alice = OlmAccount.Create();
        var bob = OlmAccount.Create();
        bob.GenerateOneTimeKeys(1);
        var oneTimeKey = bob.UnpublishedOneTimeKeys().Single().Value;
        return (alice, bob, alice.CreateOutboundSession(bob.IdentityKey, oneTimeKey));
    }

    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    [Test]
    public async Task PreKeyMessage_CreatesInboundSession_AndConsumesOneTimeKey()
    {
        var (alice, bob, aliceSession) = CreateOutbound();
        var (type, body) = aliceSession.Encrypt("hello bob"u8);
        await Assert.That(type).IsEqualTo(0);

        var (bobSession, plaintext) = bob.CreateInboundSession(alice.IdentityKey, body);
        await Assert.That(Text(plaintext)).IsEqualTo("hello bob");
        await Assert.That(bobSession.SessionId).IsEqualTo(aliceSession.SessionId);
        await Assert.That(bob.OneTimeKeys.Count).IsEqualTo(0);
        await Assert.That(bobSession.Matches(body)).IsTrue();
    }

    [Test]
    public async Task Conversation_RatchetsBothWays_AndSwitchesToNormalMessages()
    {
        var (alice, bob, aliceSession) = CreateOutbound();
        var (firstType, firstBody) = aliceSession.Encrypt("1"u8);
        var (secondType, secondBody) = aliceSession.Encrypt("2"u8);
        await Assert.That(secondType).IsEqualTo(0);
        var (bobSession, _) = bob.CreateInboundSession(alice.IdentityKey, firstBody);
        await Assert.That(Text(bobSession.Decrypt(secondType, secondBody))).IsEqualTo("2");

        for (var round = 0; round < 3; round++)
        {
            var reply = bobSession.Encrypt(Encoding.UTF8.GetBytes($"bob {round}"));
            await Assert.That(reply.Type).IsEqualTo(1);
            await Assert.That(Text(aliceSession.Decrypt(reply.Type, reply.Body))).IsEqualTo($"bob {round}");

            var message = aliceSession.Encrypt(Encoding.UTF8.GetBytes($"alice {round}"));
            await Assert.That(message.Type).IsEqualTo(1);
            await Assert.That(Text(bobSession.Decrypt(message.Type, message.Body))).IsEqualTo($"alice {round}");
        }
    }

    [Test]
    public async Task OutOfOrderMessages_UseSkippedKeys_AndCannotBeReplayed()
    {
        var (alice, bob, aliceSession) = CreateOutbound();
        var (bobSession, _) = bob.CreateInboundSession(alice.IdentityKey, aliceSession.Encrypt("setup"u8).Body);
        var messages = Enumerable.Range(0, 4).Select(i => bobSession.Encrypt(Encoding.UTF8.GetBytes($"m{i}"))).ToList();

        foreach (var i in new[] { 2, 0, 3, 1 })
            await Assert.That(Text(aliceSession.Decrypt(1, messages[i].Body))).IsEqualTo($"m{i}");
        foreach (var message in messages)
            await Assert.ThrowsAsync<CryptographicException>(() => Task.FromResult(aliceSession.Decrypt(1, message.Body)));
    }

    [Test]
    public async Task MessagesFromAnOlderChain_StillDecrypt()
    {
        var (alice, bob, aliceSession) = CreateOutbound();
        var (bobSession, _) = bob.CreateInboundSession(alice.IdentityKey, aliceSession.Encrypt("setup"u8).Body);
        var first = bobSession.Encrypt("first"u8);
        var delayed = bobSession.Encrypt("delayed"u8);
        aliceSession.Decrypt(1, first.Body);
        bobSession.Decrypt(1, aliceSession.Encrypt("ratchet"u8).Body);
        var newChain = bobSession.Encrypt("new chain"u8);

        await Assert.That(Text(aliceSession.Decrypt(1, newChain.Body))).IsEqualTo("new chain");
        await Assert.That(Text(aliceSession.Decrypt(1, delayed.Body))).IsEqualTo("delayed");
    }

    [Test]
    public async Task TamperedMessage_IsRejected_WithoutChangingState()
    {
        var (alice, bob, aliceSession) = CreateOutbound();
        var (bobSession, _) = bob.CreateInboundSession(alice.IdentityKey, aliceSession.Encrypt("setup"u8).Body);
        var reply = bobSession.Encrypt("reply"u8);
        var tampered = CryptoPrimitives.FromBase64(reply.Body);
        tampered[^1] ^= 1;

        await Assert.ThrowsAsync<CryptographicException>(() =>
            Task.FromResult(aliceSession.Decrypt(1, CryptoPrimitives.ToBase64(tampered))));
        await Assert.That(aliceSession.HasReceivedMessage).IsFalse();
        await Assert.That(Text(aliceSession.Decrypt(1, reply.Body))).IsEqualTo("reply");
    }

    [Test]
    public async Task PreKeyMessage_WithWrongSenderIdentity_IsRejected_AndKeepsOneTimeKey()
    {
        var (_, bob, aliceSession) = CreateOutbound();
        var (_, body) = aliceSession.Encrypt("hello"u8);
        await Assert.ThrowsAsync<CryptographicException>(() =>
            Task.FromResult(bob.CreateInboundSession(OlmAccount.Create().IdentityKey, body)));
        await Assert.That(bob.OneTimeKeys.Count).IsEqualTo(1);
    }

    [Test]
    public async Task FallbackKey_CanBeUsedRepeatedly()
    {
        var bob = OlmAccount.Create();
        bob.GenerateFallbackKey();
        var (keyId, fallbackKey) = bob.UnpublishedFallbackKey()!.Value;
        bob.MarkKeysAsPublished();
        await Assert.That(bob.UnpublishedFallbackKey()).IsNull();

        foreach (var name in new[] { "alice", "carol" })
        {
            var sender = OlmAccount.Create();
            var session = sender.CreateOutboundSession(bob.IdentityKey, fallbackKey);
            var (_, plaintext) = bob.CreateInboundSession(sender.IdentityKey, session.Encrypt(Encoding.UTF8.GetBytes(name)).Body);
            await Assert.That(Text(plaintext)).IsEqualTo(name);
        }
        await Assert.That(bob.FallbackKeyId).IsEqualTo(keyId);
    }

    [Test]
    public async Task AccountAndSession_SurviveJsonRoundTrip()
    {
        var (alice, bob, aliceSession) = CreateOutbound();
        var (bobSession, _) = bob.CreateInboundSession(alice.IdentityKey, aliceSession.Encrypt("setup"u8).Body);
        aliceSession.Decrypt(1, bobSession.Encrypt("reply"u8).Body);

        var restoredAlice = JsonSerializer.Deserialize<OlmAccount>(JsonSerializer.Serialize(alice))!;
        var restoredSession = JsonSerializer.Deserialize<OlmSession>(JsonSerializer.Serialize(aliceSession))!;
        await Assert.That(restoredAlice.IdentityKey.SequenceEqual(alice.IdentityKey)).IsTrue();
        await Assert.That(restoredAlice.SigningKey.SequenceEqual(alice.SigningKey)).IsTrue();
        await Assert.That(restoredSession.SessionId).IsEqualTo(aliceSession.SessionId);

        var message = restoredSession.Encrypt("after restart"u8);
        await Assert.That(Text(bobSession.Decrypt(message.Type, message.Body))).IsEqualTo("after restart");
        var answer = bobSession.Encrypt("answer"u8);
        await Assert.That(Text(restoredSession.Decrypt(answer.Type, answer.Body))).IsEqualTo("answer");
    }
}
