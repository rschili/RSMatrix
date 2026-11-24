using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RSMatrix.Desktop.Chat;
using RSMatrix.Desktop.Matrix;
using RSMatrix.Desktop.Sample;
using RSMatrix.Desktop.Storage;

namespace RSMatrix.Desktop.ViewModels;

public sealed partial class LoginViewModel : ObservableObject
{
    private readonly LocalData _localData;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Action<IChatSession> _onConnected;

    public LoginViewModel(LocalData localData, ILoggerFactory loggerFactory, Action<IChatSession> onConnected)
    {
        _localData = localData;
        _loggerFactory = loggerFactory;
        _onConnected = onConnected;
        var settings = localData.LoadSettings();
        UserId = settings.LastUserId ?? "";
        Homeserver = settings.LastHomeserver ?? "";
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    public partial string UserId { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    public partial string Password { get; set; } = "";

    /// <summary>Optional. Without it, the homeserver is looked up from the user ID's domain.</summary>
    [ObservableProperty]
    public partial string Homeserver { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    /// <summary>
    /// Shown after a failed sign-in. The server may have dropped this device, for example when a local
    /// test server was recreated, and then only a new device helps.
    /// </summary>
    [ObservableProperty]
    public partial bool CanResetDevice { get; private set; }

    private bool CanSignIn() => !string.IsNullOrWhiteSpace(UserId) && !string.IsNullOrEmpty(Password);

    [RelayCommand(CanExecute = nameof(CanSignIn), IncludeCancelCommand = true)]
    private async Task SignInAsync(CancellationToken cancellationToken)
    {
        ErrorMessage = null;
        CanResetDevice = false;
        var userId = UserId.Trim();
        if (!userId.StartsWith('@') || !userId.Contains(':'))
        {
            ErrorMessage = "Enter the full user ID, like @alice:example.org.";
            return;
        }

        Uri? homeserver = null;
        if (!string.IsNullOrWhiteSpace(Homeserver) &&
            (!Uri.TryCreate(Homeserver.Trim(), UriKind.Absolute, out homeserver) || homeserver.Scheme is not ("http" or "https")))
        {
            ErrorMessage = "The homeserver must be a URL like https://matrix.example.org.";
            return;
        }

        _localData.SaveSettings(new AppSettings { LastUserId = userId, LastHomeserver = homeserver?.ToString() });
        try
        {
            var account = _localData.GetAccount(userId);
            var session = await MatrixChatSession.ConnectAsync(new MatrixLogin(userId, Password, homeserver), account,
                _loggerFactory, cancellationToken);
            Password = "";
            _onConnected(session);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            CanResetDevice = true;
        }
    }

    [RelayCommand]
    private async Task ResetDeviceAsync()
    {
        _localData.ResetAccount(UserId.Trim());
        await SignInCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void OpenSample() => _onConnected(new SampleChatSession());

    /// <summary>Shows why the previous session ended.</summary>
    public void ShowError(string message) => ErrorMessage = message;
}
