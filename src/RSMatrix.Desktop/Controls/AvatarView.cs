using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace RSMatrix.Desktop.Controls;

/// <summary>
/// A picture in a circle or rounded square. Without a picture, or while it loads, a colored badge with the first
/// letter of the name. Pictures come from the <see cref="MediaImageView.LoaderProperty"/> the chat view sets.
/// </summary>
public sealed class AvatarView : Control
{
    /// <summary>The picture as an mxc:// URI, or null for the letter badge.</summary>
    public static readonly StyledProperty<string?> AvatarUrlProperty =
        AvaloniaProperty.Register<AvatarView, string?>(nameof(AvatarUrl));

    public static readonly StyledProperty<string?> DisplayNameProperty =
        AvaloniaProperty.Register<AvatarView, string?>(nameof(DisplayName));

    /// <summary>Chooses the color, usually the user or room ID, so it stays the same when the name changes.</summary>
    public static readonly StyledProperty<string?> ColorSeedProperty =
        AvaloniaProperty.Register<AvatarView, string?>(nameof(ColorSeed));

    public static readonly StyledProperty<bool> IsSquareProperty =
        AvaloniaProperty.Register<AvatarView, bool>(nameof(IsSquare));

    private static readonly Color[] Palette =
    [
        Color.Parse("#368BD6"), Color.Parse("#AC3BA8"), Color.Parse("#0DA678"), Color.Parse("#E64F7A"),
        Color.Parse("#F07A2B"), Color.Parse("#1FA8AB"), Color.Parse("#5C56F5"), Color.Parse("#5FAD27"),
    ];

    /// <summary>Turns a color seed into a brush in the avatar's color, for example to color names.</summary>
    public static IValueConverter ColorBrushConverter { get; } =
        new FuncValueConverter<string?, IBrush>(seed => new SolidColorBrush(GetColor(seed)));

    private static readonly Typeface InitialTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    private Bitmap? _picture;
    private int _loadVersion;

    static AvatarView()
    {
        AffectsRender<AvatarView>(DisplayNameProperty, ColorSeedProperty, IsSquareProperty);
    }

    public string? DisplayName
    {
        get => GetValue(DisplayNameProperty);
        set => SetValue(DisplayNameProperty, value);
    }

    public string? ColorSeed
    {
        get => GetValue(ColorSeedProperty);
        set => SetValue(ColorSeedProperty, value);
    }

    public bool IsSquare
    {
        get => GetValue(IsSquareProperty);
        set => SetValue(IsSquareProperty, value);
    }

    public string? AvatarUrl
    {
        get => GetValue(AvatarUrlProperty);
        set => SetValue(AvatarUrlProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AvatarUrlProperty || change.Property == MediaImageView.LoaderProperty)
            LoadPicture();
    }

    private async void LoadPicture()
    {
        var version = ++_loadVersion;
        _picture = null;
        InvalidateVisual();
        if (AvatarUrl is not { } url || MediaImageView.GetLoader(this) is not { } loader)
            return;
        // The loader owns the bitmap and keeps it for the session.
        var picture = await loader.LoadAvatarAsync(url);
        if (version != _loadVersion)
            return;
        _picture = picture;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0)
            return;
        var rect = new Rect(0, 0, size, size);
        var radius = IsSquare ? size * 0.28 : size / 2;
        if (_picture is { } picture)
        {
            // Servers cut avatar thumbnails square; other pictures lose their edges.
            var side = Math.Min(picture.Size.Width, picture.Size.Height);
            var source = new Rect((picture.Size.Width - side) / 2, (picture.Size.Height - side) / 2, side, side);
            using (context.PushClip(new RoundedRect(rect, radius)))
                context.DrawImage(picture, source, rect);
            return;
        }

        var background = new SolidColorBrush(GetColor(ColorSeed ?? DisplayName));
        context.DrawRectangle(background, null, new RoundedRect(rect, radius));

        var initial = GetInitial(DisplayName);
        if (initial.Length == 0)
            return;
        var text = new FormattedText(initial, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, InitialTypeface,
            size * 0.44, Brushes.White);
        context.DrawText(text, new Point((size - text.Width) / 2, (size - text.Height) / 2));
    }

    internal static string GetInitial(string? name)
    {
        var trimmed = name?.TrimStart('@', '#', '!', '+').Trim();
        if (string.IsNullOrEmpty(trimmed))
            return "";
        var first = StringInfo.GetNextTextElementLength(trimmed);
        return trimmed[..first].ToUpper(CultureInfo.CurrentCulture);
    }

    internal static Color GetColor(string? seed)
    {
        // A fixed hash, so colors stay the same between runs.
        var hash = 0;
        foreach (var c in seed ?? "")
            hash = unchecked(hash * 31 + c);
        return Palette[(hash & int.MaxValue) % Palette.Length];
    }
}
