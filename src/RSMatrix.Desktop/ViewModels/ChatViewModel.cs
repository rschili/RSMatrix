using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RSMatrix.Desktop.Chat;

namespace RSMatrix.Desktop.ViewModels;

public sealed partial class ChatViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IChatSession _session;
    private readonly Action<string?> _onSessionEnded;
    private readonly Func<DateTimeOffset> _getNow;
    private readonly Dictionary<string, RoomViewModel> _roomsById = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _isInitialSyncDone;

    /// <param name="onSessionEnded">Called with an error message, or null when the user disconnected.</param>
    public ChatViewModel(IChatSession session, Action<string?> onSessionEnded, Func<DateTimeOffset>? getNow = null)
    {
        _session = session;
        _onSessionEnded = onSessionEnded;
        _getNow = getNow ?? (() => DateTimeOffset.Now);
        Media = new MediaLoader(session);
    }

    public ChatUser CurrentUser => _session.CurrentUser;

    public string ServerName => _session.ServerName;

    public MediaLoader Media { get; }

    /// <summary>Most recent activity first.</summary>
    public ObservableCollection<RoomViewModel> Rooms { get; } = [];

    [ObservableProperty]
    public partial RoomViewModel? SelectedRoom { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionText), nameof(IsOnline), nameof(IsStopped))]
    public partial ConnectionState ConnectionState { get; private set; } = ConnectionState.Connecting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionText))]
    public partial string? ConnectionDetail { get; private set; }

    public bool IsOnline => ConnectionState == ConnectionState.Online;

    public bool IsStopped => ConnectionState == ConnectionState.Stopped;

    public string ConnectionText => ConnectionState switch
    {
        ConnectionState.Connecting => "Connecting…",
        ConnectionState.Online => "Online",
        ConnectionState.Reconnecting => "Reconnecting…",
        _ => ConnectionDetail == null ? "Disconnected" : $"Disconnected: {ConnectionDetail}",
    };

    /// <summary>The latest problem reported by the session, until dismissed.</summary>
    [ObservableProperty]
    public partial string? Issue { get; private set; }

    /// <summary>Reads session events until the session ends. Runs on the UI thread.</summary>
    public async Task RunAsync()
    {
        using var clock = new PeriodicTimer(TimeSpan.FromMinutes(1));
        _ = RefreshTimesAsync(clock);
        try
        {
            await foreach (var chatEvent in _session.ReadEventsAsync(_lifetime.Token))
                Apply(chatEvent);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    public void Apply(ChatEvent chatEvent)
    {
        switch (chatEvent)
        {
            case MessageReceived received:
                AddMessage(received.Room, received.Message);
                break;
            case ConnectionChanged changed:
                ConnectionState = changed.State;
                ConnectionDetail = changed.Detail;
                if (changed.State == ConnectionState.Online && !_isInitialSyncDone)
                {
                    _isInitialSyncDone = true;
                    SelectedRoom ??= Rooms.FirstOrDefault();
                }
                break;
            case IssueReported issue:
                Issue = issue.Message;
                break;
        }
    }

    partial void OnSelectedRoomChanged(RoomViewModel? value)
    {
        if (value == null)
            return;
        value.UnreadCount = 0;
        // Fill the view with some history if the sync delivered only a few messages.
        if (value.Timeline.MessageCount < 20 && value.HasMoreHistory && value.LoadOlderMessagesCommand.CanExecute(null))
            value.LoadOlderMessagesCommand.Execute(null);
    }

    [RelayCommand]
    private void DismissIssue() => Issue = null;

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        await DisposeAsync();
        _onSessionEnded(null);
    }

    [RelayCommand]
    private async Task ReturnToLoginAsync()
    {
        await DisposeAsync();
        _onSessionEnded(ConnectionDetail);
    }

    public async ValueTask DisposeAsync()
    {
        if (_lifetime.IsCancellationRequested)
            return;
        await _lifetime.CancelAsync();
        await _session.DisposeAsync();
        Media.Dispose();
    }

    private void AddMessage(ChatRoomInfo info, ChatMessage message)
    {
        if (!_roomsById.TryGetValue(info.RoomId, out var room))
        {
            room = new RoomViewModel(info, _session, () => DateOnly.FromDateTime(_getNow().LocalDateTime));
            _roomsById.Add(info.RoomId, room);
            Rooms.Add(room);
        }
        else
        {
            room.Update(info);
        }

        if (!room.AddLiveMessage(message))
            return;
        // Without read markers from RSMatrix, only messages that arrive after the initial sync count as unread.
        if (_isInitialSyncDone && room != SelectedRoom && message.Sender.UserId != CurrentUser.UserId)
            room.UnreadCount++;
        MoveToSortedPosition(room);
    }

    private void MoveToSortedPosition(RoomViewModel room)
    {
        var oldIndex = Rooms.IndexOf(room);
        var newIndex = Rooms.Count(r => r != room && r.LastActivity >= room.LastActivity);
        if (newIndex != oldIndex)
            Rooms.Move(oldIndex, newIndex);
    }

    private async Task RefreshTimesAsync(PeriodicTimer clock)
    {
        try
        {
            while (await clock.WaitForNextTickAsync(_lifetime.Token))
            {
                foreach (var room in Rooms)
                    room.RefreshTimes();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
