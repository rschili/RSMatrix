using System.ComponentModel;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RSMatrix.Desktop.Chat;
using RSMatrix.Desktop.Sample;
using RSMatrix.Desktop.Storage;

namespace RSMatrix.Desktop.ViewModels;

/// <summary>Holds the connected session and the open dialogs. The window shows a dialog while its view model is set.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    public const string DefaultWindowTitle = "Matrix";

    private readonly LocalData _localData;
    private readonly ILoggerFactory _loggerFactory;

    public MainWindowViewModel(LocalData localData, ILoggerFactory loggerFactory)
    {
        _localData = localData;
        _loggerFactory = loggerFactory;
        Login = CreateLogin();
    }

    /// <summary>The connected session, or null while signed out.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle), nameof(IsSignedOut))]
    public partial ChatViewModel? Chat { get; private set; }

    public bool IsSignedOut => Chat == null;

    public string WindowTitle => Chat?.WindowTitle ?? DefaultWindowTitle;

    [ObservableProperty]
    public partial LoginViewModel? Login { get; private set; }

    [ObservableProperty]
    public partial SettingsViewModel? Settings { get; private set; }

    /// <summary>Applies command line options: sample mode, or a prefilled login that signs in right away.</summary>
    public void Start(StartupOptions options)
    {
        if (options.UseSample)
        {
            OpenSession(new SampleChatSession());
            return;
        }
        if (Login is not { } login)
            return;
        if (options.UserId != null)
            login.UserId = options.UserId;
        if (options.Homeserver != null)
            login.Homeserver = options.Homeserver;
        login.RecoveryKeyFile = options.RecoveryKeyFile;
        if (options.Password != null)
        {
            login.Password = options.Password;
            login.SignInCommand.Execute(null);
        }
    }

    public void OpenSession(IChatSession session)
    {
        Login = null;
        Chat = new ChatViewModel(session, OnSessionEnded, OpenSettings)
        {
            IsMemberListOpen = _localData.LoadSettings().IsMemberListOpen,
        };
        _ = Chat.RunAsync();
    }

    public async Task CloseAsync()
    {
        if (Chat != null)
            await Chat.DisposeAsync();
    }

    [RelayCommand]
    private void SignIn() => Login ??= CreateLogin();

    [RelayCommand]
    private void OpenSample() => OpenSession(new SampleChatSession());

    [RelayCommand]
    private void OpenSettings()
        => Settings ??= new SettingsViewModel(_localData.LoadSettings().Appearance, Appearance.GetFontFamilies(), CloseSettings);

    private void CloseSettings(AppearanceSettings? appearance)
    {
        Settings = null;
        if (appearance == null)
            return;
        var settings = _localData.LoadSettings();
        settings.Appearance = appearance;
        _localData.SaveSettings(settings);
        Appearance.Apply(Application.Current!, appearance);
    }

    partial void OnChatChanged(ChatViewModel? oldValue, ChatViewModel? newValue)
    {
        if (oldValue != null)
            oldValue.PropertyChanged -= OnChatPropertyChanged;
        if (newValue != null)
            newValue.PropertyChanged += OnChatPropertyChanged;
    }

    private void OnChatPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatViewModel.WindowTitle))
        {
            OnPropertyChanged(nameof(WindowTitle));
        }
        else if (e.PropertyName == nameof(ChatViewModel.IsMemberListOpen) && sender is ChatViewModel chat)
        {
            var settings = _localData.LoadSettings();
            settings.IsMemberListOpen = chat.IsMemberListOpen;
            _localData.SaveSettings(settings);
        }
    }

    /// <param name="error">Why the session ended, or null when the user disconnected.</param>
    private void OnSessionEnded(string? error)
    {
        Chat = null;
        if (error == null)
            return;
        Login = CreateLogin();
        Login.ShowError(error);
    }

    private LoginViewModel CreateLogin() => new(_localData, _loggerFactory, OpenSession, () => Login = null);
}
