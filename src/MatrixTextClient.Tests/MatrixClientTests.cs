using MatrixTextClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System;
using System.Net.Http;
using System.Threading.Tasks;
using TUnit.Core;

namespace MatrixTextClient.Tests
{
    public class MatrixClientTests
    {
        private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
        private readonly Mock<ILogger<MatrixClient>> _loggerMock;

        public MatrixClientTests()
        {
            _httpClientFactoryMock = new Mock<IHttpClientFactory>();
            _httpClientFactoryMock.Setup(factory => factory.CreateClient(It.IsAny<string>())).Returns(new HttpClient());
            _loggerMock = new Mock<ILogger<MatrixClient>>();
        }

        [Test]
        [Arguments(null, "password", "deviceId")]
        [Arguments("", "password", "deviceId")]
        [Arguments("   ", "password", "deviceId")]
        public async Task ConnectAsync_InvalidUserId_ThrowsArgumentException(string userId, string password, string deviceId)
        {
            var ex = await Assert.ThrowsAsync<ArgumentException>(() => MatrixClient.ConnectAsync(userId, password, deviceId, _httpClientFactory, _logger));
            await Assert.That(ex.ParamName).IsEqualTo("userId");

            _loggerMock.Verify(
                x => x.LogError(
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Invalid userId")),
                Times.Once);
        }

        [Test]
        [Arguments("userId", null, "deviceId")]
        [Arguments("userId", "", "deviceId")]
        [Arguments("userId", "   ", "deviceId")]
        public async Task ConnectAsync_InvalidPassword_ThrowsArgumentException(string userId, string password, string deviceId)
        {
            var ex = await Assert.ThrowsAsync<ArgumentException>(() => MatrixClient.ConnectAsync(userId, password, deviceId, _httpClientFactory, _logger));
            await Assert.That(ex.ParamName).IsEqualTo("password");
        }

        [Test]
        [Arguments("userId", "password", null)]
        [Arguments("userId", "password", "")]
        [Arguments("userId", "password", "   ")]
        public async Task ConnectAsync_InvalidDeviceId_ThrowsArgumentException(string userId, string password, string deviceId)
        {
            var ex = await Assert.ThrowsAsync<ArgumentException>(() => MatrixClient.ConnectAsync(userId, password, deviceId, _httpClientFactory, _logger));
            await Assert.That(ex.ParamName).IsEqualTo("deviceId");
        }

        [Test]
        [Arguments("invalidUserId", "password", "deviceId")]
        [Arguments("user:server", "password", "deviceId")]
        public async Task ConnectAsync_InvalidUserIdFormat_ThrowsArgumentException(string userId, string password, string deviceId)
        {
            var ex = await Assert.ThrowsAsync<ArgumentException>(() => MatrixClient.ConnectAsync(userId, password, deviceId, _httpClientFactory, _logger));
            await Assert.That(ex.ParamName).IsEqualTo("userId");
        }
    }

    // Stub for IHttpClientFactory
    public class HttpClientFactoryStub : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new HttpClient();
    }
}
