namespace RSMatrix.Desktop;

/// <summary>
/// Command line options, mainly for development against the local test server:
/// <c>--sample</c>, <c>--data-dir DIR</c>, <c>--user ID</c>, <c>--password PW</c>, <c>--homeserver URL</c>,
/// <c>--recovery-key-file PATH</c>, <c>--verbose</c>.
/// With a password, the app signs in right away.
/// </summary>
public sealed record StartupOptions(bool UseSample, string? DataDirectory, string? UserId, string? Password,
    string? Homeserver, bool Verbose, string? RecoveryKeyFile = null)
{
    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        var options = new StartupOptions(false, null, null, null, null, false);
        for (var i = 0; i < args.Count; i++)
        {
            string Value() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            options = args[i] switch
            {
                "--sample" => options with { UseSample = true },
                "--verbose" => options with { Verbose = true },
                "--data-dir" => options with { DataDirectory = Value() },
                "--user" => options with { UserId = Value() },
                "--password" => options with { Password = Value() },
                "--homeserver" => options with { Homeserver = Value() },
                "--recovery-key-file" => options with { RecoveryKeyFile = Value() },
                _ => throw new ArgumentException($"Unknown option {args[i]}."),
            };
        }
        return options;
    }
}
