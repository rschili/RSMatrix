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
    public MessageViewModel(ChatMessage message, bool isOwn, IAsyncRelayCommand<MessageViewModel?>? retryCommand = null)
    {
        EventId = message.EventId;
        Sender = message.Sender;
        Timestamp = message.Timestamp;
        Kind = message.Kind;
        Body = message.Body;
        Content = message.Kind == ChatMessageKind.Emote ? PrefixEmote(message.Sender, message.Content) : message.Content;
        IsEncrypted = message.IsEncrypted;
        IsOwn = isOwn;
        RetryCommand = retryCommand;
    }

    /// <summary>Empty while a sent message waits for the server.</summary>
    [ObservableProperty]
    public partial string EventId { get; set; }

    public ChatUser Sender { get; }

    public DateTimeOffset Timestamp { get; }

    public ChatMessageKind Kind { get; }

    public string Body { get; }

    public MessageDocument Content { get; }

    public bool IsEncrypted { get; }

    public bool IsOwn { get; }

    public bool IsNotice => Kind == ChatMessageKind.Notice;

    public bool IsEmote => Kind == ChatMessageKind.Emote;

    public DateOnly LocalDate => DateOnly.FromDateTime(Timestamp.LocalDateTime);

    public string TimeText => Timestamp.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    public string FullTimeText => Timestamp.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);

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

    /// <summary>Emotes read as "* Bob waves".</summary>
    private static MessageDocument PrefixEmote(ChatUser sender, MessageDocument content)
    {
        var prefix = new TextInline($"* {sender.DisplayName} ", InlineStyle.Italic);
        if (content.Blocks.Count > 0 && content.Blocks[0] is ParagraphBlock first)
            return new MessageDocument([new ParagraphBlock([prefix, .. first.Inlines]), .. content.Blocks.Skip(1)]);
        return new MessageDocument([new ParagraphBlock([prefix]), .. content.Blocks]);
    }
}

/// <summary>
/// The messages of one room in display order, with day separators and sender grouping.
/// Live messages are appended, history is prepended, and duplicates by event ID are dropped.
/// </summary>
public sealed class RoomTimeline(Func<DateOnly> getToday)
{
    private static readonly TimeSpan GroupingWindow = TimeSpan.FromMinutes(5);
    private readonly Dictionary<string, MessageViewModel> _messagesByEventId = new(StringComparer.Ordinal);

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
        if (_messagesByEventId.ContainsKey(eventId))
        {
            Remove(pending);
            return;
        }
        pending.EventId = eventId;
        _messagesByEventId[eventId] = pending;
    }

    public bool Contains(string eventId) => _messagesByEventId.ContainsKey(eventId);

    private bool Register(MessageViewModel message)
    {
        if (message.EventId.Length == 0)
        {
            MessageCount++;
            return true; // Pending messages get their ID later.
        }
        if (!_messagesByEventId.TryAdd(message.EventId, message))
            return false;
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
           previous.IsNotice != message.IsNotice;
}
