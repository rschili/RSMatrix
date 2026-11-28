using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RSMatrix.Desktop.ViewModels;

namespace RSMatrix.Desktop.Views;

public partial class ChatView : UserControl
{
    private ChatViewModel? _viewModel;

    public ChatView()
    {
        InitializeComponent();
        // Tunnel, so Enter sends before the text box turns it into a line break.
        Composer.AddHandler(KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel);
        StatusBox.KeyDown += OnStatusBoxKeyDown;
        // Any input in the app counts as activity for the presence, also when a control handled it.
        AddHandler(PointerMovedEvent, OnActivity, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, OnActivity, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerWheelChangedEvent, OnActivity, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnActivity, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is Window window)
            window.Activated += OnWindowActivated;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (TopLevel.GetTopLevel(this) is Window window)
            window.Activated -= OnWindowActivated;
    }

    private void OnWindowActivated(object? sender, EventArgs e) => _viewModel?.Presence.ReportActivity();

    private void OnActivity(object? sender, RoutedEventArgs e) => _viewModel?.Presence.ReportActivity();

    private void OnPresenceMenuOpening(object? sender, EventArgs e) => _viewModel?.Presence.BeginStatusEdit();

    private void OnStatusBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _viewModel is not { } chat)
            return;
        e.Handled = true;
        chat.Presence.SaveStatusCommand.Execute(null);
        PresenceButton.Flyout?.Hide();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel != null)
            _viewModel.ComposerFocusRequested -= OnComposerFocusRequested;
        _viewModel = DataContext as ChatViewModel;
        if (_viewModel != null)
            _viewModel.ComposerFocusRequested += OnComposerFocusRequested;
    }

    private void OnComposerFocusRequested() => Composer.Focus();

    private void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        var conversation = _viewModel?.SelectedConversation;
        if (e.Key == Key.Escape && conversation is { IsReplying: true })
        {
            e.Handled = true;
            conversation.CancelReplyCommand.Execute(null);
            return;
        }
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None)
            return;
        e.Handled = true;
        if (conversation?.SendCommand.CanExecute(null) == true)
            conversation.SendCommand.Execute(null);
    }

    private void OnMoreThreadsClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: MoreThreadsViewModel more } button || _viewModel is not { } chat)
            return;
        var menu = new MenuFlyout { Placement = PlacementMode.RightEdgeAlignedTop };
        foreach (var thread in more.Threads)
        {
            var item = new MenuItem { Header = thread.Title };
            item.Click += (_, _) => chat.SelectedConversation = thread;
            menu.Items.Add(item);
        }
        menu.ShowAt(button);
    }
}
