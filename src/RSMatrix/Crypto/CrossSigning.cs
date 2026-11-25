using System.Text.Json;
using System.Text.Json.Nodes;

namespace RSMatrix.Crypto;

/// <summary>A user's published cross-signing keys, after checking that the master key signed the self-signing key.</summary>
/// <param name="SelfSigningKey">Null when the user has none, or its signature does not check out.</param>
internal sealed record UserIdentity(string MasterKey, string? SelfSigningKey);

/// <summary>The cross-signing key format and the checks for "is this device cross-signed by its owner".</summary>
internal static class CrossSigning
{
    public const string Master = "master";
    public const string SelfSigning = "self_signing";
    public const string UserSigning = "user_signing";

    /// <summary>Secret names in secret storage. The values are the unpadded base64 private keys.</summary>
    public static string SecretName(string usage) => $"m.cross_signing.{usage}";

    public static JsonObject CreateKey(string userId, string usage, Ed25519KeyPair key, Ed25519KeyPair? signedBy)
    {
        var publicKey = CryptoPrimitives.ToBase64(key.PublicKey);
        var value = new JsonObject
        {
            ["user_id"] = userId,
            ["usage"] = new JsonArray(usage),
            ["keys"] = new JsonObject { [$"ed25519:{publicKey}"] = publicKey }
        };
        if (signedBy != null)
            CanonicalJson.Sign(value, userId, $"ed25519:{CryptoPrimitives.ToBase64(signedBy.PublicKey)}", data => signedBy.Sign(data));
        return value;
    }

    /// <summary>Reads a user's identity from a <c>/keys/query</c> response, or null if the user has no valid master key.</summary>
    public static UserIdentity? ReadIdentity(JsonElement queryResponse, string userId)
    {
        if (!queryResponse.TryGetProperty("master_keys", out var masterKeys) || masterKeys.ValueKind != JsonValueKind.Object ||
            !masterKeys.TryGetProperty(userId, out var masterKey) || ReadPublicKey(masterKey, userId, Master) is not { } master)
            return null;

        string? selfSigning = null;
        if (queryResponse.TryGetProperty("self_signing_keys", out var selfSigningKeys) && selfSigningKeys.ValueKind == JsonValueKind.Object &&
            selfSigningKeys.TryGetProperty(userId, out var selfSigningKey) &&
            ReadPublicKey(selfSigningKey, userId, SelfSigning) is { } candidate &&
            IsSignedBy(selfSigningKey, userId, master))
        {
            selfSigning = candidate;
        }
        return new UserIdentity(master, selfSigning);
    }

    /// <summary>Whether the user's self-signing key signed these device keys.</summary>
    public static bool IsDeviceCrossSigned(JsonElement deviceKeys, string userId, string deviceId, UserIdentity? identity)
    {
        // Device IDs and cross-signing keys share a namespace; a device named like a key is an attack.
        if (identity?.SelfSigningKey is not { } selfSigningKey || deviceId == identity.MasterKey || deviceId == selfSigningKey)
            return false;
        return IsSignedBy(deviceKeys, userId, selfSigningKey);
    }

    public static bool IsSignedBy(JsonElement value, string userId, string publicKey)
    {
        try
        {
            return CanonicalJson.Verify(value, userId, $"ed25519:{publicKey}", CryptoPrimitives.FromBase64(publicKey));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>The single Ed25519 key of a cross-signing key object, if it belongs to the user and has the expected usage.</summary>
    private static string? ReadPublicKey(JsonElement value, string userId, string usage)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("user_id", out var user) || user.ValueKind != JsonValueKind.String || user.GetString() != userId ||
            !value.TryGetProperty("usage", out var usages) || usages.ValueKind != JsonValueKind.Array ||
            !usages.EnumerateArray().Any(u => u.ValueKind == JsonValueKind.String && u.GetString() == usage) ||
            !value.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Object)
            return null;

        var entries = keys.EnumerateObject().ToList();
        if (entries.Count != 1 || entries[0].Value.ValueKind != JsonValueKind.String)
            return null;
        var publicKey = entries[0].Value.GetString()!;
        if (entries[0].Name != $"ed25519:{publicKey}")
            return null;
        try
        {
            return CryptoPrimitives.FromBase64(publicKey).Length == 32 ? publicKey : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
