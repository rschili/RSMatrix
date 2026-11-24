using Avalonia.Controls;
using RSMatrix.Desktop.ViewModels;

namespace RSMatrix.Desktop.Views;

public partial class MainWindow : Window
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(3);
    private bool _isSessionClosed;

    public MainWindow()
    {
        InitializeComponent();
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
}
