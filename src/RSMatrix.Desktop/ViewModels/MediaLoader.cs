using Avalonia.Media.Imaging;
using RSMatrix.Desktop.Chat;
using RSMatrix.Desktop.Content;

namespace RSMatrix.Desktop.ViewModels;

/// <summary>Downloads and decodes images of one session, and keeps them while the session is open.</summary>
public sealed class MediaLoader(IChatSession session) : IDisposable
{
    /// <summary>Images are decoded no wider than this. The timeline shows them smaller.</summary>
    private const int MaxDecodeWidth = 1200;

    /// <summary>The viewer decodes up to this width, which is plenty for zooming in and bounds the memory.</summary>
    private const int MaxFullDecodeWidth = 4096;

    // The timeline shows images at up to 400×320; twice that stays sharp on high-DPI screens. Servers keep 800×600 ready.
    private const int ImageThumbnailWidth = 800;
    private const int ImageThumbnailHeight = 600;

    // Avatars are 36 pixels at most; 96×96 is a size servers keep ready.
    private const int AvatarSize = 96;

    private readonly Dictionary<string, Task<Bitmap?>> _images = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<Bitmap?>> _avatars = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>An image in a message. Call on the UI thread. Returns null when the image is not available.</summary>
    public Task<Bitmap?> LoadImageAsync(MediaReference media)
        => GetOrLoad(_images, media.MxcUri, LoadImageAsync);

    /// <summary>A user's or room's picture, cut square. Call on the UI thread. Returns null when there is none.</summary>
    public Task<Bitmap?> LoadAvatarAsync(string mxcUri)
        => GetOrLoad(_avatars, mxcUri, uri => DecodeAsync(token => session.DownloadThumbnailAsync(uri, AvatarSize, AvatarSize, crop: true, token)));

    /// <summary>
    /// The whole image for the viewer, and the file it came from. Not cached: the caller owns the bitmap.
    /// Returns null when the image is not available.
    /// </summary>
    public async Task<(byte[] Content, Bitmap Picture)?> LoadFullImageAsync(string mxcUri)
    {
        try
        {
            var content = await session.DownloadMediaAsync(mxcUri, _lifetime.Token).ConfigureAwait(false);
            if (content == null)
                return null;
            return (content, await Task.Run(() => Decode(content, MaxFullDecodeWidth)).ConfigureAwait(false));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes a whole file into <paramref name="destination"/> and reports the bytes written so far. Throws when the
    /// file is not available or turns out damaged, possibly after writing part of it.
    /// </summary>
    public async Task SaveAsync(string mxcUri, Stream destination, IProgress<long> progress, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        await session.DownloadMediaAsync(mxcUri, new ProgressStream(destination, progress), linked.Token).ConfigureAwait(false);
    }

    private static Task<Bitmap?> GetOrLoad(Dictionary<string, Task<Bitmap?>> cache, string mxcUri, Func<string, Task<Bitmap?>> load)
    {
        if (!cache.TryGetValue(mxcUri, out var image))
        {
            image = load(mxcUri);
            cache.Add(mxcUri, image);
        }
        return image;
    }

    // An image without a thumbnail, like an encrypted one whose sender didn't attach one, loads whole.
    private Task<Bitmap?> LoadImageAsync(string mxcUri) => DecodeAsync(async token =>
        await session.DownloadThumbnailAsync(mxcUri, ImageThumbnailWidth, ImageThumbnailHeight, crop: false, token).ConfigureAwait(false)
        ?? await session.DownloadMediaAsync(mxcUri, token).ConfigureAwait(false));

    private async Task<Bitmap?> DecodeAsync(Func<CancellationToken, Task<byte[]?>> download)
    {
        try
        {
            var bytes = await download(_lifetime.Token).ConfigureAwait(false);
            if (bytes == null)
                return null;
            return await Task.Run(() => Decode(bytes, MaxDecodeWidth)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Broken or unsupported images show as unavailable, and so does everything after the session closed.
            return null;
        }
    }

    private static Bitmap Decode(byte[] bytes, int maxWidth)
    {
        using var stream = new MemoryStream(bytes);
        var bitmap = new Bitmap(stream);
        if (bitmap.PixelSize.Width <= maxWidth)
            return bitmap;
        using (bitmap)
        {
            var height = Math.Max(1, (int)((long)bitmap.PixelSize.Height * maxWidth / bitmap.PixelSize.Width));
            return bitmap.CreateScaledBitmap(new Avalonia.PixelSize(maxWidth, height));
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        foreach (var image in _images.Values.Concat(_avatars.Values))
        {
            if (image.IsCompletedSuccessfully)
                image.Result?.Dispose();
        }
        _images.Clear();
        _avatars.Clear();
    }
}

/// <summary>Passes writes on and counts them.</summary>
internal sealed class ProgressStream(Stream inner, IProgress<long> progress) : Stream
{
    private long _written;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _written; set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        Report(count);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        Report(buffer.Length);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    private void Report(int count)
    {
        _written += count;
        progress.Report(_written);
    }
}
