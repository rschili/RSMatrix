using System.Text.Json.Serialization;

namespace RSMatrix.Desktop.Storage;

public sealed record AppearanceSettings
{
    public const string DefaultFontFamily = "Inter";
    public const double DefaultFontSize = 14;

    public ThemePreference Theme { get; init; } = ThemePreference.System;

    /// <summary>A font family name. <see cref="DefaultFontFamily"/> is the bundled Inter.</summary>
    public string FontFamily { get; init; } = DefaultFontFamily;

    public double FontSize { get; init; } = DefaultFontSize;

    public bool ShowRoomIcons { get; init; } = true;

    /// <summary>Shows the latest message below each room name.</summary>
    public bool ShowRoomPreviews { get; init; } = true;
}

[JsonConverter(typeof(JsonStringEnumConverter<ThemePreference>))]
public enum ThemePreference
{
    System,
    Light,
    Dark,
}
