using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RSMatrix.Desktop.Views;

public partial class LoginView : UserControl
{
    public LoginView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        UserIdBox.Focus();
    }
}
