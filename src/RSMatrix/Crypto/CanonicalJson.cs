using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RSMatrix.Crypto;

/// <summary>Canonical JSON and JSON signing, as defined in the spec appendices.</summary>
internal static class CanonicalJson
{
    public static byte[] Encode(JsonElement value)
    {
        var builder = new StringBuilder();
        Write(builder, value);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>Signs an object and adds the signature to its <c>signatures</c> property.</summary>
    public static void Sign(JsonObject value, string userId, string keyId, Func<byte[], byte[]> sign)
    {
        var signature = CryptoPrimitives.ToBase64(sign(EncodeForSigning(value)));
        if (value["signatures"] is not JsonObject signatures)
            value["signatures"] = signatures = [];
        if (signatures[userId] is not JsonObject userSignatures)
            signatures[userId] = userSignatures = [];
        userSignatures[keyId] = signature;
    }

    /// <summary>Checks the signature of <paramref name="userId"/>'s Ed25519 key <paramref name="keyId"/>.</summary>
    public static bool Verify(JsonElement value, string userId, string keyId, byte[] publicKey)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("signatures", out var signatures) || signatures.ValueKind != JsonValueKind.Object ||
            !signatures.TryGetProperty(userId, out var userSignatures) || userSignatures.ValueKind != JsonValueKind.Object ||
            !userSignatures.TryGetProperty(keyId, out var signature) || signature.ValueKind != JsonValueKind.String)
            return false;
        try
        {
            var node = JsonNode.Parse(value.GetRawText())!.AsObject();
            return Ed25519KeyPair.Verify(publicKey, EncodeForSigning(node), CryptoPrimitives.FromBase64(signature.GetString()!));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] EncodeForSigning(JsonObject value)
    {
        var copy = value.DeepClone().AsObject();
        copy.Remove("signatures");
        copy.Remove("unsigned");
        return Encode(JsonSerializer.SerializeToElement(copy));
    }

    private static void Write(StringBuilder output, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                output.Append('{');
                // Sorting UTF-8 bytes gives the same order as sorting by code point.
                var properties = value.EnumerateObject()
                    .OrderBy(p => Encoding.UTF8.GetBytes(p.Name), Utf8Comparer.Instance);
                var first = true;
                foreach (var property in properties)
                {
                    if (!first)
                        output.Append(',');
                    first = false;
                    WriteString(output, property.Name);
                    output.Append(':');
                    Write(output, property.Value);
                }
                output.Append('}');
                break;
            case JsonValueKind.Array:
                output.Append('[');
                var firstItem = true;
                foreach (var item in value.EnumerateArray())
                {
                    if (!firstItem)
                        output.Append(',');
                    firstItem = false;
                    Write(output, item);
                }
                output.Append(']');
                break;
            case JsonValueKind.String:
                WriteString(output, value.GetString()!);
                break;
            case JsonValueKind.Number:
                // Integers written with an exponent, such as 1e10, are still integers.
                if (value.TryGetInt64(out var number))
                    output.Append(number);
                else if (value.TryGetDouble(out var real) && real == Math.Floor(real) && Math.Abs(real) < 9007199254740992)
                    output.Append((long)real);
                else
                    throw new JsonException("Canonical JSON only allows integers.");
                break;
            default:
                output.Append(value.GetRawText()); // true, false, null
                break;
        }
    }

    private static void WriteString(StringBuilder output, string value)
    {
        output.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': output.Append("\\\""); break;
                case '\\': output.Append("\\\\"); break;
                case '\b': output.Append("\\b"); break;
                case '\t': output.Append("\\t"); break;
                case '\n': output.Append("\\n"); break;
                case '\f': output.Append("\\f"); break;
                case '\r': output.Append("\\r"); break;
                case < ' ': output.Append($"\\u{(int)c:x4}"); break;
                default: output.Append(c); break;
            }
        }
        output.Append('"');
    }

    private sealed class Utf8Comparer : IComparer<byte[]>
    {
        public static readonly Utf8Comparer Instance = new();
        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }
}
