using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using RSMatrix.Desktop.Chat;
using RSMatrix.Desktop.Sample;
using RSMatrix.Desktop.Storage;

namespace RSMatrix.Desktop.ViewModels;

/// <summary>Switches between the login page and the chat of a connected session.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly LocalData _localData;
    private readonly ILoggerFactory _loggerFactory;

    public MainWindowViewModel(LocalData localData, ILoggerFactory loggerFactory)
    {
        _localData = localData;
        _loggerFactory = loggerFactory;
        CurrentPage = CreateLogin();
    }

    public const string DefaultWindowTitle = "Matrix";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    public partial ObservableObject CurrentPage { get; private set; }

    public string WindowTitle => (CurrentPage as ChatViewModel)?.WindowTitle ?? DefaultWindowTitle;

    partial void OnCurrentPageChanged(ObservableObject? oldValue, ObservableObject newValue)
    {
        if (oldValue is ChatViewModel oldChat)
            oldChat.PropertyChanged -= OnChatPropertyChanged;
        if (newValue is ChatViewModel newChat)
            newChat.PropertyChanged += OnChatPropertyChanged;
    }

    private void OnChatPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatViewModel.WindowTitle))
            OnPropertyChanged(nameof(WindowTitle));
    }

    /// <summary>Applies command line options: sample mode, or a prefilled login that signs in right away.</summary>
    public void Start(StartupOptions options)
    {
        if (options.UseSample)
        {
            OpenSession(new SampleChatSession());
            return;
        }
        if (CurrentPage is not LoginViewModel login)
            return;
        if (options.UserId != null)
            login.UserId = options.UserId;
        if (options.Homeserver != null)
            login.Homeserver = options.Homeserver;
        if (options.Password != null)
        {
            login.Password = options.Password;
            login.SignInCommand.Execute(null);
        }
    }

    public void OpenSession(IChatSession session)
    {
        var chat = new ChatViewModel(session, OnSessionEnded);
        CurrentPage = chat;
        _ = chat.RunAsync();
    }

    public async Task CloseAsync()
    {
        if (CurrentPage is ChatViewModel chat)
            await chat.DisposeAsync();
    }

    private void OnSessionEnded(string? error)
    {
        var login = CreateLogin();
        if (error != null)
            login.ShowError(error);
        CurrentPage = login;
    }

    private LoginViewModel CreateLogin() => new(_localData, _loggerFactory, OpenSession);
}
