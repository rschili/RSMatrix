using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace RSMatrix.Desktop.Controls;

/// <summary>A colored circle or rounded square with the first letter of a name.</summary>
public sealed class AvatarView : Control
{
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

    private static readonly Typeface InitialTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

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

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0)
            return;
        var rect = new Rect(0, 0, size, size);
        var background = new SolidColorBrush(GetColor(ColorSeed ?? DisplayName));
        var radius = IsSquare ? size * 0.28 : size / 2;
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
