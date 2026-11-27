using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RSMatrix.Http;
using RSMatrix.Models;

namespace RSMatrix.Tests;

/// <summary>
/// Feeds sync responses from JSON fixtures through the handler and checks the resulting rooms and users.
/// </summary>
public class SyncHandlerTests
{
    private readonly CountingHandler _handler = new();
    private readonly MatrixClient _client;

    public SyncHandlerTests()
    {
        var parameters = new HttpClientParameters(new HttpClient(_handler), "https://example.org", "test_token",
            NullLogger<SyncHandlerTests>.Instance, CancellationToken.None);
        _client = MatrixClient.CreateForTesting(parameters, ParseUserId("@testuser:example.org"));
    }

    private static async Task<SyncResponse> LoadFixtureAsync(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        await using var stream = File.OpenRead(path);
        var response = await JsonSerializer.DeserializeAsync<SyncResponse>(stream);
        return response ?? throw new InvalidOperationException($"Failed to deserialize fixture {fileName}");
    }

    private async Task<List<Message>> ProcessSyncAndCollectMessagesAsync(string fixtureName)
    {
        var syncResponse = await LoadFixtureAsync(fixtureName);
        await _client.HandleSyncResponseForTestingAsync(syncResponse);

        var messages = new List<Message>();
        while (_client.Updates.TryRead(out var update))
        {
            if (update.Value is NewMessage message)
                messages.Add(message.Message);
        }
        return messages;
    }

    [Test]
    public async Task RoomState_SetsNameAliasesAndMembers()
    {
        await ProcessSyncAndCollectMessagesAsync("sync_room_state.json");

        var room = _client.GetOrAddRoom(ParseRoomId("!room1:example.org"));
        await Assert.That(room.Name).IsEqualTo("Test Room");
        await Assert.That(room.CanonicalAlias!.Full).IsEqualTo("#test:example.org");
        await Assert.That(room.AltAliases!.Single().Full).IsEqualTo("#test2:example.org");
        await Assert.That(room.Users.Count).IsEqualTo(2);
        await Assert.That(room.Users["@alice:example.org"].RoomName).IsEqualTo("Alice Wonderland");
        await Assert.That(room.Users["@alice:example.org"].Membership).IsEqualTo(Membership.Join);
        await Assert.That(room.Users["@bob:example.org"].RoomName).IsEqualTo("Bob Builder");
    }

    [Test]
    public async Task Presence_UpdatesUsers()
    {
        await ProcessSyncAndCollectMessagesAsync("sync_presence.json");

        var alice = _client.GetOrAddUser(ParseUserId("@alice:example.org"));
        await Assert.That(alice.Presence).IsEqualTo(Presence.Online);
        await Assert.That(alice.CurrentlyActive).IsTrue();
        await Assert.That(alice.StatusMessage).IsEqualTo("Working on tests");
        await Assert.That(alice.ProfileName).IsEqualTo("Alice Online");
        await Assert.That(alice.AvatarUrl).IsEqualTo("mxc://example.org/alice");
        var bob = _client.GetOrAddUser(ParseUserId("@bob:example.org"));
        await Assert.That(bob.Presence).IsEqualTo(Presence.Unavailable);
        await Assert.That(bob.StatusMessage).IsEqualTo("AFK");
    }

    [Test]
    public async Task MessageWithMentions_ParsesMentionedUsers()
    {
        var messages = await ProcessSyncAndCollectMessagesAsync("sync_message_with_mentions.json");

        var mentionIds = messages.Single().Mentions.Select(m => m.User.UserId.Full).ToList();
        await Assert.That(mentionIds).IsEquivalentTo(["@bob:example.org", "@charlie:example.org"]);
    }

    [Test]
    public async Task DirectMessageRoom_SetsIsDirect()
    {
        await ProcessSyncAndCollectMessagesAsync("sync_direct_message_room.json");

        var room = _client.GetOrAddRoom(ParseRoomId("!dm_room:example.org"));
        await Assert.That(room.IsDirect).IsTrue();
    }

    [Test]
    public async Task MultipleRooms_AreAllProcessed()
    {
        var messages = await ProcessSyncAndCollectMessagesAsync("sync_multiple_rooms.json");

        await Assert.That(messages.Count(m => m.Room.RoomId.Full == "!room1:example.org")).IsEqualTo(1);
        await Assert.That(messages.Count(m => m.Room.RoomId.Full == "!room2:example.org")).IsEqualTo(2);
        await Assert.That(_client.GetOrAddRoom(ParseRoomId("!room1:example.org")).Name).IsEqualTo("Room Alpha");
        await Assert.That(_client.GetOrAddRoom(ParseRoomId("!room2:example.org")).Name).IsEqualTo("Room Beta");
    }

    [Test]
    public async Task Invite_WithoutAutoJoin_SendsAndReportsNothing()
    {
        await _client.HandleSyncResponseForTestingAsync(await LoadFixtureAsync("sync_invite.json"));

        await Assert.That(_client.Updates.TryRead(out _)).IsFalse();
        await Assert.That(_handler.RequestCount).IsEqualTo(0);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

    private static MatrixId ParseUserId(string input)
    {
        UserId.TryParse(input, out var id);
        return id!;
    }

    private static MatrixId ParseRoomId(string input)
    {
        RoomId.TryParse(input, out var id);
        return id!;
    }
}
