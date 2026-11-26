using System.Text.Json.Nodes;
using RSMatrix.Models;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RSMatrix.Crypto;
using RSMatrix.Http;

namespace RSMatrix.Tests;

public class SessionResumeTests
{
    private const string User = "@bot:example.org";
    private const string Device = "BOT";

    private static string NewDirectory() => Path.Combine(Path.GetTempPath(), $"rsmatrix-session-{Guid.NewGuid():N}");

    private static Task NoDelay(TimeSpan delay, CancellationToken token) => Task.CompletedTask;

    private static Task<MatrixClient> LoginAsync(FakeServer server, string? directory, string? deviceId = Device,
        string userId = User, CancellationToken cancellationToken = default)
        => MatrixClient.LoginAsync(userId, "password", cancellationToken, deviceId, null, directory, null, NullLogger.Instance, null,
            new HttpClient(server, disposeHandler: false), NoDelay);

    private static Task<MatrixClient> ResumeAsync(FakeServer server, string directory, string? password = "password")
        => MatrixClient.ResumeAsync(MatrixClient.GetStoredSession(directory, User)!, CancellationToken.None, password,
            NullLogger.Instance, null, new HttpClient(server, disposeHandler: false), NoDelay);

    /// <summary>The fake server ends the second sync with a terminal error, which also releases the store.</summary>
    private static async Task RunUntilEndAsync(Task<MatrixClient> starting)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var client = await starting;
        await Assert.ThrowsAsync<MatrixResponseException>(() => client.Updates.Completion.WaitAsync(timeout.Token));
    }

    private static void StoreSession(string directory, string? syncToken = "stored-batch")
    {
        using var store = SessionStore.Open(directory, User);
        store.Save(new SessionData
        {
            UserId = User,
            DeviceId = Device,
            Homeserver = "https://matrix.example.org",
            AccessToken = "stored-token",
            SyncToken = syncToken,
        });
    }

    private static SessionData? ReadSession(string directory) => SessionStore.Read(directory, User);

    [Test]
    public async Task Resume_ContinuesStoredSession_WithoutLogin()
    {
        var directory = NewDirectory();
        try
        {
            var first = new FakeServer();
            await RunUntilEndAsync(LoginAsync(first, directory));
            await Assert.That(first.LoginCount).IsEqualTo(1);

            var session = MatrixClient.GetStoredSession(directory, User);
            await Assert.That(session!.DeviceId).IsEqualTo(Device);
            await Assert.That(session.Homeserver).IsEqualTo(new Uri("https://matrix.example.org"));
            await Assert.That(session.StoreDirectory).IsEqualTo(directory);
            await Assert.That(session.ToString()).DoesNotContain("token");

            var second = new FakeServer { DeviceKeys = first.DeviceKeys };
            await RunUntilEndAsync(ResumeAsync(second, directory, password: null));
            await Assert.That(second.LoginCount).IsEqualTo(0);
            await Assert.That(second.WellKnownCount).IsEqualTo(0);
            await Assert.That(second.WhoAmICount).IsEqualTo(1);
            await Assert.That(second.Tokens.All(t => t == "token-1")).IsTrue();
            await Assert.That(second.SyncQueries[0]).Contains("since=batch-1");
            await Assert.That(second.SyncQueries[0]).Contains("full_state=true");
            await Assert.That(second.SyncQueries[1]).DoesNotContain("full_state");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task GetStoredSession_WithoutSession_ReturnsNull()
    {
        var directory = NewDirectory();
        await Assert.That(MatrixClient.GetStoredSession(directory, User)).IsNull();
        await Assert.That(Directory.Exists(directory)).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Login_ReplacesStoredSession_AndLogsOutOldDevice(bool failLogout)
    {
        var directory = NewDirectory();
        try
        {
            StoreSession(directory);
            byte[] oldIdentityKey;
            using (var store = SessionStore.Open(directory, User))
                oldIdentityKey = EncryptionStore.Create(store.Directory, User, Device).Data.Account.IdentityKey;

            var server = new FakeServer { FailLogout = failLogout };
            await RunUntilEndAsync(LoginAsync(server, directory));
            await Assert.That(server.LoggedOutTokens).IsEquivalentTo(["stored-token"]);
            await Assert.That(server.LoginCount).IsEqualTo(1);
            await Assert.That(server.SyncQueries[0]).DoesNotContain("since=");

            using (var store = SessionStore.Open(directory, User))
            {
                await Assert.That(store.Session!.AccessToken).IsEqualTo("token-1");
                var identityKey = EncryptionStore.Open(store.Directory, User, Device).Data.Account.IdentityKey;
                await Assert.That(identityKey.SequenceEqual(oldIdentityKey)).IsFalse();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Login_WithoutDeviceId_StoresServerGeneratedDevice()
    {
        var directory = NewDirectory();
        try
        {
            var server = new FakeServer();
            await RunUntilEndAsync(LoginAsync(server, directory, deviceId: null));
            var login = JsonNode.Parse(server.LoginBodies.Single())!;
            await Assert.That(login["device_id"]).IsNull();
            await Assert.That(login["initial_device_display_name"]!.GetValue<string>()).IsEqualTo("RSMatrix");
            await Assert.That(server.PublishedDeviceIds).IsEquivalentTo([FakeServer.GeneratedDeviceId]);
            await Assert.That(MatrixClient.GetStoredSession(directory, User)!.DeviceId).IsEqualTo(FakeServer.GeneratedDeviceId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Login_StoresSessionUnderConfirmedUserId()
    {
        var directory = NewDirectory();
        try
        {
            var server = new FakeServer();
            await RunUntilEndAsync(LoginAsync(server, directory, userId: "@BOT:example.org"));
            await Assert.That(MatrixClient.GetStoredSession(directory, User)).IsNotNull();
            await Assert.That(MatrixClient.GetStoredSession(directory, "@BOT:example.org")).IsNull();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task TwoUsers_ShareOneStore()
    {
        var directory = NewDirectory();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var botServer = new FakeServer { HangOnSync = 2 };
            var otherServer = new FakeServer { HangOnSync = 2, ConfirmedUserId = "@other:example.org" };
            await using var bot = await LoginAsync(botServer, directory);
            await using var other = await LoginAsync(otherServer, directory, deviceId: "OTHER", userId: "@other:example.org");
            await botServer.SyncHanging.Task.WaitAsync(timeout.Token);
            await otherServer.SyncHanging.Task.WaitAsync(timeout.Token);

            await Assert.That(MatrixClient.GetStoredSession(directory, User)!.DeviceId).IsEqualTo(Device);
            await Assert.That(MatrixClient.GetStoredSession(directory, "@other:example.org")!.DeviceId).IsEqualTo("OTHER");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Resume_AfterSoftLogout_LogsInAgainAndKeepsSyncPosition()
    {
        var directory = NewDirectory();
        try
        {
            StoreSession(directory);
            var server = new FakeServer { WhoAmIError = """{"errcode":"M_UNKNOWN_TOKEN","soft_logout":true}""" };
            await RunUntilEndAsync(ResumeAsync(server, directory));
            await Assert.That(server.LoginCount).IsEqualTo(1);
            await Assert.That(server.WellKnownCount).IsEqualTo(0);
            await Assert.That(JsonNode.Parse(server.LoginBodies.Single())!["device_id"]!.GetValue<string>()).IsEqualTo(Device);
            await Assert.That(server.SyncQueries[0]).Contains("since=stored-batch");

            var stored = ReadSession(directory)!;
            await Assert.That(stored.AccessToken).IsEqualTo("token-1");
            await Assert.That(stored.SyncToken).IsEqualTo("batch-1");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Resume_AfterHardLogout_ThrowsSessionEnded_AndDiscardsSession()
    {
        var directory = NewDirectory();
        try
        {
            await RunUntilEndAsync(LoginAsync(new FakeServer(), directory));

            // The server deleted the device together with its keys.
            var server = new FakeServer { WhoAmIError = """{"errcode":"M_UNKNOWN_TOKEN"}""" };
            await Assert.ThrowsAsync<SessionEndedException>(() => ResumeAsync(server, directory));
            await Assert.That(server.LoginCount).IsEqualTo(0);
            await Assert.That(MatrixClient.GetStoredSession(directory, User)).IsNull();
            using var store = SessionStore.Open(directory, User);
            await Assert.That(File.Exists(Path.Combine(store.Directory, "encryption.json"))).IsFalse();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task HardLogoutDuringSync_FaultsWithSessionEnded()
    {
        var directory = NewDirectory();
        try
        {
            var server = new FakeServer { HardLogoutOnSync = 2 };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using (var client = await LoginAsync(server, directory))
            {
                await Assert.ThrowsAsync<SessionEndedException>(() => client.Updates.Completion.WaitAsync(timeout.Token));
                await Assert.That(server.LoginCount).IsEqualTo(1);
            }
            await Assert.That(MatrixClient.GetStoredSession(directory, User)).IsNull();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task SoftLogoutDuringSync_LogsInAgainAndSavesNewToken()
    {
        var directory = NewDirectory();
        try
        {
            var server = new FakeServer { SoftLogoutOnSync = 2 };
            await RunUntilEndAsync(LoginAsync(server, directory));
            await Assert.That(server.LoginCount).IsEqualTo(2);
            await Assert.That(server.SyncQueries[2]).Contains("since=batch-1");
            await Assert.That(ReadSession(directory)!.AccessToken).IsEqualTo("token-2");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Resume_AfterSoftLogout_WithoutPassword_ThrowsPasswordRequired()
    {
        var directory = NewDirectory();
        try
        {
            StoreSession(directory);
            var server = new FakeServer { WhoAmIError = """{"errcode":"M_UNKNOWN_TOKEN","soft_logout":true}""" };
            await Assert.ThrowsAsync<PasswordRequiredException>(() => ResumeAsync(server, directory, password: null));
            await Assert.That(server.LoginCount).IsEqualTo(0);

            // The store was released, and resuming again with the password continues where the session stopped.
            var retry = new FakeServer { WhoAmIError = """{"errcode":"M_UNKNOWN_TOKEN","soft_logout":true}""" };
            await RunUntilEndAsync(ResumeAsync(retry, directory));
            await Assert.That(retry.LoginCount).IsEqualTo(1);
            await Assert.That(retry.SyncQueries[0]).Contains("since=stored-batch");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task SoftLogoutDuringSync_WithoutPassword_FaultsWithPasswordRequired()
    {
        var directory = NewDirectory();
        try
        {
            StoreSession(directory);
            var server = new FakeServer { SoftLogoutOnSync = 2 };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var client = await ResumeAsync(server, directory, password: null);
            await Assert.ThrowsAsync<PasswordRequiredException>(() => client.Updates.Completion.WaitAsync(timeout.Token));
            await Assert.That(server.LoginCount).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task LoginToken_OnlyCancelsConnecting()
    {
        var directory = NewDirectory();
        try
        {
            var server = new FakeServer();
            using var connecting = new CancellationTokenSource();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var client = await LoginAsync(server, directory, cancellationToken: connecting.Token);
            connecting.Cancel();

            // The client keeps syncing until the fake server's terminal error on the second sync.
            await Assert.ThrowsAsync<MatrixResponseException>(() => client.Updates.Completion.WaitAsync(timeout.Token));
            await Assert.That(server.SyncQueries.Count).IsEqualTo(2);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Dispose_CancelsWaitingSync_AndReleasesStore()
    {
        var directory = NewDirectory();
        try
        {
            var server = new FakeServer { HangOnSync = 2 };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var client = await LoginAsync(server, directory);
            await server.SyncHanging.Task.WaitAsync(timeout.Token);

            await client.DisposeAsync().AsTask().WaitAsync(timeout.Token);
            await client.Updates.Completion.WaitAsync(timeout.Token);
            // Reading to the end shows that the channel completed.
            await foreach (var unused in client.Updates.ReadAllAsync(timeout.Token)) { }
            await Assert.That(client.IsSyncing).IsFalse();
            await client.DisposeAsync();

            RoomId.TryParse("!room:example.org", out var roomId);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.GetOrAddRoom(roomId!).SendMessageAsync("hi"));

            using var store = SessionStore.Open(directory, User);
            await Assert.That(store.Session!.SyncToken).IsEqualTo("batch-1");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task StoreInUse_IsRejected()
    {
        var directory = NewDirectory();
        try
        {
            StoreSession(directory);
            using var held = SessionStore.Open(directory, User);
            var server = new FakeServer();
            await Assert.ThrowsAsync<InvalidOperationException>(() => LoginAsync(server, directory));
            await Assert.ThrowsAsync<InvalidOperationException>(() => ResumeAsync(server, directory));
            await Assert.That(server.LoginCount).IsEqualTo(0);
            await Assert.That(server.WhoAmICount).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task FreshLogin_FailsOnTransientFailureBeforeLogin()
    {
        var directory = NewDirectory();
        try
        {
            var server = new FakeServer { FailWellKnownOnce = true };
            var error = await Assert.ThrowsAsync<MatrixResponseException>(() => LoginAsync(server, directory));
            await Assert.That(error!.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(server.WellKnownCount).IsEqualTo(1);
            await Assert.That(server.LoginCount).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Resume_RetriesTransientFailures()
    {
        var directory = NewDirectory();
        try
        {
            StoreSession(directory);
            var server = new FakeServer { FailWhoAmIOnce = true };
            await RunUntilEndAsync(ResumeAsync(server, directory));
            await Assert.That(server.WhoAmICount).IsEqualTo(2);
            await Assert.That(server.LoginCount).IsEqualTo(0);
            await Assert.That(server.SyncQueries[0]).Contains("since=stored-batch");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Resume_WithRejectedSyncToken_SyncsFromScratch()
    {
        var directory = NewDirectory();
        try
        {
            StoreSession(directory, syncToken: "broken");
            var server = new FakeServer { RejectedSyncToken = "broken" };
            await RunUntilEndAsync(ResumeAsync(server, directory));
            await Assert.That(server.LoginCount).IsEqualTo(0);
            await Assert.That(server.SyncQueries[0]).Contains("since=broken");
            await Assert.That(server.SyncQueries[1]).DoesNotContain("since=");
            await Assert.That(server.SyncQueries[1]).DoesNotContain("full_state");
            await Assert.That(server.SyncQueries[2]).Contains("since=batch-1");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FakeServer : HttpMessageHandler
    {
        public const string GeneratedDeviceId = "GENERATED";

        public string ConfirmedUserId { get; init; } = User;
        public string? WhoAmIError { get; init; }
        public int? SoftLogoutOnSync { get; init; }
        public bool FailWellKnownOnce { get; init; }
        public bool FailWhoAmIOnce { get; init; }
        public bool FailLogout { get; init; }
        public string? RejectedSyncToken { get; init; }
        public int? HangOnSync { get; init; }
        public int? HardLogoutOnSync { get; init; }
        /// <summary>The device keys the client published, so a second server instance can answer like the first.</summary>
        public JsonNode? DeviceKeys { get; set; }
        public List<string> PublishedDeviceIds { get; } = [];
        public TaskCompletionSource SyncHanging { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int LoginCount { get; private set; }
        public List<string> LoginBodies { get; } = [];
        public List<string> LoggedOutTokens { get; } = [];
        public int WellKnownCount { get; private set; }
        public int WhoAmICount { get; private set; }
        public List<string?> Tokens { get; } = [];
        public List<string> SyncQueries { get; } = [];
        private int _acceptedSyncs;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/.well-known/matrix/client")
            {
                WellKnownCount++;
                if (FailWellKnownOnce && WellKnownCount == 1)
                    return Json("""{"errcode":"M_UNKNOWN"}""", HttpStatusCode.ServiceUnavailable);
                return Json("""{"m.homeserver":{"base_url":"https://matrix.example.org/"}}""");
            }
            if (path == "/_matrix/client/versions")
                return Json("""{"versions":["v1.17"]}""");
            if (path == "/_matrix/client/v3/login" && request.Method == HttpMethod.Get)
                return Json("""{"flows":[{"type":"m.login.password"}]}""");
            if (path == "/_matrix/client/v3/login" && request.Method == HttpMethod.Post)
            {
                LoginCount++;
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                LoginBodies.Add(body);
                var deviceId = JsonNode.Parse(body)!["device_id"]?.GetValue<string>() ?? GeneratedDeviceId;
                return Json(new JsonObject
                {
                    ["access_token"] = $"token-{LoginCount}",
                    ["device_id"] = deviceId,
                    ["user_id"] = ConfirmedUserId,
                }.ToJsonString());
            }

            Tokens.Add(request.Headers.Authorization?.Parameter);
            if (path == "/_matrix/client/v3/logout")
            {
                LoggedOutTokens.Add(request.Headers.Authorization?.Parameter ?? "");
                return FailLogout ? Json("""{"errcode":"M_UNKNOWN"}""", HttpStatusCode.InternalServerError) : Json("{}");
            }
            if (path == "/_matrix/client/v3/account/whoami")
            {
                WhoAmICount++;
                if (FailWhoAmIOnce && WhoAmICount == 1)
                    return Json("""{"errcode":"M_UNKNOWN"}""", HttpStatusCode.ServiceUnavailable);
                return WhoAmIError != null
                    ? Json(WhoAmIError, HttpStatusCode.Unauthorized)
                    : Json($$"""{"user_id":"{{ConfirmedUserId}}","device_id":"{{Device}}"}""");
            }
            if (path == "/_matrix/client/v3/capabilities")
                return Json("""{"capabilities":{}}""");
            if (path == "/_matrix/client/v3/keys/upload")
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
                if (body["device_keys"] is { } published)
                {
                    PublishedDeviceIds.Add(published["device_id"]!.GetValue<string>());
                    DeviceKeys ??= published.DeepClone();
                }
                return Json("{}");
            }
            if (path == "/_matrix/client/v3/keys/query")
                return Json(new JsonObject { ["device_keys"] = new JsonObject { [ConfirmedUserId] = DeviceKeys == null
                    ? new JsonObject() : new JsonObject { [DeviceKeys["device_id"]!.GetValue<string>()] = DeviceKeys.DeepClone() } } }.ToJsonString());
            if (path.StartsWith("/_matrix/client/v3/keys/", StringComparison.Ordinal))
                return Json("{}");
            if (path.EndsWith("/status", StringComparison.Ordinal))
                return Json("{}");
            if (path.EndsWith("/filter", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
                return Json("""{"filter_id":"filter1"}""");
            if (path.EndsWith("/filter/filter1", StringComparison.Ordinal))
                return Json("{}");
            if (path == "/_matrix/client/v3/sync")
            {
                SyncQueries.Add(request.RequestUri.Query);
                if (RejectedSyncToken != null && request.RequestUri.Query.Contains($"since={RejectedSyncToken}"))
                    return Json("""{"errcode":"M_UNKNOWN","error":"Invalid stream token"}""", HttpStatusCode.BadRequest);
                var syncCount = ++_acceptedSyncs;
                if (syncCount == HangOnSync)
                {
                    SyncHanging.TrySetResult();
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                if (syncCount == SoftLogoutOnSync)
                    return Json("""{"errcode":"M_UNKNOWN_TOKEN","soft_logout":true}""", HttpStatusCode.Unauthorized);
                if (syncCount == HardLogoutOnSync)
                {
                    // The server deletes the device together with its keys.
                    DeviceKeys = null;
                    return Json("""{"errcode":"M_UNKNOWN_TOKEN"}""", HttpStatusCode.Unauthorized);
                }
                if (syncCount == 1)
                    return Json("""{"next_batch":"batch-1"}""");
                if (syncCount == 2 && SoftLogoutOnSync == null)
                    return Json("""{"errcode":"M_FORBIDDEN"}""", HttpStatusCode.Forbidden);
                if (syncCount == 3)
                    return Json("""{"errcode":"M_FORBIDDEN"}""", HttpStatusCode.Forbidden);
            }

            throw new InvalidOperationException($"Unexpected request: {request.Method} {path}");
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
            => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
