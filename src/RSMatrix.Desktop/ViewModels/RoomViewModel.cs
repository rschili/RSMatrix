using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RSMatrix.Desktop.Chat;
using RSMatrix.Desktop.Content;

namespace RSMatrix.Desktop.ViewModels;

public sealed partial class RoomViewModel : ObservableObject
{
    private const int HistoryPageSize = 30;
    private readonly IChatSession _session;
    private string? _historyToken;

    public RoomViewModel(ChatRoomInfo info, IChatSession session, Func<DateOnly> getToday)
    {
        _session = session;
        RoomId = info.RoomId;
        Timeline = new RoomTimeline(getToday);
        Update(info);
    }

    public string RoomId { get; }

    public RoomTimeline Timeline { get; }

    [ObservableProperty]
    public partial string Name { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsEncrypted { get; private set; }

    [ObservableProperty]
    public partial bool IsDirect { get; private set; }

    [ObservableProperty]
    public partial string LastMessagePreview { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastActivityText))]
    public partial DateTimeOffset LastActivity { get; private set; }

    public string LastActivityText => FormatActivity(LastActivity, DateTimeOffset.Now);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    public partial int UnreadCount { get; set; }

    public bool HasUnread => UnreadCount > 0;

    /// <summary>Text in the composer, kept per room while switching.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial string Draft { get; set; } = "";

    [ObservableProperty]
    public partial bool HasMoreHistory { get; private set; } = true;

    public string ComposerPlaceholder => IsDirect ? $"Message {Name}" : $"Message #{Name}";

    public void Update(ChatRoomInfo info)
    {
        Name = info.Name;
        IsEncrypted = info.IsEncrypted;
        IsDirect = info.IsDirect;
        OnPropertyChanged(nameof(ComposerPlaceholder));
    }

    /// <summary>Adds a message from the live sync. Returns false for duplicates.</summary>
    public bool AddLiveMessage(ChatMessage message)
    {
        var viewModel = CreateMessageViewModel(message);
        if (!Timeline.Append(viewModel))
            return false;
        UpdateLastActivity(message);
        return true;
    }

    public void RefreshTimes() => OnPropertyChanged(nameof(LastActivityText));

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = NormalizeOutgoingText(Draft);
        if (text.Length == 0)
            return;
        Draft = "";
        var message = new ChatMessage(RoomId, "", _session.CurrentUser, DateTimeOffset.Now, ChatMessageKind.Text,
            text, PlainTextParser.Parse(text), IsEncrypted);
        var pending = CreateMessageViewModel(message);
        pending.IsPending = true;
        Timeline.Append(pending);
        UpdateLastActivity(message);
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
            var eventId = await _session.SendTextAsync(RoomId, pending.Body, CancellationToken.None);
            Timeline.ConfirmSent(pending, eventId);
        }
        catch (Exception ex)
        {
            pending.IsPending = false;
            pending.SendError = ex.Message;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task LoadOlderMessagesAsync(CancellationToken cancellationToken)
    {
        if (!HasMoreHistory)
            return;
        try
        {
            // Pages can come back without text messages, so keep going until something new shows up.
            var added = 0;
            while (added == 0 && HasMoreHistory)
            {
                var page = await _session.LoadHistoryAsync(RoomId, _historyToken, HistoryPageSize, cancellationToken);
                _historyToken = page.NextToken;
                HasMoreHistory = page.NextToken != null;
                added = Timeline.Prepend(page.Messages.Select(CreateMessageViewModel).ToList());
                if (Timeline.LastMessage == null && page.Messages.Count > 0)
                    UpdateLastActivity(page.Messages[^1]);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            HistoryError = ex.Message;
        }
    }

    [ObservableProperty]
    public partial string? HistoryError { get; private set; }

    private MessageViewModel CreateMessageViewModel(ChatMessage message)
        => new(message, message.Sender.UserId == _session.CurrentUser.UserId, RetryCommand);

    private void UpdateLastActivity(ChatMessage message)
    {
        if (message.Timestamp < LastActivity)
            return;
        LastActivity = message.Timestamp;
        var preview = message.Kind == ChatMessageKind.Image ? "Image" : PlainTextParser.ToPreview(message.Body);
        LastMessagePreview = IsDirect && message.Sender.UserId != _session.CurrentUser.UserId
            ? preview
            : $"{(message.Sender.UserId == _session.CurrentUser.UserId ? "You" : message.Sender.DisplayName)}: {preview}";
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
