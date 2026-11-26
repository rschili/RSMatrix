using System.Net;
using RSMatrix.Models;

namespace RSMatrix.Tests;

public class ClientIssueTests
{
    [Test]
    public async Task EncryptedEvent_WithoutEncryption_IsReported_ExceptReactions()
    {
        using var context = new MatrixTestContext();
        await context.Process("""
        {"next_batch":"next","rooms":{"join":{"!room:example.org":{"timeline":{"events":[
         {"type":"m.room.encrypted","sender":"@alice:example.org","event_id":"$secret","origin_server_ts":1700000000000,
          "content":{"algorithm":"m.megolm.v1.aes-sha2","session_id":"s","ciphertext":"c"}},
         {"type":"m.room.encrypted","sender":"@alice:example.org","event_id":"$reaction","origin_server_ts":1700000000000,
          "content":{"algorithm":"m.megolm.v1.aes-sha2","session_id":"s","ciphertext":"c",
                     "m.relates_to":{"rel_type":"m.annotation","event_id":"$secret","key":"👍"}}}]}}}}}
        """);

        var issues = ReadAll(context.Client);
        await Assert.That(issues.Count).IsEqualTo(1);
        var issue = issues[0];
        await Assert.That(issue.Kind).IsEqualTo(ClientIssueKind.DecryptionFailed);
        await Assert.That(issue.EventId).IsEqualTo("$secret");
        await Assert.That(issue.Room).IsSameReferenceAs(context.Room());
        await Assert.That(issue.User!.User.UserId.Full).IsEqualTo("@alice:example.org");
        await Assert.That(issue.Message.Contains("encryption is not enabled")).IsTrue();
        await Assert.That(context.ReadUpdates().OfType<Update>().Any(update => update.Value is NewMessage)).IsFalse();
    }

    [Test]
    public async Task FetchMessages_ReportsUndecryptableHistory()
    {
        using var context = new MatrixTestContext();
        context.Send = (_, _) => Task.FromResult(MatrixTestContext.Json("""
            {"start":"t1","end":"t2","chunk":[
             {"type":"m.room.message","room_id":"!room:example.org","sender":"@alice:example.org","event_id":"$plain","origin_server_ts":1700000000000,
              "content":{"msgtype":"m.text","body":"hello"}},
             {"type":"m.room.encrypted","room_id":"!room:example.org","sender":"@alice:example.org","event_id":"$secret","origin_server_ts":1700000000000,
              "content":{"algorithm":"m.megolm.v1.aes-sha2","session_id":"s","ciphertext":"c"}}]}
            """));

        var history = await context.Room().FetchMessagesAsync();

        await Assert.That(history.Messages.Single().Id).IsEqualTo("$plain");
        var issue = ReadAll(context.Client).Single();
        await Assert.That(issue.Kind).IsEqualTo(ClientIssueKind.DecryptionFailed);
        await Assert.That(issue.EventId).IsEqualTo("$secret");
    }

    [Test]
    public async Task FailedAutoJoin_IsReported()
    {
        using var context = new MatrixTestContext();
        context.Client.AutoJoinOnInvite = true;
        context.Send = (_, _) => Task.FromResult(MatrixTestContext.Json("""{"errcode":"M_FORBIDDEN"}""", HttpStatusCode.Forbidden));
        await context.Process("""
        {"next_batch":"next","rooms":{"invite":{"!invite:example.org":{"invite_state":{"events":[
         {"type":"m.room.member","state_key":"@bot:example.org","sender":"@alice:example.org","content":{"membership":"invite"}}]}}}}}
        """);

        var issue = ReadAll(context.Client).Single();
        await Assert.That(issue.Kind).IsEqualTo(ClientIssueKind.AutoJoinFailed);
        await Assert.That(issue.Room!.RoomId.Full).IsEqualTo("!invite:example.org");
        await Assert.That(issue.User!.User.UserId.Full).IsEqualTo("@alice:example.org");
        await Assert.That(issue.Exception).IsNotNull();
    }

    [Test]
    public async Task SyncInterruption_IsReportedOncePerOutage_AndLastSyncAtUpdates()
    {
        using var context = new MatrixTestContext();
        // Two failures, a success, one failure, then a terminal error that stops syncing.
        HttpStatusCode?[] responses = [HttpStatusCode.ServiceUnavailable, HttpStatusCode.BadGateway, null,
            HttpStatusCode.ServiceUnavailable, HttpStatusCode.Unauthorized];
        var syncCount = 0;
        context.Send = (_, _) => Task.FromResult(responses[syncCount++] is { } status
            ? MatrixTestContext.Json(status == HttpStatusCode.Unauthorized ? """{"errcode":"M_UNKNOWN_TOKEN"}""" : "{}", status)
            : MatrixTestContext.Json("""{"next_batch":"next"}"""));
        await Assert.That(context.Client.LastSyncAt).IsNull();
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        await context.Client.SyncAsync();

        await Assert.That(syncCount).IsEqualTo(5);
        await Assert.That(context.Client.LastSyncAt).IsNotNull();
        await Assert.That(context.Client.LastSyncAt!.Value > before).IsTrue();
        var issues = ReadAll(context.Client);
        await Assert.That(issues.Count).IsEqualTo(2);
        await Assert.That(issues.All(i => i.Kind == ClientIssueKind.SyncInterrupted && i.Exception != null)).IsTrue();
        await Assert.That(context.Client.Updates.Completion.IsCompleted).IsTrue();
    }

    internal static List<ClientIssue> ReadAll(MatrixClient client)
    {
        var issues = new List<ClientIssue>();
        while (client.Updates.TryRead(out var update))
        {
            if (update.Value is ClientIssue issue)
                issues.Add(issue);
        }
        return issues;
    }
}
