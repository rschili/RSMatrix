using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RSMatrix.Desktop.Storage;

namespace RSMatrix.Desktop.ViewModels;

/// <summary>The settings dialog. Changes apply only when the user confirms with OK.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly Action<AppearanceSettings?> _onClosed;

    /// <param name="onClosed">Called with the new settings after OK, or null after Cancel.</param>
    public SettingsViewModel(AppearanceSettings appearance, IReadOnlyList<string> fontFamilies, Action<AppearanceSettings?> onClosed)
    {
        _onClosed = onClosed;
        FontFamilies = fontFamilies.Contains(appearance.FontFamily) ? fontFamilies : [.. fontFamilies, appearance.FontFamily];
        Theme = Themes.FirstOrDefault(t => t.Value == appearance.Theme) ?? Themes[0];
        FontFamily = appearance.FontFamily;
        FontSize = FontSizes.Contains(appearance.FontSize) ? appearance.FontSize : AppearanceSettings.DefaultFontSize;
        ShowRoomIcons = appearance.ShowRoomIcons;
        ShowRoomPreviews = appearance.ShowRoomPreviews;
    }

    public IReadOnlyList<ThemeOption> Themes { get; } =
    [
        new(ThemePreference.System, "Same as the system"),
        new(ThemePreference.Light, "Light"),
        new(ThemePreference.Dark, "Dark"),
    ];

    public IReadOnlyList<string> FontFamilies { get; }

    public IReadOnlyList<double> FontSizes { get; } = [10, 11, 12, 13, 14, 15, 16, 17, 18, 20, 22, 24];

    [ObservableProperty]
    public partial ThemeOption Theme { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewFontFamily))]
    public partial string FontFamily { get; set; }

    [ObservableProperty]
    public partial double FontSize { get; set; }

    public FontFamily PreviewFontFamily => Appearance.ToFontFamily(FontFamily);

    [ObservableProperty]
    public partial bool ShowRoomIcons { get; set; }

    [ObservableProperty]
    public partial bool ShowRoomPreviews { get; set; }

    [RelayCommand]
    private void Confirm() => _onClosed(new AppearanceSettings
    {
        Theme = Theme.Value,
        FontFamily = FontFamily,
        FontSize = FontSize,
        ShowRoomIcons = ShowRoomIcons,
        ShowRoomPreviews = ShowRoomPreviews,
    });

    [RelayCommand]
    private void Cancel() => _onClosed(null);
}

public sealed record ThemeOption(ThemePreference Value, string Label);
