using RSMatrix.Http;

namespace RSMatrix.Tests;

public class HttpParameterHelperTests
{
    [Test]
    public async Task AppendParameters_WithoutParameters_ReturnsPathUnchanged()
    {
        await Assert.That(HttpParameterHelper.AppendParameters("/_matrix/client/v3/sync", [])).IsEqualTo("/_matrix/client/v3/sync");
        await Assert.That(HttpParameterHelper.AppendParameters("/_matrix/client/v3/sync", null!)).IsEqualTo("/_matrix/client/v3/sync");
    }

    // Tokens like 'since' are opaque and may contain characters reserved in query strings. Spaces become %20, not '+'.
    [Test]
    [Arguments("since", "s123+456&abc=def#ghi", "?since=s123%2B456%26abc%3Ddef%23ghi")]
    [Arguments("filter", "my filter", "?filter=my%20filter")]
    [Arguments("weird key", "value", "?weird%20key=value")]
    public async Task AppendParameters_EncodesKeysAndValues(string key, string value, string expectedQuery)
    {
        await Assert.That(HttpParameterHelper.AppendParameters("/path", [KeyValuePair.Create(key, value)])).IsEqualTo("/path" + expectedQuery);
        await Assert.That(HttpParameterHelper.AppendParameters("/path", [KeyValuePair.Create("dir", "b"), KeyValuePair.Create(key, value)]))
            .IsEqualTo("/path?dir=b&" + expectedQuery[1..]);
    }
}
