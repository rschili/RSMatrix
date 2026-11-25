using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RSMatrix.Crypto;

/// <summary>
/// Secret storage on the server (account data encrypted with m.secret_storage.v1.aes-hmac-sha2)
/// and the recovery key format users see.
/// </summary>
internal static class SecretStorage
{
    public const string Algorithm = "m.secret_storage.v1.aes-hmac-sha2";
    public const string DefaultKeyType = "m.secret_storage.default_key";
    public static string KeyType(string keyId) => $"m.secret_storage.key.{keyId}";

    private const string Base58Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
    private static readonly byte[] RecoveryKeyPrefix = [0x8B, 0x01];

    public static string CreateRecoveryKey() => EncodeRecoveryKey(RandomNumberGenerator.GetBytes(32));

    /// <summary>Formats a 32-byte key as described in "Cryptographic key representation".</summary>
    public static string EncodeRecoveryKey(byte[] key)
    {
        var bytes = new byte[RecoveryKeyPrefix.Length + key.Length + 1];
        RecoveryKeyPrefix.CopyTo(bytes, 0);
        key.CopyTo(bytes, RecoveryKeyPrefix.Length);
        bytes[^1] = bytes[..^1].Aggregate((byte)0, (parity, b) => (byte)(parity ^ b));

        var value = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
        var encoded = new StringBuilder();
        while (value > 0)
        {
            value = BigInteger.DivRem(value, 58, out var remainder);
            encoded.Insert(0, Base58Alphabet[(int)remainder]);
        }
        // The prefix byte 0x8B is never zero, so there are no leading zero bytes to encode as '1'.
        return string.Join(' ', encoded.ToString().Chunk(4).Select(chunk => new string(chunk)));
    }

    /// <summary>Reads a recovery key, ignoring whitespace. Throws <see cref="FormatException"/> if it is invalid.</summary>
    public static byte[] DecodeRecoveryKey(string recoveryKey)
    {
        BigInteger value = 0;
        foreach (var c in recoveryKey.Where(c => !char.IsWhiteSpace(c)))
        {
            var digit = Base58Alphabet.IndexOf(c);
            if (digit < 0)
                throw new FormatException("The recovery key contains characters that are not part of a recovery key.");
            value = value * 58 + digit;
        }
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bytes.Length != RecoveryKeyPrefix.Length + 32 + 1 || !bytes.AsSpan(0, 2).SequenceEqual(RecoveryKeyPrefix))
            throw new FormatException("The recovery key has the wrong length or format.");
        if (bytes.Aggregate((byte)0, (parity, b) => (byte)(parity ^ b)) != 0)
            throw new FormatException("The recovery key is mistyped: its check digit does not match.");
        return bytes[2..^1];
    }

    /// <summary>The account data describing a new key, with the check values that let clients test a key.</summary>
    public static JsonObject CreateKeyDescription(byte[] key)
    {
        var (iv, mac) = EncryptZeros(key, CreateIv());
        return new JsonObject
        {
            ["algorithm"] = Algorithm,
            ["iv"] = CryptoPrimitives.ToBase64(iv),
            ["mac"] = CryptoPrimitives.ToBase64(mac)
        };
    }

    /// <summary>Whether the key fits the description. Descriptions without check values accept any key, as the spec requires.</summary>
    public static bool MatchesKeyDescription(byte[] key, JsonElement description)
    {
        if (GetString(description, "algorithm") != Algorithm)
            return false;
        if (GetString(description, "iv") is not { } iv || GetString(description, "mac") is not { } mac)
            return true;
        try
        {
            var (_, expected) = EncryptZeros(key, CryptoPrimitives.FromBase64(iv));
            return CryptographicOperations.FixedTimeEquals(expected, CryptoPrimitives.FromBase64(mac));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static JsonObject Encrypt(byte[] key, string secretName, string secret)
    {
        var (aesKey, macKey) = DeriveKeys(key, secretName);
        var iv = CreateIv();
        var ciphertext = CryptoPrimitives.AesCtr(aesKey, iv, Encoding.UTF8.GetBytes(secret));
        return new JsonObject
        {
            ["iv"] = CryptoPrimitives.ToBase64(iv),
            ["ciphertext"] = CryptoPrimitives.ToBase64(ciphertext),
            ["mac"] = CryptoPrimitives.ToBase64(CryptoPrimitives.Hmac(macKey, ciphertext))
        };
    }

    /// <summary>Throws <see cref="CryptographicException"/> if the data was not encrypted with this key and name.</summary>
    public static string Decrypt(byte[] key, string secretName, JsonElement encrypted)
    {
        if (GetString(encrypted, "iv") is not { } iv || GetString(encrypted, "ciphertext") is not { } ciphertext ||
            GetString(encrypted, "mac") is not { } mac)
            throw new CryptographicException($"The stored secret {secretName} is incomplete.");
        var (aesKey, macKey) = DeriveKeys(key, secretName);
        try
        {
            var data = CryptoPrimitives.FromBase64(ciphertext);
            if (!CryptographicOperations.FixedTimeEquals(CryptoPrimitives.Hmac(macKey, data), CryptoPrimitives.FromBase64(mac)))
                throw new CryptographicException($"The stored secret {secretName} was not encrypted with this key.");
            return Encoding.UTF8.GetString(CryptoPrimitives.AesCtr(aesKey, CryptoPrimitives.FromBase64(iv), data));
        }
        catch (FormatException ex)
        {
            throw new CryptographicException($"The stored secret {secretName} is not valid base64.", ex);
        }
    }

    private static (byte[] Iv, byte[] Mac) EncryptZeros(byte[] key, byte[] iv)
    {
        var (aesKey, macKey) = DeriveKeys(key, "");
        var ciphertext = CryptoPrimitives.AesCtr(aesKey, iv, new byte[32]);
        return (iv, CryptoPrimitives.Hmac(macKey, ciphertext));
    }

    private static (byte[] AesKey, byte[] MacKey) DeriveKeys(byte[] key, string name)
    {
        var keys = CryptoPrimitives.Hkdf(key, new byte[32], name, 64);
        return (keys[..32], keys[32..]);
    }

    /// <summary>Random, with bit 63 cleared so AES-CTR implementations with a 64-bit counter agree.</summary>
    private static byte[] CreateIv()
    {
        var iv = RandomNumberGenerator.GetBytes(16);
        iv[8] &= 0x7F;
        return iv;
    }

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
           value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
