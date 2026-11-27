using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RSMatrix.Desktop.Chat;

namespace RSMatrix.Desktop.ViewModels;

public sealed partial class ChatViewModel : ObservableObject, IAsyncDisposable
{
    private const int ThreadsShownPerRoom = 3;
    private readonly IChatSession _session;
    private readonly Action<string?> _onSessionEnded;
    private readonly Action? _onSettingsRequested;
    private readonly Func<DateTimeOffset> _getNow;
    private readonly Dictionary<string, RoomViewModel> _roomsById = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _isInitialSyncDone;
    private RoomViewModel? _membersRoom;

    /// <param name="onSessionEnded">Called with an error message, or null when the user disconnected.</param>
    public ChatViewModel(IChatSession session, Action<string?> onSessionEnded, Action? onSettingsRequested = null,
        Func<DateTimeOffset>? getNow = null)
    {
        _session = session;
        _onSessionEnded = onSessionEnded;
        _onSettingsRequested = onSettingsRequested;
        _getNow = getNow ?? (() => DateTimeOffset.Now);
        Media = new MediaLoader(session);
    }

    public ChatUser CurrentUser => _session.CurrentUser;

    internal IChatSession Session => _session;

    internal DateOnly GetToday() => DateOnly.FromDateTime(_getNow().LocalDateTime);

    public RecentReactions RecentReactions { get; } = new();

    /// <summary>Asks the view to put the keyboard focus into the composer.</summary>
    public event Action? ComposerFocusRequested;

    internal void RequestComposerFocus() => ComposerFocusRequested?.Invoke();

    internal void ReportIssue(string message) => Issue = message;

    public string ServerName => _session.ServerName;

    public MediaLoader Media { get; }

    /// <summary>Most recent activity first.</summary>
    public ObservableCollection<RoomViewModel> Rooms { get; } = [];

    /// <summary>
    /// The rooms, each followed by its most recently active threads and, when there are more,
    /// a <see cref="MoreThreadsViewModel"/>. The selected thread is always listed.
    /// </summary>
    public ObservableCollection<object> SidebarEntries { get; } = [];

    /// <summary>The room or thread shown on the right.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    public partial ConversationViewModel? SelectedConversation { get; set; }

    /// <summary>The selected room's name, or the server name when no room is selected.</summary>
    public string WindowTitle
    {
        get
        {
            if (SelectedConversation?.Room.Name is { Length: > 0 } roomName)
                return roomName;
            return string.IsNullOrEmpty(ServerName) ? MainWindowViewModel.DefaultWindowTitle : ServerName;
        }
    }

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

    /// <summary>Shows the members of the selected room on the right.</summary>
    [ObservableProperty]
    public partial bool IsMemberListOpen { get; set; }

    /// <summary>Members of the selected room while the member list is open: joined by name, then invited.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(JoinedMemberCount))]
    public partial IReadOnlyList<ChatMember> Members { get; private set; } = [];

    public int JoinedMemberCount => Members.Count(m => !m.IsInvited);

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
            case RoomJoined joined:
                AddRoom(joined.Room, joined.LatestMessage);
                break;
            case RoomUpdated updated when _roomsById.ContainsKey(updated.Room.RoomId):
                GetOrAddRoom(updated.Room);
                break;
            case RoomLeft left when _roomsById.TryGetValue(left.RoomId, out var room):
                RemoveRoom(room);
                break;
            case MessageReceived received:
                AddMessage(received.Room, received.Message);
                break;
            case ConnectionChanged changed:
                ConnectionState = changed.State;
                ConnectionDetail = changed.Detail;
                if (changed.State == ConnectionState.Online && !_isInitialSyncDone)
                {
                    _isInitialSyncDone = true;
                    SelectedConversation ??= Rooms.FirstOrDefault();
                }
                break;
            case ReactionReceived reaction when _roomsById.TryGetValue(reaction.RoomId, out var room):
                room.Timeline.Messages.AddReaction(reaction.TargetEventId, reaction.Key, reaction.Sender);
                break;
            case ReactionRemoved reaction when _roomsById.TryGetValue(reaction.RoomId, out var room):
                room.Timeline.Messages.RemoveReaction(reaction.TargetEventId, reaction.Key, reaction.Sender);
                break;
            case ReactionsChanged { Reactions: { } reactions } changed when _roomsById.TryGetValue(changed.RoomId, out var room):
                room.Timeline.Messages.SetReactions(changed.TargetEventId, reactions);
                break;
            case ReactionsChanged changed when _roomsById.TryGetValue(changed.RoomId, out var room) &&
                                                room.Timeline.Messages.Contains(changed.TargetEventId):
                _ = ReloadReactionsAsync(room, changed.TargetEventId);
                break;
            case ReceiptReceived receipt when _roomsById.TryGetValue(receipt.RoomId, out var room):
                room.Timeline.Messages.ApplyReceipt(receipt.EventId, receipt.User);
                break;
            case IssueReported issue:
                Issue = issue.Message;
                break;
        }
    }

    partial void OnSelectedConversationChanged(ConversationViewModel? value)
    {
        RefreshSidebar();
        if (IsMemberListOpen && value?.Room != _membersRoom)
            _ = LoadMembersAsync();
        if (value == null)
            return;
        value.UnreadCount = 0;
        // Fill the view with some history if the sync delivered only a few messages.
        if (value.Timeline.MessageCount < 20 && value.HasMoreHistory && value.LoadOlderMessagesCommand.CanExecute(null))
            value.LoadOlderMessagesCommand.Execute(null);
        RequestComposerFocus();
    }

    internal void RefreshSidebar()
    {
        var entries = new List<object>();
        foreach (var room in Rooms)
        {
            entries.Add(room);
            var active = room.ActiveThreads.ToList();
            var shown = active.Take(ThreadsShownPerRoom).ToList();
            if (SelectedConversation is ThreadViewModel selected && selected.Room == room && !shown.Contains(selected))
                shown.Add(selected);
            entries.AddRange(shown);
            room.MoreThreads.Threads = active.Except(shown).ToList();
            if (room.MoreThreads.Threads.Count > 0)
                entries.Add(room.MoreThreads);
        }
        CollectionSync.Apply(SidebarEntries, entries);
    }

    [RelayCommand]
    private void DismissIssue() => Issue = null;

    [RelayCommand]
    private void ToggleMemberList() => IsMemberListOpen = !IsMemberListOpen;

    partial void OnIsMemberListOpenChanged(bool value)
    {
        // Load again on every opening, since the list doesn't follow membership changes.
        _membersRoom = null;
        Members = [];
        if (value)
            _ = LoadMembersAsync();
    }

    private async Task ReloadReactionsAsync(RoomViewModel room, string eventId)
    {
        try
        {
            if (await _session.GetReactionsAsync(room.RoomId, eventId, _lifetime.Token) is { } reactions)
                room.Timeline.Messages.SetReactions(eventId, reactions);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Issue = $"The reactions of a message in {room.Name} could not be loaded: {ex.Message}";
        }
    }

    private async Task LoadMembersAsync()
    {
        var room = SelectedConversation?.Room;
        _membersRoom = room;
        Members = [];
        if (room == null)
            return;
        try
        {
            var members = await _session.GetMembersAsync(room.RoomId, _lifetime.Token);
            if (_membersRoom != room)
                return;
            Members = members.OrderBy(m => m.IsInvited)
                .ThenBy(m => m.User.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Issue = $"The members of {room.Name} could not be loaded: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenSettings() => _onSettingsRequested?.Invoke();

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

    private RoomViewModel GetOrAddRoom(ChatRoomInfo info)
    {
        if (!_roomsById.TryGetValue(info.RoomId, out var room))
        {
            room = new RoomViewModel(info, this);
            _roomsById.Add(info.RoomId, room);
            Rooms.Add(room);
            return room;
        }
        room.Update(info);
        if (room == SelectedConversation?.Room)
            OnPropertyChanged(nameof(WindowTitle));
        return room;
    }

    private void AddRoom(ChatRoomInfo info, ChatMessage? latestMessage)
    {
        var room = GetOrAddRoom(info);
        if (latestMessage != null)
            room.AddReceivedMessage(latestMessage);
        MoveToSortedPosition(room);
        RefreshSidebar();
    }

    private void RemoveRoom(RoomViewModel room)
    {
        _roomsById.Remove(room.RoomId);
        Rooms.Remove(room);
        if (SelectedConversation?.Room == room)
            SelectedConversation = Rooms.FirstOrDefault();
        RefreshSidebar();
    }

    private void AddMessage(ChatRoomInfo info, ChatMessage message)
    {
        var room = GetOrAddRoom(info);
        if (!room.AddReceivedMessage(message))
            return;
        ConversationViewModel conversation = message.ThreadRootId is { } rootId ? room.GetOrCreateThread(rootId) : room;
        // Without read markers from RSMatrix, only messages that arrive after the initial sync count as unread.
        if (_isInitialSyncDone && conversation != SelectedConversation && message.Sender.UserId != CurrentUser.UserId)
            conversation.UnreadCount++;
        MoveToSortedPosition(room);
        RefreshSidebar();
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
