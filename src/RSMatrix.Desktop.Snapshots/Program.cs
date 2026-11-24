using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using RSMatrix.Desktop;
using RSMatrix.Desktop.Chat;
using RSMatrix.Desktop.Matrix;
using RSMatrix.Desktop.Sample;
using RSMatrix.Desktop.Storage;
using RSMatrix.Desktop.ViewModels;
using RSMatrix.Desktop.Views;

// Renders the desktop app without a display and writes PNG files, to review the look without running it.
// Usage: snapshots OUTPUT_DIR [--live USER_ID PASSWORD HOMESERVER]
if (args.Length is not (1 or 5) || (args.Length == 5 && args[1] != "--live"))
{
    Console.Error.WriteLine("Usage: snapshots OUTPUT_DIR [--live USER_ID PASSWORD HOMESERVER]");
    return 2;
}

var outputDirectory = Directory.CreateDirectory(args[0]).FullName;
RSMatrix.Desktop.Program.ConfigureFonts(AppBuilder.Configure<App>())
    .UseSkia()
    .UseHarfBuzz()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

var dataDirectory = Path.Combine(Path.GetTempPath(), $"rsmatrix-snapshots-{Environment.ProcessId}");
var localData = new LocalData(dataDirectory);
var main = new MainWindowViewModel(localData, NullLoggerFactory.Instance);
var window = new MainWindow { DataContext = main, Width = 1180, Height = 760 };
window.Show();

try
{
    if (args.Length == 5)
        await RenderLiveAsync(args[2], args[3], args[4]);
    else
        RenderSample();
}
finally
{
    if (Directory.Exists(dataDirectory))
        Directory.Delete(dataDirectory, recursive: true);
}
return 0;

void RenderSample()
{
    Pump(TimeSpan.FromMilliseconds(300));
    Save("login");

    main.OpenSession(new SampleChatSession());
    var chat = (ChatViewModel)main.CurrentPage;
    Pump(TimeSpan.FromMilliseconds(500));
    foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
    {
        Application.Current!.RequestedThemeVariant = theme;
        var suffix = theme == ThemeVariant.Dark ? "-dark" : "";
        foreach (var room in new[] { "General", "Design", "Builds", "Alice" })
        {
            chat.SelectedRoom = chat.Rooms.First(r => r.Name == room);
            Pump(TimeSpan.FromMilliseconds(500));
            Save($"room-{room.ToLowerInvariant()}{suffix}");
        }
    }

    Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
    var lounge = chat.Rooms.First(r => r.Name == "Lounge");
    chat.SelectedRoom = lounge;
    Pump(TimeSpan.FromSeconds(3));
    Save("room-lounge-history");

    lounge.Draft = "A draft that grows.\nSecond line\nThird line\nand a fourth one, which is long enough to wrap around at the end of the composer box when the window is not too wide.";
    Pump(TimeSpan.FromMilliseconds(400));
    Save("composer-multiline");

    lounge.SendCommand.Execute(null);
    Pump(TimeSpan.FromMilliseconds(100));
    Save("composer-pending");
    Pump(TimeSpan.FromMilliseconds(600));
    Save("composer-sent");

    window.Width = 760;
    Pump(TimeSpan.FromMilliseconds(400));
    Save("narrow");
}

async Task RenderLiveAsync(string userId, string password, string homeserver)
{
    var account = localData.GetAccount(userId);
    var connecting = MatrixChatSession.ConnectAsync(new MatrixLogin(userId, password, new Uri(homeserver)), account,
        NullLoggerFactory.Instance, CancellationToken.None);
    while (!connecting.IsCompleted)
        Pump(TimeSpan.FromMilliseconds(100));
    main.OpenSession(await connecting);
    var chat = (ChatViewModel)main.CurrentPage;
    var deadline = DateTime.UtcNow.AddSeconds(30);
    while (chat.ConnectionState != ConnectionState.Online && DateTime.UtcNow < deadline)
        Pump(TimeSpan.FromMilliseconds(200));
    Pump(TimeSpan.FromSeconds(2));
    Console.WriteLine($"Connection: {chat.ConnectionText}. Rooms: {string.Join(", ", chat.Rooms.Select(r => r.Name))}");
    foreach (var room in chat.Rooms.ToList())
    {
        chat.SelectedRoom = room;
        Pump(TimeSpan.FromSeconds(2));
        Save($"live-{string.Concat(room.Name.Where(char.IsAsciiLetterOrDigit)).ToLowerInvariant()}");
    }

    // Scroll back through the room with the longest history. Each step should load older messages
    // while the messages on screen stay where they are.
    var longest = chat.Rooms.MaxBy(r => r.Timeline.MessageCount)!;
    chat.SelectedRoom = longest;
    Pump(TimeSpan.FromSeconds(2));
    var scroller = window.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "Scroller");
    for (var step = 1; step <= 3; step++)
    {
        scroller.Offset = new Vector(0, 0);
        Pump(TimeSpan.FromMilliseconds(50));
        Save($"live-scrollback-{step}a");
        Pump(TimeSpan.FromSeconds(14));
        Console.WriteLine($"Step {step}: {longest.Timeline.MessageCount} messages, offset {scroller.Offset.Y:0}, extent {scroller.Extent.Height:0}");
        Save($"live-scrollback-{step}b");
    }
    await main.CloseAsync();
}

void Pump(TimeSpan duration)
{
    var end = DateTime.UtcNow + duration;
    do
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Thread.Sleep(10);
    }
    while (DateTime.UtcNow < end);
}

void Save(string name)
{
    Dispatcher.UIThread.RunJobs();
    var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
    var path = Path.Combine(outputDirectory, name + ".png");
    frame.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    Console.WriteLine(path);
}
