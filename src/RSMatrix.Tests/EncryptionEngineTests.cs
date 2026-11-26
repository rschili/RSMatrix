using System.Text.Json;
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

    [Test]
    public async Task SetUpCrossSigning_CreatesSigningKeysAndRecoveryKey_AndSignsItself()
    {
        using var fixture = await EncryptedRoomFixture.StartAsync();

        var recoveryKey = SecretStorage.DecodeRecoveryKey(fixture.SavedRecoveryKeys.Single());
        await Assert.That(fixture.Context.Client.EncryptionState).IsEqualTo(EncryptionState.Ready);
        var upload = JsonNode.Parse(fixture.Context.Requests.Single(r => r.Path.EndsWith("/keys/device_signing/upload")).Body)!;
        var master = SingleKey(upload["master_key"]!);
        var selfSigning = SingleKey(upload["self_signing_key"]!);
        await Assert.That(Verify(upload["self_signing_key"]!, master)).IsTrue();
        await Assert.That(Verify(upload["user_signing_key"]!, master)).IsTrue();
        var signatures = JsonNode.Parse(fixture.Context.Requests.Single(r => r.Path.EndsWith("/keys/signatures/upload")).Body)!;
        await Assert.That(Verify(signatures[BotUser]!["BOT"]!, selfSigning)).IsTrue();
        await Assert.That(fixture.Store.Data.PinnedMasterKeys[BotUser]).IsEqualTo(master);

        // The private keys are in secret storage, under the saved recovery key.
        var keyId = JsonNode.Parse(fixture.AccountData[SecretStorage.DefaultKeyType])!["key"]!.GetValue<string>();
        var description = JsonSerializer.Deserialize<JsonElement>(fixture.AccountData[SecretStorage.KeyType(keyId)]);
        await Assert.That(SecretStorage.MatchesKeyDescription(recoveryKey, description)).IsTrue();
        foreach (var (usage, property) in new[] { ("master", "master_key"), ("self_signing", "self_signing_key"), ("user_signing", "user_signing_key") })
        {
            var name = $"m.cross_signing.{usage}";
            var secret = JsonSerializer.Deserialize<JsonElement>(fixture.AccountData[name]).GetProperty("encrypted").GetProperty(keyId);
            var privateKey = new Ed25519KeyPair(CryptoPrimitives.FromBase64(SecretStorage.Decrypt(recoveryKey, name, secret)));
            await Assert.That(CryptoPrimitives.ToBase64(privateKey.PublicKey)).IsEqualTo(SingleKey(upload[property]!));
        }
        // Secrets are stored before the keys are published, so they never exist without them.
        var secretIndex = fixture.Context.Requests.FindIndex(r => r.Path.EndsWith("/account_data/m.cross_signing.master"));
        var uploadIndex = fixture.Context.Requests.FindIndex(r => r.Path.EndsWith("/keys/device_signing/upload"));
        await Assert.That(secretIndex).IsLessThan(uploadIndex);
        await Assert.That(ClientIssueTests.ReadAll(fixture.Context.Client).Count).IsEqualTo(0);
    }

    [Test]
    public async Task NoIdentity_IsNotReported_AndConnectingCreatesNone()
    {
        using var fixture = await EncryptedRoomFixture.StartAsync(f => f.SetUpCrossSigning = false);

        await Assert.That(fixture.Context.Client.EncryptionState).IsEqualTo(EncryptionState.NoIdentity);
        await Assert.That(fixture.Context.Requests.Any(r => r.Path.Contains("/keys/device_signing/") ||
            r.Path.Contains("/keys/signatures/") || r.Path.Contains("/account_data/"))).IsFalse();
        await Assert.That(ClientIssueTests.ReadAll(fixture.Context.Client).Count).IsEqualTo(0);
    }

    [Test]
    public async Task ExistingIdentity_WithoutRecoveryKey_IsReported()
    {
        var recoveryKey = SecretStorage.DecodeRecoveryKey(SecretStorage.CreateRecoveryKey());
        var identity = new TestIdentity();
        using var fixture = await EncryptedRoomFixture.StartAsync(f =>
        {
            f.OwnIdentity = identity;
            f.StoreSecret(recoveryKey, "m.cross_signing.self_signing", CryptoPrimitives.ToBase64(identity.SelfSigning.Seed));
        });

        await Assert.That(fixture.Context.Client.EncryptionState).IsEqualTo(EncryptionState.DeviceNotSigned);
        await Assert.That(fixture.Context.Requests.Any(r => r.Path.Contains("/keys/device_signing/") || r.Path.Contains("/keys/signatures/"))).IsFalse();
        var issue = ClientIssueTests.ReadAll(fixture.Context.Client).Single();
        await Assert.That(issue.Kind).IsEqualTo(ClientIssueKind.DeviceNotCrossSigned);
        await Assert.That(issue.Message.Contains("recovery key")).IsTrue();
    }

    [Test]
    public async Task ExistingIdentity_WithoutSecretStorage_IsReported()
    {
        using var fixture = await EncryptedRoomFixture.StartAsync(f =>
        {
            f.OwnIdentity = new TestIdentity();
            f.GivenRecoveryKey = SecretStorage.CreateRecoveryKey();
        });

        var issue = ClientIssueTests.ReadAll(fixture.Context.Client).Single();
        await Assert.That(issue.Kind).IsEqualTo(ClientIssueKind.DeviceNotCrossSigned);
        await Assert.That(issue.Message.Contains("SetUpCrossSigningAsync")).IsTrue();
    }

    [Test]
    public async Task ExistingIdentity_WithRecoveryKey_SignsDeviceWithStoredKey()
    {
        var recoveryKey = SecretStorage.CreateRecoveryKey();
        var identity = new TestIdentity();
        using var fixture = await EncryptedRoomFixture.StartAsync(f =>
        {
            f.OwnIdentity = identity;
            f.GivenRecoveryKey = recoveryKey;
            f.StoreSecret(SecretStorage.DecodeRecoveryKey(recoveryKey), "m.cross_signing.self_signing", CryptoPrimitives.ToBase64(identity.SelfSigning.Seed));
        });

        var signatures = JsonNode.Parse(fixture.Context.Requests.Single(r => r.Path.EndsWith("/keys/signatures/upload")).Body)!;
        await Assert.That(Verify(signatures[BotUser]!["BOT"]!, CryptoPrimitives.ToBase64(identity.SelfSigning.PublicKey))).IsTrue();
        await Assert.That(fixture.Context.Requests.Any(r => r.Path.EndsWith("/keys/device_signing/upload"))).IsFalse();
        await Assert.That(fixture.SavedRecoveryKeys.Count).IsEqualTo(0);
        await Assert.That(ClientIssueTests.ReadAll(fixture.Context.Client).Count).IsEqualTo(0);
    }

    [Test]
    public async Task ExistingIdentity_WithWrongRecoveryKey_Throws()
    {
        var identity = new TestIdentity();
        await Assert.That(async () => await EncryptedRoomFixture.StartAsync(f =>
        {
            f.OwnIdentity = identity;
            f.GivenRecoveryKey = SecretStorage.CreateRecoveryKey();
            f.StoreSecret(SecretStorage.DecodeRecoveryKey(SecretStorage.CreateRecoveryKey()), "m.cross_signing.self_signing",
                CryptoPrimitives.ToBase64(identity.SelfSigning.Seed));
        })).Throws<InvalidOperationException>().WithMessageContaining("does not match");
    }

    [Test]
    public async Task SetUpCrossSigning_ReplacesExistingKeys_ConfirmedWithThePassword()
    {
        var identity = new TestIdentity();
        using var fixture = await EncryptedRoomFixture.StartAsync(f =>
        {
            f.OwnIdentity = identity;
            f.RequirePasswordForSigningKeys = true;
        });
        ClientIssueTests.ReadAll(fixture.Context.Client);

        var newRecoveryKey = await fixture.Context.Client.SetUpCrossSigningAsync(EncryptedRoomFixture.Password);

        var uploads = fixture.Context.Requests.Where(r => r.Path.EndsWith("/keys/device_signing/upload")).ToList();
        await Assert.That(uploads.Count).IsEqualTo(2);
        var auth = JsonNode.Parse(uploads[1].Body)!["auth"]!;
        await Assert.That(auth["type"]!.GetValue<string>()).IsEqualTo("m.login.password");
        await Assert.That(auth["password"]!.GetValue<string>()).IsEqualTo(EncryptedRoomFixture.Password);
        await Assert.That(auth["session"]!.GetValue<string>()).IsEqualTo("uia-session");
        var master = SingleKey(JsonNode.Parse(uploads[1].Body)!["master_key"]!);
        await Assert.That(master).IsNotEqualTo(CryptoPrimitives.ToBase64(identity.Master.PublicKey));
        await Assert.That(SecretStorage.MatchesKeyDescription(SecretStorage.DecodeRecoveryKey(newRecoveryKey),
            JsonSerializer.Deserialize<JsonElement>(fixture.AccountData[SecretStorage.KeyType(
                JsonNode.Parse(fixture.AccountData[SecretStorage.DefaultKeyType])!["key"]!.GetValue<string>())]))).IsTrue();
        await Assert.That(fixture.Context.Client.EncryptionState).IsEqualTo(EncryptionState.Ready);
        await Assert.That(fixture.Context.Requests.Any(r => r.Path.EndsWith("/keys/signatures/upload"))).IsTrue();
        // A deliberate reset is not reported as a changed identity.
        await Assert.That(ClientIssueTests.ReadAll(fixture.Context.Client).Count).IsEqualTo(0);
    }

    [Test]
    public async Task DeviceNotCrossSigned_GetsNoRoomKey_ButAWithheldNotice()
    {
        using var fixture = await EncryptedRoomFixture.StartAsync(f => f.AliceIdentity = null);
        await fixture.Context.Room().SendTextMessageAsync("first", null, null);
        await fixture.Context.Room().SendTextMessageAsync("second", null, null);

        await Assert.That(fixture.Context.Requests.Any(r => r.Path.EndsWith("/keys/claim"))).IsFalse();
        var withheld = fixture.Context.Requests.Single(r => r.Path.Contains("/sendToDevice/m.room_key.withheld/"));
        var content = JsonNode.Parse(withheld.Body)!["messages"]!["@alice:example.org"]!["ALICE"]!;
        await Assert.That(content["code"]!.GetValue<string>()).IsEqualTo("m.unverified");
        await Assert.That(content["room_id"]!.GetValue<string>()).IsEqualTo("!room:example.org");
        var issue = ClientIssueTests.ReadAll(fixture.Context.Client).Single();
        await Assert.That(issue.Kind).IsEqualTo(ClientIssueKind.RoomKeyNotShared);
        await Assert.That(issue.Message.Contains("not cross-signed")).IsTrue();
    }

    [Test]
    public async Task MessageFromDeviceNotCrossSigned_IsNotDelivered_UntilItsOwnerSignsIt()
    {
        using var fixture = await EncryptedRoomFixture.StartAsync(f => f.AliceIdentity = null);
        var session = new MegolmOutboundSession();
        fixture.Store.Data.InboundGroupSessions[session.SessionId] = new InboundGroupSession
        {
            RoomId = "!room:example.org",
            SenderUserId = "@alice:example.org",
            SenderKey = fixture.AliceIdentityKey,
            Export = MegolmInboundSession.FromSessionKey(session.ExportSessionKey()).Export(),
            SenderCrossSigned = false
        };
        var first = fixture.EncryptFromAlice(session, "first");
        await fixture.ProcessEncryptedEvent("$first", "@alice:example.org", first);
        await Assert.That(fixture.Context.Client.Messages.TryRead(out _)).IsFalse();
        var issue = ClientIssueTests.ReadAll(fixture.Context.Client).Single();
        await Assert.That(issue.Kind).IsEqualTo(ClientIssueKind.DecryptionFailed);
        await Assert.That(issue.Message.Contains("not cross-signed")).IsTrue();

        // Alice cross-signs the device; the next sync lists her as changed.
        fixture.AliceIdentity = new TestIdentity();
        await fixture.Context.Process("""{"next_batch":"next","device_lists":{"changed":["@alice:example.org"]}}""");
        await fixture.ProcessEncryptedEvent("$second", "@alice:example.org", fixture.EncryptFromAlice(session, "second"));
        await Assert.That(fixture.Context.Client.Messages.TryRead(out var message)).IsTrue();
        await Assert.That(message!.Body).IsEqualTo("second");
    }

    [Test]
    public async Task LegacySessionWithoutTrustInformation_IsStillDecrypted()
    {
        using var fixture = await EncryptedRoomFixture.StartAsync(f => f.AliceIdentity = null);
        var session = new MegolmOutboundSession();
        fixture.Store.Data.InboundGroupSessions[session.SessionId] = new InboundGroupSession
        {
            RoomId = "!room:example.org",
            SenderUserId = "@alice:example.org",
            SenderKey = fixture.AliceIdentityKey,
            Export = MegolmInboundSession.FromSessionKey(session.ExportSessionKey()).Export()
        };
        await fixture.ProcessEncryptedEvent("$legacy", "@alice:example.org", fixture.EncryptFromAlice(session, "legacy"));
        await Assert.That(fixture.Context.Client.Messages.TryRead(out var message)).IsTrue();
        await Assert.That(message!.Body).IsEqualTo("legacy");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task WithheldKey_ExplainsUndecryptableMessage(bool hasIdentity)
    {
        using var fixture = await EncryptedRoomFixture.StartAsync(f => f.SetUpCrossSigning = hasIdentity);
        await fixture.Context.Process("""
            {"next_batch":"next","to_device":{"events":[{"type":"m.room_key.withheld","sender":"@alice:example.org",
              "content":{"algorithm":"m.megolm.v1.aes-sha2","room_id":"!room:example.org","session_id":"withheld",
                         "sender_key":"key","code":"m.unverified"}}]}}
            """);
        await fixture.ProcessEncryptedEvent("$withheld", "@alice:example.org",
            """{"algorithm":"m.megolm.v1.aes-sha2","session_id":"withheld","ciphertext":"AAAA"}""");

        var issue = ClientIssueTests.ReadAll(fixture.Context.Client).Single();
        await Assert.That(issue.Message.Contains("withheld the key")).IsTrue();
        await Assert.That(issue.Message.Contains("m.unverified")).IsTrue();
        await Assert.That(issue.Message.Contains("SetUpCrossSigningAsync")).IsEqualTo(!hasIdentity);
    }

    [Test]
    public async Task ChangedMasterKey_IsReported()
    {
        using var fixture = await EncryptedRoomFixture.StartAsync();
        await fixture.Context.Room().SendTextMessageAsync("first", null, null);

        fixture.AliceIdentity = new TestIdentity();
        await fixture.Context.Process("""{"next_batch":"next","device_lists":{"changed":["@alice:example.org"]}}""");
        await fixture.Context.Room().SendTextMessageAsync("second", null, null);

        var issue = ClientIssueTests.ReadAll(fixture.Context.Client).Single(i => i.Kind == ClientIssueKind.IdentityChanged);
        await Assert.That(issue.Message.Contains("@alice:example.org")).IsTrue();
        await Assert.That(fixture.Store.Data.PinnedMasterKeys["@alice:example.org"])
            .IsEqualTo(CryptoPrimitives.ToBase64(fixture.AliceIdentity.Master.PublicKey));
    }

    private const string BotUser = "@bot:example.org";

    private static string SingleKey(JsonNode key) => key["keys"]!.AsObject().Single().Value!.GetValue<string>();

    private static bool Verify(JsonNode value, string publicKey)
        => CrossSigning.IsSignedBy(JsonSerializer.SerializeToElement(value), BotUser, publicKey);

    /// <summary>Master and self-signing key of a test user.</summary>
    private sealed class TestIdentity
    {
        public Ed25519KeyPair Master { get; } = Ed25519KeyPair.Generate();
        public Ed25519KeyPair SelfSigning { get; } = Ed25519KeyPair.Generate();

        public void AddTo(JsonObject response, string userId)
        {
            AddKey(response, "master_keys", userId, CrossSigning.CreateKey(userId, CrossSigning.Master, Master, null));
            AddKey(response, "self_signing_keys", userId, CrossSigning.CreateKey(userId, CrossSigning.SelfSigning, SelfSigning, Master));
        }

        private static void AddKey(JsonObject response, string property, string userId, JsonObject key)
        {
            if (response[property] is not JsonObject keys)
                response[property] = keys = [];
            keys[userId] = key;
        }
    }

    private sealed class EncryptedRoomFixture : IDisposable
    {
        public MatrixTestContext Context { get; } = new();
        public EncryptionStore Store { get; private set; } = null!;
        /// <summary>Account data by type, as the server stores it.</summary>
        public Dictionary<string, string> AccountData { get; } = [];
        public TestIdentity? OwnIdentity { get; set; }
        /// <summary>Alice's cross-signing keys. Her device is cross-signed while this is set.</summary>
        public TestIdentity? AliceIdentity { get; set; } = new();
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rsmatrix-engine-{Guid.NewGuid():N}");
        private readonly Ed25519KeyPair _aliceSigningKey = Ed25519KeyPair.Generate();
        public string AliceIdentityKey { get; } = CryptoPrimitives.ToBase64(Curve25519KeyPair.Generate().PublicKey);
        private int _sentCount;

        public const string Password = "bot-password";
        /// <summary>The recovery key given when connecting.</summary>
        public string? GivenRecoveryKey { get; set; }
        /// <summary>Whether to create an identity after connecting, when the account has none.</summary>
        public bool SetUpCrossSigning { get; set; } = true;
        public List<string> SavedRecoveryKeys { get; } = [];
        /// <summary>The server asks for the password before accepting signing keys, as when they replace existing ones.</summary>
        public bool RequirePasswordForSigningKeys { get; set; }

        public static async Task<EncryptedRoomFixture> StartAsync(Action<EncryptedRoomFixture>? configure = null)
        {
            var fixture = new EncryptedRoomFixture();
            configure?.Invoke(fixture);
            fixture.Context.Send = (request, _) => Task.FromResult(fixture.Respond(request));
            fixture.Store = EncryptionStore.Open(fixture._directory, BotUser, "BOT");
            try
            {
                fixture.Context.Client.Encryption = await EncryptionEngine.StartAsync(fixture.Store, fixture.Context.Client, fixture.GivenRecoveryKey);
                if (fixture.OwnIdentity == null && fixture.SetUpCrossSigning)
                {
                    // Connecting reported the missing identity; the explicit setup resolves it.
                    ClientIssueTests.ReadAll(fixture.Context.Client);
                    fixture.SavedRecoveryKeys.Add(await fixture.Context.Client.SetUpCrossSigningAsync(Password));
                }
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
            await fixture.Context.Process("""
                {"next_batch":"next","rooms":{"join":{"!room:example.org":{"state":{"events":[
                  {"type":"m.room.encryption","state_key":"","sender":"@alice:example.org","event_id":"$encryption",
                   "origin_server_ts":1700000000000,"content":{"algorithm":"m.megolm.v1.aes-sha2"}}]}}}}}
                """);
            return fixture;
        }

        public void StoreSecret(byte[] recoveryKey, string name, string secret)
        {
            AccountData.TryAdd(SecretStorage.DefaultKeyType, """{"key":"testkey"}""");
            AccountData.TryAdd(SecretStorage.KeyType("testkey"), SecretStorage.CreateKeyDescription(recoveryKey).ToJsonString());
            AccountData[name] = new JsonObject
            {
                ["encrypted"] = new JsonObject { ["testkey"] = SecretStorage.Encrypt(recoveryKey, name, secret) }
            }.ToJsonString();
        }

        public string EncryptFromAlice(MegolmOutboundSession session, string body)
        {
            var payload = new JsonObject
            {
                ["type"] = "m.room.message",
                ["room_id"] = "!room:example.org",
                ["content"] = new JsonObject { ["msgtype"] = "m.text", ["body"] = body }
            };
            return new JsonObject
            {
                ["algorithm"] = EncryptionEngine.MegolmAlgorithm,
                ["sender_key"] = AliceIdentityKey,
                ["device_id"] = "ALICE",
                ["session_id"] = session.SessionId,
                ["ciphertext"] = session.Encrypt(System.Text.Encoding.UTF8.GetBytes(payload.ToJsonString()))
            }.ToJsonString();
        }

        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = Context.Requests[^1].Body;
            if (path.EndsWith("/keys/query"))
                return MatrixTestContext.Json(QueryKeys(body).ToJsonString());
            if (path.Contains("/account_data/"))
            {
                var type = Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]);
                if (request.Method == HttpMethod.Put)
                {
                    AccountData[type] = body;
                    return MatrixTestContext.Json("{}");
                }
                return AccountData.TryGetValue(type, out var content)
                    ? MatrixTestContext.Json(content)
                    : MatrixTestContext.Json("""{"errcode":"M_NOT_FOUND","error":"Not found"}""", System.Net.HttpStatusCode.NotFound);
            }
            if (path.EndsWith("/keys/device_signing/upload") && RequirePasswordForSigningKeys && !body.Contains("\"auth\""))
                return MatrixTestContext.Json("""{"session":"uia-session","flows":[{"stages":["m.login.password"]}],"params":{}}""",
                    System.Net.HttpStatusCode.Unauthorized);
            if (path.EndsWith("/keys/upload"))
                return MatrixTestContext.Json("""{"one_time_key_counts":{"signed_curve25519":50}}""");
            if (path.EndsWith("/members"))
                return MatrixTestContext.Json("""
                    {"chunk":[{"type":"m.room.member","state_key":"@alice:example.org","content":{"membership":"join"}},
                              {"type":"m.room.member","state_key":"@bot:example.org","content":{"membership":"join"}}]}
                    """);
            if (path.EndsWith("/keys/claim"))
                return MatrixTestContext.Json("""{"one_time_keys":{}}""");
            if (path.Contains("/send/"))
                return MatrixTestContext.Json($$"""{"event_id":"$sent{{++_sentCount}}"}""");
            return MatrixTestContext.Json("{}");
        }

        private JsonObject QueryKeys(string body)
        {
            var devices = new JsonObject();
            var response = new JsonObject { ["device_keys"] = devices };
            if (body.Contains("@alice:example.org"))
            {
                devices["@alice:example.org"] = new JsonObject { ["ALICE"] = CreateAliceDeviceKeys() };
                AliceIdentity?.AddTo(response, "@alice:example.org");
            }
            if (body.Contains(BotUser))
            {
                devices[BotUser] = new JsonObject();
                OwnIdentity?.AddTo(response, BotUser);
            }
            return response;
        }

        private JsonObject CreateAliceDeviceKeys()
        {
            const string userId = "@alice:example.org";
            var keys = new JsonObject
            {
                ["user_id"] = userId,
                ["device_id"] = "ALICE",
                ["algorithms"] = new JsonArray(EncryptionEngine.OlmAlgorithm, EncryptionEngine.MegolmAlgorithm),
                ["keys"] = new JsonObject
                {
                    ["curve25519:ALICE"] = AliceIdentityKey,
                    ["ed25519:ALICE"] = CryptoPrimitives.ToBase64(_aliceSigningKey.PublicKey)
                }
            };
            CanonicalJson.Sign(keys, userId, "ed25519:ALICE", data => _aliceSigningKey.Sign(data));
            if (AliceIdentity is { } identity)
                CanonicalJson.Sign(keys, userId, $"ed25519:{CryptoPrimitives.ToBase64(identity.SelfSigning.PublicKey)}", data => identity.SelfSigning.Sign(data));
            return keys;
        }

        public Task ProcessEncryptedEvent(string eventId, string sender, string content)
            => Context.Process($$"""
                {"next_batch":"next","rooms":{"join":{"!room:example.org":{"timeline":{"events":[
                  {"type":"m.room.encrypted","sender":"{{sender}}","event_id":"{{eventId}}",
                   "origin_server_ts":1700000000000,"content":
                """ + content + "}]}}}}}");

        public void Dispose()
        {
            Context.Dispose();
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, true);
        }
    }
}
