using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RSMatrix.Desktop.Views;

/// <summary>A grid of emoji to react with.</summary>
public partial class ReactionPicker : UserControl
{
    public ReactionPicker()
    {
        InitializeComponent();
    }

    public event Action<string>? Picked;

    private void OnEmojiClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string key })
            Picked?.Invoke(key);
    }
}
