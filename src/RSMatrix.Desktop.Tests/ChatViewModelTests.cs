using System.Collections.ObjectModel;
using RSMatrix.Desktop.Chat;
using RSMatrix.Desktop.Content;
using RSMatrix.Desktop.Sample;
using RSMatrix.Desktop.ViewModels;

namespace RSMatrix.Desktop.Tests;

public class ChatViewModelTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly ChatRoomInfo Room = new("!room:example.org", "Room", IsEncrypted: false, IsDirect: false);
    private static readonly ChatUser Alice = new("@alice:example.org", "Alice");

    [Test]
    public async Task Thread_Replies_Go_To_Their_Thread_And_Show_On_The_Root()
    {
        await using var chat = CreateChat();
        chat.Apply(new MessageReceived(Room, Message("$root", Noon, "Root")));
        chat.Apply(new MessageReceived(Room, Message("$reply", Noon.AddMinutes(1), "Reply", threadRootId: "$root")));

        var room = chat.Rooms.Single();
        var root = (MessageViewModel)room.Timeline.Items.Last();
        await Assert.That(room.Timeline.MessageCount).IsEqualTo(1);
        await Assert.That(root.Thread!.ReplyCount).IsEqualTo(1);
        await Assert.That(root.Thread.Title).IsEqualTo("Root");
    }

    [Test]
    public async Task The_Sidebar_Shows_Three_Recent_Threads_And_The_Selected_One()
    {
        await using var chat = CreateChat();
        for (var i = 0; i < 5; i++)
        {
            chat.Apply(new MessageReceived(Room, Message($"$root{i}", Noon.AddMinutes(i), $"Root {i}")));
            chat.Apply(new MessageReceived(Room, Message($"$reply{i}", Noon.AddMinutes(10 + i), "Reply", threadRootId: $"$root{i}")));
        }
        var room = chat.Rooms.Single();

        await Assert.That(SidebarTitles(chat)).IsEqualTo("Room | Root 4 | Root 3 | Root 2 | 2 more threads");

        chat.SelectedConversation = room.GetOrCreateThread("$root0");
        await Assert.That(SidebarTitles(chat)).IsEqualTo("Room | Root 4 | Root 3 | Root 2 | Root 0 | 1 more thread");
    }

    [Test]
    public async Task The_Window_Title_Shows_The_Selected_Room_Or_The_Server()
    {
        await using var chat = CreateChat();
        await Assert.That(chat.WindowTitle).IsEqualTo("sample.local");

        chat.Apply(new MessageReceived(Room, Message("$root", Noon, "Root")));
        chat.Apply(new MessageReceived(Room, Message("$reply", Noon.AddMinutes(1), "Reply", threadRootId: "$root")));
        var room = chat.Rooms.Single();
        chat.SelectedConversation = room.GetOrCreateThread("$root");
        await Assert.That(chat.WindowTitle).IsEqualTo("Room");

        chat.Apply(new MessageReceived(Room with { Name = "Renamed" }, Message("$next", Noon.AddMinutes(2), "Next")));
        await Assert.That(chat.WindowTitle).IsEqualTo("Renamed");
    }

    [Test]
    public async Task The_Member_List_Follows_The_Selected_Room()
    {
        await using var chat = CreateChat();
        var general = Room with { RoomId = "!general:sample.local", Name = "General" };
        var builds = Room with { RoomId = "!builds:sample.local", Name = "Builds" };
        chat.Apply(new MessageReceived(general, Message("$1", Noon, "Hi")));
        chat.Apply(new MessageReceived(builds, Message("$2", Noon, "Build passed")));
        chat.SelectedConversation = chat.Rooms.First(r => r.Name == "General");

        chat.ToggleMemberListCommand.Execute(null);
        await WaitUntil(() => chat.Members.Count > 0);
        await Assert.That(string.Join(",", chat.Members.Select(m => m.User.DisplayName)))
            .IsEqualTo("Alice,Bob,Carol,Dave,Erin,Rob,Frank");
        await Assert.That(chat.Members[^1].IsInvited).IsTrue();
        await Assert.That(chat.JoinedMemberCount).IsEqualTo(6);

        chat.SelectedConversation = chat.Rooms.First(r => r.Name == "Builds");
        await WaitUntil(() => chat.Members.Count == 3);
        await Assert.That(string.Join(",", chat.Members.Select(m => m.User.DisplayName))).IsEqualTo("Bob,Build Bot,Rob");

        chat.ToggleMemberListCommand.Execute(null);
        await Assert.That(chat.Members.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Joined_Rooms_Are_Listed_By_Latest_Message_And_Follow_Changes()
    {
        await using var chat = CreateChat();
        var quiet = Room with { RoomId = "!quiet:example.org", Name = "Quiet" };
        var older = Room with { RoomId = "!older:example.org", Name = "Older" };
        chat.Apply(new RoomJoined(quiet, null));
        chat.Apply(new RoomJoined(older, Message("$old", Noon, "Old news") with { RoomId = older.RoomId }));
        chat.Apply(new RoomJoined(Room, Message("$new", Noon.AddMinutes(1), "Fresh")));

        await Assert.That(string.Join(",", chat.Rooms.Select(r => r.Name))).IsEqualTo("Room,Older,Quiet");
        await Assert.That(chat.Rooms[0].LastMessagePreview).IsEqualTo("Alice: Fresh");
        await Assert.That(chat.Rooms.All(r => r.UnreadCount == 0)).IsTrue();

        chat.SelectedConversation = chat.Rooms[0];
        chat.Apply(new RoomUpdated(Room with { Name = "Renamed" }));
        await Assert.That(chat.WindowTitle).IsEqualTo("Renamed");

        chat.Apply(new RoomLeft(Room.RoomId));
        await Assert.That(string.Join(",", chat.Rooms.Select(r => r.Name))).IsEqualTo("Older,Quiet");
        await Assert.That(chat.SelectedConversation).IsEqualTo(chat.Rooms[0]);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(20);
    }

    [Test]
    public async Task Collection_Sync_Moves_Inserts_And_Removes()
    {
        string a = "a", b = "b", c = "c", d = "d";
        var items = new ObservableCollection<string>([a, b, c]);

        CollectionSync.Apply(items, [c, a, d]);

        await Assert.That(string.Join(",", items)).IsEqualTo("c,a,d");
    }

    [Test]
    public async Task Recent_Reactions_Keep_The_Last_Three_Used()
    {
        var recent = new RecentReactions();

        recent.Use("🍪");
        recent.Use("❤️");
        recent.Use("🚀");

        await Assert.That(string.Join(",", recent.Keys)).IsEqualTo("🚀,❤️,🍪");
    }

    private static ChatViewModel CreateChat() => new(new SampleChatSession(Noon), _ => { }, getNow: () => Noon);

    private static ChatMessage Message(string eventId, DateTimeOffset timestamp, string body, string? threadRootId = null)
        => new(Room.RoomId, eventId, Alice, timestamp, ChatMessageKind.Text, body, PlainTextParser.Parse(body),
            IsEncrypted: false, threadRootId);

    private static string SidebarTitles(ChatViewModel chat)
        => string.Join(" | ", chat.SidebarEntries.Select(entry => entry switch
        {
            ConversationViewModel conversation => conversation.Title,
            MoreThreadsViewModel more => more.Text,
            _ => "?",
        }));
}
