using System.Text.Json.Nodes;
using RSMatrix.Crypto;
using RSMatrix.Models;

namespace RSMatrix.Tests;

/// <summary>Runs the encryption engine against a scripted server with one other member, @alice, who has one device.</summary>
public class EncryptionEngineTests
{
    [Test]
    public async Task OwnEncryptedMessage_IsDecryptedAndMarkedEncrypted_AndReplayIsReported()
    {
        using var fixture = await EncryptedRoomFixture.StartAsync();
        var sentId = await fixture.Context.Room().SendTextMessageAsync("secret", null, null);
        var sent = fixture.Context.Requests.Last(r => r.Path.Contains("/send/m.room.encrypted/"));
        await Assert.That(sent.Body.Contains("secret")).IsFalse();

        await fixture.ProcessEncryptedEvent(sentId, "@bot:example.org", sent.Body);
        await Assert.That(fixture.Context.Client.Messages.TryRead(out var message)).IsTrue();
        await Assert.That(message!.Body).IsEqualTo("secret");
        await Assert.That(message.IsEncrypted).IsTrue();

        await fixture.ProcessEncryptedEvent("$replay", "@bot:example.org", sent.Body);
        await Assert.That(fixture.Context.Client.Messages.TryRead(out _)).IsFalse();
        var issue = ClientIssueTests.ReadAll(fixture.Context.Client).Single(i => i.Kind == ClientIssueKind.DecryptionFailed);
        await Assert.That(issue.EventId).IsEqualTo("$replay");
        await Assert.That(issue.Message.Contains(sentId)).IsTrue();
    }

    [Test]
    public async Task UnknownSession_IsReportedAsDecryptionFailed()
    {
        using var fixture = await EncryptedRoomFixture.StartAsync();
        await fixture.ProcessEncryptedEvent("$unknown", "@alice:example.org",
            """{"algorithm":"m.megolm.v1.aes-sha2","session_id":"unknown","ciphertext":"AAAA"}""");

        await Assert.That(fixture.Context.Client.Messages.TryRead(out _)).IsFalse();
        var issue = ClientIssueTests.ReadAll(fixture.Context.Client).Single();
        await Assert.That(issue.Kind).IsEqualTo(ClientIssueKind.DecryptionFailed);
        await Assert.That(issue.EventId).IsEqualTo("$unknown");
        await Assert.That(issue.User!.User.UserId.Full).IsEqualTo("@alice:example.org");
    }

    [Test]
    public async Task DeviceWithoutOneTimeKey_IsReportedOncePerSession()
    {
        using var fixture = await EncryptedRoomFixture.StartAsync();
        await fixture.Context.Room().SendTextMessageAsync("first", null, null);
        await fixture.Context.Room().SendTextMessageAsync("second", null, null);

        // Sharing is retried for every message, but reported once.
        await Assert.That(fixture.Context.Requests.Count(r => r.Path.EndsWith("/keys/claim"))).IsEqualTo(2);
        await Assert.That(fixture.Context.Requests.Count(r => r.Path.Contains("/send/m.room.encrypted/"))).IsEqualTo(2);
        var issue = ClientIssueTests.ReadAll(fixture.Context.Client).Single();
        await Assert.That(issue.Kind).IsEqualTo(ClientIssueKind.RoomKeyNotShared);
        await Assert.That(issue.Room).IsSameReferenceAs(fixture.Context.Room());
        await Assert.That(issue.User!.User.UserId.Full).IsEqualTo("@alice:example.org");
        await Assert.That(issue.Message.Contains("ALICE")).IsTrue();
    }

    private sealed class EncryptedRoomFixture : IDisposable
    {
        public MatrixTestContext Context { get; } = new();
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rsmatrix-engine-{Guid.NewGuid():N}");
        private readonly string _aliceDeviceKeys = CreateDeviceKeys("@alice:example.org", "ALICE");
        private int _sentCount;

        public static async Task<EncryptedRoomFixture> StartAsync()
        {
            var fixture = new EncryptedRoomFixture();
            fixture.Context.Send = (request, _) => Task.FromResult(MatrixTestContext.Json(fixture.Respond(request)));
            var store = EncryptionEngine.OpenStore(fixture._directory, "@bot:example.org", "BOT");
            fixture.Context.Client.Encryption = await EncryptionEngine.StartAsync(store, fixture.Context.Client.HttpClientParameters);
            await fixture.Context.Process("""
                {"next_batch":"next","rooms":{"join":{"!room:example.org":{"state":{"events":[
                  {"type":"m.room.encryption","state_key":"","sender":"@alice:example.org","event_id":"$encryption",
                   "origin_server_ts":1700000000000,"content":{"algorithm":"m.megolm.v1.aes-sha2"}}]}}}}}
                """);
            return fixture;
        }

        private string Respond(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = Context.Requests[^1].Body;
            if (path.EndsWith("/keys/query"))
                return body.Contains("@alice:example.org")
                    ? """{"device_keys":{"@alice:example.org":{"ALICE":""" + _aliceDeviceKeys + "}}}"
                    : """{"device_keys":{}}""";
            if (path.EndsWith("/keys/upload"))
                return """{"one_time_key_counts":{"signed_curve25519":50}}""";
            if (path.EndsWith("/members"))
                return """
                    {"chunk":[{"type":"m.room.member","state_key":"@alice:example.org","content":{"membership":"join"}},
                              {"type":"m.room.member","state_key":"@bot:example.org","content":{"membership":"join"}}]}
                    """;
            if (path.EndsWith("/keys/claim"))
                return """{"one_time_keys":{}}""";
            if (path.Contains("/send/"))
                return $$"""{"event_id":"$sent{{++_sentCount}}"}""";
            return "{}";
        }

        public Task ProcessEncryptedEvent(string eventId, string sender, string content)
            => Context.Process($$"""
                {"next_batch":"next","rooms":{"join":{"!room:example.org":{"timeline":{"events":[
                  {"type":"m.room.encrypted","sender":"{{sender}}","event_id":"{{eventId}}",
                   "origin_server_ts":1700000000000,"content":
                """ + content + "}]}}}}}");

        private static string CreateDeviceKeys(string userId, string deviceId)
        {
            var signingKey = Ed25519KeyPair.Generate();
            var keys = new JsonObject
            {
                ["user_id"] = userId,
                ["device_id"] = deviceId,
                ["algorithms"] = new JsonArray(EncryptionEngine.OlmAlgorithm, EncryptionEngine.MegolmAlgorithm),
                ["keys"] = new JsonObject
                {
                    [$"curve25519:{deviceId}"] = CryptoPrimitives.ToBase64(Curve25519KeyPair.Generate().PublicKey),
                    [$"ed25519:{deviceId}"] = CryptoPrimitives.ToBase64(signingKey.PublicKey)
                }
            };
            CanonicalJson.Sign(keys, userId, $"ed25519:{deviceId}", data => signingKey.Sign(data));
            return keys.ToJsonString();
        }

        public void Dispose()
        {
            Context.Client.Encryption?.Dispose();
            Context.Dispose();
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, true);
        }
    }
}
