using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RSMatrix.Crypto;

namespace RSMatrix.Tests;

/// <summary>Test vectors from the spec appendices.</summary>
public class CanonicalJsonTests
{
    [Test]
    [Arguments("{}", "{}")]
    [Arguments("""{ "one": 1, "two": "Two" }""", """{"one":1,"two":"Two"}""")]
    [Arguments("""{ "b": "2", "a": "1" }""", """{"a":"1","b":"2"}""")]
    [Arguments("""{"auth":{"success":true,"mxid":"@john.doe:example.com","profile":{"display_name":"John Doe","three_pids":[{"medium":"email","address":"john.doe@example.org"},{"medium":"msisdn","address":"123456789"}]}}}""",
        """{"auth":{"mxid":"@john.doe:example.com","profile":{"display_name":"John Doe","three_pids":[{"address":"john.doe@example.org","medium":"email"},{"address":"123456789","medium":"msisdn"}]},"success":true}}""")]
    [Arguments("""{ "a": "日本語" }""", """{"a":"日本語"}""")]
    [Arguments("""{ "本": 2, "日": 1 }""", """{"日":1,"本":2}""")]
    [Arguments("""{ "a": "\u65E5" }""", """{"a":"日"}""")]
    [Arguments("""{ "a": null }""", """{"a":null}""")]
    [Arguments("""{ "a": -0, "b": 1e10 }""", """{"a":0,"b":10000000000}""")]
    [Arguments("""{ "a": "\"\\\n\u0001\u001f/" }""", """{"a":"\"\\\n\u0001\u001f/"}""")]
    public async Task Encode_MatchesSpecExamples(string input, string expected)
    {
        var encoded = Encoding.UTF8.GetString(CanonicalJson.Encode(JsonSerializer.Deserialize<JsonElement>(input)));
        await Assert.That(encoded).IsEqualTo(expected);
    }

    [Test]
    [Arguments("{}", "K8280/U9SSy9IVtjBuVeLr+HpOB4BQFWbg+UZaADMtTdGYI7Geitb76LTrr5QV/7Xg4ahLwYGYZzuHGZKM5ZAQ")]
    [Arguments("""{ "one": 1, "two": "Two" }""", "KqmLSbO39/Bzb0QIYE82zqLwsA+PDzYIpIRA2sRQ4sL53+sN6/fpNSoqE7BP7vBZhG6kYdD13EIMJpvhJI+6Bw")]
    public async Task Sign_MatchesSpecTestVectors_AndVerifies(string input, string expectedSignature)
    {
        var key = new Ed25519KeyPair(CryptoPrimitives.FromBase64("YJDBA9Xnr2sVqXD9Vj7XVUnmFZcZrlw8Md7kMW+3XA1"));
        var value = JsonNode.Parse(input)!.AsObject();
        CanonicalJson.Sign(value, "domain", "ed25519:1", data => key.Sign(data));

        await Assert.That(value["signatures"]!["domain"]!["ed25519:1"]!.GetValue<string>()).IsEqualTo(expectedSignature);
        var signed = JsonSerializer.SerializeToElement(value);
        await Assert.That(CanonicalJson.Verify(signed, "domain", "ed25519:1", key.PublicKey)).IsTrue();
        await Assert.That(CanonicalJson.Verify(signed, "domain", "ed25519:2", key.PublicKey)).IsFalse();

        value["extra"] = "changed";
        await Assert.That(CanonicalJson.Verify(JsonSerializer.SerializeToElement(value), "domain", "ed25519:1", key.PublicKey)).IsFalse();
    }
}
