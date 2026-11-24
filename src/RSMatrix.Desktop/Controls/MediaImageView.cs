using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using RSMatrix.Desktop.Content;
using RSMatrix.Desktop.ViewModels;

namespace RSMatrix.Desktop.Controls;

/// <summary>
/// Shows an image from the homeserver, scaled down to fit. Media comes from the <see cref="LoaderProperty"/>
/// that the chat view sets for all its descendants.
/// </summary>
public sealed class MediaImageView : Border
{
    public static readonly AttachedProperty<MediaLoader?> LoaderProperty =
        AvaloniaProperty.RegisterAttached<MediaImageView, Control, MediaLoader?>("Loader", inherits: true);

    public static readonly StyledProperty<MediaReference?> MediaProperty =
        AvaloniaProperty.Register<MediaImageView, MediaReference?>(nameof(Media));

    public static readonly StyledProperty<double> MaxImageWidthProperty =
        AvaloniaProperty.Register<MediaImageView, double>(nameof(MaxImageWidth), double.PositiveInfinity);

    public static readonly StyledProperty<double> MaxImageHeightProperty =
        AvaloniaProperty.Register<MediaImageView, double>(nameof(MaxImageHeight), double.PositiveInfinity);

    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private int _loadVersion;

    public static MediaLoader? GetLoader(Control control) => control.GetValue(LoaderProperty);

    public static void SetLoader(Control control, MediaLoader? value) => control.SetValue(LoaderProperty, value);

    public MediaImageView()
    {
        HorizontalAlignment = HorizontalAlignment.Left;
        ClipToBounds = true;
        Child = _image;
    }

    public MediaReference? Media
    {
        get => GetValue(MediaProperty);
        set => SetValue(MediaProperty, value);
    }

    public double MaxImageWidth
    {
        get => GetValue(MaxImageWidthProperty);
        set => SetValue(MaxImageWidthProperty, value);
    }

    public double MaxImageHeight
    {
        get => GetValue(MaxImageHeightProperty);
        set => SetValue(MaxImageHeightProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MediaProperty || change.Property == LoaderProperty ||
            change.Property == MaxImageWidthProperty || change.Property == MaxImageHeightProperty)
            Load();
    }

    private async void Load()
    {
        var version = ++_loadVersion;
        _image.Source = null;
        Child = _image;
        var media = Media;
        var loader = GetLoader(this);
        if (media == null || loader == null)
            return;

        // Reserve the space while loading, so the timeline doesn't jump when the image arrives.
        ApplySize(media.Width, media.Height);
        var bitmap = await loader.LoadImageAsync(media);
        if (version != _loadVersion)
            return;
        if (bitmap == null)
        {
            ShowUnavailable(media);
            return;
        }
        _image.Source = bitmap;
        ApplySize(bitmap.Size.Width, bitmap.Size.Height);
    }

    private void ApplySize(double? width, double? height)
    {
        if (width is not > 0 || height is not > 0)
        {
            _image.Width = double.NaN;
            _image.Height = double.NaN;
            return;
        }
        var scale = Math.Min(1, Math.Min(MaxImageWidth / width.Value, MaxImageHeight / height.Value));
        _image.Width = Math.Round(width.Value * scale);
        _image.Height = Math.Round(height.Value * scale);
    }

    private void ShowUnavailable(MediaReference media)
    {
        var description = string.IsNullOrWhiteSpace(media.Description) ? "Image" : media.Description;
        var text = new TextBlock { Text = $"{description} (not available)", FontStyle = FontStyle.Italic };
        text.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("TextTertiaryBrush"));
        Child = text;
    }
}
