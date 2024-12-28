namespace Narrensicher.Matrix.Models;

public class User
{
    public User(MatrixId userId)
    {
        UserId = userId;
    }

    public MatrixId UserId { get; }
    public bool? CurrentlyActive { get; internal set; }
    public string? AvatarUrl { get; internal set; }
    public string? DisplayName { get; internal set; }
    public Presence? Presence { get; internal set; } // Initially null if we have not received presence information
    public string? StatusMessage { get; internal set; }
}