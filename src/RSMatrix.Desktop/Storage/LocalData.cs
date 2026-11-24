using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RSMatrix.Desktop.Storage;

/// <summary>
/// Files the app keeps between runs: login form defaults and, per account, the device ID with
/// RSMatrix's session and encryption stores.
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
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RSMatrix.Desktop");

    public AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath), JsonOptions) ?? new();
        }
        catch (JsonException)
        {
            // A damaged settings file only holds login form defaults, so start over.
        }
        return new();
    }

    public void SaveSettings(AppSettings settings)
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    /// <summary>Returns the account's local data, creating a device ID on first use.</summary>
    public AccountData GetAccount(string userId)
    {
        var directory = Path.Combine(Directory, "accounts", ToDirectoryName(userId));
        var deviceIdPath = Path.Combine(directory, "device-id");
        System.IO.Directory.CreateDirectory(directory);
        if (!File.Exists(deviceIdPath))
            File.WriteAllText(deviceIdPath, CreateDeviceId());
        return new AccountData(userId, File.ReadAllText(deviceIdPath).Trim(), directory);
    }

    /// <summary>
    /// Deletes the account's session, encryption keys and device ID. The next login creates a new device.
    /// </summary>
    public void ResetAccount(string userId)
    {
        var directory = Path.Combine(Directory, "accounts", ToDirectoryName(userId));
        if (System.IO.Directory.Exists(directory))
            System.IO.Directory.Delete(directory, recursive: true);
    }

    private static string CreateDeviceId()
        => "RSMATRIXDESKTOP" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4));

    /// <summary>A readable, file system safe name that stays unique per user ID.</summary>
    private static string ToDirectoryName(string userId)
    {
        var readable = new string(userId.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '_').ToArray()).Trim('_');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userId)))[..8].ToLowerInvariant();
        return $"{readable}-{hash}";
    }
}

public sealed record AccountData(string UserId, string DeviceId, string Directory)
{
    public string SessionDirectory => Path.Combine(Directory, "session");

    public string EncryptionDirectory => Path.Combine(Directory, "encryption");
}

public sealed class AppSettings
{
    public string? LastUserId { get; set; }

    public string? LastHomeserver { get; set; }
}
