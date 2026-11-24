using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RSMatrix.Desktop.Chat;
using RSMatrix.Desktop.Content;

namespace RSMatrix.Desktop.ViewModels;

/// <summary>A room's main timeline or one of its threads: messages with a composer below.</summary>
public abstract partial class ConversationViewModel : ObservableObject
{
    protected ConversationViewModel(ChatViewModel chat, RoomMessages messages)
    {
        Chat = chat;
        Timeline = new RoomTimeline(messages, chat.GetToday);
    }

    protected ChatViewModel Chat { get; }

    public RoomTimeline Timeline { get; }

    public abstract RoomViewModel Room { get; }

    /// <summary>Null for a room's main timeline.</summary>
    public abstract string? ThreadRootId { get; }

    public bool IsThread => ThreadRootId != null;

    /// <summary>The message that started the thread, shown above the replies.</summary>
    public virtual MessageViewModel? ThreadRoot => null;

    public bool IsThreadRootMissing => IsThread && ThreadRoot == null;

    public abstract string Title { get; }

    public abstract string ComposerPlaceholder { get; }

    public RecentReactions RecentReactions => Chat.RecentReactions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    public partial int UnreadCount { get; set; }

    public bool HasUnread => UnreadCount > 0;

    /// <summary>Text in the composer, kept while switching.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial string Draft { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReplying))]
    public partial MessageViewModel? ReplyingTo { get; set; }

    public bool IsReplying => ReplyingTo != null;

    [ObservableProperty]
    public partial bool HasMoreHistory { get; protected set; }

    [ObservableProperty]
    public partial string? HistoryError { get; protected set; }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task LoadOlderMessagesAsync(CancellationToken cancellationToken)
    {
        if (!HasMoreHistory)
            return;
        try
        {
            await LoadOlderAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            HistoryError = ex.Message;
        }
    }

    protected virtual Task LoadOlderAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = NormalizeOutgoingText(Draft);
        if (text.Length == 0)
            return;
        Draft = "";
        var replyTo = ReplyingTo?.EventId;
        ReplyingTo = null;
        var message = new ChatMessage(Room.RoomId, "", Chat.CurrentUser, DateTimeOffset.Now, ChatMessageKind.Text,
            text, PlainTextParser.Parse(text), Room.IsEncrypted, ThreadRootId, replyTo);
        var pending = CreateMessageViewModel(message);
        pending.IsPending = true;
        Timeline.Append(pending);
        OnMessageAdded(message);
        await DeliverAsync(pending);
    }

    private bool CanSend() => !string.IsNullOrWhiteSpace(Draft);

    [RelayCommand]
    private Task RetryAsync(MessageViewModel? message)
        => message is { SendError: not null } ? DeliverAsync(message) : Task.CompletedTask;

    private async Task DeliverAsync(MessageViewModel pending)
    {
        pending.SendError = null;
        pending.IsPending = true;
        try
        {
            var eventId = await Chat.Session.SendTextAsync(Room.RoomId, pending.Body, pending.ReplyToEventId,
                pending.ThreadRootId, CancellationToken.None);
            Timeline.ConfirmSent(pending, eventId);
        }
        catch (Exception ex)
        {
            pending.IsPending = false;
            pending.SendError = ex.Message;
        }
    }

    [RelayCommand]
    private void StartReply(MessageViewModel? message)
    {
        if (message is not { IsSent: true })
            return;
        ReplyingTo = message;
        Chat.RequestComposerFocus();
    }

    [RelayCommand]
    private void CancelReply() => ReplyingTo = null;

    [RelayCommand]
    private void OpenThread(MessageViewModel? message)
    {
        if (message is { IsSent: true, ThreadRootId: null })
            Chat.SelectedConversation = Room.GetOrCreateThread(message.EventId);
    }

    [RelayCommand]
    private void ShowRoom() => Chat.SelectedConversation = Room;

    /// <summary>Adds a message from the live sync. Returns false for duplicates.</summary>
    internal bool AddLiveMessage(ChatMessage message)
    {
        var viewModel = CreateMessageViewModel(message);
        if (!Timeline.Append(viewModel))
            return false;
        OnMessageAdded(message);
        OnMessageShown(viewModel);
        return true;
    }

    /// <summary>Called for each message added to the timeline, sent or received.</summary>
    protected virtual void OnMessageAdded(ChatMessage message)
    {
    }

    /// <summary>Called for each received message once it is in the timeline.</summary>
    protected virtual void OnMessageShown(MessageViewModel message)
    {
    }

    internal MessageViewModel CreateMessageViewModel(ChatMessage message)
        => new(message, message.Sender.UserId == Chat.CurrentUser.UserId, RetryCommand, ToggleReactionAsync);

    // The change shows up once the session echoes it back.
    private async Task ToggleReactionAsync(MessageViewModel message, string key)
    {
        try
        {
            if (message.HasReacted(key))
            {
                await Chat.Session.RemoveReactionAsync(Room.RoomId, message.EventId, key, CancellationToken.None);
                return;
            }
            Chat.RecentReactions.Use(key);
            await Chat.Session.SendReactionAsync(Room.RoomId, message.EventId, key, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Chat.ReportIssue(ex.Message);
        }
    }

    internal static string NormalizeOutgoingText(string text)
        => text.ReplaceLineEndings("\n").Trim();

    internal static string FormatActivity(DateTimeOffset timestamp, DateTimeOffset now)
    {
        if (timestamp == default)
            return "";
        var local = timestamp.ToLocalTime();
        var localNow = now.ToLocalTime();
        if (local.Date == localNow.Date)
            return local.ToString("t");
        if (local.Date > localNow.Date.AddDays(-7))
            return local.ToString("ddd");
        return local.ToString("d");
    }
}
