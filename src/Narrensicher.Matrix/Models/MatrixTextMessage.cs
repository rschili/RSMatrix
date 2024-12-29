namespace Narrensicher.Matrix.Models;

public class MatrixTextMessage
{
    public string? Body { get; private set; }
    public Room Room { get; private set; }
    public RoomUser Sender { get; private set; }
    public MatrixTextClient Client { get; private set; }

    internal MatrixTextMessage(string? body, Room room, RoomUser sender, MatrixTextClient client)
    {
        Body = body;
        Room = room;
        Sender = sender;
        Client = client;
    }
}