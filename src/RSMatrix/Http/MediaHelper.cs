using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using RSMatrix.Crypto;
using RSMatrix.Models;

namespace RSMatrix.Http;

/// <summary>An mxc:// URI: the server that holds a file and the file's ID there.</summary>
internal readonly partial record struct MatrixContentUri(string ServerName, string MediaId)
{
    public static bool TryParse([NotNullWhen(true)] string? value, out MatrixContentUri uri)
    {
        uri = default;
        if (value == null || !value.StartsWith("mxc://", StringComparison.Ordinal))
            return false;
        var rest = value.AsSpan("mxc://".Length);
        var slash = rest.IndexOf('/');
        if (slash <= 0)
            return false;
        var serverName = rest[..slash].ToString();
        var mediaId = rest[(slash + 1)..].ToString();
        if (!ServerNamePattern().IsMatch(serverName) || !MediaIdPattern().IsMatch(mediaId))
            return false;
        uri = new MatrixContentUri(serverName, mediaId);
        return true;
    }

    public string DownloadPath => $"/_matrix/client/v1/media/download/{Uri.EscapeDataString(ServerName)}/{Uri.EscapeDataString(MediaId)}";

    public string ThumbnailPath(int width, int height, bool crop) => HttpParameterHelper.AppendParameters(
        $"/_matrix/client/v1/media/thumbnail/{Uri.EscapeDataString(ServerName)}/{Uri.EscapeDataString(MediaId)}",
        [KeyValuePair.Create("width", width.ToString()), KeyValuePair.Create("height", height.ToString()),
         KeyValuePair.Create("method", crop ? "crop" : "scale")]);

    public override string ToString() => $"mxc://{ServerName}/{MediaId}";

    // A host name, IPv4 address or [IPv6] address with an optional port.
    [GeneratedRegex(@"^[A-Za-z0-9.\-:\[\]]+$")]
    private static partial Regex ServerNamePattern();

    // The spec's media ID grammar. It keeps out "..", which would let a URI point at another path on the server.
    [GeneratedRegex(@"^[A-Za-z0-9_\-]+$")]
    private static partial Regex MediaIdPattern();
}

/// <summary>The AES-CTR key and IV of an encrypted file, and the SHA-256 hash of its encrypted bytes.</summary>
internal sealed record FileEncryption(byte[] Key, byte[] Iv, byte[] Sha256);

/// <summary>A file in the content repository, and how to decrypt it if it is encrypted.</summary>
internal sealed record MediaSource(MatrixContentUri Uri, FileEncryption? Encryption)
{
    public static MediaSource? FromUrl(string? url)
        => MatrixContentUri.TryParse(url, out var uri) ? new MediaSource(uri, null) : null;

    /// <summary>Null if the file is unusable: an unknown version or algorithm, or a key, IV or hash of the wrong size.</summary>
    public static MediaSource? FromEncryptedFile(EncryptedFileContent? file)
    {
        if (file is not { Version: "v2", Key: { KeyType: "oct", Algorithm: "A256CTR", Key: { } key }, Iv: { } iv } ||
            file.Hashes?.GetValueOrDefault("sha256") is not { } sha256 || !MatrixContentUri.TryParse(file.Url, out var uri))
        {
            return null;
        }
        try
        {
            // The key is URL-safe base64; some clients send it in the standard alphabet.
            var encryption = new FileEncryption(CryptoPrimitives.FromBase64(key.Replace('-', '+').Replace('_', '/')),
                CryptoPrimitives.FromBase64(iv), CryptoPrimitives.FromBase64(sha256));
            return encryption is { Key.Length: 32, Iv.Length: 16, Sha256.Length: 32 } ? new MediaSource(uri, encryption) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>What the server's response says about a downloaded file.</summary>
internal sealed record MediaResponseInfo(string? ContentType, string? FileName);

internal static class MediaHelper
{
    /// <summary>
    /// Writes the file into <paramref name="destination"/>, decrypted if it is encrypted. The hash of an encrypted
    /// file can only be checked at the end, so a mismatch throws after the data was written.
    /// </summary>
    /// <exception cref="InvalidDataException">The encrypted file doesn't match its hash.</exception>
    public static Task<MediaResponseInfo> DownloadAsync(HttpClientParameters parameters, MediaSource source, Stream destination,
        CancellationToken cancellationToken)
        => CopyAsync(parameters, source.Uri.DownloadPath, source.Encryption, destination, cancellationToken);

    /// <summary>The whole file in memory. An encrypted file is only returned once its hash matched.</summary>
    public static async Task<(byte[] Content, MediaResponseInfo Info)> DownloadAsync(HttpClientParameters parameters, MediaSource source,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var info = await DownloadAsync(parameters, source, buffer, cancellationToken).ConfigureAwait(false);
        return (buffer.ToArray(), info);
    }

    /// <summary>A thumbnail the server made, or null if it has none for this file.</summary>
    public static async Task<byte[]?> TryDownloadThumbnailAsync(HttpClientParameters parameters, MatrixContentUri uri, int width, int height, bool crop,
        CancellationToken cancellationToken)
    {
        try
        {
            using var buffer = new MemoryStream();
            await CopyAsync(parameters, uri.ThumbnailPath(width, height, crop), null, buffer, cancellationToken).ConfigureAwait(false);
            return buffer.ToArray();
        }
        catch (MatrixResponseException ex) when (IsThumbnailUnavailable(ex))
        {
            return null;
        }
    }

    public static bool IsNotFound(MatrixResponseException ex) => ex.ErrorCode == "M_NOT_FOUND" || ex.StatusCode == HttpStatusCode.NotFound;

    /// <summary>
    /// Servers answer like this when they can't make a thumbnail: the file is missing or too large, or its type
    /// can't be thumbnailed.
    /// </summary>
    private static bool IsThumbnailUnavailable(MatrixResponseException ex)
        => IsNotFound(ex) || ex.ErrorCode == "M_TOO_LARGE"
            || ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnsupportedMediaType;

    private static async Task<MediaResponseInfo> CopyAsync(HttpClientParameters parameters, string path, FileEncryption? encryption,
        Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var linked = HttpClientHelper.Link(parameters, cancellationToken);
        cancellationToken = linked?.Token ?? parameters.CancellationToken;
        using var response = await HttpClientHelper.GetStreamingAsync(parameters, path, cancellationToken).ConfigureAwait(false);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        if (encryption == null)
            await body.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        else
            await DecryptAsync(body, encryption, destination, cancellationToken).ConfigureAwait(false);

        var disposition = response.Content.Headers.ContentDisposition;
        var fileName = (disposition?.FileNameStar ?? disposition?.FileName)?.Trim('"');
        return new MediaResponseInfo(response.Content.Headers.ContentType?.MediaType, string.IsNullOrEmpty(fileName) ? null : fileName);
    }

    private static async Task DecryptAsync(Stream encrypted, FileEncryption encryption, Stream destination, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var cipher = new AesCtrCipher(encryption.Key, encryption.Iv);
        var input = new byte[81920];
        var output = new byte[input.Length];
        int read;
        while ((read = await encrypted.ReadAsync(input, cancellationToken).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(input, 0, read);
            cipher.Transform(input.AsSpan(0, read), output);
            await destination.WriteAsync(output.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), encryption.Sha256))
            throw new InvalidDataException("The downloaded file doesn't match the hash in its message. The server may have changed it.");
    }
}
