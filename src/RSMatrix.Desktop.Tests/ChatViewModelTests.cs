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

    private static ChatViewModel CreateChat() => new(new SampleChatSession(Noon), _ => { }, () => Noon);

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
