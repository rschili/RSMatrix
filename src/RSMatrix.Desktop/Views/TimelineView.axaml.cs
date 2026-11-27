using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Controls.Templates;
using RSMatrix.Desktop.Storage;
using RSMatrix.Desktop.ViewModels;

namespace RSMatrix.Desktop.Views;

/// <summary>
/// Keeps the timeline at the newest message while the reader is there, keeps the visible messages in place
/// when older ones are added above, and loads older messages when scrolling near the top.
/// </summary>
public partial class TimelineView : UserControl
{
    public static readonly StyledProperty<MessageLayout> MessageLayoutProperty =
        AvaloniaProperty.Register<TimelineView, MessageLayout>(nameof(MessageLayout));

    private const double BottomTolerance = 40;
    private const double LoadOlderThreshold = 400;
    // Avalonia's own distance per wheel notch.
    private const double WheelStep = 50;
    // How quickly a wheel scroll catches up with its target, in seconds.
    private const double WheelEaseTime = 0.05;

    private ConversationViewModel? _conversation;
    private bool _isAtBottom = true;
    private bool _isRestorePending;
    // The first visible message and its distance from the top of the viewport, as of the last scroll.
    private (MessageViewModel Item, double Top)? _anchor;
    // Where a wheel scroll is heading, and the offset it set last, to notice when something else moves the view.
    private double? _wheelTarget;
    private double _wheelOffset;
    private TimeSpan? _lastWheelFrame;

    public TimelineView()
    {
        InitializeComponent();
        Scroller.ScrollChanged += OnScrollChanged;
        Scroller.AddHandler(PointerWheelChangedEvent, OnPointerWheelChanged, RoutingStrategies.Tunnel);
        ApplyMessageLayout();
    }

    public MessageLayout MessageLayout
    {
        get => GetValue(MessageLayoutProperty);
        set => SetValue(MessageLayoutProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MessageLayoutProperty)
            ApplyMessageLayout();
    }

    // The comfortable template is the type-based one among the item templates. The classic one takes precedence when set.
    // XAML can set the layout before InitializeComponent created the items control.
    private void ApplyMessageLayout()
    {
        if (Items != null)
            Items.ItemTemplate = MessageLayout == MessageLayout.Classic ? (IDataTemplate)Resources["ClassicMessageTemplate"]! : null;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_conversation != null)
            _conversation.Timeline.Items.CollectionChanged -= OnItemsChanged;
        _conversation = DataContext as ConversationViewModel;
        if (_conversation == null)
            return;
        _conversation.Timeline.Items.CollectionChanged += OnItemsChanged;
        _isAtBottom = true;
        _anchor = null;
        ScrollToEndAfterLayout();
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var count = _conversation?.Timeline.Items.Count ?? 0;
        var isAppend = e.Action == NotifyCollectionChangedAction.Add && e.NewStartingIndex >= count - e.NewItems!.Count;
        if (isAppend)
        {
            if (_isAtBottom)
                ScrollToEndAfterLayout();
            return;
        }
        _wheelTarget = null;
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

        if (offset < LoadOlderThreshold && _conversation is { HasMoreHistory: true } conversation && conversation.LoadOlderMessagesCommand.CanExecute(null))
            conversation.LoadOlderMessagesCommand.Execute(null);
    }

    // Mouse wheels jump by a whole step, so we glide there instead. Touchpads send fractional steps, which are smooth already.
    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var delta = e.Delta.Y;
        if (delta == 0 || delta != Math.Round(delta) || e.KeyModifiers.HasFlag(KeyModifiers.Shift) ||
            TopLevel.GetTopLevel(this) is not { } topLevel)
            return;
        e.Handled = true;

        var isAnimating = _wheelTarget != null && Math.Abs(Scroller.Offset.Y - _wheelOffset) < 0.5;
        var maxOffset = Math.Max(0, Scroller.Extent.Height - Scroller.Viewport.Height);
        _wheelTarget = Math.Clamp((isAnimating ? _wheelTarget!.Value : Scroller.Offset.Y) - delta * WheelStep, 0, maxOffset);
        if (isAnimating)
            return;
        _wheelOffset = Scroller.Offset.Y;
        _lastWheelFrame = null;
        topLevel.RequestAnimationFrame(OnWheelFrame);
    }

    private void OnWheelFrame(TimeSpan time)
    {
        // Stops when the target was dropped or something else moved the view, like loading history or a new message.
        if (_wheelTarget is not { } target || Math.Abs(Scroller.Offset.Y - _wheelOffset) >= 0.5)
        {
            _wheelTarget = null;
            return;
        }
        var elapsed = _lastWheelFrame is { } last ? (time - last).TotalSeconds : 1.0 / 60;
        _lastWheelFrame = time;

        target = Math.Clamp(target, 0, Math.Max(0, Scroller.Extent.Height - Scroller.Viewport.Height));
        var offset = _wheelOffset + (target - _wheelOffset) * (1 - Math.Exp(-elapsed / WheelEaseTime));
        if (Math.Abs(target - offset) < 0.5)
        {
            offset = target;
            _wheelTarget = null;
        }
        Scroller.Offset = new Vector(Scroller.Offset.X, offset);
        _wheelOffset = Scroller.Offset.Y;
        if (_wheelTarget != null)
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnWheelFrame);
    }

    private void OnAddReactionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: MessageViewModel message } button)
            return;
        var picker = new ReactionPicker();
        var flyout = new Flyout { Content = picker, Placement = PlacementMode.BottomEdgeAlignedRight };
        picker.Picked += key =>
        {
            flyout.Hide();
            message.ToggleReactionCommand.Execute(key);
        };
        ShowPinned(flyout, button);
    }

    private void OnMessageMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: MessageViewModel message } button)
            return;
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        menu.Items.Add(CreateMenuItem("Copy text", "CopyIcon", () => CopyAsync(message.Body)));
        menu.Items.Add(CreateMenuItem("Copy message ID", "IdIcon", () => CopyAsync(message.EventId)));
        menu.Items.Add(new Separator());
        var viewSource = CreateMenuItem("View source", "CodeIcon", () => ShowSource(message));
        viewSource.IsEnabled = message.Source != null;
        menu.Items.Add(viewSource);
        ShowPinned(menu, button);
    }

    private MenuItem CreateMenuItem(string header, string iconKey, Action onClick)
    {
        var icon = new Avalonia.Controls.Shapes.Path { Data = (Geometry)this.FindResource(iconKey)! };
        icon.Classes.Add("icon");
        var item = new MenuItem { Header = header, Icon = icon };
        item.Click += (_, _) => onClick();
        return item;
    }

    private async void CopyAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    private void ShowSource(MessageViewModel message)
    {
        var text = new SelectableTextBlock { Text = message.Source, Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap };
        text.Bind(TextBlock.FontFamilyProperty, this.GetResourceObservable("MonospaceFont"));
        var window = new Window
        {
            Title = $"Event source: {message.EventId}",
            Width = 640,
            Height = 520,
            Content = new ScrollViewer { Content = text },
        };
        if (TopLevel.GetTopLevel(this) is Window owner)
            window.Show(owner);
        else
            window.Show();
    }

    // Keeps the hover actions of the message visible while the flyout is open.
    private static void ShowPinned(FlyoutBase flyout, Control target)
    {
        var message = target.GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("message"));
        message?.Classes.Set("pinned", true);
        flyout.Closed += (_, _) => message?.Classes.Set("pinned", false);
        flyout.ShowAt(target);
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
