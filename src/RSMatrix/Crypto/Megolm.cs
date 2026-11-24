using System.Buffers.Binary;
using System.Security.Cryptography;

namespace RSMatrix.Crypto;

/// <summary>
/// The Megolm ratchet: four 256-bit parts R0..R3 and a 32-bit counter. R3 changes
/// every step, R2 every 2^8, R1 every 2^16 and R0 every 2^24 steps.
/// </summary>
internal sealed class MegolmRatchet
{
    public const int Size = 128;
    private const int PartSize = 32;
    private readonly byte[] _data;

    public uint Index { get; private set; }

    public MegolmRatchet(uint index, ReadOnlySpan<byte> data)
    {
        if (data.Length != Size)
            throw new ArgumentException("Megolm ratchets are 128 bytes.", nameof(data));
        Index = index;
        _data = data.ToArray();
    }

    public ReadOnlySpan<byte> Data => _data;

    public MegolmRatchet Clone() => new(Index, _data);

    public void Advance()
    {
        Index++;
        // The part to reseed from: R0 if the index is a multiple of 2^24, R1 for 2^16, R2 for 2^8.
        var from = (Index & 0xFFFFFF) == 0 ? 0 : (Index & 0xFFFF) == 0 ? 1 : (Index & 0xFF) == 0 ? 2 : 3;
        Reseed(from);
    }

    /// <summary>
    /// Advances to a later index without stepping through every index in between: first jump
    /// R0 to each 2^24 boundary, then R1 to each 2^16 boundary, R2 to each 2^8 boundary, and
    /// finally step R3. Each jump is the same reseed that <see cref="Advance"/> does at that boundary.
    /// </summary>
    public void AdvanceTo(uint target)
    {
        if (target < Index)
            throw new ArgumentOutOfRangeException(nameof(target), "Megolm ratchets cannot move backwards.");

        for (var part = 0; part < 4; part++)
        {
            var shift = (3 - part) * 8;
            while ((Index >> shift) < (target >> shift))
            {
                Index = ((Index >> shift) + 1) << shift;
                Reseed(part);
            }
        }
    }

    /// <summary>Derives R(from+1)..R3 from R(from), then advances R(from) itself.</summary>
    private void Reseed(int from)
    {
        for (var part = 3; part >= from; part--)
            Rehash(from, part);
    }

    private void Rehash(int from, int to)
    {
        var hash = CryptoPrimitives.Hmac(_data[(from * PartSize)..((from + 1) * PartSize)], [(byte)to]);
        hash.CopyTo(_data, to * PartSize);
    }
}

internal sealed class MegolmOutboundSession
{
    private readonly Ed25519KeyPair _signingKey;
    private readonly MegolmRatchet _ratchet;

    public MegolmOutboundSession()
    {
        _signingKey = Ed25519KeyPair.Generate();
        _ratchet = new MegolmRatchet(0, RandomNumberGenerator.GetBytes(MegolmRatchet.Size));
    }

    /// <summary>The session's public signing key, which also identifies it in Matrix events.</summary>
    public string SessionId => CryptoPrimitives.ToBase64(_signingKey.PublicKey);

    /// <summary>Number of messages encrypted so far.</summary>
    public uint MessageIndex => _ratchet.Index;

    /// <summary>The signed session sharing format, sent to other devices as <c>session_key</c>.</summary>
    public string ExportSessionKey()
    {
        var unsigned = MegolmFormat.Write(2, _ratchet, _signingKey.PublicKey);
        return CryptoPrimitives.ToBase64([.. unsigned, .. _signingKey.Sign(unsigned)]);
    }

    public string Encrypt(ReadOnlySpan<byte> plaintext)
    {
        var (aesKey, hmacKey, iv) = CryptoPrimitives.DeriveCipherKeys(_ratchet.Data.ToArray(), "MEGOLM_KEYS");
        var message = new List<byte> { MegolmFormat.MessageVersion };
        MessagePayload.WriteInteger(message, MegolmFormat.IndexTag, _ratchet.Index);
        MessagePayload.WriteBytes(message, MegolmFormat.CiphertextTag, CryptoPrimitives.AesEncrypt(aesKey, iv, plaintext));
        message.AddRange(CryptoPrimitives.Hmac(hmacKey, message.ToArray()).AsSpan(0, MegolmFormat.MacLength));
        message.AddRange(_signingKey.Sign(message.ToArray()));
        _ratchet.Advance();
        return CryptoPrimitives.ToBase64(message.ToArray());
    }
}

internal sealed class MegolmInboundSession
{
    private readonly MegolmRatchet _initialRatchet;
    private readonly byte[] _signingKey;

    private MegolmInboundSession(MegolmRatchet initialRatchet, byte[] signingKey)
    {
        _initialRatchet = initialRatchet;
        _signingKey = signingKey;
    }

    public string SessionId => CryptoPrimitives.ToBase64(_signingKey);

    /// <summary>Earliest message index this session can decrypt.</summary>
    public uint FirstKnownIndex => _initialRatchet.Index;

    /// <summary>Imports a signed <c>session_key</c> received from the session's owner.</summary>
    public static MegolmInboundSession FromSessionKey(string sessionKey)
    {
        var data = CryptoPrimitives.FromBase64(sessionKey);
        if (data.Length != MegolmFormat.ExportLength + 64 || data[0] != 2)
            throw new CryptographicException("Invalid Megolm session key.");
        var (ratchet, signingKey) = MegolmFormat.Read(data);
        if (!Ed25519KeyPair.Verify(signingKey, data.AsSpan(0, MegolmFormat.ExportLength), data[MegolmFormat.ExportLength..]))
            throw new CryptographicException("Megolm session key has an invalid signature.");
        return new MegolmInboundSession(ratchet, signingKey);
    }

    /// <summary>Imports the unsigned export format, as written by <see cref="Export"/>.</summary>
    public static MegolmInboundSession FromExport(string export)
    {
        var data = CryptoPrimitives.FromBase64(export);
        if (data.Length != MegolmFormat.ExportLength || data[0] != 1)
            throw new CryptographicException("Invalid Megolm session export.");
        var (ratchet, signingKey) = MegolmFormat.Read(data);
        return new MegolmInboundSession(ratchet, signingKey);
    }

    public string Export() => CryptoPrimitives.ToBase64(MegolmFormat.Write(1, _initialRatchet, _signingKey));

    public (byte[] Plaintext, uint MessageIndex) Decrypt(string ciphertext)
    {
        var message = CryptoPrimitives.FromBase64(ciphertext);
        var macEnd = message.Length - 64;
        var payloadEnd = macEnd - MegolmFormat.MacLength;
        if (payloadEnd < 1 || message[0] != MegolmFormat.MessageVersion)
            throw new CryptographicException("Unsupported Megolm message.");
        if (!Ed25519KeyPair.Verify(_signingKey, message.AsSpan(0, macEnd), message[macEnd..]))
            throw new CryptographicException("Megolm message has an invalid signature.");

        var fields = MessagePayload.Read(message.AsSpan(1, payloadEnd - 1));
        if (fields.GetValueOrDefault(MegolmFormat.IndexTag) is not ulong index || index > uint.MaxValue ||
            fields.GetValueOrDefault(MegolmFormat.CiphertextTag) is not byte[] encrypted)
            throw new CryptographicException("Megolm message is missing fields.");
        if (index < _initialRatchet.Index)
            throw new CryptographicException($"Megolm session only decrypts from index {_initialRatchet.Index}, message has {index}.");

        var ratchet = _initialRatchet.Clone();
        ratchet.AdvanceTo((uint)index);
        var (aesKey, hmacKey, iv) = CryptoPrimitives.DeriveCipherKeys(ratchet.Data.ToArray(), "MEGOLM_KEYS");
        var mac = CryptoPrimitives.Hmac(hmacKey, message.AsSpan(0, payloadEnd)).AsSpan(0, MegolmFormat.MacLength);
        if (!CryptographicOperations.FixedTimeEquals(mac, message.AsSpan(payloadEnd, MegolmFormat.MacLength)))
            throw new CryptographicException("Megolm message has an invalid MAC.");
        return (CryptoPrimitives.AesDecrypt(aesKey, iv, encrypted), (uint)index);
    }
}

/// <summary>Byte layouts from the Megolm specification.</summary>
internal static class MegolmFormat
{
    public const byte MessageVersion = 3;
    public const byte IndexTag = 0x08;
    public const byte CiphertextTag = 0x12;
    public const int MacLength = 8;
    /// <summary>Version byte, big-endian index, ratchet and public signing key.</summary>
    public const int ExportLength = 1 + 4 + MegolmRatchet.Size + 32;

    public static byte[] Write(byte version, MegolmRatchet ratchet, byte[] signingKey)
    {
        var data = new byte[ExportLength];
        data[0] = version;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(1), ratchet.Index);
        ratchet.Data.CopyTo(data.AsSpan(5));
        signingKey.CopyTo(data, 5 + MegolmRatchet.Size);
        return data;
    }

    public static (MegolmRatchet Ratchet, byte[] SigningKey) Read(byte[] data)
    {
        var index = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(1));
        var ratchet = new MegolmRatchet(index, data.AsSpan(5, MegolmRatchet.Size));
        return (ratchet, data[(5 + MegolmRatchet.Size)..ExportLength]);
    }
}
