using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RSMatrix.Http;

namespace RSMatrix.Tests;

public class SessionResumeTests
{
    private const string User = "@bot:example.org";
    private const string Device = "BOT";

    private static string NewDirectory() => Path.Combine(Path.GetTempPath(), $"rsmatrix-session-{Guid.NewGuid():N}");

    private static async Task<MatrixResponseException?> ConnectAndWaitForEndAsync(FakeServer server, string sessionDirectory,
        string? encryptionDirectory = null)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await MatrixTextClient.ConnectAsync(User, "password", Device, new ClientFactory(server), lifetime.Token,
            NullLogger.Instance, (_, token) => Task.CompletedTask, encryptionDirectory, sessionDirectory);
        // The fake server ends the second sync with a terminal error, which also releases the session.
        return await Assert.ThrowsAsync<MatrixResponseException>(() => client.Messages.Completion.WaitAsync(lifetime.Token));
    }

    private static void StoreSession(string directory, string? syncToken = "stored-batch")
    {
        using var store = SessionStore.Open(directory, User, Device);
        store.Save(new StoredSession
        {
            UserId = User,
            DeviceId = Device,
            Homeserver = "https://matrix.example.org",
            AccessToken = "stored-token",
            SyncToken = syncToken,
        });
    }

    [Test]
    public async Task SecondConnect_ResumesStoredSession_WithoutLogin()
    {
        var directory = NewDirectory();
        try
        {
            var first = new FakeServer();
            await ConnectAndWaitForEndAsync(first, directory);
            await Assert.That(first.LoginCount).IsEqualTo(1);

            var second = new FakeServer();
            await ConnectAndWaitForEndAsync(second, directory);
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
    public async Task Resume_AfterSoftLogout_LogsInAgainAndKeepsSyncPosition()
    {
        var directory = NewDirectory();
        try
        {
            StoreSession(directory);
            var server = new FakeServer { WhoAmIError = """{"errcode":"M_UNKNOWN_TOKEN","soft_logout":true}""" };
            await ConnectAndWaitForEndAsync(server, directory);
            await Assert.That(server.LoginCount).IsEqualTo(1);
            await Assert.That(server.WellKnownCount).IsEqualTo(0);
            await Assert.That(server.SyncQueries[0]).Contains("since=stored-batch");

            using var store = SessionStore.Open(directory, User, Device);
            await Assert.That(store.Session!.AccessToken).IsEqualTo("token-1");
            await Assert.That(store.Session.SyncToken).IsEqualTo("batch-1");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Resume_AfterHardLogout_WithoutEncryption_SyncsFromScratch()
    {
        var directory = NewDirectory();
        try
        {
            StoreSession(directory);
            var server = new FakeServer { WhoAmIError = """{"errcode":"M_UNKNOWN_TOKEN"}""" };
            await ConnectAndWaitForEndAsync(server, directory);
            await Assert.That(server.LoginCount).IsEqualTo(1);
            await Assert.That(server.SyncQueries[0]).DoesNotContain("since=");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Resume_AfterHardLogout_WithEncryption_Throws()
    {
        var directory = NewDirectory();
        try
        {
            StoreSession(directory);
            var server = new FakeServer { WhoAmIError = """{"errcode":"M_UNKNOWN_TOKEN"}""" };
            await Assert.ThrowsAsync<InvalidOperationException>(() => MatrixTextClient.ConnectAsync(User, "password", Device,
                new ClientFactory(server), CancellationToken.None, NullLogger.Instance, (_, token) => Task.CompletedTask,
                encryptionStoreDirectory: directory, sessionStoreDirectory: directory));
            await Assert.That(server.LoginCount).IsEqualTo(0);

            // Both stores were released.
            SessionStore.Open(directory, User, Device).Dispose();
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
            await ConnectAndWaitForEndAsync(server, directory);
            await Assert.That(server.LoginCount).IsEqualTo(2);
            await Assert.That(server.SyncQueries[2]).Contains("since=batch-1");

            using var store = SessionStore.Open(directory, User, Device);
            await Assert.That(store.Session!.AccessToken).IsEqualTo("token-2");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task SessionInUse_IsRejected()
    {
        var directory = NewDirectory();
        try
        {
            using var held = SessionStore.Open(directory, User, Device);
            await Assert.ThrowsAsync<InvalidOperationException>(() => MatrixTextClient.ConnectAsync(User, "password", Device,
                new ClientFactory(new FakeServer()), CancellationToken.None, NullLogger.Instance, (_, token) => Task.CompletedTask,
                sessionStoreDirectory: directory));

            // Another device of the same user has its own session.
            SessionStore.Open(directory, User, "OTHER").Dispose();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task FreshConnect_FailsOnTransientFailureBeforeLogin()
    {
        var directory = NewDirectory();
        try
        {
            var server = new FakeServer { FailWellKnownOnce = true };
            var error = await Assert.ThrowsAsync<MatrixResponseException>(() => MatrixTextClient.ConnectAsync(User, "password", Device,
                new ClientFactory(server), CancellationToken.None, NullLogger.Instance, (_, token) => Task.CompletedTask,
                sessionStoreDirectory: directory));
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
            await ConnectAndWaitForEndAsync(server, directory);
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
            await ConnectAndWaitForEndAsync(server, directory);
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

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeServer : HttpMessageHandler
    {
        public string? WhoAmIError { get; init; }
        public int? SoftLogoutOnSync { get; init; }
        public bool FailWellKnownOnce { get; init; }
        public bool FailWhoAmIOnce { get; init; }
        public string? RejectedSyncToken { get; init; }
        public int LoginCount { get; private set; }
        public int WellKnownCount { get; private set; }
        public int WhoAmICount { get; private set; }
        public List<string?> Tokens { get; } = [];
        public List<string> SyncQueries { get; } = [];
        private int _acceptedSyncs;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
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
                return Json($$"""{"access_token":"token-{{LoginCount}}","device_id":"BOT","user_id":"@bot:example.org"}""");
            }

            Tokens.Add(request.Headers.Authorization?.Parameter);
            if (path == "/_matrix/client/v3/account/whoami")
            {
                WhoAmICount++;
                if (FailWhoAmIOnce && WhoAmICount == 1)
                    return Json("""{"errcode":"M_UNKNOWN"}""", HttpStatusCode.ServiceUnavailable);
                return WhoAmIError != null
                    ? Json(WhoAmIError, HttpStatusCode.Unauthorized)
                    : Json("""{"user_id":"@bot:example.org","device_id":"BOT"}""");
            }
            if (path == "/_matrix/client/v3/capabilities")
                return Json("""{"capabilities":{}}""");
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
                if (syncCount == SoftLogoutOnSync)
                    return Json("""{"errcode":"M_UNKNOWN_TOKEN","soft_logout":true}""", HttpStatusCode.Unauthorized);
                if (syncCount == 1)
                    return Json("""{"next_batch":"batch-1"}""");
                if (syncCount == 2 && SoftLogoutOnSync == null)
                    return Json("""{"errcode":"M_FORBIDDEN"}""", HttpStatusCode.Forbidden);
                if (syncCount == 3)
                    return Json("""{"errcode":"M_FORBIDDEN"}""", HttpStatusCode.Forbidden);
            }

            throw new InvalidOperationException($"Unexpected request: {request.Method} {path}");
        }

        private static Task<HttpResponseMessage> Json(string body, HttpStatusCode status = HttpStatusCode.OK)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
