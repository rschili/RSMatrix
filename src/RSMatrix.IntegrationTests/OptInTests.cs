using TUnit.Core.Exceptions;

namespace RSMatrix.IntegrationTests;

public class OptInTests
{
    [Test]
    public async Task MissingAddress_SkipsLiveTests()
    {
        await Assert.ThrowsAsync<SkipTestException>(() =>
            Task.FromResult(LocalHomeserver.GetBaseUri(null)));
    }

    [Test]
    [Arguments("")]
    [Arguments("not-a-url")]
    [Arguments("https://127.0.0.1:8008")]
    [Arguments("http://example.invalid:8008")]
    [Arguments("http://127.0.0.1:8008/path")]
    public async Task InvalidConfiguredAddress_FailsRatherThanSkipping(string address)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Task.FromResult(LocalHomeserver.GetBaseUri(address)));
    }

    [Test]
    public async Task ConfiguredLoopbackAddress_EnablesLiveTests()
    {
        var uri = LocalHomeserver.GetBaseUri("http://127.0.0.1:18008");
        await Assert.That(uri.AbsoluteUri).IsEqualTo("http://127.0.0.1:18008/");
    }
}
