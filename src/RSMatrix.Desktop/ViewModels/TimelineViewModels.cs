using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RSMatrix.Desktop.Chat;
using RSMatrix.Desktop.Content;

namespace RSMatrix.Desktop.ViewModels;

public abstract class TimelineItemViewModel : ObservableObject;

public sealed class DaySeparatorViewModel(DateOnly date, DateOnly today) : TimelineItemViewModel
{
    public DateOnly Date { get; } = date;

    public string Text { get; } = FormatDay(date, today);

    internal static string FormatDay(DateOnly date, DateOnly today)
    {
        if (date == today)
            return "Today";
        if (date == today.AddDays(-1))
            return "Yesterday";
        var culture = CultureInfo.CurrentCulture;
        return date.Year == today.Year
            ? $"{date.ToString("dddd", culture)}, {date.ToString(culture.DateTimeFormat.MonthDayPattern, culture)}"
            : date.ToString("D", culture);
    }
}

public sealed partial class MessageViewModel : TimelineItemViewModel
{
    private const int MaxReaderAvatars = 3;
    private readonly Func<MessageViewModel, string, Task>? _toggleReaction;

    /// <param name="toggleReaction">Adds or takes back the current user's reaction with the given key.</param>
    public MessageViewModel(ChatMessage message, string currentUserId, IAsyncRelayCommand<MessageViewModel?>? retryCommand = null,
        Func<MessageViewModel, string, Task>? toggleReaction = null)
    {
        _toggleReaction = toggleReaction;
        EventId = message.EventId;
        Sender = message.Sender;
        Timestamp = message.Timestamp;
        Kind = message.Kind;
        Body = message.Body;
        Content = message.Kind == ChatMessageKind.Emote ? PrefixEmote(message.Sender, message.Content) : message.Content;
        IsEncrypted = message.IsEncrypted;
        ThreadRootId = message.ThreadRootId;
        ReplyToEventId = message.ReplyToEventId;
        Source = message.Source;
        IsOwn = message.Sender.UserId == currentUserId;
        RetryCommand = retryCommand;
        SetReactions(message.Reactions ?? [], currentUserId);
    }

    /// <summary>Empty while a sent message waits for the server.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSent))]
    [NotifyCanExecuteChangedFor(nameof(ToggleReactionCommand))]
    public partial string EventId { get; set; }

    public ChatUser Sender { get; }

    public DateTimeOffset Timestamp { get; }

    public ChatMessageKind Kind { get; }

    public string Body { get; }

    public MessageDocument Content { get; }

    public bool IsEncrypted { get; }

    public string? ThreadRootId { get; }

    public string? ReplyToEventId { get; }

    /// <summary>The event as JSON, when the session has it.</summary>
    public string? Source { get; }

    public bool IsOwn { get; }

    public bool IsSent => EventId.Length > 0;

    public bool IsNotice => Kind == ChatMessageKind.Notice;

    public bool IsEmote => Kind == ChatMessageKind.Emote;

    public bool IsReply => ReplyToEventId != null;

    public DateOnly LocalDate => DateOnly.FromDateTime(Timestamp.LocalDateTime);

    public string TimeText => Timestamp.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    /// <summary>A time about as wide as any <see cref="TimeText"/>, to size a column of times.</summary>
    public static string WidestTimeText { get; } = new DateTime(2000, 1, 1, 10, 08, 0).ToString("t", CultureInfo.CurrentCulture);

    public string FullTimeText => Timestamp.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);

    public string PreviewText => Kind == ChatMessageKind.Image ? "Image" : PlainTextParser.ToPreview(Body);

    /// <summary>First message of a run from the same sender. Only these show the sender's name and avatar.</summary>
    [ObservableProperty]
    public partial bool IsGroupStart { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial bool IsPending { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string? SendError { get; set; }

    public string? StatusText => SendError != null ? "Not sent" : IsPending ? "Sending…" : null;

    public IAsyncRelayCommand<MessageViewModel?>? RetryCommand { get; }

    /// <summary>The message this one replies to, once it is loaded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReplyToName), nameof(ReplyToPreview))]
    public partial MessageViewModel? ReplyTo { get; internal set; }

    public string ReplyToName => ReplyTo?.Sender.DisplayName ?? "";

    public string ReplyToPreview => ReplyTo?.PreviewText ?? "Original message isn't loaded";

    /// <summary>The thread started at this message, if any.</summary>
    [ObservableProperty]
    public partial ThreadViewModel? Thread { get; internal set; }

    /// <summary>In the order they were first used.</summary>
    public ObservableCollection<ReactionViewModel> Reactions { get; } = [];

    /// <summary>Other users whose read receipt is at this message.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReaders), nameof(ReaderAvatars), nameof(ReaderOverflowText), nameof(ReadersToolTip))]
    public partial IReadOnlyList<ChatUser> ReadBy { get; private set; } = [];

    public bool HasReaders => ReadBy.Count > 0;

    public IReadOnlyList<ChatUser> ReaderAvatars => ReadBy.Take(MaxReaderAvatars).ToList();

    public string? ReaderOverflowText => ReadBy.Count > MaxReaderAvatars ? $"+{ReadBy.Count - MaxReaderAvatars}" : null;

    public string ReadersToolTip => ReadBy.Count == 1
        ? $"Seen by {ReadBy[0].DisplayName}"
        : $"Seen by {ReadBy.Count} people\n{JoinNames(ReadBy)}";

    public bool HasReacted(string key) => Reactions.Any(r => r.Key == key && r.IncludesMe);

    [RelayCommand(CanExecute = nameof(CanToggleReaction))]
    private Task ToggleReactionAsync(string? key)
        => key == null || _toggleReaction == null ? Task.CompletedTask : _toggleReaction(this, key);

    private bool CanToggleReaction() => _toggleReaction != null && IsSent;

    internal void AddReaction(string key, ChatUser sender, string currentUserId)
    {
        var index = IndexOfReaction(key);
        if (index < 0)
        {
            Reactions.Add(new ReactionViewModel(key, [sender], currentUserId));
            return;
        }
        var existing = Reactions[index];
        if (existing.Senders.Any(s => s.UserId == sender.UserId))
            return;
        Reactions[index] = new ReactionViewModel(key, [.. existing.Senders, sender], currentUserId);
    }

    internal void RemoveReaction(string key, string senderId, string currentUserId)
    {
        var index = IndexOfReaction(key);
        if (index < 0)
            return;
        var senders = Reactions[index].Senders.Where(s => s.UserId != senderId).ToList();
        if (senders.Count == 0)
            Reactions.RemoveAt(index);
        else
            Reactions[index] = new ReactionViewModel(key, senders, currentUserId);
    }

    internal void SetReactions(IReadOnlyList<ChatReaction> reactions, string currentUserId)
    {
        Reactions.Clear();
        foreach (var reaction in reactions)
            Reactions.Add(new ReactionViewModel(reaction.Key, reaction.Users, currentUserId));
    }

    internal void AddReader(ChatUser user) => ReadBy = [.. ReadBy.Where(u => u.UserId != user.UserId), user];

    internal void RemoveReader(string userId) => ReadBy = ReadBy.Where(u => u.UserId != userId).ToList();

    /// <summary>"Alice", "Alice and Bob", "Alice, Bob and Carol".</summary>
    internal static string JoinNames(IReadOnlyList<ChatUser> users)
        => users.Count == 1
            ? users[0].DisplayName
            : $"{string.Join(", ", users.Take(users.Count - 1).Select(u => u.DisplayName))} and {users[^1].DisplayName}";

    private int IndexOfReaction(string key)
    {
        for (var i = 0; i < Reactions.Count; i++)
        {
            if (Reactions[i].Key == key)
                return i;
        }
        return -1;
    }

    /// <summary>Emotes read as "* Bob waves".</summary>
    private static MessageDocument PrefixEmote(ChatUser sender, MessageDocument content)
    {
        var prefix = new TextInline($"* {sender.DisplayName} ", InlineStyle.Italic);
        if (content.Blocks.Count > 0 && content.Blocks[0] is ParagraphBlock first)
            return new MessageDocument([new ParagraphBlock([prefix, .. first.Inlines]), .. content.Blocks.Skip(1)]);
        return new MessageDocument([new ParagraphBlock([prefix]), .. content.Blocks]);
    }
}

/// <summary>All users who reacted to a message with the same key.</summary>
public sealed class ReactionViewModel(string key, IReadOnlyList<ChatUser> senders, string currentUserId)
{
    public string Key { get; } = key;

    public IReadOnlyList<ChatUser> Senders { get; } = senders;

    public int Count => Senders.Count;

    public bool IncludesMe { get; } = senders.Any(s => s.UserId == currentUserId);

    public string ToolTipText => $"{MessageViewModel.JoinNames(Senders)} reacted with {Key}";
}

/// <summary>Reactions offered in the picker. Matrix allows any text as a key.</summary>
public static class ReactionChoices
{
    public static IReadOnlyList<string> All { get; } =
    [
        "👍", "👎", "❤️", "😂", "😮", "😢", "😡", "🎉",
        "🙏", "👀", "🔥", "✅", "❌", "💯", "🚀", "🤔",
        "😅", "😍", "🥳", "👏", "🍪", "☕", "🍕", "🐛",
    ];
}

/// <summary>The reactions offered when hovering a message: the last three used, starting with common ones.</summary>
public sealed class RecentReactions
{
    public ObservableCollection<string> Keys { get; } = ["👍", "❤️", "😂"];

    public void Use(string key)
    {
        var index = Keys.IndexOf(key);
        if (index > 0)
        {
            Keys.Move(index, 0);
        }
        else if (index < 0)
        {
            Keys.Insert(0, key);
            Keys.RemoveAt(Keys.Count - 1);
        }
    }
}

/// <summary>
/// The messages of one room by event ID, from its main timeline and its threads. Reactions, read receipts
/// and reply targets are attached to their message here. Receipts and replies wait for their message when it
/// isn't loaded yet. Reactions don't need to: a message from history brings its own.
/// </summary>
public sealed class RoomMessages(string currentUserId)
{
    private readonly Dictionary<string, MessageViewModel> _messagesByEventId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<MessageViewModel>> _waitingRepliesByEventId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string EventId, ChatUser User)> _receiptsByUserId = new(StringComparer.Ordinal);

    public bool Contains(string eventId) => _messagesByEventId.ContainsKey(eventId);

    public MessageViewModel? Find(string eventId) => _messagesByEventId.GetValueOrDefault(eventId);

    public void AddReaction(string targetEventId, string key, ChatUser sender)
    {
        if (_messagesByEventId.TryGetValue(targetEventId, out var message))
            message.AddReaction(key, sender, currentUserId);
    }

    public void RemoveReaction(string targetEventId, string key, ChatUser sender)
    {
        if (_messagesByEventId.TryGetValue(targetEventId, out var message))
            message.RemoveReaction(key, sender.UserId, currentUserId);
    }

    public void SetReactions(string targetEventId, IReadOnlyList<ChatReaction> reactions)
    {
        if (_messagesByEventId.TryGetValue(targetEventId, out var message))
            message.SetReactions(reactions, currentUserId);
    }

    /// <summary>Moves the user's avatar to the message they read last. The current user's receipts are not shown.</summary>
    public void ApplyReceipt(string eventId, ChatUser user)
    {
        if (user.UserId == currentUserId)
            return;
        if (_receiptsByUserId.TryGetValue(user.UserId, out var previous) &&
            _messagesByEventId.TryGetValue(previous.EventId, out var previousMessage))
            previousMessage.RemoveReader(user.UserId);
        _receiptsByUserId[user.UserId] = (eventId, user);
        if (_messagesByEventId.TryGetValue(eventId, out var message))
            message.AddReader(user);
    }

    /// <summary>Links a reply to its target, now or once the target is added.</summary>
    internal void ResolveReply(MessageViewModel message)
    {
        if (message.ReplyToEventId is not { } targetId || message.ReplyTo != null)
            return;
        if (_messagesByEventId.TryGetValue(targetId, out var target))
        {
            message.ReplyTo = target;
            return;
        }
        if (!_waitingRepliesByEventId.TryGetValue(targetId, out var waiting))
            _waitingRepliesByEventId[targetId] = waiting = [];
        waiting.Add(message);
    }

    /// <summary>Returns false when a message with the same event ID is already there.</summary>
    internal bool TryAdd(MessageViewModel message)
    {
        if (!_messagesByEventId.TryAdd(message.EventId, message))
            return false;
        if (_waitingRepliesByEventId.Remove(message.EventId, out var replies))
        {
            foreach (var reply in replies)
                reply.ReplyTo = message;
        }
        foreach (var (eventId, user) in _receiptsByUserId.Values)
        {
            if (eventId == message.EventId)
                message.AddReader(user);
        }
        return true;
    }
}

/// <summary>
/// The messages of a room or thread in display order, with day separators and sender grouping.
/// Live messages are appended, history is prepended, and duplicates by event ID are dropped.
/// </summary>
public sealed class RoomTimeline(RoomMessages messages, Func<DateOnly> getToday)
{
    private static readonly TimeSpan GroupingWindow = TimeSpan.FromMinutes(5);

    public RoomMessages Messages { get; } = messages;

    public ObservableCollection<TimelineItemViewModel> Items { get; } = [];

    public int MessageCount { get; private set; }

    public MessageViewModel? LastMessage => Items.Count > 0 ? Items[^1] as MessageViewModel : null;

    /// <summary>Adds a message from the live sync. Returns false when it was already there.</summary>
    public bool Append(MessageViewModel message)
    {
        if (!Register(message))
            return false;

        var previous = FindMessage(Items.Count - 1, -1);
        if (previous == null || previous.LocalDate != message.LocalDate)
            Items.Add(new DaySeparatorViewModel(message.LocalDate, getToday()));
        message.IsGroupStart = StartsGroup(previous, message);
        Items.Add(message);
        return true;
    }

    /// <summary>Adds older messages, oldest first, above the existing ones. Returns how many were new.</summary>
    public int Prepend(IReadOnlyList<MessageViewModel> olderMessages)
    {
        var prefix = new List<TimelineItemViewModel>();
        MessageViewModel? previous = null;
        foreach (var message in olderMessages)
        {
            if (!Register(message))
                continue;
            if (previous == null || previous.LocalDate != message.LocalDate)
                prefix.Add(new DaySeparatorViewModel(message.LocalDate, getToday()));
            message.IsGroupStart = StartsGroup(previous, message);
            prefix.Add(message);
            previous = message;
        }
        if (prefix.Count == 0)
            return 0;

        // The existing first day separator is redundant when the older messages end on the same day.
        if (Items.Count > 0 && Items[0] is DaySeparatorViewModel separator && separator.Date == previous!.LocalDate)
            Items.RemoveAt(0);
        if (FindMessage(0, 1) is { } firstExisting)
            firstExisting.IsGroupStart = StartsGroup(previous, firstExisting);

        for (var i = 0; i < prefix.Count; i++)
            Items.Insert(i, prefix[i]);
        return prefix.Count(item => item is MessageViewModel);
    }

    /// <summary>Records the event ID of a sent message. Drops it if the sync already delivered the echo.</summary>
    public void ConfirmSent(MessageViewModel pending, string eventId)
    {
        pending.IsPending = false;
        if (Messages.Contains(eventId))
        {
            Remove(pending);
            return;
        }
        pending.EventId = eventId;
        Messages.TryAdd(pending);
    }

    private bool Register(MessageViewModel message)
    {
        // Pending messages get their ID later.
        if (message.EventId.Length > 0 && !Messages.TryAdd(message))
            return false;
        Messages.ResolveReply(message);
        MessageCount++;
        return true;
    }

    private void Remove(MessageViewModel message)
    {
        var index = Items.IndexOf(message);
        if (index < 0)
            return;
        Items.RemoveAt(index);
        MessageCount--;
        // Drop a day separator that has no messages left below it.
        if (index > 0 && Items[index - 1] is DaySeparatorViewModel && (index == Items.Count || Items[index] is DaySeparatorViewModel))
            Items.RemoveAt(index - 1);
        else if (index < Items.Count && Items[index] is MessageViewModel next)
            next.IsGroupStart = StartsGroup(FindMessage(index - 1, -1), next);
    }

    private MessageViewModel? FindMessage(int start, int step)
    {
        for (var i = start; i >= 0 && i < Items.Count; i += step)
        {
            if (Items[i] is MessageViewModel message)
                return message;
        }
        return null;
    }

    private static bool StartsGroup(MessageViewModel? previous, MessageViewModel message)
        => previous == null ||
           previous.Sender.UserId != message.Sender.UserId ||
           previous.LocalDate != message.LocalDate ||
           message.Timestamp - previous.Timestamp > GroupingWindow ||
           previous.IsNotice != message.IsNotice ||
           message.IsReply;
}
