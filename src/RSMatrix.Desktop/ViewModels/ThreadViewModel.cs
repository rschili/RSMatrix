using CommunityToolkit.Mvvm.ComponentModel;
using RSMatrix.Desktop.Chat;

namespace RSMatrix.Desktop.ViewModels;

/// <summary>The replies to one message, shown as their own conversation below the room.</summary>
public sealed partial class ThreadViewModel : ConversationViewModel
{
    public ThreadViewModel(RoomViewModel room, string rootEventId, ChatViewModel chat)
        : base(chat, room.Timeline.Messages)
    {
        Room = room;
        RootEventId = rootEventId;
    }

    public override RoomViewModel Room { get; }

    public string RootEventId { get; }

    public override string? ThreadRootId => RootEventId;

    public override string Title => Root?.PreviewText ?? "Thread";

    public override string ComposerPlaceholder => "Reply in thread";

    /// <summary>The message that started the thread, once it is loaded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(ThreadRoot), nameof(IsThreadRootMissing))]
    public partial MessageViewModel? Root { get; internal set; }

    public override MessageViewModel? ThreadRoot => Root;

    public int ReplyCount => Timeline.MessageCount;

    public bool HasReplies => ReplyCount > 0;

    public string ReplyCountText => ReplyCount == 1 ? "1 reply" : $"{ReplyCount} replies";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastReplyText))]
    public partial DateTimeOffset LastActivity { get; private set; }

    public string LastReplyText => $"Last reply {FormatActivity(LastActivity, DateTimeOffset.Now)}";

    internal void AddOlderMessages(IReadOnlyList<ChatMessage> messages)
    {
        if (Timeline.Prepend(messages.Select(CreateMessageViewModel).ToList()) == 0)
            return;
        OnMessageAdded(messages[^1]);
    }

    internal void RefreshTimes() => OnPropertyChanged(nameof(LastReplyText));

    protected override void OnMessageAdded(ChatMessage message)
    {
        OnPropertyChanged(nameof(ReplyCount));
        OnPropertyChanged(nameof(HasReplies));
        OnPropertyChanged(nameof(ReplyCountText));
        if (message.Timestamp > LastActivity)
            LastActivity = message.Timestamp;
    }
}

/// <summary>Sidebar entry that opens the threads of a room that don't fit below it.</summary>
public sealed partial class MoreThreadsViewModel(RoomViewModel room) : ObservableObject
{
    public RoomViewModel Room { get; } = room;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text))]
    public partial IReadOnlyList<ThreadViewModel> Threads { get; internal set; } = [];

    public string Text => Threads.Count == 1 ? "1 more thread" : $"{Threads.Count} more threads";
}
