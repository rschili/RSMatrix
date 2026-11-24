using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using RSMatrix.Desktop.ViewModels;

namespace RSMatrix.Desktop.Views;

/// <summary>
/// Keeps the timeline at the newest message while the reader is there, keeps the visible messages in place
/// when older ones are added above, and loads older messages when scrolling near the top.
/// </summary>
public partial class TimelineView : UserControl
{
    private const double BottomTolerance = 40;
    private const double LoadOlderThreshold = 400;

    private RoomViewModel? _room;
    private bool _isAtBottom = true;
    private bool _isRestorePending;
    // The first visible message and its distance from the top of the viewport, as of the last scroll.
    private (MessageViewModel Item, double Top)? _anchor;

    public TimelineView()
    {
        InitializeComponent();
        Scroller.ScrollChanged += OnScrollChanged;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_room != null)
            _room.Timeline.Items.CollectionChanged -= OnItemsChanged;
        _room = DataContext as RoomViewModel;
        if (_room == null)
            return;
        _room.Timeline.Items.CollectionChanged += OnItemsChanged;
        _isAtBottom = true;
        _anchor = null;
        ScrollToEndAfterLayout();
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var count = _room?.Timeline.Items.Count ?? 0;
        var isAppend = e.Action == NotifyCollectionChangedAction.Add && e.NewStartingIndex >= count - e.NewItems!.Count;
        if (isAppend)
        {
            if (_isAtBottom)
                ScrollToEndAfterLayout();
            return;
        }
        if (_isRestorePending)
            return;
        _isRestorePending = true;
        Dispatcher.UIThread.Post(BringAnchorIntoView, DispatcherPriority.Loaded);
    }

    private void BringAnchorIntoView()
    {
        var index = _anchor is { } anchor ? Items.Items.IndexOf(anchor.Item) : -1;
        if (_isAtBottom || index < 0)
        {
            _isRestorePending = false;
            if (_isAtBottom)
                Scroller.ScrollToEnd();
            return;
        }
        // The anchor moved down by the inserted items and may not be realized anymore.
        Items.ScrollIntoView(index);
        Dispatcher.UIThread.Post(RestoreAnchorPosition, DispatcherPriority.Loaded);
    }

    private void RestoreAnchorPosition()
    {
        _isRestorePending = false;
        if (_anchor is not { } anchor || Items.ContainerFromItem(anchor.Item) is not { } container ||
            container.TranslatePoint(default, Scroller) is not { } position)
            return;
        Scroller.Offset = new Vector(Scroller.Offset.X, Math.Max(0, Scroller.Offset.Y + position.Y - anchor.Top));
    }

    private void ScrollToEndAfterLayout() => Dispatcher.UIThread.Post(() => Scroller.ScrollToEnd(), DispatcherPriority.Loaded);

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_isRestorePending)
            return;

        var offset = Scroller.Offset.Y;
        var extent = Scroller.Extent.Height;
        var viewport = Scroller.Viewport.Height;

        // Size changes without scrolling, like a growing composer or rewrapped text, keep the reader at the bottom.
        if (_isAtBottom && e.OffsetDelta.Y == 0 && offset < extent - viewport - 1)
        {
            ScrollToEndAfterLayout();
            return;
        }

        _isAtBottom = offset >= extent - viewport - BottomTolerance;
        _anchor = FindFirstVisibleMessage();

        if (offset < LoadOlderThreshold && _room is { HasMoreHistory: true } room && room.LoadOlderMessagesCommand.CanExecute(null))
            room.LoadOlderMessagesCommand.Execute(null);
    }

    // Day separators make poor anchors, because loading history can remove them.
    private (MessageViewModel Item, double Top)? FindFirstVisibleMessage()
    {
        (MessageViewModel Item, double Top)? first = null;
        foreach (var container in Items.GetRealizedContainers())
        {
            if (container.DataContext is not MessageViewModel message ||
                container.TranslatePoint(default, Scroller) is not { } position || position.Y + container.Bounds.Height <= 0)
                continue;
            if (first == null || position.Y < first.Value.Top)
                first = (message, position.Y);
        }
        return first;
    }
}
