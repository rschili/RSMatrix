using System.ComponentModel;
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
