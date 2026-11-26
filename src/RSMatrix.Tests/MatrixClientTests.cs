namespace RSMatrix.Tests;

public class MatrixClientTests
{
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("invalidUserId")]
    [Arguments("user:server")]
    public async Task LoginAsync_InvalidUserId_ThrowsArgumentException(string? userId)
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => MatrixClient.LoginAsync(userId!, "password", CancellationToken.None, deviceId: "deviceId"));
        await Assert.That(ex?.ParamName).IsEqualTo("userId");
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task LoginAsync_InvalidDeviceId_ThrowsArgumentException(string deviceId)
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => MatrixClient.LoginAsync("@bot:example.org", "password", CancellationToken.None, deviceId: deviceId));
        await Assert.That(ex?.ParamName).IsEqualTo("deviceId");
    }

    [Test]
    public async Task LoginAsync_WithoutDeviceIdOrStore_ThrowsArgumentException()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => MatrixClient.LoginAsync("@bot:example.org", "password", CancellationToken.None));
        await Assert.That(ex?.ParamName).IsEqualTo("deviceId");
    }

    [Test]
    public async Task LoginAsync_EmptyPassword_ThrowsArgumentException()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => MatrixClient.LoginAsync("@bot:example.org", "", CancellationToken.None, deviceId: "deviceId"));
        await Assert.That(ex?.ParamName).IsEqualTo("password");
    }

    [Test]
    [Arguments("matrix.example.org")]
    [Arguments("ftp://matrix.example.org")]
    public async Task LoginAsync_InvalidHomeserver_ThrowsArgumentException(string homeserver)
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => MatrixClient.LoginAsync("@bot:example.org", "password", CancellationToken.None,
            deviceId: "deviceId", homeserver: new Uri(homeserver, UriKind.RelativeOrAbsolute)));
        await Assert.That(ex?.ParamName).IsEqualTo("homeserver");
    }
}
