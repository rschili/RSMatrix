using System.Text.Json;
using System.Text.Json.Serialization;

namespace RSMatrix.Desktop.Storage;

public sealed record AppearanceSettings
{
    public const string DefaultFontFamily = "Inter";
    public const double DefaultFontSize = 14;

    public static IReadOnlyList<double> FontSizes { get; } = [10, 11, 12, 13, 14, 15, 16, 17, 18, 20, 22, 24];

    public ThemePreference Theme { get; init; } = ThemePreference.System;

    /// <summary>A font family name. <see cref="DefaultFontFamily"/> is the bundled Inter.</summary>
    public string FontFamily { get; init; } = DefaultFontFamily;

    public double FontSize { get; init; } = DefaultFontSize;

    public bool ShowRoomIcons { get; init; } = true;

    /// <summary>Shows the latest message below each room name.</summary>
    public bool ShowRoomPreviews { get; init; } = true;

    /// <summary>Replaces values the app can't use, for example from a damaged file or another app version, with defaults.</summary>
    public AppearanceSettings Normalized() => this with
    {
        Theme = Enum.IsDefined(Theme) ? Theme : ThemePreference.System,
        FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? DefaultFontFamily : FontFamily,
        FontSize = FontSizes.Contains(FontSize) ? FontSize : DefaultFontSize,
    };
}

[JsonConverter(typeof(ThemePreferenceConverter))]
public enum ThemePreference
{
    System,
    Light,
    Dark,
}

/// <summary>Reads themes this version doesn't know, for example from a newer version, as <see cref="ThemePreference.System"/>.</summary>
internal sealed class ThemePreferenceConverter : JsonConverter<ThemePreference>
{
    public override ThemePreference Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            reader.Skip();
            return ThemePreference.System;
        }
        return Enum.TryParse<ThemePreference>(reader.GetString(), out var theme) && Enum.IsDefined(theme) ? theme : ThemePreference.System;
    }

    public override void Write(Utf8JsonWriter writer, ThemePreference value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
