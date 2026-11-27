using RSMatrix.Http;

namespace RSMatrix.Models;

/// <summary>
/// Something that waits for an answer from the user. Ignoring a request means doing nothing; requests the server still
/// holds, like invites, come again with the first sync of the next session. Switch over the kinds in a method of its own;
/// a switch expression over a request gets a compiler warning when a new kind is added.
/// </summary>
public union Request(RoomInvite)
{
    public RoomUser From => Case.From;

    public Task AcceptAsync(CancellationToken cancellationToken = default) => Case.AcceptAsync(cancellationToken);

    public Task DeclineAsync(string? reason = null, CancellationToken cancellationToken = default)
        => Case.DeclineAsync(reason, cancellationToken);

    private IRequest Case => (IRequest)Value!;
}

internal interface IRequest
{
    RoomUser From { get; }
    Task AcceptAsync(CancellationToken cancellationToken);
    Task DeclineAsync(string? reason, CancellationToken cancellationToken);
}

/// <summary>
/// An invite to a room. The details come from the invite preview the inviter's server sends, since the room isn't joined
/// yet. Once joined, the room comes as <see cref="RoomChanged"/> like any other join.
/// </summary>
public sealed class RoomInvite : IRequest
{
    private readonly MatrixClient _client;

    internal RoomInvite(MatrixClient client, MatrixId roomId, RoomUser from, string? roomName, string? topic, string? avatarUrl,
        MatrixId? canonicalAlias, bool isDirect, bool isEncrypted)
    {
        _client = client;
        RoomId = roomId;
        From = from;
        RoomName = roomName;
        Topic = topic;
        AvatarUrl = avatarUrl;
        IsDirect = isDirect;
        IsEncrypted = isEncrypted;
        DisplayName = roomName ?? canonicalAlias?.Full ?? from.DisplayName;
    }

    /// <summary>The user who sent the invite, with the name and avatar they have in the room.</summary>
    public RoomUser From { get; }
    public MatrixId RoomId { get; }

    /// <summary>The name to show: the room's name, else its alias, else the inviter's name, since direct chats have no name.</summary>
    public string DisplayName { get; }
    public string? RoomName { get; }
    public string? Topic { get; }
    /// <summary>The room's avatar as an mxc:// URI.</summary>
    public string? AvatarUrl { get; }
    /// <summary>Whether the inviter marked this as a direct chat.</summary>
    public bool IsDirect { get; }
    public bool IsEncrypted { get; }

    /// <summary>Joins the room.</summary>
    public async Task AcceptAsync(CancellationToken cancellationToken = default)
    {
        await MatrixHelper.PostJoinRoomAsync(_client.HttpClientParameters, RoomId, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (IsDirect)
        {
            var room = _client.GetOrAddRoom(RoomId);
            lock (room)
                room.IsDirect = true;
        }
    }

    /// <summary>Rejects the invite. The reason is shown to the inviter.</summary>
    public Task DeclineAsync(string? reason = null, CancellationToken cancellationToken = default)
        => MatrixHelper.PostLeaveRoomAsync(_client.HttpClientParameters, RoomId, reason, cancellationToken);

    public override string ToString() => $"Invite to {DisplayName} from {From}";
}
