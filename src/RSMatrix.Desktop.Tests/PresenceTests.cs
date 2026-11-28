using RSMatrix.Desktop.Chat;
using RSMatrix.Desktop.Sample;
using RSMatrix.Desktop.Storage;
using RSMatrix.Desktop.ViewModels;

namespace RSMatrix.Desktop.Tests;

public class PresenceTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task Online_Turns_Idle_Without_Activity_And_Back_With_It()
    {
        var session = new PresenceSession();
        var now = Noon;
        var presence = new OwnPresenceViewModel(session, () => now, _ => { }, CancellationToken.None);
        presence.Start(new PresenceSettings { IdleMinutes = 5 });

        now = Noon.AddMinutes(4);
        presence.CheckIdle();
        await Assert.That(presence.IsIdle).IsFalse();

        now = Noon.AddMinutes(5);
        presence.CheckIdle();
        await Assert.That(presence.StateText).IsEqualTo("Idle");
        await Assert.That(presence.IsAway).IsTrue();

        presence.ReportActivity();
        await Assert.That(presence.StateText).IsEqualTo("Online");
        await Assert.That(string.Join(",", session.SentStates)).IsEqualTo("Online,Away,Online");
    }

    [Test]
    public async Task Away_And_Invisible_Stay_Until_Changed()
    {
        var session = new PresenceSession();
        var now = Noon;
        var presence = new OwnPresenceViewModel(session, () => now, _ => { }, CancellationToken.None);
        presence.Start(new PresenceSettings { Mode = PresenceMode.Invisible, IdleMinutes = 1 });

        presence.IsAwayMode = true;
        now = Noon.AddHours(1);
        presence.CheckIdle();
        presence.ReportActivity();

        await Assert.That(presence.StateText).IsEqualTo("Away");
        await Assert.That(string.Join(",", session.SentStates)).IsEqualTo("Offline,Away");
    }

    [Test]
    public async Task What_Others_See_Shows_Only_When_It_Lasts()
    {
        var session = new PresenceSession();
        var presence = new OwnPresenceViewModel(session, () => Noon, _ => { }, CancellationToken.None)
        {
            DifferenceDelay = TimeSpan.FromMilliseconds(50),
        };
        presence.Start(new PresenceSettings { Mode = PresenceMode.Invisible });

        // Another device keeps the user online.
        presence.ApplyReported(new ChatPresence(PresenceState.Online));
        await Assert.That(presence.LineText).IsEqualTo("Invisible");
        await WaitUntil(() => presence.OthersSee != null);
        await Assert.That(presence.LineText).IsEqualTo("Invisible · others see you online");

        presence.ApplyReported(new ChatPresence(PresenceState.Offline, "lunch"));
        await Assert.That(presence.LineText).IsEqualTo("Invisible · lunch");

        // A short blip doesn't show.
        presence.ApplyReported(new ChatPresence(PresenceState.Online, "lunch"));
        presence.ApplyReported(new ChatPresence(PresenceState.Offline, "lunch"));
        await Task.Delay(150);
        await Assert.That(presence.OthersSee).IsNull();
    }

    [Test]
    public async Task The_Status_Text_Is_Trimmed_And_Cleared()
    {
        var session = new PresenceSession();
        var presence = new OwnPresenceViewModel(session, () => Noon, _ => { }, CancellationToken.None);
        presence.StatusDraft = "  lunch ";
        presence.SaveStatusCommand.Execute(null);
        await Assert.That(presence.LineText).IsEqualTo("Online · lunch");

        presence.ClearStatusCommand.Execute(null);
        await Assert.That(presence.LineText).IsEqualTo("Online");
        await Assert.That(string.Join(",", session.SentStatusMessages.Select(m => m ?? "(none)"))).IsEqualTo("lunch,(none)");
    }

    [Test]
    public async Task Members_Follow_Presence_Changes()
    {
        await using var chat = new ChatViewModel(new SampleChatSession(Noon), _ => { }, getNow: () => Noon);
        chat.Apply(new RoomJoined(new ChatRoomInfo("!general:sample.local", "General", false, false), null));
        chat.SelectedConversation = chat.Rooms.Single();
        chat.ToggleMemberListCommand.Execute(null);
        await WaitUntil(() => chat.Members.Count > 0);

        var dave = chat.Members.Single(m => m.User.DisplayName == "Dave");
        await Assert.That(dave.IsOffline).IsTrue();
        await Assert.That(dave.ToolTipText).IsEqualTo("@dave:sample.local\nOffline, last seen yesterday");

        chat.Apply(new PresenceChanged("@dave:sample.local", new ChatPresence(PresenceState.Online, "back")));
        await Assert.That(dave.IsOnline).IsTrue();
        await Assert.That(dave.StatusMessage).IsEqualTo("back");
        await Assert.That(chat.Members.Single(m => m.User.DisplayName == "Frank").HasPresence).IsFalse();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(20);
    }

    /// <summary>Records what the app sets; nothing else is used.</summary>
    private sealed class PresenceSession : IChatSession
    {
        public List<PresenceState> SentStates { get; } = [];
        public List<string?> SentStatusMessages { get; } = [];
        public ChatUser CurrentUser { get; } = new("@me:example.org", "Me");
        public ChatPresence? CurrentUserPresence => null;
        public string ServerName => "example.org";

        public Task SetPresenceAsync(PresenceState state, CancellationToken cancellationToken)
        {
            SentStates.Add(state);
            return Task.CompletedTask;
        }

        public Task SetStatusMessageAsync(string? statusMessage, CancellationToken cancellationToken)
        {
            SentStatusMessages.Add(statusMessage);
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<ChatEvent> ReadEventsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> SendTextAsync(string roomId, string text, string? replyToEventId, string? threadRootId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SendReactionAsync(string roomId, string targetEventId, string key, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task RemoveReactionAsync(string roomId, string targetEventId, string key, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<HistoryPage> LoadHistoryAsync(string roomId, string? beforeEventId, int limit, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<ChatReaction>?> GetReactionsAsync(string roomId, string eventId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<ChatMember>> GetMembersAsync(string roomId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<byte[]?> DownloadMediaAsync(string mxcUri, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
