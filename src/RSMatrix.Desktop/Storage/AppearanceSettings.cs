using System.Text.Json;
using System.Text.Json.Serialization;

namespace RSMatrix.Desktop.Storage;

public sealed record AppearanceSettings
{
    public const string DefaultFontFamily = "Inter";
    public const double DefaultFontSize = 14;

    public static IReadOnlyList<double> FontSizes { get; } = [10, 11, 12, 13, 14, 15, 16, 17, 18, 20, 22, 24];

    public ThemePreference Theme { get; init; } = ThemePreference.System;

    public MessageLayout MessageLayout { get; init; } = MessageLayout.Comfortable;

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
        MessageLayout = Enum.IsDefined(MessageLayout) ? MessageLayout : MessageLayout.Comfortable,
        FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? DefaultFontFamily : FontFamily,
        FontSize = FontSizes.Contains(FontSize) ? FontSize : DefaultFontSize,
    };
}

[JsonConverter(typeof(LenientEnumConverter<ThemePreference>))]
public enum ThemePreference
{
    System,
    Light,
    Dark,
}

[JsonConverter(typeof(LenientEnumConverter<MessageLayout>))]
public enum MessageLayout
{
    /// <summary>Avatar and name above the text, consecutive messages grouped.</summary>
    Comfortable,

    /// <summary>One line per message with time, name and text in columns, like IRC clients.</summary>
    Classic,
}

/// <summary>Reads values this version doesn't know, for example from a newer version, as the first one of the enum.</summary>
internal sealed class LenientEnumConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            reader.Skip();
            return default;
        }
        return Enum.TryParse<TEnum>(reader.GetString(), out var value) && Enum.IsDefined(value) ? value : default;
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
