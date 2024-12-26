namespace Narrensicher.Matrix.Models;

public class User
{
    public User(MatrixId userId)
    {
        UserId = userId;
    }

    public MatrixId UserId { get; }
}