using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using RSMatrix.Desktop.Content;
using Path = Avalonia.Controls.Shapes.Path;

namespace RSMatrix.Desktop.Controls;

/// <summary>
/// A file in a message: icon, name and size, and a button that saves it through a save dialog with a progress bar.
/// Media comes from the <see cref="MediaImageView.LoaderProperty"/> the chat view sets.
/// </summary>
public sealed class FileRowView : Border
{
    private readonly FileBlock _file;
    private readonly TextBlock _status = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ProgressBar _progress = new() { Height = 4, MinWidth = 0, Margin = new Thickness(0, 5, 0, 3), IsVisible = false };
    private readonly Path _saveIcon = new() { Width = 14, Height = 14 };
    private readonly Path _cancelIcon = new() { Width = 12, Height = 12, Margin = new Thickness(1) };
    private readonly Button _button = new() { Padding = new Thickness(6) };
    private CancellationTokenSource? _download;

    public FileRowView(FileBlock file)
    {
        _file = file;
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(10, 8, 6, 8);
        MinWidth = 260;
        MaxWidth = 400;
        HorizontalAlignment = HorizontalAlignment.Left;
        Margin = new Thickness(0, 2);
        this.Bind(BackgroundProperty, this.GetResourceObservable("CodeBackgroundBrush"));
        this.Bind(BorderBrushProperty, this.GetResourceObservable("CodeBorderBrush"));

        var icon = new Path { Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center };
        icon.Classes.Add("icon");
        icon.Bind(Path.DataProperty, this.GetResourceObservable("FileIcon"));

        var name = new TextBlock { Text = file.FileName, TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTip.SetTip(name, file.FileName);
        _status.Classes.Add("caption");
        _status.Classes.Add("tertiary");
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { name, _progress, _status } };

        _saveIcon.Classes.Add("icon");
        _saveIcon.Bind(Path.DataProperty, this.GetResourceObservable("DownloadIcon"));
        _cancelIcon.Classes.Add("icon");
        _cancelIcon.Bind(Path.DataProperty, this.GetResourceObservable("CloseIcon"));
        _button.Classes.Add("icon");
        _button.Content = new Panel { Children = { _saveIcon, _cancelIcon } };
        _button.Click += OnButtonClick;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10 };
        grid.Children.Add(icon);
        grid.Children.Add(text);
        grid.Children.Add(_button);
        Grid.SetColumn(text, 1);
        Grid.SetColumn(_button, 2);
        Child = grid;
        ShowIdle(file.Size is { } size ? FormatSize(size) : null);
    }

    private async void OnButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_download != null)
        {
            _download.Cancel();
            return;
        }
        if (MediaImageView.GetLoader(this) is not { } loader || TopLevel.GetTopLevel(this) is not { } topLevel)
            return;
        var target = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = ToSafeFileName(_file.FileName),
            ShowOverwritePrompt = true,
        });
        if (target == null)
            return;

        using var download = _download = new CancellationTokenSource();
        ShowProgress(0);
        string? failure = null;
        try
        {
            await using (var stream = await target.OpenWriteAsync())
            {
                if (stream.CanSeek)
                    stream.SetLength(0);
                await loader.SaveAsync(_file.MxcUri, stream, new Progress<long>(ShowProgress), download.Token);
            }
        }
        catch (Exception ex)
        {
            failure = ex is OperationCanceledException ? "Canceled" : "Download failed";
            await TryDeleteAsync(target);
        }
        finally
        {
            _download = null;
        }
        ShowIdle(failure ?? "Saved");
    }

    private void ShowIdle(string? status)
    {
        _progress.IsVisible = false;
        _status.Text = status;
        _status.IsVisible = status != null;
        ShowButton(isDownloading: false);
    }

    private void ShowProgress(long written)
    {
        _progress.IsVisible = true;
        _progress.IsIndeterminate = _file.Size is not > 0;
        if (_file.Size is > 0 and var size)
        {
            _progress.Maximum = size;
            _progress.Value = Math.Min(written, size);
            _status.Text = $"{FormatSize(Math.Min(written, size))} of {FormatSize(size)}";
        }
        else
        {
            _status.Text = FormatSize(written);
        }
        _status.IsVisible = true;
        ShowButton(isDownloading: true);
    }

    private void ShowButton(bool isDownloading)
    {
        _saveIcon.IsVisible = !isDownloading;
        _cancelIcon.IsVisible = isDownloading;
        ToolTip.SetTip(_button, isDownloading ? "Cancel" : "Save");
    }

    private static async Task TryDeleteAsync(IStorageFile file)
    {
        try
        {
            await file.DeleteAsync();
        }
        catch (Exception)
        {
            // A partial file stays behind; nothing more to do about it.
        }
    }

    /// <summary>The name comes from another user: no directories, no characters the file system rejects.</summary>
    internal static string ToSafeFileName(string fileName)
    {
        var name = System.IO.Path.GetFileName(fileName.Replace('\\', '/'));
        foreach (var invalid in System.IO.Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        name = name.Trim().Trim('.');
        return name.Length > 0 ? name : "download";
    }

    internal static string FormatSize(long bytes)
    {
        string[] units = ["KB", "MB", "GB", "TB"];
        if (bytes < 1024)
            return bytes == 1 ? "1 byte" : $"{bytes} bytes";
        double value = bytes;
        var unit = -1;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value.ToString(value < 10 ? "0.#" : "0", CultureInfo.CurrentCulture)} {units[unit]}";
    }
}
