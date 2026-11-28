using System.Text.Json.Serialization;

namespace RSMatrix.Desktop.Storage;

public sealed record PresenceSettings
{
    public const int DefaultIdleMinutes = 5;

    /// <summary>The choices in the settings dialog; 0 means never.</summary>
    public static IReadOnlyList<int> IdleMinuteChoices { get; } = [1, 5, 10, 15, 30, 60, 0];

    public PresenceMode Mode { get; init; } = PresenceMode.Online;

    /// <summary>Minutes without activity in the app before it shows the user as idle; 0 never does.</summary>
    public int IdleMinutes { get; init; } = DefaultIdleMinutes;

    /// <summary>Replaces values the app can't use with defaults.</summary>
    public PresenceSettings Normalized() => this with
    {
        Mode = Enum.IsDefined(Mode) ? Mode : PresenceMode.Online,
        IdleMinutes = IdleMinuteChoices.Contains(IdleMinutes) ? IdleMinutes : DefaultIdleMinutes,
    };
}

/// <summary>How the user wants to appear to others.</summary>
[JsonConverter(typeof(LenientEnumConverter<PresenceMode>))]
public enum PresenceMode
{
    /// <summary>Online while the user is active in the app, idle after <see cref="PresenceSettings.IdleMinutes"/>.</summary>
    Online,

    /// <summary>Away until the user changes it.</summary>
    Away,

    /// <summary>Offline to others until the user changes it.</summary>
    Invisible,
}
