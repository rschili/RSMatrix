using Avalonia.Media.Imaging;
using RSMatrix.Desktop.Chat;
using RSMatrix.Desktop.Content;

namespace RSMatrix.Desktop.ViewModels;

/// <summary>Downloads and decodes images of one session, and keeps them while the session is open.</summary>
public sealed class MediaLoader(IChatSession session) : IDisposable
{
    /// <summary>Images are decoded no wider than this. The timeline shows them smaller.</summary>
    private const int MaxDecodeWidth = 1200;

    private readonly Dictionary<string, Task<Bitmap?>> _images = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>Call on the UI thread. Returns null when the image is not available.</summary>
    public Task<Bitmap?> LoadImageAsync(MediaReference media)
    {
        if (!_images.TryGetValue(media.MxcUri, out var image))
        {
            image = DownloadAsync(media.MxcUri);
            _images.Add(media.MxcUri, image);
        }
        return image;
    }

    private async Task<Bitmap?> DownloadAsync(string mxcUri)
    {
        try
        {
            var bytes = await session.DownloadMediaAsync(mxcUri, _lifetime.Token).ConfigureAwait(false);
            if (bytes == null)
                return null;
            return await Task.Run(() => Decode(bytes)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Broken or unsupported images show as unavailable.
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static Bitmap Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var bitmap = new Bitmap(stream);
        if (bitmap.PixelSize.Width <= MaxDecodeWidth)
            return bitmap;
        using (bitmap)
        {
            var height = Math.Max(1, bitmap.PixelSize.Height * MaxDecodeWidth / bitmap.PixelSize.Width);
            return bitmap.CreateScaledBitmap(new Avalonia.PixelSize(MaxDecodeWidth, height));
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        foreach (var image in _images.Values)
        {
            if (image.IsCompletedSuccessfully)
                image.Result?.Dispose();
        }
        _images.Clear();
    }
}
