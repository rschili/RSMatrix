using Org.BouncyCastle.Security;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Agreement;
using System.Numerics;

namespace RSMatrix.Crypto;
public static class CryptoJsonHelper
{
    public static string ToUnpaddedBase64String(byte[] data)
    {
        return Convert.ToBase64String(data).TrimEnd('=');
    }
}

public class Ed25519Helper
{
    internal (Ed25519PrivateKeyParameters privateKey, Ed25519PublicKeyParameters publicKey) LoadOrGenerateKeys()
    {
        string privateKeyPath = "privateKey.pem";
        string publicKeyPath = "publicKey.pem";

        // Check if key files exist
        if (File.Exists(privateKeyPath) && File.Exists(publicKeyPath))
        {
            // Load keys from files
            var privateKeyBytes = File.ReadAllBytes(privateKeyPath);
            var publicKeyBytes = File.ReadAllBytes(publicKeyPath);

            var privateKey = new Ed25519PrivateKeyParameters(privateKeyBytes, 0);
            var publicKey = new Ed25519PublicKeyParameters(publicKeyBytes, 0);
            return (privateKey, publicKey);
        }
        else
        {
            var generator = new Ed25519KeyPairGenerator();
            Ed25519KeyGenerationParameters keygenParams = new(new SecureRandom());
            generator.Init(keygenParams);

            var keyPair = generator.GenerateKeyPair();
            var privateKey = (Ed25519PrivateKeyParameters)keyPair.Private;
            var publicKey = (Ed25519PublicKeyParameters)keyPair.Public;
            // Save keys to files
            File.WriteAllBytes(privateKeyPath, privateKey.GetEncoded());
            File.WriteAllBytes(publicKeyPath, publicKey.GetEncoded());
            return (privateKey, publicKey);
        }
    }


    public void Test(string msg)
    {
        try
        {
            var (privateKey, publicKey) = LoadOrGenerateKeys();

            // Create signature
            var signer = SignerUtilities.GetSigner("Ed25519");
            signer.Init(true, privateKey);
            signer.BlockUpdate(System.Text.Encoding.ASCII.GetBytes(msg), 0, msg.Length);

            // Verify signature
            var signer2 = SignerUtilities.GetSigner("Ed25519");
            signer2.Init(false, publicKey);
            signer2.BlockUpdate(System.Text.Encoding.ASCII.GetBytes(msg), 0, msg.Length);
            var rtn = signer2.VerifySignature(signer.GenerateSignature());

            Console.WriteLine("Ed25519");
            Console.WriteLine("== Message: {0} ", msg);

            Console.WriteLine("\n== Signature === ");
            Console.WriteLine("== Signature: {0} [{1}] ", Convert.ToHexString(signer.GenerateSignature()), Convert.ToBase64String(signer.GenerateSignature()));
            Console.WriteLine("== Verified: {0} ", rtn);

            Console.WriteLine("\n== Private key === ");
            Console.WriteLine("== Private key ==={0} ", Convert.ToHexString(privateKey.GetEncoded()));
            Console.WriteLine("\n== Public key === ");
            Console.WriteLine("== Public key ==={0} ", Convert.ToHexString(publicKey.GetEncoded()));
        }
        catch (Exception e)
        {
            Console.WriteLine("Error: {0}", e.Message);
        }
    }

}


public class Curve25519Helper
{
    public static void Test()
    {
        // Generate key pair for Party A
        var partyAKeyPair = GenerateCurve25519KeyPair();
        // Generate key pair for Party B
        var partyBKeyPair = GenerateCurve25519KeyPair();

        // Compute shared secrets
        var sharedSecretA = ComputeSharedSecret(partyAKeyPair.Private, partyBKeyPair.Public);
        var sharedSecretB = ComputeSharedSecret(partyBKeyPair.Private, partyAKeyPair.Public);

        Console.WriteLine("Shared Secret A: " + BitConverter.ToString(sharedSecretA));
        Console.WriteLine("Shared Secret B: " + BitConverter.ToString(sharedSecretB));

        // Verify that shared secrets match
        Console.WriteLine("Secrets Match: " + (BitConverter.ToString(sharedSecretA) == BitConverter.ToString(sharedSecretB)));
    }

    static AsymmetricCipherKeyPair GenerateCurve25519KeyPair()
    {
        var keyPairGenerator = new X25519KeyPairGenerator();
        Ed25519KeyGenerationParameters keygenParams = new(new SecureRandom());
        keyPairGenerator.Init(keygenParams);
        return keyPairGenerator.GenerateKeyPair();
    }

    static byte[] ComputeSharedSecret(ICipherParameters privateKey, ICipherParameters publicKey)
    {
        var agreement = new X25519Agreement();
        agreement.Init(privateKey);
        byte[] agreementValue = new byte[agreement.AgreementSize];
        agreement.CalculateAgreement(publicKey, agreementValue, 0);
        return agreementValue;
    }
}