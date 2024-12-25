using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using System.Net;
using System.Text;
using Narrensicher.Matrix.Models;

namespace Narrensicher.Matrix.Tests;

public class LoginTests
{
    /// <summary>
    /// Goes through the process of a full password login
    /// </summary>
    [Test]
    public async Task TestSuccessfulPasswordLogin()
    {
        // TODO: We do not actually verify the server request contents. This test flow is tedious enough as it is. May add later.
        // arrange
        var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);

        HttpResponseMessage wellKnownResponse = new(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            {
            "m.homeserver": {
                "base_url": "https://matrix.example.com"
            },
            "m.identity_server": {
                "base_url": "https://identity.example.com"
            },
            "org.example.custom.property": {
                "app_url": "https://custom.app.example.org"
            }
            }
            """, Encoding.UTF8, "application/json")
        };

        var protectedMock = handlerMock.Protected();
        protectedMock
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri == new Uri("https://example.org/.well-known/matrix/client")),
                ItExpr.IsAny<CancellationToken>())
            .Returns(Task.FromResult(wellKnownResponse))
            .Verifiable(Times.Once, "Expected well known request to be made once");

        HttpResponseMessage versionsResponse = new(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            {
            "unstable_features": {
                "org.example.my_feature": true
            },
            "versions": [
                "r0.0.1",
                "v1.1"
            ]
            }
            """, Encoding.UTF8, "application/json")
        };

        protectedMock
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri == new Uri("https://matrix.example.com/_matrix/client/versions")),
                ItExpr.IsAny<CancellationToken>())
            .Returns(Task.FromResult(versionsResponse))
            .Verifiable(Times.Once, "Expected versions request to be made once");

        HttpResponseMessage supportedAuthFlowsResponse = new(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            {
            "flows": [
                {
                    "type": "m.login.password"
                },
                {
                    "type": "m.login.token"
                }
            ]
            }
            """, Encoding.UTF8, "application/json")
        };

        protectedMock
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri == new Uri("https://matrix.example.com/_matrix/client/v3/login") && r.Method == HttpMethod.Get),
                ItExpr.IsAny<CancellationToken>())
            .Returns(Task.FromResult(supportedAuthFlowsResponse))
            .Verifiable(Times.Once, "Expected auth flows request to be made once");

        HttpResponseMessage loginResponse = new(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            {
            "access_token": "abc123",
            "device_id": "GHTYAJCE",
            "user_id": "@cheeky_monkey:matrix.org"
            }
            """, Encoding.UTF8, "application/json")
        };

        protectedMock
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri == new Uri("https://matrix.example.com/_matrix/client/v3/login") && r.Method == HttpMethod.Post),
                ItExpr.IsAny<CancellationToken>())
            .Returns(Task.FromResult(loginResponse))
            .Verifiable(Times.Once, "Expected login request to be made once");

        HttpResponseMessage capabilitiesResponse = new(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            {
            "capabilities": {
                "com.example.custom.ratelimit": {
                "max_requests_per_hour": 600
                },
                "m.change_password": {
                "enabled": false
                },
                "m.room_versions": {
                "available": {
                    "1": "stable",
                    "2": "stable",
                    "3": "unstable",
                    "test-version": "unstable"
                },
                "default": "1"
                }
            }
            }
            """, Encoding.UTF8, "application/json")
        };

        protectedMock
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri == new Uri("https://matrix.example.com/_matrix/client/v3/capabilities") &&
                    r.Headers.Authorization!.Parameter == "abc123"),
                ItExpr.IsAny<CancellationToken>())
            .Returns(Task.FromResult(capabilitiesResponse))
            .Verifiable(Times.Once, "Expected capabilities request to be made once");

            
        var mockHttpClientFactory = new Mock<IHttpClientFactory>();

        mockHttpClientFactory.Setup(_ => _.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handlerMock.Object, false));

        // act
        var result = await MatrixClientCore.ConnectAsync("@nobody:example.org", "password", "deviceId", mockHttpClientFactory.Object, CancellationToken.None, NullLogger.Instance);

        // assert
        await Assert.That(result).IsNotNull();
        handlerMock.VerifyAll();
        await Assert.That(result.User.Full).IsEqualTo("@nobody:example.org");
        await Assert.That(result.HttpClientParameters.BearerToken).IsEqualTo("abc123");
        await Assert.That(result.ServerCapabilities?.ChangePassword?.Enabled).IsFalse();
        await Assert.That(result.SupportedSpecVersions).Contains(new SpecVersion(1, 1, null, null));
    }
}
