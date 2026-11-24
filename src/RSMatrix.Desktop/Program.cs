using Avalonia;
using Avalonia.Media;

namespace RSMatrix.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        StartupOptions options;
        try
        {
            options = StartupOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        return BuildAvaloniaApp(() => new App { Options = options }).StartWithClassicDesktopLifetime(args);
    }

    // Used by the IDE previewer.
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(() => new App());

    internal static AppBuilder BuildAvaloniaApp(Func<App> createApp)
        => ConfigureFonts(AppBuilder.Configure(createApp).UsePlatformDetect()).LogToTrace();

    /// <summary>Inter is bundled, so text looks the same on every platform.</summary>
    internal static AppBuilder ConfigureFonts(AppBuilder builder)
        => builder.WithInterFont().With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" });
}
