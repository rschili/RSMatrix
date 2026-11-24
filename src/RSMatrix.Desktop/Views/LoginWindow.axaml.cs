using Avalonia.Controls;

namespace RSMatrix.Desktop.Views;

public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        UserIdBox.Focus();
    }
}
