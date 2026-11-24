using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace RSMatrix.Crypto;

/// <summary>
/// A device's long-term Olm identity plus its one-time and fallback keys.
/// Public properties hold the persisted state.
/// </summary>
internal sealed class OlmAccount
{
    public required byte[] SigningKeySeed { get; init; }
    public required byte[] IdentityKeyPrivate { get; init; }
    public Dictionary<string, OlmOneTimeKey> OneTimeKeys { get; init; } = [];
    public uint NextKeyId { get; set; }
    public OlmOneTimeKey? FallbackKey { get; set; }
    public string? FallbackKeyId { get; set; }
    public OlmOneTimeKey? PreviousFallbackKey { get; set; }

    public static OlmAccount Create() => new()
    {
        SigningKeySeed = RandomNumberGenerator.GetBytes(32),
        IdentityKeyPrivate = RandomNumberGenerator.GetBytes(32)
    };

    /// <summary>The Curve25519 identity key, published as <c>curve25519:DEVICEID</c>.</summary>
    [JsonIgnore]
    public byte[] IdentityKey => new Curve25519KeyPair(IdentityKeyPrivate).PublicKey;

    /// <summary>The Ed25519 fingerprint key, published as <c>ed25519:DEVICEID</c>.</summary>
    [JsonIgnore]
    public byte[] SigningKey => new Ed25519KeyPair(SigningKeySeed).PublicKey;

    public byte[] Sign(ReadOnlySpan<byte> data) => new Ed25519KeyPair(SigningKeySeed).Sign(data);

    public void GenerateOneTimeKeys(int count)
    {
        for (var i = 0; i < count; i++)
            OneTimeKeys[NewKeyId()] = new OlmOneTimeKey { PrivateKey = RandomNumberGenerator.GetBytes(32) };
    }

    /// <summary>Replaces the fallback key. The previous one stays usable for late pre-key messages.</summary>
    public void GenerateFallbackKey()
    {
        PreviousFallbackKey = FallbackKey;
        FallbackKey = new OlmOneTimeKey { PrivateKey = RandomNumberGenerator.GetBytes(32) };
        FallbackKeyId = NewKeyId();
    }

    public Dictionary<string, byte[]> UnpublishedOneTimeKeys()
        => OneTimeKeys.Where(pair => !pair.Value.Published)
            .ToDictionary(pair => pair.Key, pair => pair.Value.PublicKey);

    public (string KeyId, byte[] PublicKey)? UnpublishedFallbackKey()
        => FallbackKey is { Published: false } key ? (FallbackKeyId!, key.PublicKey) : null;

    /// <summary>
    /// Published keys may never be claimed. Drops the oldest published ones beyond <paramref name="maxCount"/>.
    /// </summary>
    public void ForgetOldestOneTimeKeys(int maxCount)
    {
        var excess = OneTimeKeys.Count - maxCount;
        if (excess <= 0)
            return;
        var oldest = OneTimeKeys.Where(pair => pair.Value.Published)
            .OrderBy(pair => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(CryptoPrimitives.FromBase64(pair.Key)))
            .Take(excess).Select(pair => pair.Key).ToList();
        foreach (var keyId in oldest)
            OneTimeKeys.Remove(keyId);
    }

    public void MarkKeysAsPublished()
    {
        foreach (var key in OneTimeKeys.Values)
            key.Published = true;
        if (FallbackKey != null)
            FallbackKey.Published = true;
    }

    public OlmSession CreateOutboundSession(byte[] theirIdentityKey, byte[] theirOneTimeKey)
    {
        var identityKey = new Curve25519KeyPair(IdentityKeyPrivate);
        var baseKey = Curve25519KeyPair.Generate();
        var secret = Concat(
            identityKey.Agree(theirOneTimeKey),
            baseKey.Agree(theirIdentityKey),
            baseKey.Agree(theirOneTimeKey));
        var keys = new OlmSessionKeys(identityKey.PublicKey, baseKey.PublicKey, theirOneTimeKey);
        return OlmSession.CreateOutbound(secret, keys, theirIdentityKey);
    }

    /// <summary>
    /// Creates a session from a received pre-key message and decrypts it. The used
    /// one-time key is removed only after successful decryption.
    /// </summary>
    public (OlmSession Session, byte[] Plaintext) CreateInboundSession(byte[] theirIdentityKey, string preKeyMessage)
    {
        var message = OlmPreKeyMessage.Parse(CryptoPrimitives.FromBase64(preKeyMessage));
        if (!CryptographicOperations.FixedTimeEquals(message.Keys.IdentityKey, theirIdentityKey))
            throw new CryptographicException("Pre-key message was not created with the sender's identity key.");

        var (keyId, oneTimeKey) = FindOneTimeKey(message.Keys.OneTimeKey);
        var ourOneTimeKey = new Curve25519KeyPair(oneTimeKey.PrivateKey);
        var identityKey = new Curve25519KeyPair(IdentityKeyPrivate);
        var secret = Concat(
            ourOneTimeKey.Agree(theirIdentityKey),
            identityKey.Agree(message.Keys.BaseKey),
            ourOneTimeKey.Agree(message.Keys.BaseKey));

        var session = OlmSession.CreateInbound(secret, message.Keys, theirIdentityKey, message.Message);
        var plaintext = session.Decrypt(0, preKeyMessage);
        if (keyId != null)
            OneTimeKeys.Remove(keyId);
        return (session, plaintext);
    }

    private (string? KeyId, OlmOneTimeKey Key) FindOneTimeKey(byte[] publicKey)
    {
        foreach (var pair in OneTimeKeys)
        {
            if (CryptographicOperations.FixedTimeEquals(pair.Value.PublicKey, publicKey))
                return (pair.Key, pair.Value);
        }
        foreach (var fallback in new[] { FallbackKey, PreviousFallbackKey })
        {
            if (fallback != null && CryptographicOperations.FixedTimeEquals(fallback.PublicKey, publicKey))
                return (null, fallback);
        }
        throw new CryptographicException("Pre-key message uses an unknown one-time key.");
    }

    private string NewKeyId()
    {
        var bytes = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, ++NextKeyId);
        return CryptoPrimitives.ToBase64(bytes);
    }

    internal static byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();
}

internal sealed class OlmOneTimeKey
{
    public required byte[] PrivateKey { get; init; }
    public bool Published { get; set; }
    [JsonIgnore]
    public byte[] PublicKey => new Curve25519KeyPair(PrivateKey).PublicKey;
}

/// <summary>Public keys that created a session: the initiator's identity and base key, and the receiver's one-time key.</summary>
internal sealed record OlmSessionKeys(byte[] IdentityKey, byte[] BaseKey, byte[] OneTimeKey)
{
    [JsonIgnore]
    public string SessionId => CryptoPrimitives.ToBase64(SHA256.HashData(OlmAccount.Concat(IdentityKey, BaseKey, OneTimeKey)));

    public bool Matches(OlmSessionKeys other)
        => CryptographicOperations.FixedTimeEquals(IdentityKey, other.IdentityKey) &&
           CryptographicOperations.FixedTimeEquals(BaseKey, other.BaseKey) &&
           CryptographicOperations.FixedTimeEquals(OneTimeKey, other.OneTimeKey);
}

internal sealed class OlmReceiverChain
{
    public required byte[] RatchetKey { get; init; }
    public required byte[] ChainKey { get; set; }
    public uint Index { get; set; }
}

internal sealed class OlmSkippedMessageKey
{
    public required byte[] RatchetKey { get; init; }
    public required uint Index { get; init; }
    public required byte[] MessageKey { get; init; }
}

/// <summary>
/// A double ratchet session with one other device. Public properties hold the persisted state.
/// </summary>
internal sealed class OlmSession
{
    private const int MaxReceiverChains = 5;
    private const int MaxSkippedMessageKeys = 40;
    private const uint MaxMessageGap = 2000;

    public required OlmSessionKeys Keys { get; init; }
    /// <summary>The other device's Curve25519 identity key.</summary>
    public required byte[] TheirIdentityKey { get; init; }
    /// <summary>True for the side that created the session from a one-time key and sends pre-key messages.</summary>
    public required bool IsOutbound { get; init; }
    public required byte[] RootKey { get; set; }
    /// <summary>Private part of our latest ratchet key. Absent until the inbound side first sends.</summary>
    public byte[]? OurRatchetKey { get; set; }
    /// <summary>Absent after receiving on a new chain; a new one is derived on the next send.</summary>
    public byte[]? SenderChainKey { get; set; }
    public uint SenderChainIndex { get; set; }
    public List<OlmReceiverChain> ReceiverChains { get; init; } = [];
    public List<OlmSkippedMessageKey> SkippedMessageKeys { get; init; } = [];
    public bool HasReceivedMessage { get; set; }
    public DateTimeOffset LastUsedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public string SessionId => Keys.SessionId;

    internal static OlmSession CreateOutbound(byte[] sharedSecret, OlmSessionKeys keys, byte[] theirIdentityKey)
    {
        var derived = CryptoPrimitives.Hkdf(sharedSecret, null, "OLM_ROOT", 64);
        return new OlmSession
        {
            Keys = keys,
            TheirIdentityKey = theirIdentityKey,
            IsOutbound = true,
            RootKey = derived[..32],
            OurRatchetKey = RandomNumberGenerator.GetBytes(32),
            SenderChainKey = derived[32..]
        };
    }

    internal static OlmSession CreateInbound(byte[] sharedSecret, OlmSessionKeys keys, byte[] theirIdentityKey, byte[] firstMessage)
    {
        var derived = CryptoPrimitives.Hkdf(sharedSecret, null, "OLM_ROOT", 64);
        var ratchetKey = OlmMessage.Parse(firstMessage).RatchetKey;
        return new OlmSession
        {
            Keys = keys,
            TheirIdentityKey = theirIdentityKey,
            IsOutbound = false,
            RootKey = derived[..32],
            ReceiverChains = [new OlmReceiverChain { RatchetKey = ratchetKey, ChainKey = derived[32..] }],
            HasReceivedMessage = true
        };
    }

    /// <summary>Whether a pre-key message belongs to this session rather than starting a new one.</summary>
    public bool Matches(string preKeyMessage)
    {
        try
        {
            return OlmPreKeyMessage.Parse(CryptoPrimitives.FromBase64(preKeyMessage)).Keys.Matches(Keys);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return false;
        }
    }

    /// <summary>Returns the Matrix message type (0 = pre-key, 1 = normal) and the base64 body.</summary>
    public (int Type, string Body) Encrypt(ReadOnlySpan<byte> plaintext)
    {
        if (SenderChainKey == null)
        {
            var ratchetKey = Curve25519KeyPair.Generate();
            var derived = CryptoPrimitives.Hkdf(ratchetKey.Agree(ReceiverChains[0].RatchetKey), RootKey, "OLM_RATCHET", 64);
            RootKey = derived[..32];
            OurRatchetKey = ratchetKey.PrivateKey;
            SenderChainKey = derived[32..];
            SenderChainIndex = 0;
        }

        var messageKey = CryptoPrimitives.Hmac(SenderChainKey, [0x01]);
        var message = OlmMessage.Write(new Curve25519KeyPair(OurRatchetKey!).PublicKey, SenderChainIndex, messageKey, plaintext);
        SenderChainKey = CryptoPrimitives.Hmac(SenderChainKey, [0x02]);
        SenderChainIndex++;
        LastUsedAt = DateTimeOffset.UtcNow;

        if (IsOutbound && !HasReceivedMessage)
            return (0, CryptoPrimitives.ToBase64(OlmPreKeyMessage.Write(Keys, message)));
        return (1, CryptoPrimitives.ToBase64(message));
    }

    /// <summary>Decrypts a message. State only changes if authentication succeeds.</summary>
    public byte[] Decrypt(int type, string body)
    {
        var bytes = CryptoPrimitives.FromBase64(body);
        if (type == 0)
        {
            var preKey = OlmPreKeyMessage.Parse(bytes);
            if (!preKey.Keys.Matches(Keys))
                throw new CryptographicException("Pre-key message belongs to a different session.");
            bytes = preKey.Message;
        }
        else if (type != 1)
        {
            throw new CryptographicException($"Unknown Olm message type {type}.");
        }

        var message = OlmMessage.Parse(bytes);
        var skipped = SkippedMessageKeys.FirstOrDefault(key => key.Index == message.Index &&
            CryptographicOperations.FixedTimeEquals(key.RatchetKey, message.RatchetKey));
        if (skipped != null)
        {
            var plaintext = OlmMessage.Decrypt(bytes, message, skipped.MessageKey);
            SkippedMessageKeys.Remove(skipped);
            MarkUsed();
            return plaintext;
        }

        var chain = ReceiverChains.FirstOrDefault(c => CryptographicOperations.FixedTimeEquals(c.RatchetKey, message.RatchetKey));
        byte[]? newRootKey = null;
        if (chain == null)
        {
            if (OurRatchetKey == null)
                throw new CryptographicException("Message uses an unknown ratchet key.");
            var derived = CryptoPrimitives.Hkdf(new Curve25519KeyPair(OurRatchetKey).Agree(message.RatchetKey), RootKey, "OLM_RATCHET", 64);
            newRootKey = derived[..32];
            chain = new OlmReceiverChain { RatchetKey = message.RatchetKey, ChainKey = derived[32..] };
        }

        if (message.Index < chain.Index)
            throw new CryptographicException("Message key was already used or discarded.");
        if (message.Index - chain.Index > MaxMessageGap)
            throw new CryptographicException("Too many skipped messages.");

        var chainKey = chain.ChainKey;
        var newSkippedKeys = new List<OlmSkippedMessageKey>();
        for (var index = chain.Index; index < message.Index; index++)
        {
            newSkippedKeys.Add(new OlmSkippedMessageKey
            {
                RatchetKey = chain.RatchetKey,
                Index = index,
                MessageKey = CryptoPrimitives.Hmac(chainKey, [0x01])
            });
            chainKey = CryptoPrimitives.Hmac(chainKey, [0x02]);
        }
        var result = OlmMessage.Decrypt(bytes, message, CryptoPrimitives.Hmac(chainKey, [0x01]));

        // Authenticated: commit the new state.
        if (newRootKey != null)
        {
            RootKey = newRootKey;
            ReceiverChains.Insert(0, chain);
            if (ReceiverChains.Count > MaxReceiverChains)
                ReceiverChains.RemoveAt(ReceiverChains.Count - 1);
            SenderChainKey = null;
        }
        chain.ChainKey = CryptoPrimitives.Hmac(chainKey, [0x02]);
        chain.Index = message.Index + 1;
        SkippedMessageKeys.AddRange(newSkippedKeys);
        if (SkippedMessageKeys.Count > MaxSkippedMessageKeys)
            SkippedMessageKeys.RemoveRange(0, SkippedMessageKeys.Count - MaxSkippedMessageKeys);
        MarkUsed();
        return result;
    }

    private void MarkUsed()
    {
        HasReceivedMessage = true;
        LastUsedAt = DateTimeOffset.UtcNow;
    }
}

/// <summary>A normal Olm message: version byte, payload, truncated MAC.</summary>
internal sealed record OlmMessage(byte[] RatchetKey, uint Index, byte[] Ciphertext)
{
    private const byte Version = 3;
    private const byte RatchetKeyTag = 0x0A;
    private const byte IndexTag = 0x10;
    private const byte CiphertextTag = 0x22;
    private const int MacLength = 8;

    public static byte[] Write(byte[] ratchetKey, uint index, byte[] messageKey, ReadOnlySpan<byte> plaintext)
    {
        var (aesKey, hmacKey, iv) = CryptoPrimitives.DeriveCipherKeys(messageKey, "OLM_KEYS");
        var message = new List<byte> { Version };
        MessagePayload.WriteBytes(message, RatchetKeyTag, ratchetKey);
        MessagePayload.WriteInteger(message, IndexTag, index);
        MessagePayload.WriteBytes(message, CiphertextTag, CryptoPrimitives.AesEncrypt(aesKey, iv, plaintext));
        message.AddRange(CryptoPrimitives.Hmac(hmacKey, message.ToArray()).AsSpan(0, MacLength));
        return message.ToArray();
    }

    public static OlmMessage Parse(byte[] message)
    {
        if (message.Length < 1 + MacLength || message[0] != Version)
            throw new CryptographicException("Unsupported Olm message.");
        var fields = MessagePayload.Read(message.AsSpan(1, message.Length - 1 - MacLength));
        if (fields.GetValueOrDefault(RatchetKeyTag) is not byte[] { Length: 32 } ratchetKey ||
            fields.GetValueOrDefault(IndexTag) is not ulong index || index > uint.MaxValue ||
            fields.GetValueOrDefault(CiphertextTag) is not byte[] ciphertext)
            throw new CryptographicException("Olm message is missing fields.");
        return new OlmMessage(ratchetKey, (uint)index, ciphertext);
    }

    public static byte[] Decrypt(byte[] raw, OlmMessage message, byte[] messageKey)
    {
        var (aesKey, hmacKey, iv) = CryptoPrimitives.DeriveCipherKeys(messageKey, "OLM_KEYS");
        var mac = CryptoPrimitives.Hmac(hmacKey, raw.AsSpan(0, raw.Length - MacLength)).AsSpan(0, MacLength);
        if (!CryptographicOperations.FixedTimeEquals(mac, raw.AsSpan(raw.Length - MacLength)))
            throw new CryptographicException("Olm message has an invalid MAC.");
        return CryptoPrimitives.AesDecrypt(aesKey, iv, message.Ciphertext);
    }
}

/// <summary>A pre-key message: the session's creation keys plus an embedded normal message.</summary>
internal sealed record OlmPreKeyMessage(OlmSessionKeys Keys, byte[] Message)
{
    private const byte Version = 3;
    private const byte OneTimeKeyTag = 0x0A;
    private const byte BaseKeyTag = 0x12;
    private const byte IdentityKeyTag = 0x1A;
    private const byte MessageTag = 0x22;

    public static byte[] Write(OlmSessionKeys keys, byte[] message)
    {
        var output = new List<byte> { Version };
        MessagePayload.WriteBytes(output, OneTimeKeyTag, keys.OneTimeKey);
        MessagePayload.WriteBytes(output, BaseKeyTag, keys.BaseKey);
        MessagePayload.WriteBytes(output, IdentityKeyTag, keys.IdentityKey);
        MessagePayload.WriteBytes(output, MessageTag, message);
        return output.ToArray();
    }

    public static OlmPreKeyMessage Parse(byte[] message)
    {
        if (message.Length < 1 || message[0] != Version)
            throw new CryptographicException("Unsupported Olm pre-key message.");
        var fields = MessagePayload.Read(message.AsSpan(1));
        if (fields.GetValueOrDefault(OneTimeKeyTag) is not byte[] { Length: 32 } oneTimeKey ||
            fields.GetValueOrDefault(BaseKeyTag) is not byte[] { Length: 32 } baseKey ||
            fields.GetValueOrDefault(IdentityKeyTag) is not byte[] { Length: 32 } identityKey ||
            fields.GetValueOrDefault(MessageTag) is not byte[] embedded)
            throw new CryptographicException("Olm pre-key message is missing fields.");
        return new OlmPreKeyMessage(new OlmSessionKeys(identityKey, baseKey, oneTimeKey), embedded);
    }
}
