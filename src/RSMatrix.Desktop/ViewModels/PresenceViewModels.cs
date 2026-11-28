using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RSMatrix.Desktop.Chat;
using RSMatrix.Desktop.Storage;

namespace RSMatrix.Desktop.ViewModels;

/// <summary>
/// The current user's presence in the badge below the room list. In <see cref="PresenceMode.Online"/> the user is online
/// while active in the app and idle after <see cref="IdleMinutes"/>; Away and Invisible stay until the user changes them.
/// </summary>
public sealed partial class OwnPresenceViewModel : ObservableObject
{
    private readonly IChatSession _session;
    private readonly Func<DateTimeOffset> _getNow;
    private readonly Action<string> _reportIssue;
    private readonly CancellationToken _lifetime;
    private DateTimeOffset _lastActivity;
    private ChatPresence? _reported;
    private int _differenceCheck;

    public OwnPresenceViewModel(IChatSession session, Func<DateTimeOffset> getNow, Action<string> reportIssue,
        CancellationToken lifetime)
    {
        _session = session;
        _getNow = getNow;
        _reportIssue = reportIssue;
        _lifetime = lifetime;
        _lastActivity = getNow();
        _reported = session.CurrentUserPresence;
        StatusMessage = _reported?.StatusMessage;
    }

    /// <summary>
    /// How long a difference between what we asked for and what others see must last before it shows. Sending a
    /// message while away shows the user online for a moment.
    /// </summary>
    internal TimeSpan DifferenceDelay { get; init; } = TimeSpan.FromSeconds(3);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnlineMode), nameof(IsAwayMode), nameof(IsInvisibleMode))]
    [NotifyPropertyChangedFor(nameof(RequestedState), nameof(IsOnline), nameof(IsAway), nameof(IsOffline), nameof(StateText), nameof(LineText))]
    public partial PresenceMode Mode { get; private set; }

    /// <summary>The user was inactive for <see cref="IdleMinutes"/> in <see cref="PresenceMode.Online"/>; others see them away.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequestedState), nameof(IsOnline), nameof(IsAway), nameof(IsOffline), nameof(StateText), nameof(LineText))]
    public partial bool IsIdle { get; private set; }

    /// <summary>Minutes without activity before the user shows as idle; 0 never.</summary>
    public int IdleMinutes { get; set; } = PresenceSettings.DefaultIdleMinutes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LineText))]
    public partial string? StatusMessage { get; private set; }

    /// <summary>The text field in the presence menu.</summary>
    [ObservableProperty]
    public partial string StatusDraft { get; set; } = "";

    /// <summary>
    /// Set while others see something else than <see cref="RequestedState"/>, usually because another device of the user is
    /// online.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LineText), nameof(LineToolTip))]
    public partial PresenceState? OthersSee { get; private set; }

    public PresenceState RequestedState => Mode switch
    {
        PresenceMode.Away => PresenceState.Away,
        PresenceMode.Invisible => PresenceState.Offline,
        _ => IsIdle ? PresenceState.Away : PresenceState.Online,
    };

    public bool IsOnline => RequestedState == PresenceState.Online;

    public bool IsAway => RequestedState == PresenceState.Away;

    public bool IsOffline => RequestedState == PresenceState.Offline;

    public string StateText => Mode switch
    {
        PresenceMode.Away => "Away",
        PresenceMode.Invisible => "Invisible",
        _ => IsIdle ? "Idle" : "Online",
    };

    /// <summary>The line below the user's name, like "Online · lunch".</summary>
    public string LineText => OthersSee is { } othersSee
        ? $"{StateText} · others see you {PresenceText.StateName(othersSee)}"
        : StatusMessage is { } status ? $"{StateText} · {status}" : StateText;

    public string? LineToolTip => OthersSee is { } othersSee
        ? $"Another of your devices is {PresenceText.StateName(othersSee)}, so others see you {PresenceText.StateName(othersSee)}."
        : StatusMessage;

    // For the radio buttons in the presence menu: checking one selects its mode, unchecking does nothing.
    public bool IsOnlineMode
    {
        get => Mode == PresenceMode.Online;
        set
        {
            if (value)
                SetMode(PresenceMode.Online);
        }
    }

    public bool IsAwayMode
    {
        get => Mode == PresenceMode.Away;
        set
        {
            if (value)
                SetMode(PresenceMode.Away);
        }
    }

    public bool IsInvisibleMode
    {
        get => Mode == PresenceMode.Invisible;
        set
        {
            if (value)
                SetMode(PresenceMode.Invisible);
        }
    }

    /// <summary>Applies the stored choice at the start of the session.</summary>
    public void Start(PresenceSettings settings)
    {
        IdleMinutes = settings.IdleMinutes;
        Mode = settings.Mode;
        Send();
    }

    public void SetMode(PresenceMode mode)
    {
        if (mode == Mode)
            return;
        Mode = mode;
        IsIdle = false;
        _lastActivity = _getNow();
        Send();
    }

    /// <summary>The user did something in the app, like moving the mouse or bringing the window to the front.</summary>
    public void ReportActivity()
    {
        _lastActivity = _getNow();
        if (!IsIdle)
            return;
        IsIdle = false;
        Send();
    }

    /// <summary>Makes the user idle once the idle time has passed. Called regularly.</summary>
    public void CheckIdle()
    {
        if (Mode != PresenceMode.Online || IsIdle || IdleMinutes <= 0 || _getNow() - _lastActivity < TimeSpan.FromMinutes(IdleMinutes))
            return;
        IsIdle = true;
        Send();
    }

    /// <summary>What the server reports for the current user, which is what others see.</summary>
    public void ApplyReported(ChatPresence presence)
    {
        _reported = presence;
        StatusMessage = presence.StatusMessage;
        UpdateOthersSee();
    }

    /// <summary>Fills the text field when the presence menu opens.</summary>
    public void BeginStatusEdit() => StatusDraft = StatusMessage ?? "";

    [RelayCommand]
    private void SaveStatus()
    {
        var status = string.IsNullOrWhiteSpace(StatusDraft) ? null : StatusDraft.Trim();
        StatusDraft = status ?? "";
        if (status == StatusMessage)
            return;
        StatusMessage = status;
        _ = RunAsync(() => _session.SetStatusMessageAsync(status, _lifetime), "Your status could not be set");
    }

    [RelayCommand]
    private void ClearStatus()
    {
        StatusDraft = "";
        SaveStatus();
    }

    private void Send()
    {
        var state = RequestedState;
        UpdateOthersSee();
        _ = RunAsync(() => _session.SetPresenceAsync(state, _lifetime), "Your presence could not be set");
    }

    private async Task RunAsync(Func<Task> request, string failure)
    {
        try
        {
            await request();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _reportIssue($"{failure}: {ex.Message}");
        }
    }

    // Hides the difference right away when it's gone, but shows it only once it lasts.
    private void UpdateOthersSee()
    {
        var check = ++_differenceCheck;
        if (GetDifference() is not { } difference)
        {
            OthersSee = null;
            return;
        }
        if (OthersSee != null)
        {
            OthersSee = difference;
            return;
        }
        _ = ShowDifferenceLaterAsync(check);
    }

    private async Task ShowDifferenceLaterAsync(int check)
    {
        try
        {
            await Task.Delay(DifferenceDelay, _lifetime);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (check == _differenceCheck)
            OthersSee = GetDifference();
    }

    private PresenceState? GetDifference()
        => _reported is { } reported && reported.State != RequestedState ? reported.State : null;
}

/// <summary>A member in the member list. Follows presence changes while the list is open.</summary>
public sealed partial class MemberViewModel(ChatMember member, Func<DateTimeOffset> getNow) : ObservableObject
{
    public ChatUser User => member.User;

    public bool IsInvited => member.IsInvited;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPresence), nameof(IsOnline), nameof(IsAway), nameof(IsOffline))]
    [NotifyPropertyChangedFor(nameof(StatusMessage), nameof(HasStatusMessage), nameof(ToolTipText))]
    public partial ChatPresence? Presence { get; set; } = member.Presence;

    public bool HasPresence => Presence != null;

    public bool IsOnline => Presence?.State == PresenceState.Online;

    public bool IsAway => Presence?.State == PresenceState.Away;

    public bool IsOffline => Presence?.State == PresenceState.Offline;

    public string? StatusMessage => Presence?.StatusMessage;

    public bool HasStatusMessage => !IsInvited && StatusMessage != null;

    /// <summary>The user ID, presence with last seen, and the full status text.</summary>
    public string ToolTipText
    {
        get
        {
            var lines = new List<string> { User.UserId };
            if (Presence is { } presence)
            {
                var state = PresenceText.StateName(presence.State);
                var line = char.ToUpperInvariant(state[0]) + state[1..];
                if (presence.State != PresenceState.Online && PresenceText.LastSeen(presence, getNow()) is { } lastSeen)
                    line += $", last seen {lastSeen}";
                lines.Add(line);
                if (presence.StatusMessage is { } status)
                    lines.Add(status);
            }
            return string.Join("\n", lines);
        }
    }
}

internal static class PresenceText
{
    public static string StateName(PresenceState state) => state switch
    {
        PresenceState.Away => "away",
        PresenceState.Offline => "offline",
        _ => "online",
    };

    /// <summary>A rough time like "today" or "last week", or null when unknown.</summary>
    public static string? LastSeen(ChatPresence presence, DateTimeOffset now)
    {
        if (presence.IsCurrentlyActive)
            return "just now";
        if (presence.LastActiveAt is not { } lastActive)
            return null;
        var days = DateOnly.FromDateTime(now.LocalDateTime).DayNumber - DateOnly.FromDateTime(lastActive.LocalDateTime).DayNumber;
        return days switch
        {
            <= 0 => "today",
            1 => "yesterday",
            < 7 => $"{days} days ago",
            < 14 => "last week",
            _ => "a while ago",
        };
    }
}
