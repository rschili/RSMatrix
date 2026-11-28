using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RSMatrix.Desktop.Storage;

namespace RSMatrix.Desktop.ViewModels;

/// <summary>The settings dialog. Changes apply only when the user saves them.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly Action<SettingsDialogResult?> _onClosed;

    /// <param name="onClosed">Called with the new settings after Save, or null after Cancel.</param>
    public SettingsViewModel(AppearanceSettings appearance, int idleMinutes, IReadOnlyList<string> fontFamilies,
        Action<SettingsDialogResult?> onClosed)
    {
        _onClosed = onClosed;
        IdleTime = IdleTimes.FirstOrDefault(t => t.Minutes == idleMinutes) ?? IdleTimes.First(t => t.Minutes == PresenceSettings.DefaultIdleMinutes);
        FontFamilies = fontFamilies.Contains(appearance.FontFamily) ? fontFamilies : [.. fontFamilies, appearance.FontFamily];
        Theme = Themes.FirstOrDefault(t => t.Value == appearance.Theme) ?? Themes[0];
        MessageLayout = appearance.MessageLayout;
        FontFamily = appearance.FontFamily;
        FontSize = appearance.FontSize;
        ShowRoomIcons = appearance.ShowRoomIcons;
        ShowRoomPreviews = appearance.ShowRoomPreviews;
    }

    public IReadOnlyList<ThemeOption> Themes { get; } =
    [
        new(ThemePreference.System, "Same as the system"),
        new(ThemePreference.Light, "Light"),
        new(ThemePreference.Dark, "Dark"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsComfortableLayout), nameof(IsClassicLayout))]
    public partial MessageLayout MessageLayout { get; set; }

    // For radio buttons: checking one selects its layout, unchecking does nothing.
    public bool IsComfortableLayout
    {
        get => MessageLayout == MessageLayout.Comfortable;
        set
        {
            if (value)
                MessageLayout = MessageLayout.Comfortable;
        }
    }

    public bool IsClassicLayout
    {
        get => MessageLayout == MessageLayout.Classic;
        set
        {
            if (value)
                MessageLayout = MessageLayout.Classic;
        }
    }

    public IReadOnlyList<string> FontFamilies { get; }

    public IReadOnlyList<double> FontSizes => AppearanceSettings.FontSizes;

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

    public IReadOnlyList<IdleTimeOption> IdleTimes { get; } = PresenceSettings.IdleMinuteChoices
        .Select(minutes => new IdleTimeOption(minutes, minutes switch
        {
            0 => "Never",
            1 => "1 minute",
            60 => "1 hour",
            _ => $"{minutes} minutes",
        }))
        .ToList();

    [ObservableProperty]
    public partial IdleTimeOption IdleTime { get; set; }

    [RelayCommand]
    private void Save() => _onClosed(new SettingsDialogResult(new AppearanceSettings
    {
        Theme = Theme.Value,
        MessageLayout = MessageLayout,
        FontFamily = FontFamily,
        FontSize = FontSize,
        ShowRoomIcons = ShowRoomIcons,
        ShowRoomPreviews = ShowRoomPreviews,
    }, IdleTime.Minutes));

    [RelayCommand]
    private void Cancel() => _onClosed(null);
}

public sealed record ThemeOption(ThemePreference Value, string Label);

public sealed record IdleTimeOption(int Minutes, string Label);

/// <param name="IdleMinutes">See <see cref="PresenceSettings.IdleMinutes"/>.</param>
public sealed record SettingsDialogResult(AppearanceSettings Appearance, int IdleMinutes);
