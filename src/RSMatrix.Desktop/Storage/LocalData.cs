using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RSMatrix.Desktop.Storage;

/// <summary>
/// Files the app keeps between runs: login form defaults, RSMatrix's store with the sessions and encryption keys,
/// and each account's recovery key.
/// </summary>
public sealed class LocalData
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsPath;

    public LocalData(string directory)
    {
        Directory = directory;
        _settingsPath = Path.Combine(directory, "settings.json");
    }

    public string Directory { get; }

    public static string DefaultDirectory
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "rsmatrix");

    /// <summary>
    /// Settings are cheap to lose, so a file that can't be read gives defaults, and values this version can't use are
    /// replaced one by one.
    /// </summary>
    public AppSettings LoadSettings()
    {
        AppSettings? settings = null;
        try
        {
            if (File.Exists(_settingsPath))
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath), JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
        }
        settings ??= new();
        settings.Appearance = settings.Appearance?.Normalized() ?? new();
        return settings;
    }

    public void SaveSettings(AppSettings settings)
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    /// <summary>RSMatrix's store directory, with one session per account.</summary>
    public string StoreDirectory => Path.Combine(Directory, "store");

    /// <summary>Where the app reads the account's recovery key from until there is a way to enter it.</summary>
    public string GetRecoveryKeyPath(string userId)
        => Path.Combine(Directory, "accounts", ToDirectoryName(userId), "recovery-key");

    /// <summary>A readable, file system safe name that stays unique per user ID.</summary>
    private static string ToDirectoryName(string userId)
    {
        var readable = new string(userId.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '_').ToArray()).Trim('_');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userId)))[..8].ToLowerInvariant();
        return $"{readable}-{hash}";
    }
}

public sealed class AppSettings
{
    public string? LastUserId { get; set; }

    public string? LastHomeserver { get; set; }

    public AppearanceSettings Appearance { get; set; } = new();

    public bool IsMemberListOpen { get; set; }
}
