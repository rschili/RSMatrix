using System.Security.Cryptography;
using System.Text;
using RSMatrix.Crypto;

namespace RSMatrix.Tests;

public class MegolmTests
{
    [Test]
    public async Task AdvanceTo_MatchesSingleSteps_AcrossPartBoundaries()
    {
        var start = new MegolmRatchet(0, RandomNumberGenerator.GetBytes(MegolmRatchet.Size));
        var stepped = start.Clone();
        uint[] targets = [1, 255, 256, 257, 511, 65535, 65536, 65537, 70000];
        foreach (var target in targets)
        {
            while (stepped.Index < target)
                stepped.Advance();
            var jumped = start.Clone();
            jumped.AdvanceTo(target);
            await Assert.That(jumped.Index).IsEqualTo(target);
            await Assert.That(jumped.Data.SequenceEqual(stepped.Data)).IsTrue();
        }
    }

    [Test]
    public async Task AdvanceTo_FromNonZeroIndex_MatchesSingleSteps()
    {
        var start = new MegolmRatchet(250, RandomNumberGenerator.GetBytes(MegolmRatchet.Size));
        var stepped = start.Clone();
        while (stepped.Index < 66000)
            stepped.Advance();
        var jumped = start.Clone();
        jumped.AdvanceTo(66000);
        await Assert.That(jumped.Data.SequenceEqual(stepped.Data)).IsTrue();
    }

    [Test]
    public async Task AdvanceTo_NearTheEndOfTheIndexRange_MatchesSingleSteps()
    {
        var start = new MegolmRatchet(0xFEFFFF00, RandomNumberGenerator.GetBytes(MegolmRatchet.Size));
        var stepped = start.Clone();
        while (stepped.Index < 0xFF000105)
            stepped.Advance();
        var jumped = start.Clone();
        jumped.AdvanceTo(0xFF000105);
        await Assert.That(jumped.Data.SequenceEqual(stepped.Data)).IsTrue();
    }

    [Test]
    public async Task Advance_ReseedsLowerPartsFromHigherPart()
    {
        var data = RandomNumberGenerator.GetBytes(MegolmRatchet.Size);
        var ratchet = new MegolmRatchet(255, data);
        ratchet.Advance();
        // At index 256 R2 and R3 derive from the previous R2; R0 and R1 stay unchanged.
        var previousR2 = data[64..96];
        await Assert.That(ratchet.Data[..64].SequenceEqual(data[..64])).IsTrue();
        await Assert.That(ratchet.Data[64..96].SequenceEqual(HMACSHA256.HashData(previousR2, new byte[] { 2 }))).IsTrue();
        await Assert.That(ratchet.Data[96..].SequenceEqual(HMACSHA256.HashData(previousR2, new byte[] { 3 }))).IsTrue();
    }

    [Test]
    public async Task SharedSession_DecryptsInAnyOrder_AndReportsIndex()
    {
        var outbound = new MegolmOutboundSession();
        var inbound = MegolmInboundSession.FromSessionKey(outbound.ExportSessionKey());
        await Assert.That(inbound.SessionId).IsEqualTo(outbound.SessionId);

        var messages = Enumerable.Range(0, 5).Select(i => outbound.Encrypt(Encoding.UTF8.GetBytes($"message {i}"))).ToList();
        await Assert.That(outbound.MessageIndex).IsEqualTo(5u);
        foreach (var i in new[] { 3, 0, 4, 1, 2, 3 })
        {
            var (plaintext, index) = inbound.Decrypt(messages[i]);
            await Assert.That(Encoding.UTF8.GetString(plaintext)).IsEqualTo($"message {i}");
            await Assert.That(index).IsEqualTo((uint)i);
        }
    }

    [Test]
    public async Task LateSessionKey_CannotDecryptEarlierMessages()
    {
        var outbound = new MegolmOutboundSession();
        var early = outbound.Encrypt("early"u8);
        var inbound = MegolmInboundSession.FromSessionKey(outbound.ExportSessionKey());
        var late = outbound.Encrypt("late"u8);

        await Assert.That(inbound.FirstKnownIndex).IsEqualTo(1u);
        await Assert.That(Encoding.UTF8.GetString(inbound.Decrypt(late).Plaintext)).IsEqualTo("late");
        await Assert.ThrowsAsync<CryptographicException>(() => Task.FromResult(inbound.Decrypt(early)));
    }

    [Test]
    public async Task Export_RoundTrips()
    {
        var outbound = new MegolmOutboundSession();
        var inbound = MegolmInboundSession.FromSessionKey(outbound.ExportSessionKey());
        var restored = MegolmInboundSession.FromExport(inbound.Export());
        var message = outbound.Encrypt("hello"u8);
        await Assert.That(Encoding.UTF8.GetString(restored.Decrypt(message).Plaintext)).IsEqualTo("hello");
        await Assert.That(restored.SessionId).IsEqualTo(outbound.SessionId);
    }

    [Test]
    public async Task TamperedSessionKey_IsRejected()
    {
        var key = CryptoPrimitives.FromBase64(new MegolmOutboundSession().ExportSessionKey());
        key[10] ^= 1;
        await Assert.ThrowsAsync<CryptographicException>(() =>
            Task.FromResult(MegolmInboundSession.FromSessionKey(CryptoPrimitives.ToBase64(key))));
    }

    [Test]
    public async Task TamperedOrForeignMessage_IsRejected()
    {
        var outbound = new MegolmOutboundSession();
        var inbound = MegolmInboundSession.FromSessionKey(outbound.ExportSessionKey());
        var message = CryptoPrimitives.FromBase64(outbound.Encrypt("hello"u8));
        foreach (var position in new[] { 0, 3, message.Length - 70, message.Length - 1 })
        {
            var tampered = (byte[])message.Clone();
            tampered[position] ^= 1;
            await Assert.ThrowsAsync<CryptographicException>(() =>
                Task.FromResult(inbound.Decrypt(CryptoPrimitives.ToBase64(tampered))));
        }

        var other = new MegolmOutboundSession();
        await Assert.ThrowsAsync<CryptographicException>(() =>
            Task.FromResult(inbound.Decrypt(other.Encrypt("hello"u8))));
    }
}
