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
