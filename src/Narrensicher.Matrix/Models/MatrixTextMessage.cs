namespace Narrensicher.Matrix.Models;

public class MatrixTextMessage
{
    public string Content { get; set; }
    public string Sender { get; set; }
    public string RoomId { get; set; }
    public string MessageId { get; set; }
    public string Timestamp { get; set; }
    public string MessageType { get; set; }
}