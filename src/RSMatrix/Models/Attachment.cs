using RSMatrix.Http;

namespace RSMatrix.Models;

/// <summary>
/// The file of an image, file, audio or video message. The sender states its name, type, size and dimensions; any of
/// them can be missing or wrong.
/// </summary>
public sealed class Attachment
{
    /// <summary>
    /// The file name the sender gave. It comes from another user: strip directories, for example with
    /// <see cref="Path.GetFileName(string)"/>, before using it as a path.
    /// </summary>
    public string FileName { get; }

    public string? MimeType { get; }

    /// <summary>The size in bytes, to show before downloading.</summary>
    public long? Size { get; }

    /// <summary>The intended display width in pixels, for images and videos.</summary>
    public int? Width { get; }

    /// <summary>The intended display height in pixels, for images and videos.</summary>
    public int? Height { get; }

    /// <summary>The length of audio and video.</summary>
    public TimeSpan? Duration { get; }

    /// <summary>The file's mxc:// URI. The same file keeps the same URI, so it works as a cache key.</summary>
    public string MxcUri => _file.Uri.ToString();

    private readonly MatrixClient _client;
    private readonly MessageKind _kind;
    private readonly MediaSource _file;
    private readonly MediaSource? _thumbnail;

    private Attachment(MatrixClient client, MessageKind kind, MediaSource file, MediaSource? thumbnail, string fileName, MediaInfoContent? info)
    {
        _client = client;
        _kind = kind;
        _file = file;
        _thumbnail = thumbnail;
        FileName = fileName;
        MimeType = info?.MimeType;
        Size = info?.Size is >= 0 and var size ? size : null;
        Width = ToPixels(info?.Width);
        Height = ToPixels(info?.Height);
        Duration = info?.Duration is >= 0 and var duration ? TimeSpan.FromMilliseconds(duration) : null;
    }

    /// <summary>Null if the message has no file the client can use.</summary>
    internal static Attachment? FromContent(MatrixClient client, MessageKind kind, MessageBodyContent content)
    {
        var file = content.File != null ? MediaSource.FromEncryptedFile(content.File) : MediaSource.FromUrl(content.Url);
        if (file == null)
            return null;
        var info = content.Info;
        var thumbnail = info?.ThumbnailFile != null ? MediaSource.FromEncryptedFile(info.ThumbnailFile) : MediaSource.FromUrl(info?.ThumbnailUrl);
        var fileName = string.IsNullOrEmpty(content.FileName) ? content.Body : content.FileName;
        return new Attachment(client, kind, file, thumbnail, fileName, info);
    }

    /// <summary>
    /// A smaller version of an image or video for display, at least the given size unless the original is smaller.
    /// Unencrypted images are scaled by the server. Other files only have a thumbnail when the sender attached one, and
    /// the server can't scale encrypted ones, so the result can be smaller or larger than asked. Null if there is no
    /// thumbnail; this never downloads the whole file, see <see cref="Size"/> to decide about that.
    /// </summary>
    /// <param name="width">The width in pixels the thumbnail is shown at. Servers keep 320×240, 640×480 and 800×600 ready.</param>
    /// <param name="height">The height in pixels the thumbnail is shown at.</param>
    /// <exception cref="InvalidDataException">An encrypted thumbnail doesn't match its hash.</exception>
    public async Task<byte[]?> DownloadThumbnailAsync(int width, int height, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var http = _client.HttpClientParameters;
        if (_file.Encryption == null && _kind == MessageKind.Image &&
            await MediaHelper.TryDownloadThumbnailAsync(http, _file.Uri, width, height, crop: false, cancellationToken).ConfigureAwait(false) is { } scaled)
        {
            return scaled;
        }
        if (_thumbnail == null)
            return null;
        if (_thumbnail.Encryption == null)
            return await MediaHelper.TryDownloadThumbnailAsync(http, _thumbnail.Uri, width, height, crop: false, cancellationToken).ConfigureAwait(false);
        try
        {
            return (await MediaHelper.DownloadAsync(http, _thumbnail, cancellationToken).ConfigureAwait(false)).Content;
        }
        catch (MatrixResponseException ex) when (MediaHelper.IsNotFound(ex))
        {
            return null;
        }
    }

    /// <summary>The whole file. An encrypted file is decrypted and checked against the hash in its message.</summary>
    /// <exception cref="InvalidDataException">The encrypted file doesn't match its hash.</exception>
    /// <exception cref="MatrixResponseException">The server doesn't have the file anymore, or refused it.</exception>
    public async Task<byte[]> DownloadAsync(CancellationToken cancellationToken = default)
        => (await MediaHelper.DownloadAsync(_client.HttpClientParameters, _file, cancellationToken).ConfigureAwait(false)).Content;

    /// <summary>
    /// Writes the whole file into <paramref name="destination"/>, for large files that shouldn't be held in memory.
    /// An encrypted file is decrypted on the way. Its hash can only be checked at the end: on a mismatch this throws
    /// after the data was written, and the caller should delete what it wrote.
    /// </summary>
    /// <exception cref="InvalidDataException">The encrypted file doesn't match its hash.</exception>
    /// <exception cref="MatrixResponseException">The server doesn't have the file anymore, or refused it.</exception>
    public Task DownloadAsync(Stream destination, CancellationToken cancellationToken = default)
        => MediaHelper.DownloadAsync(_client.HttpClientParameters, _file, destination, cancellationToken);

    public override string ToString() => FileName;

    private static int? ToPixels(long? value) => value is > 0 and <= int.MaxValue ? (int)value.Value : null;
}

/// <summary>A file downloaded by its mxc:// URI, with what the server said about it.</summary>
/// <param name="ContentType">The MIME type the server gave.</param>
/// <param name="FileName">The name the uploader gave, if any. It comes from another user: strip directories before using it as a path.</param>
public sealed record MediaFile(byte[] Content, string? ContentType, string? FileName);
