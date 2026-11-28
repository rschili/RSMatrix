using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
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
//        snapshots --layout-previews OUTPUT_DIR   (the previews in the settings dialog)
var isLayoutPreviews = args is ["--layout-previews", _];
if (!isLayoutPreviews && (args.Length is not (1 or 5) || (args.Length == 5 && args[1] != "--live")))
{
    Console.Error.WriteLine("Usage: snapshots OUTPUT_DIR [--live USER_ID PASSWORD HOMESERVER]");
    Console.Error.WriteLine("       snapshots --layout-previews OUTPUT_DIR");
    return 2;
}

var outputDirectory = Directory.CreateDirectory(isLayoutPreviews ? args[1] : args[0]).FullName;
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
    if (isLayoutPreviews)
        RenderLayoutPreviews();
    else if (args.Length == 5)
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
    Save("signed-out");
    SaveDialog("login");

    main.OpenSession(new SampleChatSession());
    var chat = main.Chat!;
    Pump(TimeSpan.FromMilliseconds(500));
    foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
    {
        Application.Current!.RequestedThemeVariant = theme;
        var suffix = theme == ThemeVariant.Dark ? "-dark" : "";
        foreach (var room in new[] { "General", "Design", "Builds", "Alice" })
        {
            chat.SelectedConversation = chat.Rooms.First(r => r.Name == room);
            Pump(TimeSpan.FromMilliseconds(500));
            Save($"room-{room.ToLowerInvariant()}{suffix}");
        }
    }

    Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
    chat.SelectedConversation = chat.Rooms.First(r => r.Name == "General");
    foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
    {
        Appearance.Apply(Application.Current!, new AppearanceSettings { MessageLayout = MessageLayout.Classic, Theme = theme });
        Pump(TimeSpan.FromMilliseconds(500));
        Save(theme == ThemePreference.Dark ? "room-general-classic-dark" : "room-general-classic");
    }
    Appearance.Apply(Application.Current!, new AppearanceSettings { Theme = ThemePreference.Light });
    Pump(TimeSpan.FromMilliseconds(300));

    RenderMessageActions(chat);

    var lounge = chat.Rooms.First(r => r.Name == "Lounge");
    chat.SelectedConversation = lounge;
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
    window.Width = 1180;

    chat.SelectedConversation = chat.Rooms.First(r => r.Name == "General");
    chat.ToggleMemberListCommand.Execute(null);
    Pump(TimeSpan.FromMilliseconds(500));
    Save("members");
    window.Width = 900;
    Pump(TimeSpan.FromMilliseconds(400));
    Save("members-narrow");
    window.Width = 1180;

    RenderPresence(chat);
    chat.ToggleMemberListCommand.Execute(null);

    RenderSettings(chat);
}

// The end of the General room in each message layout and theme, as small pictures for the settings dialog.
void RenderLayoutPreviews()
{
    main.OpenSession(new SampleChatSession());
    var chat = main.Chat!;
    chat.SelectedConversation = chat.Rooms.First(r => r.Name == "General");
    window.Width = 900;
    window.Height = 600;
    Pump(TimeSpan.FromMilliseconds(500));
    var size = new PixelSize(448, 280);
    var timeline = window.GetVisualDescendants().OfType<TimelineView>().First();
    window.Height += timeline.Bounds.Width * size.Height / size.Width - timeline.Bounds.Height;
    foreach (var layout in Enum.GetValues<MessageLayout>())
    {
        foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
        {
            Appearance.Apply(Application.Current!, new AppearanceSettings { MessageLayout = layout, Theme = theme });
            Pump(TimeSpan.FromMilliseconds(500));
            // The timeline is transparent on the window background, which a picture of the timeline alone doesn't have.
            timeline.Background = (Avalonia.Media.IBrush)window.FindResource(Application.Current!.ActualThemeVariant, "WindowBackgroundBrush")!;
            var dpi = 96.0 * size.Width / timeline.Bounds.Width;
            using var bitmap = new RenderTargetBitmap(size, new Vector(dpi, dpi));
            bitmap.Render(timeline);
            var path = Path.Combine(outputDirectory, $"{layout}-{theme}.png".ToLowerInvariant());
            bitmap.Save(path, new PngBitmapEncoderOptions());
            Console.WriteLine(path);
        }
    }
}

// Opens the settings dialog and confirms a few appearance changes, the way a user would.
void RenderSettings(ChatViewModel chat)
{
    chat.SelectedConversation = chat.Rooms.First(r => r.Name == "General");
    main.OpenSettingsCommand.Execute(null);
    Pump(TimeSpan.FromMilliseconds(400));
    SaveDialog("settings");
    var categories = window.OwnedWindows.Last().GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "Categories");
    foreach (var (index, name) in new[] { (1, "text"), (2, "room-list"), (3, "presence") })
    {
        categories.SelectedIndex = index;
        Pump(TimeSpan.FromMilliseconds(200));
        SaveDialog($"settings-{name}");
    }
    categories.SelectedIndex = 0;

    var settings = main.Settings!;
    settings.ShowRoomPreviews = false;
    settings.SaveCommand.Execute(null);
    Pump(TimeSpan.FromMilliseconds(500));
    Save("appearance-no-previews");

    main.OpenSettingsCommand.Execute(null);
    settings = main.Settings!;
    settings.ShowRoomIcons = false;
    settings.FontFamily = settings.FontFamilies.FirstOrDefault(f => f.Contains("Mono")) ?? settings.FontFamily;
    settings.FontSize = 16;
    settings.Theme = settings.Themes.First(t => t.Value == ThemePreference.Dark);
    Pump(TimeSpan.FromMilliseconds(300));
    SaveDialog("settings-changed");
    settings.SaveCommand.Execute(null);
    Pump(TimeSpan.FromMilliseconds(500));
    Save("appearance-mono-large-dark");
}

// Opens the presence menu below the user's name, sets a status text and goes away.
void RenderPresence(ChatViewModel chat)
{
    var button = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PresenceButton");
    Click(button);
    Pump(TimeSpan.FromMilliseconds(300));
    // Popups are separate top levels here.
    var menu = TopLevel.GetTopLevel((Control)((Flyout)button.Flyout!).Content!) ?? throw new InvalidOperationException("The menu didn't open.");
    Save("presence-menu", menu);
    button.Flyout!.Hide();

    chat.Presence.StatusDraft = "🍜 lunch";
    chat.Presence.SaveStatusCommand.Execute(null);
    chat.Presence.IsAwayMode = true;
    Pump(TimeSpan.FromMilliseconds(500));
    Save("presence-away");
    chat.Presence.ClearStatusCommand.Execute(null);
    chat.Presence.IsOnlineMode = true;
    Pump(TimeSpan.FromMilliseconds(300));
}

async Task RenderLiveAsync(string userId, string password, string homeserver)
{
    var connecting = MatrixChatSession.ConnectAsync(new MatrixLogin(userId, password, new Uri(homeserver)), localData,
        NullLoggerFactory.Instance, CancellationToken.None);
    while (!connecting.IsCompleted)
        Pump(TimeSpan.FromMilliseconds(100));
    main.OpenSession(await connecting);
    var chat = main.Chat!;
    var deadline = DateTime.UtcNow.AddSeconds(30);
    while (chat.ConnectionState != ConnectionState.Online && DateTime.UtcNow < deadline)
        Pump(TimeSpan.FromMilliseconds(200));
    Pump(TimeSpan.FromSeconds(2));
    Console.WriteLine($"Connection: {chat.ConnectionText}. Rooms: {string.Join(", ", chat.Rooms.Select(r => r.Name))}");
    foreach (var room in chat.Rooms.ToList())
    {
        chat.SelectedConversation = room;
        Pump(TimeSpan.FromSeconds(2));
        Save($"live-{string.Concat(room.Name.Where(char.IsAsciiLetterOrDigit)).ToLowerInvariant()}");
    }

    // Scroll back through the room with the longest history. Each step should load older messages
    // while the messages on screen stay where they are.
    var longest = chat.Rooms.MaxBy(r => r.Timeline.MessageCount)!;
    chat.SelectedConversation = longest;
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

// Hovers a message, reacts through the picker, takes the reaction back, opens the menu, replies and opens a thread.
void RenderMessageActions(ChatViewModel chat)
{
    chat.SelectedConversation = chat.Rooms.First(r => r.Name == "General");
    Pump(TimeSpan.FromMilliseconds(500));
    Border FindMessage() => window.GetVisualDescendants().OfType<Border>()
        .First(b => b.Classes.Contains("message") && b.DataContext is MessageViewModel { Body: "All of them. Lists too." });
    var viewModel = (MessageViewModel)FindMessage().DataContext!;
    window.MouseMove(Center(FindMessage()));
    Pump(TimeSpan.FromMilliseconds(200));
    Save("actions-hover");

    Click(FindButton(FindMessage(), "Add reaction"));
    Pump(TimeSpan.FromMilliseconds(300));
    Save("actions-picker");
    window.MouseMove(new Point(1, 1));
    Pump(TimeSpan.FromMilliseconds(100));

    // Popups are separate top levels here, so the picker is checked on its own.
    var picker = new ReactionPicker();
    var pickerHost = new Window { Content = picker, Width = 400, Height = 200 };
    pickerHost.Show();
    Pump(TimeSpan.FromMilliseconds(100));
    string? picked = null;
    picker.Picked += key => picked = key;
    Invoke(picker.GetVisualDescendants().OfType<Button>().First(b => b.Content is "🎉"));
    pickerHost.Close();
    viewModel.ToggleReactionCommand.Execute(picked);
    Pump(TimeSpan.FromMilliseconds(400));
    Console.WriteLine($"Picked: {picked}. Reactions: {string.Join(" ", viewModel.Reactions.Select(r => $"{r.Key}{r.Count}"))}. Recent: {string.Join(" ", chat.RecentReactions.Keys)}");
    window.MouseMove(Center(FindMessage()));
    Pump(TimeSpan.FromMilliseconds(200));
    Save("actions-reacted");

    Click(window.GetVisualDescendants().OfType<Button>().First(b => b.DataContext is ReactionViewModel { Key: "🎉" }));
    Pump(TimeSpan.FromMilliseconds(400));
    Console.WriteLine($"After clicking 🎉: {viewModel.Reactions.Count} reactions");

    window.MouseMove(Center(FindMessage()));
    Pump(TimeSpan.FromMilliseconds(100));
    Click(FindButton(FindMessage(), "More"));
    Pump(TimeSpan.FromMilliseconds(300));
    Save("actions-menu");
    window.KeyPress(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Escape, null);
    window.MouseMove(new Point(1, 1));
    Pump(TimeSpan.FromMilliseconds(100));

    window.MouseMove(Center(FindMessage()));
    Pump(TimeSpan.FromMilliseconds(100));
    Click(FindButton(FindMessage(), "Reply"));
    Pump(TimeSpan.FromMilliseconds(300));
    Save("actions-reply");
    chat.SelectedConversation!.CancelReplyCommand.Execute(null);

    window.MouseMove(new Point(1, 1));
    var summary = window.GetVisualDescendants().OfType<Button>()
        .First(b => b.Classes.Contains("threadSummary") && b.DataContext is MessageViewModel { Body: "All of them. Lists too." });
    Click(summary);
    Pump(TimeSpan.FromMilliseconds(500));
    Save("thread");
    window.MouseMove(new Point(1, 1));
    Pump(TimeSpan.FromMilliseconds(200));
}

Button FindButton(Control parent, string toolTip)
    => parent.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) as string == toolTip);

// Goes through the control's own click handling, like a user would.
void Invoke(Control control)
{
    var peer = Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(control);
    ((Avalonia.Automation.Provider.IInvokeProvider)peer).Invoke();
}

Point Center(Control control)
    => control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
       ?? throw new InvalidOperationException("Not on screen.");

void Click(Control control)
{
    var point = Center(control);
    window.MouseMove(point);
    window.MouseDown(point, Avalonia.Input.MouseButton.Left);
    window.MouseUp(point, Avalonia.Input.MouseButton.Left);
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

// Dialogs are their own windows, owned by the main window.
void SaveDialog(string name) => Save(name, window.OwnedWindows.Last());

void Save(string name, TopLevel? target = null)
{
    Dispatcher.UIThread.RunJobs();
    var frame = (target ?? window).CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
    var path = Path.Combine(outputDirectory, name + ".png");
    frame.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    Console.WriteLine(path);
}
