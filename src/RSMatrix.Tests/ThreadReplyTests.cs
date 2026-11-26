using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RSMatrix.Http;
using RSMatrix.Models;

namespace RSMatrix.Tests;

public class ThreadReplyTests
{
    private const string ThreadRoot = "$thread-root";
    private const string ReceivedEvent = "$received-event";
    private const string Body = "Hello <world> & friends";
    private const string HtmlBody = "<p>Hello &lt;world&gt; &amp; <strong>friends</strong></p>";

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task Reply_StaysInTheThread_AndQuotesOnlyWhenAsked(bool threaded, bool quote)
    {
        using var handler = new CaptureHandler();
        var message = CreateMessage(handler, threaded);
        var mentions = new List<MatrixId> { ParseUserId("@Alice:example.org"), ParseUserId("@bob:example.org") };

        var eventId = await message.ReplyAsync(Body, quote: quote, mentions: mentions);

        await Assert.That(eventId).IsEqualTo("$sent");
        using var json = await ReadRequestAsync(handler);
        await AssertMessageAsync(json.RootElement, "text", withMentions: true);
        await AssertRelationAsync(json.RootElement, threaded, isReply: quote);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Reply_QuotesByDefault_WithoutMentions(bool threaded)
    {
        using var handler = new CaptureHandler();
        var message = CreateMessage(handler, threaded);

        await message.ReplyAsync(Body);

        using var json = await ReadRequestAsync(handler);
        await AssertMessageAsync(json.RootElement, "text", withMentions: false);
        await AssertRelationAsync(json.RootElement, threaded, isReply: true);
    }

    [Test]
    [Arguments("text")]
    [Arguments("html")]
    [Arguments("notice")]
    [Arguments("htmlnotice")]
    public async Task RoomSend_SetsTypeAndHtml(string kind)
    {
        using var handler = new CaptureHandler();
        var room = CreateMessage(handler, threaded: false).Room;

        var (html, notice) = Options(kind);
        await room.SendMessageAsync(Body, html, notice);

        using var json = await ReadRequestAsync(handler);
        await AssertMessageAsync(json.RootElement, kind, withMentions: false);
        await AssertRelationAsync(json.RootElement, threaded: false, isReply: false);
    }

    [Test]
    public async Task RoomSend_WithReplyTarget_DoesNotTreatItAsThreadRoot()
    {
        using var handler = new CaptureHandler();
        var room = CreateMessage(handler, threaded: true).Room;

        await room.SendMessageAsync(Body, replyToId: ReceivedEvent);

        using var json = await ReadRequestAsync(handler);
        await AssertRelationAsync(json.RootElement, threaded: false, isReply: true);
    }

    [Test]
    public async Task RoomSend_ToThread_FallsBackToTheRoot_WhenNoThreadMessageIsLoaded()
    {
        using var handler = new CaptureHandler();
        var room = CreateMessage(handler, threaded: false).Room;

        await room.SendMessageAsync(Body, threadRootId: ThreadRoot);

        using var json = await ReadRequestAsync(handler);
        var relation = json.RootElement.GetProperty("m.relates_to");
        await Assert.That(relation.GetProperty("rel_type").GetString()).IsEqualTo("m.thread");
        await Assert.That(relation.GetProperty("event_id").GetString()).IsEqualTo(ThreadRoot);
        await Assert.That(relation.GetProperty("is_falling_back").GetBoolean()).IsTrue();
        await Assert.That(relation.GetProperty("m.in_reply_to").GetProperty("event_id").GetString()).IsEqualTo(ThreadRoot);
    }

    [Test]
    public async Task RoomSend_ToThread_FallsBackToTheLatestThreadMessage()
    {
        using var handler = new CaptureHandler();
        var message = CreateMessage(handler, threaded: true);
        message.Room.Cache.Append(new MessageEntry(message.Id, message.Sender, message.Timestamp, false, false, null, ThreadRoot,
            new MessageContent(MessageKind.Text, "Incoming message", null, []), IsDeleted: false, BundledEdit: null, BundledThread: null));

        await message.Room.SendMessageAsync(Body, threadRootId: ThreadRoot);

        using var json = await ReadRequestAsync(handler);
        await AssertRelationAsync(json.RootElement, threaded: true, isReply: false);
    }

    [Test]
    [Arguments(MessageKind.Text, "m.text")]
    [Arguments(MessageKind.Notice, "m.notice")]
    [Arguments(MessageKind.Emote, "m.emote")]
    public async Task Edit_KeepsTheMessageType(MessageKind kind, string msgType)
    {
        using var handler = new CaptureHandler();
        var original = CreateMessage(handler, threaded: false);
        var message = new Message(original.Id, original.Room, original.Sender, original.Timestamp, true, false, null, null,
            new MessageContent(kind, "old", null, []));

        await message.EditAsync("new", "<b>new</b>");

        using var json = await ReadRequestAsync(handler);
        await Assert.That(json.RootElement.GetProperty("msgtype").GetString()).IsEqualTo(msgType);
        await Assert.That(json.RootElement.GetProperty("body").GetString()).IsEqualTo("* new");
        await Assert.That(json.RootElement.GetProperty("formatted_body").GetString()).IsEqualTo("* <b>new</b>");
        var newContent = json.RootElement.GetProperty("m.new_content");
        await Assert.That(newContent.GetProperty("msgtype").GetString()).IsEqualTo(msgType);
        await Assert.That(newContent.GetProperty("body").GetString()).IsEqualTo("new");
        await Assert.That(newContent.GetProperty("formatted_body").GetString()).IsEqualTo("<b>new</b>");
    }

    private static (string? Html, bool Notice) Options(string kind) => kind switch
    {
        "text" => (null, false),
        "html" => (HtmlBody, false),
        "notice" => (null, true),
        "htmlnotice" => (HtmlBody, true),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static async Task AssertMessageAsync(JsonElement content, string kind, bool withMentions)
    {
        await Assert.That(content.GetProperty("body").GetString()).IsEqualTo(Body);
        await Assert.That(content.GetProperty("msgtype").GetString())
            .IsEqualTo(kind is "notice" or "htmlnotice" ? "m.notice" : "m.text");

        if (kind is "html" or "htmlnotice")
        {
            await Assert.That(content.GetProperty("format").GetString()).IsEqualTo("org.matrix.custom.html");
            await Assert.That(content.GetProperty("formatted_body").GetString()).IsEqualTo(HtmlBody);
        }
        else
        {
            await Assert.That(content.TryGetProperty("format", out _)).IsFalse();
            await Assert.That(content.TryGetProperty("formatted_body", out _)).IsFalse();
        }

        if (withMentions)
        {
            var userIds = content.GetProperty("m.mentions").GetProperty("user_ids");
            await Assert.That(userIds.GetArrayLength()).IsEqualTo(2);
            await Assert.That(userIds[0].GetString()).IsEqualTo("@Alice:example.org");
            await Assert.That(userIds[1].GetString()).IsEqualTo("@bob:example.org");
        }
        else
        {
            await Assert.That(content.TryGetProperty("m.mentions", out _)).IsFalse();
        }
    }

    private static async Task AssertRelationAsync(JsonElement content, bool threaded, bool isReply)
    {
        if (!threaded && !isReply)
        {
            await Assert.That(content.TryGetProperty("m.relates_to", out _)).IsFalse();
            return;
        }

        var relation = content.GetProperty("m.relates_to");
        await Assert.That(relation.GetProperty("m.in_reply_to").GetProperty("event_id").GetString())
            .IsEqualTo(ReceivedEvent);
        if (threaded)
        {
            await Assert.That(relation.GetProperty("rel_type").GetString()).IsEqualTo("m.thread");
            await Assert.That(relation.GetProperty("event_id").GetString()).IsEqualTo(ThreadRoot);
            await Assert.That(relation.GetProperty("is_falling_back").GetBoolean()).IsEqualTo(!isReply);
            await Assert.That(relation.EnumerateObject().Count()).IsEqualTo(4);
        }
        else
        {
            await Assert.That(relation.TryGetProperty("rel_type", out _)).IsFalse();
            await Assert.That(relation.TryGetProperty("event_id", out _)).IsFalse();
            await Assert.That(relation.TryGetProperty("is_falling_back", out _)).IsFalse();
            await Assert.That(relation.EnumerateObject().Count()).IsEqualTo(1);
        }
    }

    private static Message CreateMessage(HttpMessageHandler handler, bool threaded)
    {
        var parameters = new HttpClientParameters(new HttpClient(handler, disposeHandler: false), "https://example.org", "test-token",
            NullLogger.Instance, CancellationToken.None);
        var client = MatrixClient.CreateForTesting(parameters, ParseUserId("@bot:example.org"));
        RoomId.TryParse("!room:example.org", out var roomId);
        var room = new Room(roomId!, client);
        var sender = new RoomUser(new User(ParseUserId("@sender:example.org")));
        return new Message(ReceivedEvent, room, sender, DateTimeOffset.UnixEpoch, false, false, null,
            threaded ? ThreadRoot : null, new MessageContent(MessageKind.Text, "Incoming message", null, []));
    }

    private static MatrixId ParseUserId(string value)
    {
        UserId.TryParse(value, out var userId);
        return userId!;
    }

    private static async Task<JsonDocument> ReadRequestAsync(CaptureHandler handler)
    {
        await Assert.That(handler.Requests.Count).IsEqualTo(1);
        var request = handler.Requests[0];
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Put);
        await Assert.That(Uri.UnescapeDataString(request.Path).StartsWith(
            "/_matrix/client/v3/rooms/!room:example.org/send/m.room.message/", StringComparison.Ordinal)).IsTrue();
        return JsonDocument.Parse(request.Body);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath,
                await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"event_id":"$sent"}""", Encoding.UTF8, "application/json")
            };
        }
    }
}
