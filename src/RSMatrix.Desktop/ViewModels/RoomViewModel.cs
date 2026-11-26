using CommunityToolkit.Mvvm.ComponentModel;
using RSMatrix.Desktop.Chat;
using RSMatrix.Desktop.Content;

namespace RSMatrix.Desktop.ViewModels;

public sealed partial class RoomViewModel : ConversationViewModel
{
    private const int HistoryPageSize = 30;
    private readonly Dictionary<string, ThreadViewModel> _threadsByRootId = new(StringComparer.Ordinal);
    private string? _oldestLoadedEventId;

    public RoomViewModel(ChatRoomInfo info, ChatViewModel chat)
        : base(chat, new RoomMessages(chat.CurrentUser.UserId))
    {
        RoomId = info.RoomId;
        HasMoreHistory = true;
        MoreThreads = new MoreThreadsViewModel(this);
        Update(info);
    }

    public string RoomId { get; }

    public override RoomViewModel Room => this;

    public override string? ThreadRootId => null;

    public override string Title => Name;

    public override string ComposerPlaceholder => IsDirect ? $"Message {Name}" : $"Message #{Name}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial string Name { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsEncrypted { get; private set; }

    [ObservableProperty]
    public partial bool IsDirect { get; private set; }

    [ObservableProperty]
    public partial string LastMessagePreview { get; private set; } = "";

    [ObservableProperty]
    public partial DateTimeOffset LastActivity { get; private set; }

    /// <summary>Threads with at least one reply, most recently active first.</summary>
    public IEnumerable<ThreadViewModel> ActiveThreads
        => _threadsByRootId.Values.Where(t => t.HasReplies).OrderByDescending(t => t.LastActivity);

    /// <summary>The sidebar entry for threads that don't fit below the room.</summary>
    public MoreThreadsViewModel MoreThreads { get; }

    public void Update(ChatRoomInfo info)
    {
        Name = info.Name;
        IsEncrypted = info.IsEncrypted;
        IsDirect = info.IsDirect;
        OnPropertyChanged(nameof(ComposerPlaceholder));
    }

    public ThreadViewModel GetOrCreateThread(string rootEventId)
    {
        if (_threadsByRootId.TryGetValue(rootEventId, out var thread))
            return thread;
        thread = new ThreadViewModel(this, rootEventId, Chat);
        _threadsByRootId.Add(rootEventId, thread);
        if (Timeline.Messages.Find(rootEventId) is { } root)
            LinkThread(root, thread);
        return thread;
    }

    /// <summary>Adds a message to the main timeline or its thread. Returns false for duplicates.</summary>
    internal bool AddReceivedMessage(ChatMessage message)
        => message.ThreadRootId is { } rootId
            ? GetOrCreateThread(rootId).AddLiveMessage(message)
            : AddLiveMessage(message);

    public void RefreshTimes()
    {
        foreach (var thread in _threadsByRootId.Values)
            thread.RefreshTimes();
    }

    protected override async Task LoadOlderAsync(CancellationToken cancellationToken)
    {
        // Pages can come back without text messages for the main timeline, so keep going until something new shows up.
        var added = 0;
        while (added == 0 && HasMoreHistory)
        {
            var page = await Chat.Session.LoadHistoryAsync(RoomId, _oldestLoadedEventId, HistoryPageSize, cancellationToken);
            if (page.Messages.Count > 0)
                _oldestLoadedEventId = page.Messages[0].EventId;
            HasMoreHistory = page.HasMore;

            var mainMessages = page.Messages.Where(m => m.ThreadRootId == null).ToList();
            var mainViewModels = mainMessages.Select(CreateMessageViewModel).ToList();
            added = Timeline.Prepend(mainViewModels);
            foreach (var message in mainViewModels)
                OnMessageShown(message);
            if (mainMessages.Count > 0)
                OnMessageAdded(mainMessages[^1]);

            foreach (var threadMessages in page.Messages.Where(m => m.ThreadRootId != null).GroupBy(m => m.ThreadRootId!))
                GetOrCreateThread(threadMessages.Key).AddOlderMessages(threadMessages.ToList());
        }
        Chat.RefreshSidebar();
    }

    protected override void OnMessageAdded(ChatMessage message)
    {
        if (message.Timestamp < LastActivity)
            return;
        LastActivity = message.Timestamp;
        var preview = message.Kind == ChatMessageKind.Image ? "Image" : PlainTextParser.ToPreview(message.Body);
        var isMine = message.Sender.UserId == Chat.CurrentUser.UserId;
        LastMessagePreview = IsDirect && !isMine ? preview : $"{(isMine ? "You" : message.Sender.DisplayName)}: {preview}";
    }

    protected override void OnMessageShown(MessageViewModel message)
    {
        if (_threadsByRootId.TryGetValue(message.EventId, out var thread))
            LinkThread(message, thread);
    }

    private static void LinkThread(MessageViewModel root, ThreadViewModel thread)
    {
        root.Thread = thread;
        thread.Root = root;
    }
}
