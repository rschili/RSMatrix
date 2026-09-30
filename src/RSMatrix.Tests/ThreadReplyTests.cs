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
    [Arguments("text", true, false)]
    [Arguments("text", true, true)]
    [Arguments("html", true, false)]
    [Arguments("html", true, true)]
    [Arguments("notice", true, false)]
    [Arguments("notice", true, true)]
    [Arguments("htmlnotice", true, false)]
    [Arguments("htmlnotice", true, true)]
    [Arguments("text", false, false)]
    [Arguments("text", false, true)]
    [Arguments("html", false, false)]
    [Arguments("html", false, true)]
    [Arguments("notice", false, false)]
    [Arguments("notice", false, true)]
    [Arguments("htmlnotice", false, false)]
    [Arguments("htmlnotice", false, true)]
    public async Task Responses_PreserveThreadAndReplySemantics(string kind, bool threaded, bool isReply)
    {
        using var handler = new CaptureHandler();
        var message = CreateMessage(handler, threaded);
        var mentions = new List<MatrixId> { ParseUserId("@Alice:example.org"), ParseUserId("@bob:example.org") };

        var eventId = await (kind switch
        {
            "text" => message.SendResponseAsync(Body, isReply, mentions),
            "html" => message.SendHtmlResponseAsync(Body, HtmlBody, isReply, mentions),
            "notice" => message.SendNoticeResponseAsync(Body, isReply, mentions),
            "htmlnotice" => message.SendHtmlNoticeResponseAsync(Body, HtmlBody, isReply, mentions),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        });

        await Assert.That(eventId).IsEqualTo("$sent");
        using var json = await ReadRequestAsync(handler);
        await AssertMessageAsync(json.RootElement, kind, withMentions: true);
        await AssertRelationAsync(json.RootElement, threaded, isReply);
    }

    [Test]
    [Arguments("text", true)]
    [Arguments("html", true)]
    [Arguments("notice", true)]
    [Arguments("htmlnotice", true)]
    [Arguments("text", false)]
    [Arguments("html", false)]
    [Arguments("notice", false)]
    [Arguments("htmlnotice", false)]
    public async Task DefaultResponses_KeepThreadWithoutAddingMentions(string kind, bool threaded)
    {
        using var handler = new CaptureHandler();
        var message = CreateMessage(handler, threaded);

        var eventId = await (kind switch
        {
            "text" => message.SendResponseAsync(Body),
            "html" => message.SendHtmlResponseAsync(Body, HtmlBody),
            "notice" => message.SendNoticeResponseAsync(Body),
            "htmlnotice" => message.SendHtmlNoticeResponseAsync(Body, HtmlBody),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        });

        await Assert.That(eventId).IsEqualTo("$sent");
        using var json = await ReadRequestAsync(handler);
        await AssertMessageAsync(json.RootElement, kind, withMentions: false);
        await AssertRelationAsync(json.RootElement, threaded, isReply: false);
    }

    [Test]
    [Arguments("text", false)]
    [Arguments("text", true)]
    [Arguments("html", false)]
    [Arguments("html", true)]
    [Arguments("notice", false)]
    [Arguments("notice", true)]
    [Arguments("htmlnotice", false)]
    [Arguments("htmlnotice", true)]
    public async Task RoomSendMethods_DoNotTreatRichReplyTargetAsThreadRoot(string kind, bool isReply)
    {
        using var handler = new CaptureHandler();
        var room = CreateMessage(handler, threaded: true).Room;
        var inReplyTo = isReply ? ReceivedEvent : null;

        var eventId = await (kind switch
        {
            "text" => room.SendTextMessageAsync(Body, inReplyTo, null),
            "html" => room.SendHtmlMessageAsync(Body, HtmlBody, inReplyTo, null),
            "notice" => room.SendNoticeAsync(Body, inReplyTo, null),
            "htmlnotice" => room.SendHtmlNoticeAsync(Body, HtmlBody, inReplyTo, null),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        });

        await Assert.That(eventId).IsEqualTo("$sent");
        using var json = await ReadRequestAsync(handler);
        await AssertMessageAsync(json.RootElement, kind, withMentions: false);
        await AssertRelationAsync(json.RootElement, threaded: false, isReply: isReply);
    }

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

    private static ReceivedTextMessage CreateMessage(HttpMessageHandler handler, bool threaded)
    {
        var parameters = new HttpClientParameters(new Factory(handler), "https://example.org", "test-token",
            NullLogger.Instance, CancellationToken.None);
        var client = MatrixTextClient.CreateForTesting(parameters, ParseUserId("@bot:example.org"));
        RoomId.TryParse("!room:example.org", out var roomId);
        var room = new Room(roomId!, client);
        var sender = new RoomUser(new User(ParseUserId("@sender:example.org")));
        return new ReceivedTextMessage("Incoming message", room, sender, ReceivedEvent,
            DateTimeOffset.UnixEpoch, threaded ? ThreadRoot : null, "m.text", client);
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

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
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
