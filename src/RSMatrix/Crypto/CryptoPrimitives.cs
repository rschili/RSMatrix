using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace RSMatrix.Crypto;

/// <summary>A Curve25519 key pair used for Diffie-Hellman agreements.</summary>
internal sealed class Curve25519KeyPair
{
    public byte[] PrivateKey { get; }
    public byte[] PublicKey { get; }

    public Curve25519KeyPair(byte[] privateKey)
    {
        if (privateKey.Length != 32)
            throw new ArgumentException("Curve25519 private keys are 32 bytes.", nameof(privateKey));
        PrivateKey = privateKey;
        PublicKey = new X25519PrivateKeyParameters(privateKey).GeneratePublicKey().GetEncoded();
    }

    public static Curve25519KeyPair Generate() => new(RandomNumberGenerator.GetBytes(32));

    /// <summary>Throws <see cref="CryptographicException"/> for keys that produce no shared secret.</summary>
    public byte[] Agree(byte[] theirPublicKey)
    {
        if (theirPublicKey.Length != 32)
            throw new CryptographicException("Curve25519 public keys are 32 bytes.");
        var secret = new byte[32];
        try
        {
            new X25519PrivateKeyParameters(PrivateKey)
                .GenerateSecret(new X25519PublicKeyParameters(theirPublicKey), secret, 0);
        }
        catch (InvalidOperationException ex)
        {
            throw new CryptographicException("Curve25519 key agreement failed.", ex);
        }
        return secret;
    }
}

/// <summary>An Ed25519 key pair used for signatures.</summary>
internal sealed class Ed25519KeyPair
{
    public byte[] Seed { get; }
    public byte[] PublicKey { get; }

    public Ed25519KeyPair(byte[] seed)
    {
        if (seed.Length != 32)
            throw new ArgumentException("Ed25519 seeds are 32 bytes.", nameof(seed));
        Seed = seed;
        PublicKey = new Ed25519PrivateKeyParameters(seed).GeneratePublicKey().GetEncoded();
    }

    public static Ed25519KeyPair Generate() => new(RandomNumberGenerator.GetBytes(32));

    public byte[] Sign(ReadOnlySpan<byte> data)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(Seed));
        signer.BlockUpdate(data);
        return signer.GenerateSignature();
    }

    public static bool Verify(byte[] publicKey, ReadOnlySpan<byte> data, byte[] signature)
    {
        if (publicKey.Length != 32 || signature.Length != 64)
            return false;
        var signer = new Ed25519Signer();
        signer.Init(false, new Ed25519PublicKeyParameters(publicKey));
        signer.BlockUpdate(data);
        return signer.VerifySignature(signature);
    }
}

internal static class CryptoPrimitives
{
    public static byte[] Hkdf(byte[] inputKeyMaterial, byte[]? salt, string info, int length)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, inputKeyMaterial, length, salt,
            System.Text.Encoding.ASCII.GetBytes(info));

    public static byte[] Hmac(byte[] key, ReadOnlySpan<byte> data) => HMACSHA256.HashData(key, data);

    /// <summary>The authenticated encryption shared by Olm and Megolm: AES-256-CBC with keys from HKDF.</summary>
    public static (byte[] AesKey, byte[] HmacKey, byte[] Iv) DeriveCipherKeys(byte[] secret, string info)
    {
        var keys = Hkdf(secret, null, info, 80);
        return (keys[..32], keys[32..64], keys[64..80]);
    }

    public static byte[] AesEncrypt(byte[] key, byte[] iv, ReadOnlySpan<byte> plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.EncryptCbc(plaintext, iv, PaddingMode.PKCS7);
    }

    public static byte[] AesDecrypt(byte[] key, byte[] iv, ReadOnlySpan<byte> ciphertext)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptCbc(ciphertext, iv, PaddingMode.PKCS7);
    }

    /// <summary>AES-256-CTR with a 128-bit big-endian counter starting at <paramref name="iv"/>. Encrypts and decrypts.</summary>
    public static byte[] AesCtr(byte[] key, byte[] iv, ReadOnlySpan<byte> input)
    {
        using var cipher = new AesCtrCipher(key, iv);
        var output = new byte[input.Length];
        cipher.Transform(input, output);
        return output;
    }

    /// <summary>Matrix encodes binary values as unpadded standard base64.</summary>
    public static string ToBase64(ReadOnlySpan<byte> data) => Convert.ToBase64String(data).TrimEnd('=');

    private const string Base64Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

    /// <summary>
    /// Accepts padded and unpadded input, and ignores unused bits in the last character like other
    /// Matrix implementations do. Throws <see cref="FormatException"/> on invalid data.
    /// </summary>
    public static byte[] FromBase64(string value)
    {
        var trimmed = value.TrimEnd('=');
        var unusedBits = (trimmed.Length % 4) switch { 2 => 4, 3 => 2, _ => 0 };
        if (unusedBits > 0 && Base64Alphabet.IndexOf(trimmed[^1]) is var index and >= 0)
            trimmed = trimmed[..^1] + Base64Alphabet[index & ~((1 << unusedBits) - 1)];
        return Convert.FromBase64String(trimmed + new string('=', (4 - trimmed.Length % 4) % 4));
    }
}

/// <summary>
/// AES-256-CTR with a 128-bit big-endian counter starting at the IV, for data that arrives in pieces.
/// Encrypts and decrypts; each call continues where the previous one stopped.
/// </summary>
internal sealed class AesCtrCipher : IDisposable
{
    private const int BlockSize = 16;
    private const int BlocksPerBatch = 256;
    private readonly Aes _aes;
    private readonly byte[] _counter;
    private readonly byte[] _counterBlocks = new byte[BlockSize * BlocksPerBatch];
    private readonly byte[] _keyStream = new byte[BlockSize * BlocksPerBatch];
    private int _keyStreamPosition = BlockSize * BlocksPerBatch;

    public AesCtrCipher(byte[] key, byte[] iv)
    {
        if (iv.Length != BlockSize)
            throw new ArgumentException("The IV must be 16 bytes.", nameof(iv));
        _aes = Aes.Create();
        _aes.Key = key;
        _counter = (byte[])iv.Clone();
    }

    public void Transform(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (output.Length < input.Length)
            throw new ArgumentException("The output is shorter than the input.", nameof(output));
        for (var i = 0; i < input.Length; i++)
        {
            if (_keyStreamPosition == _keyStream.Length)
                FillKeyStream();
            output[i] = (byte)(input[i] ^ _keyStream[_keyStreamPosition++]);
        }
    }

    /// <summary>Encrypts a batch of counter blocks at once; one ECB call per block would be slow for large files.</summary>
    private void FillKeyStream()
    {
        for (var block = 0; block < BlocksPerBatch; block++)
        {
            _counter.CopyTo(_counterBlocks, block * BlockSize);
            for (var i = BlockSize - 1; i >= 0; i--)
            {
                if (++_counter[i] != 0)
                    break;
            }
        }
        _aes.EncryptEcb(_counterBlocks, _keyStream, PaddingMode.None);
        _keyStreamPosition = 0;
    }

    public void Dispose() => _aes.Dispose();
}

/// <summary>
/// The protobuf-like key/value payload used inside Olm and Megolm messages.
/// Values are either variable length integers or length-prefixed byte strings.
/// </summary>
internal static class MessagePayload
{
    public static void WriteInteger(List<byte> output, byte tag, ulong value)
    {
        output.Add(tag);
        WriteVarint(output, value);
    }

    public static void WriteBytes(List<byte> output, byte tag, byte[] value)
    {
        output.Add(tag);
        WriteVarint(output, (ulong)value.Length);
        output.AddRange(value);
    }

    /// <summary>Reads all fields. Unknown tags are kept, so callers can ignore them.</summary>
    public static Dictionary<ulong, object> Read(ReadOnlySpan<byte> payload)
    {
        var fields = new Dictionary<ulong, object>();
        var position = 0;
        while (position < payload.Length)
        {
            var tag = ReadVarint(payload, ref position);
            switch (tag & 7)
            {
                case 0:
                    fields[tag] = ReadVarint(payload, ref position);
                    break;
                case 2:
                    var length = ReadVarint(payload, ref position);
                    if (length > (ulong)(payload.Length - position))
                        throw new CryptographicException("Message field exceeds message length.");
                    fields[tag] = payload.Slice(position, (int)length).ToArray();
                    position += (int)length;
                    break;
                default:
                    throw new CryptographicException($"Unsupported message field type in tag {tag}.");
            }
        }
        return fields;
    }

    private static void WriteVarint(List<byte> output, ulong value)
    {
        while (value >= 0x80)
        {
            output.Add((byte)(value | 0x80));
            value >>= 7;
        }
        output.Add((byte)value);
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int position)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (position >= data.Length)
                throw new CryptographicException("Truncated integer in message.");
            var b = data[position++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return value;
        }
        throw new CryptographicException("Integer in message is too long.");
    }
}
