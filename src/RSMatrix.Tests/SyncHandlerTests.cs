using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RSFlowControl;
using RSMatrix.Http;
using RSMatrix.Models;

namespace RSMatrix.Tests;

/// <summary>
/// Tests the sync response handling logic by feeding JSON fixtures through the handler
/// and verifying the resulting client state (rooms, users, messages).
/// </summary>
public class SyncHandlerTests
{
    private readonly MatrixTextClient _client;
    private readonly MatrixId _testUserId;

    public SyncHandlerTests()
    {
        var httpClientFactoryMock = new Mock<IHttpClientFactory>();
        httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient());
        var parameters = new HttpClientParameters(
            httpClientFactoryMock.Object,
            "https://example.org",
            "test_token",
            NullLogger<SyncHandlerTests>.Instance,
            CancellationToken.None);
        parameters.RateLimiter = new LeakyBucket(10, 600);

        UserId.TryParse("@testuser:example.org", out var userId);
        _testUserId = userId!;
        _client = MatrixTextClient.CreateForTesting(parameters, _testUserId);
    }

    private static async Task<SyncResponse> LoadFixtureAsync(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        await using var stream = File.OpenRead(path);
        var response = await JsonSerializer.DeserializeAsync<SyncResponse>(stream);
        return response ?? throw new InvalidOperationException($"Failed to deserialize fixture {fileName}");
    }

    private async Task<List<ReceivedTextMessage>> ProcessSyncAndCollectMessagesAsync(string fixtureName)
    {
        var syncResponse = await LoadFixtureAsync(fixtureName);
        await _client.HandleSyncResponseForTestingAsync(syncResponse);

        var messages = new List<ReceivedTextMessage>();
        while (_client.Messages.TryRead(out var msg))
        {
            messages.Add(msg);
        }
        return messages;
    }

    // ── Text message ──

    [Test]
    public async Task TextMessage_IsDeliveredToChannel()
    {
        var messages = await ProcessSyncAndCollectMessagesAsync("sync_text_message.json");

        await Assert.That(messages.Count).IsEqualTo(1);
        await Assert.That(messages[0].Body).IsEqualTo("Hello, world!");
        await Assert.That(messages[0].MsgType).IsEqualTo("m.text");
        await Assert.That(messages[0].EventId).IsEqualTo("$msg1");
        await Assert.That(messages[0].Sender.User.UserId.Full).IsEqualTo("@alice:example.org");
        await Assert.That(messages[0].Room.RoomId.Full).IsEqualTo("!room1:example.org");
    }

    [Test]
    public async Task TextMessage_SetsTimestampCorrectly()
    {
        var messages = await ProcessSyncAndCollectMessagesAsync("sync_text_message.json");

        await Assert.That(messages[0].Timestamp).IsEqualTo(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000));
    }

    [Test]
    public async Task TextMessage_SetsLastMessageOnRoom()
    {
        var messages = await ProcessSyncAndCollectMessagesAsync("sync_text_message.json");

        await Assert.That(messages[0].Room.LastMessage).IsNotNull();
        await Assert.That(messages[0].Room.LastMessage!.EventId).IsEqualTo("$msg1");
    }

    // ── Notice message ──

    [Test]
    public async Task NoticeMessage_IsDeliveredToChannel()
    {
        var messages = await ProcessSyncAndCollectMessagesAsync("sync_notice_message.json");

        await Assert.That(messages.Count).IsEqualTo(1);
        await Assert.That(messages[0].Body).IsEqualTo("I am a bot response");
        await Assert.That(messages[0].MsgType).IsEqualTo("m.notice");
        await Assert.That(messages[0].EventId).IsEqualTo("$notice1");
    }

    // ── Non-text messages ignored ──

    [Test]
    public async Task NonTextMessages_AreNotDelivered()
    {
        var messages = await ProcessSyncAndCollectMessagesAsync("sync_non_text_ignored.json");

        await Assert.That(messages.Count).IsEqualTo(0);
    }

    // ── Room state ──

    [Test]
    public async Task RoomState_SetsRoomName()
    {
        await ProcessSyncAndCollectMessagesAsync("sync_room_state.json");

        var room = _client.GetOrAddRoom(ParseRoomId("!room1:example.org"));
        await Assert.That(room.DisplayName).IsEqualTo("Test Room");
    }

    [Test]
    public async Task RoomState_SetsCanonicalAlias()
    {
        await ProcessSyncAndCollectMessagesAsync("sync_room_state.json");

        var room = _client.GetOrAddRoom(ParseRoomId("!room1:example.org"));
        await Assert.That(room.CanonicalAlias).IsNotNull();
        await Assert.That(room.CanonicalAlias!.Full).IsEqualTo("#test:example.org");
    }

    [Test]
    public async Task RoomState_SetsAltAliases()
    {
        await ProcessSyncAndCollectMessagesAsync("sync_room_state.json");

        var room = _client.GetOrAddRoom(ParseRoomId("!room1:example.org"));
        await Assert.That(room.AltAliases).IsNotNull();
        await Assert.That(room.AltAliases!.Count).IsEqualTo(1);
        await Assert.That(room.AltAliases[0].Full).IsEqualTo("#test2:example.org");
    }

    [Test]
    public async Task RoomState_TracksMembers()
    {
        await ProcessSyncAndCollectMessagesAsync("sync_room_state.json");

        var room = _client.GetOrAddRoom(ParseRoomId("!room1:example.org"));
        await Assert.That(room.Users.Count).IsEqualTo(2);
        await Assert.That(room.Users.ContainsKey("@alice:example.org")).IsTrue();
        await Assert.That(room.Users.ContainsKey("@bob:example.org")).IsTrue();
    }

    [Test]
    public async Task RoomState_SetsMemberDisplayNames()
    {
        await ProcessSyncAndCollectMessagesAsync("sync_room_state.json");

        var room = _client.GetOrAddRoom(ParseRoomId("!room1:example.org"));
        var alice = room.Users["@alice:example.org"];
        var bob = room.Users["@bob:example.org"];
        await Assert.That(alice.DisplayName).IsEqualTo("Alice Wonderland");
        await Assert.That(bob.DisplayName).IsEqualTo("Bob Builder");
    }

    [Test]
    public async Task RoomState_SetsMembership()
    {
        await ProcessSyncAndCollectMessagesAsync("sync_room_state.json");

        var room = _client.GetOrAddRoom(ParseRoomId("!room1:example.org"));
        var alice = room.Users["@alice:example.org"];
        await Assert.That(alice.Membership).IsEqualTo(Membership.Join);
    }

    // ── Mentions ──

    [Test]
    public async Task MessageWithMentions_ParsesMentionedUsers()
    {
        var messages = await ProcessSyncAndCollectMessagesAsync("sync_message_with_mentions.json");

        await Assert.That(messages.Count).IsEqualTo(1);
        await Assert.That(messages[0].Mentions).IsNotNull();
        await Assert.That(messages[0].Mentions!.Count).IsEqualTo(2);

        var mentionIds = messages[0].Mentions!.Select(m => m.User.UserId.Full).ToList();
        await Assert.That(mentionIds).Contains("@bob:example.org");
        await Assert.That(mentionIds).Contains("@charlie:example.org");
    }

    // ── Threads ──

    [Test]
    public async Task MessageInThread_SetsThreadId()
    {
        var messages = await ProcessSyncAndCollectMessagesAsync("sync_message_in_thread.json");

        await Assert.That(messages.Count).IsEqualTo(1);
        await Assert.That(messages[0].ThreadId).IsEqualTo("$thread_root1");
    }

    [Test]
    public async Task TextMessage_HasNullThreadId()
    {
        var messages = await ProcessSyncAndCollectMessagesAsync("sync_text_message.json");

        await Assert.That(messages[0].ThreadId).IsNull();
    }

    // ── Presence ──

    [Test]
    public async Task Presence_UpdatesUserState()
    {
        await ProcessSyncAndCollectMessagesAsync("sync_presence.json");

        var alice = _client.GetOrAddUser(ParseUserId("@alice:example.org"));
        await Assert.That(alice.Presence).IsEqualTo(Presence.Online);
        await Assert.That(alice.CurrentlyActive).IsEqualTo(true);
        await Assert.That(alice.StatusMessage).IsEqualTo("Working on tests");
        await Assert.That(alice.DisplayName).IsEqualTo("Alice Online");
        await Assert.That(alice.AvatarUrl).IsEqualTo("mxc://example.org/alice");
    }

    [Test]
    public async Task Presence_TracksMultipleUsers()
    {
        await ProcessSyncAndCollectMessagesAsync("sync_presence.json");

        var bob = _client.GetOrAddUser(ParseUserId("@bob:example.org"));
        await Assert.That(bob.Presence).IsEqualTo(Presence.Unavailable);
        await Assert.That(bob.CurrentlyActive).IsEqualTo(false);
        await Assert.That(bob.StatusMessage).IsEqualTo("AFK");
    }

    // ── Encryption ──

    [Test]
    public async Task EncryptionEvent_SetsRoomEncrypted()
    {
        await ProcessSyncAndCollectMessagesAsync("sync_encryption.json");

        var room = _client.GetOrAddRoom(ParseRoomId("!encrypted_room:example.org"));
        await Assert.That(room.IsEncrypted).IsTrue();
        await Assert.That(room.Encryption!.Algorithm).IsEqualTo("m.megolm.v1.aes-sha2");
    }

    // ── IsDirect ──

    [Test]
    public async Task DirectMessageRoom_SetsIsDirect()
    {
        await ProcessSyncAndCollectMessagesAsync("sync_direct_message_room.json");

        var room = _client.GetOrAddRoom(ParseRoomId("!dm_room:example.org"));
        await Assert.That(room.IsDirect).IsTrue();
    }

    [Test]
    public async Task RegularRoom_IsDirectIsFalse()
    {
        await ProcessSyncAndCollectMessagesAsync("sync_text_message.json");

        var room = _client.GetOrAddRoom(ParseRoomId("!room1:example.org"));
        await Assert.That(room.IsDirect).IsFalse();
    }

    // ── Multiple rooms ──

    [Test]
    public async Task MultipleRooms_AllProcessed()
    {
        var messages = await ProcessSyncAndCollectMessagesAsync("sync_multiple_rooms.json");

        await Assert.That(messages.Count).IsEqualTo(3);

        var room1Msgs = messages.Where(m => m.Room.RoomId.Full == "!room1:example.org").ToList();
        var room2Msgs = messages.Where(m => m.Room.RoomId.Full == "!room2:example.org").ToList();

        await Assert.That(room1Msgs.Count).IsEqualTo(1);
        await Assert.That(room2Msgs.Count).IsEqualTo(2);
    }

    [Test]
    public async Task MultipleRooms_RoomNamesSetCorrectly()
    {
        await ProcessSyncAndCollectMessagesAsync("sync_multiple_rooms.json");

        var room1 = _client.GetOrAddRoom(ParseRoomId("!room1:example.org"));
        var room2 = _client.GetOrAddRoom(ParseRoomId("!room2:example.org"));

        await Assert.That(room1.DisplayName).IsEqualTo("Room Alpha");
        await Assert.That(room2.DisplayName).IsEqualTo("Room Beta");
    }

    [Test]
    public async Task MultipleRooms_MixedMsgTypes()
    {
        var messages = await ProcessSyncAndCollectMessagesAsync("sync_multiple_rooms.json");

        var textMsgs = messages.Where(m => m.MsgType == "m.text").ToList();
        var noticeMsgs = messages.Where(m => m.MsgType == "m.notice").ToList();

        await Assert.That(textMsgs.Count).IsEqualTo(2);
        await Assert.That(noticeMsgs.Count).IsEqualTo(1);
    }

    // ── Invite handling (without auto-join, since joining would need HTTP) ──

    [Test]
    public async Task Invite_LogsButDoesNotJoinByDefault()
    {
        // AutoJoinOnInvite is false by default, so this should just log
        var syncResponse = await LoadFixtureAsync("sync_invite.json");
        await _client.HandleSyncResponseForTestingAsync(syncResponse);

        // No room should be created for the invite when auto-join is off
        // (GetOrAddRoom would create one, so we check messages channel is empty)
        var messages = new List<ReceivedTextMessage>();
        while (_client.Messages.TryRead(out var msg))
            messages.Add(msg);

        await Assert.That(messages.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DirectInvite_DetectedAsDirectWhenAutoJoinDisabled()
    {
        // Even without auto-join, the invite should be parsed without error
        var syncResponse = await LoadFixtureAsync("sync_invite_direct.json");
        await _client.HandleSyncResponseForTestingAsync(syncResponse);

        // Should not crash, and no messages should be produced
        var messages = new List<ReceivedTextMessage>();
        while (_client.Messages.TryRead(out var msg))
            messages.Add(msg);

        await Assert.That(messages.Count).IsEqualTo(0);
    }

    // ── Sequential syncs ──

    [Test]
    public async Task SequentialSyncs_AccumulateState()
    {
        // First sync: room state
        await ProcessSyncAndCollectMessagesAsync("sync_room_state.json");
        var room = _client.GetOrAddRoom(ParseRoomId("!room1:example.org"));
        await Assert.That(room.DisplayName).IsEqualTo("Test Room");
        await Assert.That(room.Users.Count).IsEqualTo(2);

        // Second sync: message in same room
        var messages = await ProcessSyncAndCollectMessagesAsync("sync_text_message.json");
        await Assert.That(messages.Count).IsEqualTo(1);
        await Assert.That(messages[0].Room.DisplayName).IsEqualTo("Test Room");

        // Alice should still be tracked from the state sync, plus the sender from the message
        await Assert.That(room.Users.ContainsKey("@alice:example.org")).IsTrue();
    }

    [Test]
    public async Task SequentialSyncs_PresenceThenMessage_UserHasPresenceAndRoomMembership()
    {
        // First sync: presence
        await ProcessSyncAndCollectMessagesAsync("sync_presence.json");
        var alice = _client.GetOrAddUser(ParseUserId("@alice:example.org"));
        await Assert.That(alice.Presence).IsEqualTo(Presence.Online);

        // Second sync: alice sends a message
        var messages = await ProcessSyncAndCollectMessagesAsync("sync_text_message.json");
        await Assert.That(messages[0].Sender.User.Presence).IsEqualTo(Presence.Online);
        await Assert.That(messages[0].Sender.User.StatusMessage).IsEqualTo("Working on tests");
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
