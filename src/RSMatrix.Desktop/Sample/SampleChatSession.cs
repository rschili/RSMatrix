using System.Threading.Channels;
using RSMatrix.Desktop.Chat;
using RSMatrix.Desktop.Content;

namespace RSMatrix.Desktop.Sample;

/// <summary>
/// Offline session with built-in rooms and messages. Shows formatting and images that RSMatrix
/// doesn't deliver yet, and serves the snapshot renderer.
/// </summary>
public sealed class SampleChatSession : IChatSession
{
    private const string LandscapeUri = "mxc://sample.local/landscape";
    private const string EmojiUri = "mxc://sample.local/emoji-smile";

    private static readonly ChatUser Me = new("@rob:sample.local", "Rob");
    private static readonly ChatUser Alice = new("@alice:sample.local", "Alice");
    private static readonly ChatUser Bob = new("@bob:sample.local", "Bob");
    private static readonly ChatUser Carol = new("@carol:sample.local", "Carol");
    private static readonly ChatUser Builder = new("@ci:sample.local", "Build Bot");

    private static readonly ChatRoomInfo General = new("!general:sample.local", "General", IsEncrypted: false, IsDirect: false);
    private static readonly ChatRoomInfo Design = new("!design:sample.local", "Design", IsEncrypted: true, IsDirect: false);
    private static readonly ChatRoomInfo AliceDirect = new("!alice:sample.local", "Alice", IsEncrypted: true, IsDirect: true);
    private static readonly ChatRoomInfo Builds = new("!builds:sample.local", "Builds", IsEncrypted: false, IsDirect: false);
    private static readonly ChatRoomInfo Lounge = new("!lounge:sample.local", "Lounge", IsEncrypted: false, IsDirect: false);

    private readonly Channel<ChatEvent> _events = Channel.CreateUnbounded<ChatEvent>();
    private readonly DateTimeOffset _now;
    private int _nextEventNumber;

    public SampleChatSession(DateTimeOffset? now = null)
    {
        _now = now ?? DateTimeOffset.Now;
        foreach (var (room, message) in CreateMessages())
            _events.Writer.TryWrite(new MessageReceived(room, message));
        _events.Writer.TryWrite(new ConnectionChanged(ConnectionState.Online, null));
        // Arrives after the initial sync, so it counts as unread.
        _events.Writer.TryWrite(new MessageReceived(Lounge, CreateMessage(Lounge, Carol, _now.AddMinutes(-1),
            ChatMessageKind.Text, "Lunch at 12 tomorrow, then?", null)));
    }

    public ChatUser CurrentUser => Me;

    public string ServerName => "sample.local";

    public IAsyncEnumerable<ChatEvent> ReadEventsAsync(CancellationToken cancellationToken)
        => _events.Reader.ReadAllAsync(cancellationToken);

    public async Task<string> SendTextAsync(string roomId, string text, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        var room = AllRooms.First(r => r.RoomId == roomId);
        var message = CreateMessage(room, Me, DateTimeOffset.Now, ChatMessageKind.Text, text, PlainTextParser.Parse(text));
        _events.Writer.TryWrite(new MessageReceived(room, message));
        return message.EventId;
    }

    public async Task<HistoryPage> LoadHistoryAsync(string roomId, string? from, int limit, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken).ConfigureAwait(false);
        if (roomId != Lounge.RoomId)
            return new HistoryPage([], null);

        // The lounge has three pages of older small talk to try scrolling back.
        var page = from == null ? 0 : int.Parse(from);
        var start = _now.AddDays(-3 - page);
        var senders = new[] { Alice, Bob, Carol, Me };
        var messages = Enumerable.Range(0, limit)
            .Select(i => CreateMessage(Lounge, senders[(i + page) % senders.Length], start.AddMinutes(i * 7),
                ChatMessageKind.Text, $"Older message {page * limit + limit - i} from the archive.", null))
            .ToList();
        return new HistoryPage(messages, page < 2 ? (page + 1).ToString() : null);
    }

    public async Task<byte[]?> DownloadMediaAsync(string mxcUri, CancellationToken cancellationToken)
    {
        var name = mxcUri switch
        {
            LandscapeUri => "landscape.png",
            EmojiUri => "emoji-smile.png",
            _ => null,
        };
        if (name == null)
            return null;
        await using var stream = typeof(SampleChatSession).Assembly.GetManifestResourceStream($"RSMatrix.Desktop.Sample.{name}");
        if (stream == null)
            return null;
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
        return memory.ToArray();
    }

    public ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private static IEnumerable<ChatRoomInfo> AllRooms => [General, Design, AliceDirect, Builds, Lounge];

    private IEnumerable<(ChatRoomInfo, ChatMessage)> CreateMessages()
    {
        var yesterday = _now.Date.AddDays(-1).AddHours(17);
        var today = _now.AddMinutes(-42);

        (ChatRoomInfo, ChatMessage) Text(ChatRoomInfo room, ChatUser sender, DateTimeOffset time, string body)
            => (room, CreateMessage(room, sender, time, ChatMessageKind.Text, body, null));
        (ChatRoomInfo, ChatMessage) Html(ChatRoomInfo room, ChatUser sender, DateTimeOffset time, string body, string html,
            ChatMessageKind kind = ChatMessageKind.Text)
            => (room, CreateMessage(room, sender, time, kind, body, MatrixHtmlParser.Parse(html)));

        yield return Text(Lounge, Carol, yesterday.AddHours(-3), "Anyone up for lunch tomorrow?");
        yield return Text(Lounge, Bob, yesterday.AddHours(-2.9), "Sure, the usual place?");

        yield return Text(General, Alice, yesterday, "Has anyone tried the new desktop client yet?");
        yield return Text(General, Bob, yesterday.AddMinutes(2), "Not yet. Is it built on RSMatrix?");
        yield return Html(General, Alice, yesterday.AddMinutes(3),
            "Yes, Avalonia on top of RSMatrix, with a small adapter in between.",
            "Yes, <b>Avalonia</b> on top of <em>RSMatrix</em>, with a small <code>IChatSession</code> adapter in between.");
        yield return Text(General, Alice, yesterday.AddMinutes(3.5),
            "It renders the HTML subset from the spec natively, so nothing goes through a browser engine.");

        yield return Html(General, Bob, today, "Rob: nice. What about code?",
            "<a href=\"https://matrix.to/#/@rob:sample.local\">Rob</a>: nice. What about code?");
        yield return Html(General, Me, today.AddMinutes(1), "Code blocks keep their whitespace:",
            """
            <p>Code blocks keep their whitespace:</p>
            <pre><code class="language-csharp">await foreach (var message in client.Messages.ReadAllAsync(token))
            {
                if (message.Body == "ping")
                    await message.SendResponseAsync("pong");
            }
            </code></pre>
            """);
        yield return Html(General, Carol, today.AddMinutes(4), "> Code blocks keep their whitespace\nAnd quotes?",
            "<blockquote><p>Code blocks keep their whitespace</p></blockquote><p>And quotes? And <s>strikethrough</s>, <u>underline</u>, H<sub>2</sub>O and E = mc<sup>2</sup>?</p>");
        yield return Html(General, Me, today.AddMinutes(5), "All of them. Lists too.",
            """
            <p>All of them. Lists too:</p>
            <ol start="3"><li>numbered lists keep their <code>start</code></li><li>nested lists work
            <ul><li>like this</li><li>and this</li></ul></li></ol>
            <p>Colors are allowed by the spec: <span data-mx-color="#2e7d32">green</span> and <span data-mx-bg-color="#fff3a0" data-mx-color="#222222">highlighted</span>. Spoilers stay hidden: <span data-mx-spoiler>the butler did it</span>.</p>
            """);
        yield return Html(General, Alice, today.AddMinutes(6), "<script>alert('hi')</script> Unsafe tags are dropped",
            "<script>alert('hi')</script><p onclick=\"steal()\">Unsafe tags are dropped, and <a href=\"javascript:alert(1)\">this link</a> stays plain text. Remote images are not loaded: <img src=\"https://tracker.example/pixel.png\" alt=\"[tracking pixel]\"></p>");
        yield return Text(General, Bob, today.AddMinutes(7),
            "Plain messages get their links detected: https://spec.matrix.org/v1.17/client-server-api/ and line breaks\nare\nkept.");
        yield return Text(General, Carol, today.AddMinutes(30),
            "Long messages wrap nicely. Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat. 🎉");

        yield return Html(Design, Carol, today.AddMinutes(-20), "Moodboard for the login screen",
            "<p>Moodboard for the login screen <img src=\"" + EmojiUri + "\" height=\"20\" alt=\":smile:\"></p>");
        yield return (Design, CreateMessage(Design, Carol, today.AddMinutes(-19), ChatMessageKind.Image, "landscape.png",
            new MessageDocument([new ImageBlock(new MediaReference(LandscapeUri, "landscape.png", 960, 600))])));
        yield return Text(Design, Me, today.AddMinutes(-10), "Calm colors, I like it. Keep the chrome minimal.");

        yield return Text(AliceDirect, Alice, today.AddMinutes(-35), "Are we still on for the review on Friday?");
        yield return Text(AliceDirect, Me, today.AddMinutes(-33), "Yes. I'll bring the screenshots.");

        yield return Html(Builds, Builder, today.AddMinutes(-5), "Build 412 passed",
            """
            <h3>Build 412 passed</h3>
            <table><tr><th>Project</th><th>Tests</th><th>Time</th></tr>
            <tr><td>RSMatrix</td><td>312 passed</td><td>41 s</td></tr>
            <tr><td>RSMatrix.Desktop</td><td>28 passed</td><td>3 s</td></tr></table>
            <p>Details: <a href="https://github.com/rschili/RSMatrix/actions">GitHub Actions</a></p>
            """, ChatMessageKind.Notice);
        yield return (General, CreateMessage(General, Bob, today.AddMinutes(31), ChatMessageKind.Emote, "waves",
            PlainTextParser.Parse("waves")));
    }

    private ChatMessage CreateMessage(ChatRoomInfo room, ChatUser sender, DateTimeOffset timestamp, ChatMessageKind kind,
        string body, MessageDocument? content)
        => new(room.RoomId, $"$sample{Interlocked.Increment(ref _nextEventNumber)}", sender, timestamp, kind, body,
            content ?? PlainTextParser.Parse(body), room.IsEncrypted);
}
