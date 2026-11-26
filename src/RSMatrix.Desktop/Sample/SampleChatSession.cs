using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private static readonly JsonSerializerOptions SourceJsonOptions =
        new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly ChatUser Me = new("@rob:sample.local", "Rob");
    private static readonly ChatUser Alice = new("@alice:sample.local", "Alice");
    private static readonly ChatUser Bob = new("@bob:sample.local", "Bob");
    private static readonly ChatUser Carol = new("@carol:sample.local", "Carol");
    private static readonly ChatUser Dave = new("@dave:sample.local", "Dave");
    private static readonly ChatUser Erin = new("@erin:sample.local", "Erin");
    private static readonly ChatUser Frank = new("@frank:sample.local", "Frank");
    private static readonly ChatUser Builder = new("@ci:sample.local", "Build Bot");

    private static readonly ChatRoomInfo General = new("!general:sample.local", "General", IsEncrypted: false, IsDirect: false);
    private static readonly ChatRoomInfo Design = new("!design:sample.local", "Design", IsEncrypted: true, IsDirect: false);
    private static readonly ChatRoomInfo AliceDirect = new("!alice:sample.local", "Alice", IsEncrypted: true, IsDirect: true);
    private static readonly ChatRoomInfo Builds = new("!builds:sample.local", "Builds", IsEncrypted: false, IsDirect: false);
    private static readonly ChatRoomInfo Lounge = new("!lounge:sample.local", "Lounge", IsEncrypted: false, IsDirect: false);

    private readonly Channel<ChatEvent> _events = Channel.CreateUnbounded<ChatEvent>();
    private readonly DateTimeOffset _now;
    private int _nextEventNumber;
    private readonly Dictionary<string, int> _historyPageByOldestEventId = [];

    public SampleChatSession(DateTimeOffset? now = null)
    {
        _now = now ?? DateTimeOffset.Now;
        foreach (var chatEvent in CreateEvents())
            _events.Writer.TryWrite(chatEvent);
        _events.Writer.TryWrite(new ConnectionChanged(ConnectionState.Online, null));
        // Arrives after the initial sync, so it counts as unread.
        _events.Writer.TryWrite(new MessageReceived(Lounge, CreateMessage(Lounge, Carol, _now.AddMinutes(-1),
            ChatMessageKind.Text, "Lunch at 12 tomorrow, then?")));
    }

    public ChatUser CurrentUser => Me;

    public string ServerName => "sample.local";

    public IAsyncEnumerable<ChatEvent> ReadEventsAsync(CancellationToken cancellationToken)
        => _events.Reader.ReadAllAsync(cancellationToken);

    public async Task<string> SendTextAsync(string roomId, string text, string? replyToEventId, string? threadRootId,
        CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        var room = AllRooms.First(r => r.RoomId == roomId);
        var message = CreateMessage(room, Me, DateTimeOffset.Now, ChatMessageKind.Text, text,
            threadRootId: threadRootId, replyToEventId: replyToEventId);
        _events.Writer.TryWrite(new MessageReceived(room, message));
        _ = ReadLaterAsync(room, message.EventId);
        return message.EventId;
    }

    public async Task SendReactionAsync(string roomId, string targetEventId, string key, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        _events.Writer.TryWrite(new ReactionReceived(roomId, targetEventId, key, Me));
    }

    public async Task RemoveReactionAsync(string roomId, string targetEventId, string key, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        _events.Writer.TryWrite(new ReactionRemoved(roomId, targetEventId, key, Me));
    }

    /// <summary>Someone in the room reads a sent message after a moment.</summary>
    private async Task ReadLaterAsync(ChatRoomInfo room, string eventId)
    {
        var reader = room == Lounge || room == Design ? Carol : room == Builds ? null : Alice;
        if (reader == null)
            return;
        await Task.Delay(TimeSpan.FromSeconds(1.5)).ConfigureAwait(false);
        _events.Writer.TryWrite(new ReceiptReceived(room.RoomId, eventId, reader));
    }

    public async Task<HistoryPage> LoadHistoryAsync(string roomId, string? beforeEventId, int limit, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken).ConfigureAwait(false);
        if (roomId != Lounge.RoomId)
            return new HistoryPage([], false);

        // The lounge has three pages of older small talk to try scrolling back.
        var page = beforeEventId != null && _historyPageByOldestEventId.TryGetValue(beforeEventId, out var previous) ? previous + 1 : 0;
        var start = _now.AddDays(-3 - page);
        var senders = new[] { Alice, Bob, Carol, Me };
        var messages = Enumerable.Range(0, limit)
            .Select(i => CreateMessage(Lounge, senders[(i + page) % senders.Length], start.AddMinutes(i * 7),
                ChatMessageKind.Text, $"Older message {page * limit + limit - i} from the archive."))
            .ToList();
        _historyPageByOldestEventId[messages[0].EventId] = page;
        return new HistoryPage(messages, page < 2);
    }

    public async Task<IReadOnlyList<ChatMember>> GetMembersAsync(string roomId, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken).ConfigureAwait(false);
        ChatUser[] joined = roomId switch
        {
            _ when roomId == AliceDirect.RoomId => [Me, Alice],
            _ when roomId == Builds.RoomId => [Me, Bob, Builder],
            _ when roomId == Design.RoomId => [Me, Alice, Carol, Erin],
            _ => [Me, Alice, Bob, Carol, Dave, Erin],
        };
        var invited = roomId == General.RoomId ? new[] { Frank } : [];
        return [.. joined.Select(u => new ChatMember(u, false)), .. invited.Select(u => new ChatMember(u, true))];
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

    private IEnumerable<ChatEvent> CreateEvents()
    {
        var yesterday = _now.Date.AddDays(-1).AddHours(17);
        var today = _now.AddMinutes(-42);

        MessageReceived Text(ChatRoomInfo room, ChatUser sender, DateTimeOffset time, string body,
            MessageReceived? replyTo = null)
            => new(room, CreateMessage(room, sender, time, ChatMessageKind.Text, body,
                replyToEventId: replyTo?.Message.EventId));
        MessageReceived Html(ChatRoomInfo room, ChatUser sender, DateTimeOffset time, string body, string html,
            ChatMessageKind kind = ChatMessageKind.Text, MessageReceived? replyTo = null)
            => new(room, CreateMessage(room, sender, time, kind, body, html, replyToEventId: replyTo?.Message.EventId));
        MessageReceived InThread(MessageReceived root, ChatUser sender, DateTimeOffset time, string body)
            => new(root.Room, CreateMessage(root.Room, sender, time, ChatMessageKind.Text, body,
                threadRootId: root.Message.EventId));
        ReactionReceived React(MessageReceived target, string key, ChatUser sender)
            => new(target.Room.RoomId, target.Message.EventId, key, sender);
        ReceiptReceived Read(MessageReceived target, ChatUser user)
            => new(target.Room.RoomId, target.Message.EventId, user);

        yield return Text(Lounge, Carol, yesterday.AddHours(-3), "Anyone up for lunch tomorrow?");
        var usualPlace = Text(Lounge, Bob, yesterday.AddHours(-2.9), "Sure, the usual place?");
        yield return usualPlace;
        yield return Read(usualPlace, Alice);

        var question = Text(General, Alice, yesterday, "Has anyone tried the new desktop client yet?");
        yield return question;
        yield return React(question, "👀", Bob);
        yield return React(question, "👀", Carol);
        yield return InThread(question, Bob, yesterday.AddMinutes(10), "Tried it this morning. Scrolling back feels smooth.");
        yield return InThread(question, Alice, yesterday.AddMinutes(12), "Same here. The dark theme is nice too.");
        yield return Text(General, Bob, yesterday.AddMinutes(2), "Not yet. Is it built on RSMatrix?");
        var answer = Html(General, Alice, yesterday.AddMinutes(3),
            "Yes, Avalonia on top of RSMatrix, with a small adapter in between.",
            "Yes, <b>Avalonia</b> on top of <em>RSMatrix</em>, with a small <code>IChatSession</code> adapter in between.");
        yield return answer;
        yield return React(answer, "🚀", Bob);
        yield return React(answer, "🚀", Me);
        yield return React(answer, "🍪", Carol);
        yield return React(answer, "🍪", Bob);
        yield return React(answer, "🍪", Me);
        yield return React(answer, "☕", Carol);
        yield return Text(General, Alice, yesterday.AddMinutes(3.5),
            "It renders the HTML subset from the spec natively, so nothing goes through a browser engine.");

        yield return Html(General, Bob, today, "Rob: nice. What about code?",
            "<a href=\"https://matrix.to/#/@rob:sample.local\">Rob</a>: nice. What about code?");
        var code = Html(General, Me, today.AddMinutes(1), "Code blocks keep their whitespace:",
            """
            <p>Code blocks keep their whitespace:</p>
            <pre><code class="language-csharp">await foreach (var update in client.Updates.ReadAllAsync(token))
            {
                if (update is NewMessage { Message.Body: "ping" } ping)
                    await ping.Message.ReplyAsync("pong");
            }
            </code></pre>
            """);
        yield return code;
        yield return React(code, "👍", Bob);
        yield return InThread(code, Carol, today.AddMinutes(2), "Does it highlight syntax?");
        yield return InThread(code, Me, today.AddMinutes(3), "Not yet, it's monospace only.");
        yield return Html(General, Carol, today.AddMinutes(4), "> Code blocks keep their whitespace\nAnd quotes?",
            "<blockquote><p>Code blocks keep their whitespace</p></blockquote><p>And quotes? And <s>strikethrough</s>, <u>underline</u>, H<sub>2</sub>O and E = mc<sup>2</sup>?</p>",
            replyTo: code);
        var lists = Html(General, Me, today.AddMinutes(5), "All of them. Lists too.",
            """
            <p>All of them. Lists too:</p>
            <ol start="3"><li>numbered lists keep their <code>start</code></li><li>nested lists work
            <ul><li>like this</li><li>and this</li></ul></li></ol>
            <p>Colors are allowed by the spec: <span data-mx-color="#2e7d32">green</span> and <span data-mx-bg-color="#fff3a0" data-mx-color="#222222">highlighted</span>. Spoilers stay hidden: <span data-mx-spoiler>the butler did it</span>.</p>
            """);
        yield return lists;
        yield return InThread(lists, Alice, today.AddMinutes(8), "Tables too?");
        yield return InThread(lists, Me, today.AddMinutes(9), "Yes, have a look at the Builds room.");
        yield return Html(General, Alice, today.AddMinutes(6), "<script>alert('hi')</script> Unsafe tags are dropped",
            "<script>alert('hi')</script><p onclick=\"steal()\">Unsafe tags are dropped, and <a href=\"javascript:alert(1)\">this link</a> stays plain text. Remote images are not loaded: <img src=\"https://tracker.example/pixel.png\" alt=\"[tracking pixel]\"></p>");
        yield return Text(General, Bob, today.AddMinutes(7),
            "Plain messages get their links detected: https://spec.matrix.org/v1.17/client-server-api/ and line breaks\nare\nkept.");
        var longMessage = Text(General, Carol, today.AddMinutes(30),
            "Long messages wrap nicely. Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat. 🎉");
        yield return longMessage;
        yield return Read(longMessage, Dave);
        yield return InThread(longMessage, Bob, today.AddMinutes(33), "Lorem ipsum never gets old.");
        yield return Html(Design, Carol, today.AddMinutes(-20), "Moodboard for the login screen",
            "<p>Moodboard for the login screen <img src=\"" + EmojiUri + "\" height=\"20\" alt=\":smile:\"></p>");
        var landscape = new MessageReceived(Design, CreateMessage(Design, Carol, today.AddMinutes(-19), ChatMessageKind.Image,
            "landscape.png", image: new MediaReference(LandscapeUri, "landscape.png", 960, 600)));
        yield return landscape;
        yield return React(landscape, "😍", Me);
        yield return InThread(landscape, Me, today.AddMinutes(-15), "Is that from the Alps?");
        yield return InThread(landscape, Carol, today.AddMinutes(-14), "The Dolomites, last summer.");
        var calmColors = Text(Design, Me, today.AddMinutes(-10), "Calm colors, I like it. Keep the chrome minimal.");
        yield return calmColors;
        yield return Read(calmColors, Carol);

        var review = Text(AliceDirect, Alice, today.AddMinutes(-35), "Are we still on for the review on Friday?");
        yield return review;
        var screenshots = Text(AliceDirect, Me, today.AddMinutes(-33), "Yes. I'll bring the screenshots.", replyTo: review);
        yield return screenshots;
        yield return React(screenshots, "👍", Alice);
        yield return Read(screenshots, Alice);

        yield return Html(Builds, Builder, today.AddMinutes(-5), "Build 412 passed",
            """
            <h3>Build 412 passed</h3>
            <table><tr><th>Project</th><th>Tests</th><th>Time</th></tr>
            <tr><td>RSMatrix</td><td>312 passed</td><td>41 s</td></tr>
            <tr><td>RSMatrix.Desktop</td><td>28 passed</td><td>3 s</td></tr></table>
            <p>Details: <a href="https://github.com/rschili/RSMatrix/actions">GitHub Actions</a></p>
            """, ChatMessageKind.Notice);
        var waves = new MessageReceived(General, CreateMessage(General, Bob, today.AddMinutes(31), ChatMessageKind.Emote, "waves"));
        yield return waves;
        yield return Read(waves, Alice);
        yield return Read(waves, Carol);
        yield return Read(waves, Erin);
        yield return Read(waves, Frank);
    }

    private ChatMessage CreateMessage(ChatRoomInfo room, ChatUser sender, DateTimeOffset timestamp, ChatMessageKind kind,
        string body, string? html = null, MediaReference? image = null, string? threadRootId = null, string? replyToEventId = null)
    {
        var eventId = $"$sample{Interlocked.Increment(ref _nextEventNumber)}";
        var document = image != null ? new MessageDocument([new ImageBlock(image)])
            : html != null ? MatrixHtmlParser.Parse(html)
            : PlainTextParser.Parse(body);

        var content = new JsonObject
        {
            ["msgtype"] = kind switch
            {
                ChatMessageKind.Notice => "m.notice",
                ChatMessageKind.Emote => "m.emote",
                ChatMessageKind.Image => "m.image",
                _ => "m.text",
            },
            ["body"] = body,
        };
        if (html != null)
        {
            content["format"] = "org.matrix.custom.html";
            content["formatted_body"] = html;
        }
        if (image != null)
        {
            content["url"] = image.MxcUri;
            content["info"] = new JsonObject { ["w"] = image.Width, ["h"] = image.Height };
        }
        if (threadRootId != null)
            content["m.relates_to"] = new JsonObject { ["rel_type"] = "m.thread", ["event_id"] = threadRootId };
        else if (replyToEventId != null)
            content["m.relates_to"] = new JsonObject { ["m.in_reply_to"] = new JsonObject { ["event_id"] = replyToEventId } };
        var source = new JsonObject
        {
            ["type"] = "m.room.message",
            ["event_id"] = eventId,
            ["room_id"] = room.RoomId,
            ["sender"] = sender.UserId,
            ["origin_server_ts"] = timestamp.ToUnixTimeMilliseconds(),
            ["content"] = content,
        };

        return new ChatMessage(room.RoomId, eventId, sender, timestamp, kind, body, document, room.IsEncrypted,
            threadRootId, replyToEventId, source.ToJsonString(SourceJsonOptions));
    }
}
