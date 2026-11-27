namespace RSMatrix.Models;

public class User
{
    internal User(MatrixId userId)
    {
        UserId = userId;
    }

    public MatrixId UserId { get; }
    public bool? CurrentlyActive { get; internal set; }
    public string? AvatarUrl { get; internal set; }
    /// <summary>The name from the user's profile, or null when the client hasn't received one.</summary>
    public string? ProfileName { get; internal set; }

    /// <summary>The name to show: the profile name, or the user ID without the server.</summary>
    public string DisplayName => ProfileName ?? UserId.Localpart.ToString();
    public Presence? Presence { get; internal set; } // Initially null if we have not received presence information
    public string? StatusMessage { get; internal set; }

    public override string ToString() => DisplayName;
}