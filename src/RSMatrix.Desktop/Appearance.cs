using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using RSMatrix.Desktop.Storage;

namespace RSMatrix.Desktop;

/// <summary>
/// Puts the appearance settings into the application resources. Views read them through
/// <c>DynamicResource</c>, so changes show up everywhere at once.
/// </summary>
internal static class Appearance
{
    private const string BundledInter = "fonts:Inter#Inter";

    /// <summary>Font sizes at the default size of 14, scaled with the chosen size.</summary>
    private static readonly (string Key, double Size)[] FontSizes =
    [
        ("ControlContentThemeFontSize", 14), // Fluent's size for windows and controls
        ("FontSizeTitle", 16),
        ("FontSizeCompact", 13),
        ("FontSizeCaption", 12),
        ("FontSizeSmall", 11),
        ("FontSizeEmoji", 17),
    ];

    public static void Apply(Application application, AppearanceSettings appearance)
    {
        application.RequestedThemeVariant = appearance.Theme switch
        {
            ThemePreference.Light => ThemeVariant.Light,
            ThemePreference.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };

        var resources = application.Resources;
        resources["ContentControlThemeFontFamily"] = ToFontFamily(appearance.FontFamily);
        var scale = appearance.FontSize / AppearanceSettings.DefaultFontSize;
        foreach (var (key, size) in FontSizes)
            resources[key] = Math.Round(size * scale, 1);
        // Name column of the classic message layout
        resources["ClassicNameWidth"] = Math.Round(120 * scale);

        resources["MessageLayout"] = appearance.MessageLayout;

        resources["ShowRoomIcons"] = appearance.ShowRoomIcons;
        resources["ShowRoomBadgeBesideName"] = !appearance.ShowRoomIcons;
        resources["ShowRoomPreviews"] = appearance.ShowRoomPreviews;
        resources["SidebarWidth"] = appearance.ShowRoomPreviews ? 250.0 : 210.0;
    }

    public static FontFamily ToFontFamily(string name)
        => new(name == AppearanceSettings.DefaultFontFamily ? BundledInter : name);

    /// <summary>Installed font families, with the bundled default first.</summary>
    public static IReadOnlyList<string> GetFontFamilies()
        => FontManager.Current.SystemFonts.Select(family => family.Name)
            .Where(name => name != AppearanceSettings.DefaultFontFamily)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Prepend(AppearanceSettings.DefaultFontFamily)
            .ToList();
}
