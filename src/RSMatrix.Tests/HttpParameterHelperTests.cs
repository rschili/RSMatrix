using RSMatrix.Http;

namespace RSMatrix.Tests;

/// <summary>
/// Tests query string construction in HttpParameterHelper.
/// </summary>
public class HttpParameterHelperTests
{
    [Test]
    public async Task AppendParameters_NoParameters_ReturnsPathUnchanged()
    {
        await Assert.That(HttpParameterHelper.AppendParameters("/_matrix/client/v3/sync", [])).IsEqualTo("/_matrix/client/v3/sync");
    }

    [Test]
    public async Task AppendParameters_NullParameters_ReturnsPathUnchanged()
    {
        await Assert.That(HttpParameterHelper.AppendParameters("/_matrix/client/v3/sync", null!)).IsEqualTo("/_matrix/client/v3/sync");
    }

    [Test]
    public async Task AppendParameters_SimpleValues_AppendsQueryString()
    {
        var parameters = new[] { KeyValuePair.Create("dir", "b"), KeyValuePair.Create("limit", "10") };
        await Assert.That(HttpParameterHelper.AppendParameters("/_matrix/client/v3/rooms/!room:example.org/messages", parameters))
            .IsEqualTo("/_matrix/client/v3/rooms/!room:example.org/messages?dir=b&limit=10");
    }

    [Test]
    public async Task AppendParameters_ReservedCharactersArePercentEncoded()
    {
        // 'since' tokens are opaque strings and may contain characters that are reserved in query strings
        var parameters = new[] { KeyValuePair.Create("since", "s123+456&abc=def#ghi") };
        await Assert.That(HttpParameterHelper.AppendParameters("/_matrix/client/v3/sync", parameters))
            .IsEqualTo("/_matrix/client/v3/sync?since=s123%2B456%26abc%3Ddef%23ghi");
    }

    [Test]
    public async Task AppendParameters_SpacesAreEncodedAsPercent20()
    {
        // Uri.EscapeDataString encodes spaces as %20, unlike HttpUtility.UrlEncode which produces '+'
        var parameters = new[] { KeyValuePair.Create("filter", "my filter") };
        await Assert.That(HttpParameterHelper.AppendParameters("/_matrix/client/v3/sync", parameters))
            .IsEqualTo("/_matrix/client/v3/sync?filter=my%20filter");
    }

    [Test]
    public async Task AppendParameters_KeysAreEncodedToo()
    {
        var parameters = new[] { KeyValuePair.Create("weird key", "value") };
        await Assert.That(HttpParameterHelper.AppendParameters("/path", parameters))
            .IsEqualTo("/path?weird%20key=value");
    }
}
