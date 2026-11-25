using System.Security.Cryptography;
using System.Text.Json;
using RSMatrix.Crypto;

namespace RSMatrix.Tests;

/// <summary>Secret storage and recovery keys. Fixed values come from matrix-rust-sdk's tests, to check that we agree with it.</summary>
public class SecretStorageTests
{
    [Test]
    public async Task RecoveryKey_DecodesKnownKey_IgnoringWhitespace()
    {
        byte[] expected =
        [
            159, 189, 70, 187, 52, 81, 113, 198, 246, 2, 44, 154, 37, 213, 104, 27,
            165, 78, 236, 106, 108, 73, 83, 243, 173, 192, 185, 110, 157, 145, 173, 163
        ];
        var decoded = SecretStorage.DecodeRecoveryKey("EsT           pRvZTnjck8    YrhRAtw XLS84Nr2r9S9LGAWDaExVAPBvLRK   ");
        await Assert.That(decoded.SequenceEqual(expected)).IsTrue();
        await Assert.That(SecretStorage.EncodeRecoveryKey(expected))
            .IsEqualTo("EsTp RvZT njck 8Yrh RAtw XLS8 4Nr2 r9S9 LGAW DaEx VAPB vLRK");
    }

    [Test]
    [Arguments("EsTpRvZTnjck8YrhRAtwXLS84Nr2r9S9LGAWDaExVAPBvLRk")] // parity
    [Arguments("AATpRvZTnjck8YrhRAtwXLS84Nr2r9S9LGAWDaExVAPBvLRk")] // prefix
    [Arguments("AATpRvZTnjck8YrhRAtwXLS84Nr2r9S9")] // length
    [Arguments("AATpRvZTnjck8YrhRAtwXLS84Nr0OIl")] // not base58
    public async Task RecoveryKey_RejectsInvalidKeys(string recoveryKey)
    {
        await Assert.That(() => SecretStorage.DecodeRecoveryKey(recoveryKey)).Throws<FormatException>();
    }

    [Test]
    public async Task RecoveryKey_RoundTrips()
    {
        var recoveryKey = SecretStorage.CreateRecoveryKey();
        var decoded = SecretStorage.DecodeRecoveryKey(recoveryKey);
        await Assert.That(decoded.Length).IsEqualTo(32);
        await Assert.That(SecretStorage.EncodeRecoveryKey(decoded)).IsEqualTo(recoveryKey);
    }

    [Test]
    public async Task KeyDescription_FromAnotherClient_MatchesItsKey()
    {
        var recoveryKey = SecretStorage.DecodeRecoveryKey("EsTj 3yST y93F SLpB jJsz eAXc 2XzA ygD3 w69H fGaN TKBj jXEd");
        var description = JsonSerializer.Deserialize<JsonElement>("""
            {"algorithm":"m.secret_storage.v1.aes-hmac-sha2","iv":"xv5b6/p3ExEw++wTyfSHEg==","mac":"ujBBbXahnTAMkmPUX2/0+VTfUh63pGyVRuBcDMgmJC8="}
            """);
        await Assert.That(SecretStorage.MatchesKeyDescription(recoveryKey, description)).IsTrue();
        await Assert.That(SecretStorage.MatchesKeyDescription(new byte[32], description)).IsFalse();
    }

    [Test]
    public async Task KeyDescription_WithoutCheckValues_AcceptsAnyKey()
    {
        var description = JsonSerializer.Deserialize<JsonElement>("""{"algorithm":"m.secret_storage.v1.aes-hmac-sha2"}""");
        await Assert.That(SecretStorage.MatchesKeyDescription(new byte[32], description)).IsTrue();
        var otherAlgorithm = JsonSerializer.Deserialize<JsonElement>("""{"algorithm":"m.secret_storage.custom"}""");
        await Assert.That(SecretStorage.MatchesKeyDescription(new byte[32], otherAlgorithm)).IsFalse();
    }

    [Test]
    public async Task Secret_RoundTrips_AndNeedsTheSameKeyAndName()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var encrypted = JsonSerializer.SerializeToElement(SecretStorage.Encrypt(key, "m.cross_signing.master", "It's a secret to everybody"));

        await Assert.That(SecretStorage.Decrypt(key, "m.cross_signing.master", encrypted)).IsEqualTo("It's a secret to everybody");
        await Assert.That(() => SecretStorage.Decrypt(key, "m.cross_signing.self_signing", encrypted)).Throws<CryptographicException>();
        await Assert.That(() => SecretStorage.Decrypt(new byte[32], "m.cross_signing.master", encrypted)).Throws<CryptographicException>();
    }

    [Test]
    public async Task AesCtr_CarriesTheCounterAcrossBytes()
    {
        // NIST SP 800-38A F.5.5, CTR-AES256.Encrypt: the counter's low byte goes from 0xFF to 0x00 in block 2.
        var key = Convert.FromHexString("603deb1015ca71be2b73aef0857d77811f352c073b6108d72d9810a30914dff4");
        var iv = Convert.FromHexString("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff");
        var plaintext = Convert.FromHexString("6bc1bee22e409f96e93d7e117393172aae2d8a571e03ac9c9eb76fac45af8e51");
        var ciphertext = CryptoPrimitives.AesCtr(key, iv, plaintext);
        await Assert.That(Convert.ToHexStringLower(ciphertext))
            .IsEqualTo("601ec313775789a5b7a7f504bbf3d228f443e3ca4d62b59aca84e990cacaf5c5");
    }
}
