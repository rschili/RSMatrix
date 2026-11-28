using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using RSMatrix.Desktop.Content;
using RSMatrix.Desktop.Controls;
using RSMatrix.Desktop.ViewModels;

namespace RSMatrix.Desktop.Views;

/// <summary>
/// Shows one image over the whole chat: fitted to the window at first, zoomable with the wheel, the buttons or +/-,
/// and draggable when zoomed. Esc or a click beside the image closes it.
/// </summary>
public partial class ImageViewer : UserControl
{
    private const double MaxZoom = 4;
    private const double ZoomStep = 1.25;
    private const double FitMargin = 24;

    private MediaReference? _media;
    private Bitmap? _fullImage;
    private byte[]? _content;
    private Size _naturalSize;
    // Null while the image fits the window; otherwise the scale, 1 being the image's own size.
    private double? _zoom;
    private int _openVersion;
    private Point? _dragStart;
    private Vector _dragStartOffset;

    public ImageViewer()
    {
        InitializeComponent();
        ZoomInButton.Click += (_, _) => ZoomBy(ZoomStep, null);
        ZoomOutButton.Click += (_, _) => ZoomBy(1 / ZoomStep, null);
        SaveButton.Click += OnSaveClick;
        CloseButton.Click += (_, _) => Close();
        Scroller.AddHandler(PointerWheelChangedEvent, OnPointerWheel, RoutingStrategies.Tunnel);
        Scroller.PointerPressed += (_, _) => Close();
        Scroller.SizeChanged += (_, _) => ApplyZoom();
        Picture.PointerPressed += OnPicturePressed;
        Picture.PointerMoved += OnPictureMoved;
        Picture.PointerReleased += OnPictureReleased;
    }

    /// <summary>Shows the cached timeline version right away and the whole image once it is downloaded.</summary>
    public async void Open(MediaReference media, MediaLoader loader)
    {
        Close();
        var version = _openVersion;
        _media = media;
        Title.Text = string.IsNullOrWhiteSpace(media.Description) ? "Image" : media.Description;
        Status.Text = "Loading…";
        IsVisible = true;
        Focus();

        var preview = await loader.LoadImageAsync(media);
        if (version != _openVersion)
            return;
        if (preview != null)
            ShowPicture(preview, media.Width, media.Height);

        var full = await loader.LoadFullImageAsync(media.MxcUri);
        if (version != _openVersion)
        {
            full?.Picture.Dispose();
            return;
        }
        if (full is not { } loaded)
        {
            Status.Text = preview == null ? "Not available" : "Full size not available";
            return;
        }
        _fullImage = loaded.Picture;
        _content = loaded.Content;
        ShowPicture(loaded.Picture, null, null);
        Status.Text = FileRowView.FormatSize(loaded.Content.Length);
        SaveButton.IsEnabled = true;
    }

    public void Close()
    {
        _openVersion++;
        IsVisible = false;
        Picture.Source = null;
        _fullImage?.Dispose();
        _fullImage = null;
        _content = null;
        _media = null;
        _zoom = null;
        _dragStart = null;
        SaveButton.IsEnabled = false;
    }

    /// <param name="width">The image's own width, when the picture is a smaller stand-in for it.</param>
    private void ShowPicture(Bitmap picture, int? width, int? height)
    {
        Picture.Source = picture;
        _naturalSize = width is > 0 && height is > 0 ? new Size(width.Value, height.Value) : picture.Size;
        ApplyZoom();
    }

    /// <summary>The scale at which the whole image fits. Small images stay at their own size.</summary>
    private double FitScale
    {
        get
        {
            var available = Scroller.Bounds.Size;
            if (_naturalSize.Width <= 0 || _naturalSize.Height <= 0 || available.Width <= 2 * FitMargin || available.Height <= 2 * FitMargin)
                return 1;
            return Math.Min(1, Math.Min((available.Width - 2 * FitMargin) / _naturalSize.Width,
                (available.Height - 2 * FitMargin) / _naturalSize.Height));
        }
    }

    private bool IsZoomedIn => _zoom is { } zoom && zoom > FitScale + 0.001;

    private void ApplyZoom()
    {
        var scale = _zoom ?? FitScale;
        Picture.Width = Math.Max(1, _naturalSize.Width * scale);
        Picture.Height = Math.Max(1, _naturalSize.Height * scale);
        ZoomInButton.IsEnabled = scale < MaxZoom;
        ZoomOutButton.IsEnabled = IsZoomedIn;
        Picture.Cursor = IsZoomedIn ? new Cursor(StandardCursorType.SizeAll) : Cursor.Default;
    }

    /// <param name="anchor">The point in the scroller that stays in place, or null for the middle.</param>
    private void ZoomBy(double factor, Point? anchor)
    {
        var fit = FitScale;
        var current = _zoom ?? fit;
        var next = Math.Clamp(current * factor, fit, Math.Max(fit, MaxZoom));
        if (Math.Abs(next - current) < 0.0001)
            return;

        var viewport = Scroller.Viewport;
        var fixedPoint = anchor ?? new Point(viewport.Width / 2, viewport.Height / 2);
        var before = Scroller.TranslatePoint(fixedPoint, Picture) ?? default;
        _zoom = next <= fit + 0.0001 ? null : next;
        ApplyZoom();
        Scroller.UpdateLayout();
        if (Picture.TranslatePoint(new Point(before.X * next / current, before.Y * next / current), Scroller) is { } after)
            Scroller.Offset += after - fixedPoint;
    }

    private void OnPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        e.Handled = true;
        if (e.Delta.Y != 0)
            ZoomBy(e.Delta.Y > 0 ? ZoomStep : 1 / ZoomStep, e.GetPosition(Scroller));
    }

    private void OnPicturePressed(object? sender, PointerPressedEventArgs e)
    {
        // A click on the image itself doesn't close the viewer.
        e.Handled = true;
        if (!IsZoomedIn || !e.GetCurrentPoint(Picture).Properties.IsLeftButtonPressed)
            return;
        _dragStart = e.GetPosition(Scroller);
        _dragStartOffset = Scroller.Offset;
        e.Pointer.Capture(Picture);
    }

    private void OnPictureMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStart is { } start)
            Scroller.Offset = _dragStartOffset - (e.GetPosition(Scroller) - start);
    }

    private void OnPictureReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragStart = null;
        e.Pointer.Capture(null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.Add or Key.OemPlus:
                ZoomBy(ZoomStep, null);
                break;
            case Key.Subtract or Key.OemMinus:
                ZoomBy(1 / ZoomStep, null);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (_content is not { } content || TopLevel.GetTopLevel(this) is not { } topLevel)
            return;
        var target = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = FileRowView.ToSafeFileName(_media?.Description ?? "image"),
            ShowOverwritePrompt = true,
        });
        if (target == null)
            return;
        try
        {
            await using var stream = await target.OpenWriteAsync();
            if (stream.CanSeek)
                stream.SetLength(0);
            await stream.WriteAsync(content);
            Status.Text = "Saved";
        }
        catch (Exception)
        {
            Status.Text = "Saving failed";
        }
    }
}
