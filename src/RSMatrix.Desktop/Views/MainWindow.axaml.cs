using System.ComponentModel;
using Avalonia.Controls;
using RSMatrix.Desktop.ViewModels;

namespace RSMatrix.Desktop.Views;

public partial class MainWindow : Window
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(3);
    private readonly DialogSlot _loginDialog;
    private readonly DialogSlot _settingsDialog;
    private MainWindowViewModel? _viewModel;
    private bool _isSessionClosed;

    public MainWindow()
    {
        InitializeComponent();
        _loginDialog = new DialogSlot(this, () => new LoginWindow(),
            viewModel => ((LoginViewModel)viewModel).CancelCommand.Execute(null));
        _settingsDialog = new DialogSlot(this, () => new SettingsWindow(),
            viewModel => ((SettingsViewModel)viewModel).CancelCommand.Execute(null));
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel != null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel != null)
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateDialogs();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        UpdateDialogs();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.Login) or nameof(MainWindowViewModel.Settings))
            UpdateDialogs();
    }

    private void UpdateDialogs()
    {
        // A dialog needs a visible owner, so the ones requested at startup open with the window.
        if (!IsVisible)
            return;
        _loginDialog.Show(_viewModel?.Login);
        _settingsDialog.Show(_viewModel?.Settings);
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_isSessionClosed || e.Cancel || DataContext is not MainWindowViewModel viewModel)
            return;

        // Stop the sync cleanly before the window goes away.
        e.Cancel = true;
        try
        {
            await viewModel.CloseAsync().WaitAsync(ShutdownTimeout);
        }
        catch (TimeoutException)
        {
        }
        _isSessionClosed = true;
        Close();
    }

    /// <summary>
    /// Keeps one modal dialog in line with its view model: open while the view model is set, closed otherwise.
    /// Closing the dialog through the window frame counts as Cancel.
    /// </summary>
    private sealed class DialogSlot(Window owner, Func<Window> createDialog, Action<object> cancel)
    {
        private Window? _dialog;

        public void Show(object? viewModel)
        {
            if (_dialog != null && _dialog.DataContext == viewModel)
                return;
            var previous = _dialog;
            _dialog = null;
            previous?.Close();
            if (viewModel == null)
                return;

            var dialog = createDialog();
            dialog.DataContext = viewModel;
            dialog.Closed += (_, _) =>
            {
                if (_dialog != dialog)
                    return;
                _dialog = null;
                cancel(viewModel);
            };
            _dialog = dialog;
            _ = dialog.ShowDialog(owner);
        }
    }
}
