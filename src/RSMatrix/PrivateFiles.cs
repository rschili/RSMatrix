using System.Text.Json;

namespace RSMatrix;

/// <summary>Owner-only files for secrets the client keeps on disk.</summary>
internal static class PrivateFiles
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static void CreateDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(directory);
        else
            Directory.CreateDirectory(directory, OwnerOnly | UnixFileMode.UserExecute);
    }

    /// <summary>Takes an exclusive lock that holds until the returned stream is disposed.</summary>
    public static FileStream Lock(string path, string inUseMessage)
    {
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(inUseMessage, ex);
        }
    }

    /// <summary>
    /// Reads a JSON file with a top-level <c>version</c>. Checks the version before reading the rest, so a file from a
    /// newer RSMatrix gets a clear error instead of a parse error. Unreadable or damaged files throw with a message
    /// that names the file.
    /// </summary>
    public static T ReadVersionedJson<T>(string path, int supportedVersion, JsonSerializerOptions options, string description)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"The {description} {path} can't be read: {ex.Message}", ex);
        }

        try
        {
            int version;
            using (var document = JsonDocument.Parse(json))
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("version", out var versionElement)
                    || !versionElement.TryGetInt32(out version))
                    throw new InvalidOperationException($"The {description} {path} is damaged: it has no version.");
            }
            if (version > supportedVersion)
                throw new InvalidOperationException(
                    $"The {description} {path} was written by a newer version of RSMatrix (format {version}). Update to use it.");
            if (version != supportedVersion)
                throw new InvalidOperationException($"The {description} {path} has unsupported format {version}.");
            return JsonSerializer.Deserialize<T>(json, options)
                ?? throw new InvalidOperationException($"The {description} {path} is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"The {description} {path} is damaged: {ex.Message}", ex);
        }
    }

    /// <summary>Writes a temporary file and renames it, so a crash leaves either the old or the new content.</summary>
    public static void WriteAtomically(string path, Action<Stream> write)
    {
        var temporaryPath = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = OwnerOnly;
        using (var stream = new FileStream(temporaryPath, options))
        {
            write(stream);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporaryPath, path, overwrite: true);
    }
}
