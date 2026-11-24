using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.Logging;
using RSMatrix.Desktop.Storage;
using RSMatrix.Desktop.ViewModels;
using RSMatrix.Desktop.Views;

namespace RSMatrix.Desktop;

public sealed class App : Application
{
    public StartupOptions Options { get; init; } = StartupOptions.Parse([]);

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Appearance.Apply(this, new AppearanceSettings());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var loggerFactory = CreateLoggerFactory(Options.Verbose);
            var localData = new LocalData(Options.DataDirectory ?? LocalData.DefaultDirectory);
            Appearance.Apply(this, localData.LoadSettings().Appearance);
            var viewModel = new MainWindowViewModel(localData, loggerFactory);
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            desktop.Exit += (_, _) => loggerFactory.Dispose();
            viewModel.Start(Options);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static ILoggerFactory CreateLoggerFactory(bool verbose) => LoggerFactory.Create(logging => logging
        .AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        })
        .AddFilter("System.Net.Http", LogLevel.Warning)
        .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information));
}
