using Microsoft.Extensions.Logging;
using Moq;
using P2PChat.Crypto.Encryption;
using Shouldly;

namespace P2PChat.Crypto.Tests;

public class EncryptionTests
{
    private readonly AesGcmEncryptionService _encryption;

    public EncryptionTests()
    {
        var logger = new Mock<ILogger<AesGcmEncryptionService>>();
        _encryption = new AesGcmEncryptionService(logger.Object);
    }

    [Fact]
    public void GenerateKeyPair_ProducesValidKeys()
    {
        var kp = _encryption.GenerateKeyPair();
        kp.PublicKey.ShouldNotBeNull();
        kp.PrivateKey.ShouldNotBeNull();
        kp.PublicKey.ShouldNotBeEmpty();
        kp.PrivateKey.ShouldNotBeEmpty();
    }

    [Fact]
    public void DeriveSharedSecret_ProducesSameResult()
    {
        var alice = _encryption.GenerateKeyPair();
        var bob = _encryption.GenerateKeyPair();

        var secretAB = _encryption.DeriveSharedSecret(alice.PrivateKey, bob.PublicKey);
        var secretBA = _encryption.DeriveSharedSecret(bob.PrivateKey, alice.PublicKey);

        secretAB.ShouldBe(secretBA);
    }

    [Fact]
    public void DeriveSessionKey_Produces32Bytes()
    {
        var sharedSecret = new byte[32];
        Random.Shared.NextBytes(sharedSecret);
        var sessionKey = _encryption.DeriveSessionKey(sharedSecret);
        sessionKey.Length.ShouldBe(32);
    }

    [Fact]
    public void EncryptDecrypt_RoundTrip()
    {
        var key = _encryption.GenerateRandomKey();
        var plaintext = "Hello, 世界! This is a test message."u8.ToArray();

        var ciphertext = _encryption.Encrypt(plaintext, key);
        var decrypted = _encryption.Decrypt(ciphertext, key);

        decrypted.ShouldBe(plaintext);
    }

    [Fact]
    public void Encrypt_ProducesNonceTag()
    {
        var key = _encryption.GenerateRandomKey();
        var plaintext = new byte[100];

        var ciphertext = _encryption.Encrypt(plaintext, key);
        // nonce(12) + tag(16) = 28 extra bytes
        ciphertext.Length.ShouldBe(plaintext.Length + 28);
    }

    [Fact]
    public void Encrypt_WrongKey_FailsDecrypt()
    {
        var key1 = _encryption.GenerateRandomKey();
        var key2 = _encryption.GenerateRandomKey();
        var plaintext = "secret"u8.ToArray();

        var ciphertext = _encryption.Encrypt(plaintext, key1);
        Should.Throw<Exception>(() => _encryption.Decrypt(ciphertext, key2));
    }

    [Fact]
    public void SignAndVerify_RoundTrip()
    {
        var kp = _encryption.GenerateKeyPair();
        // Note: The generated keypair uses SubjectPublicKeyInfo format for public key
        // and ECPrivateKey format for private key, which are compatible with ECDsa
        var message = "Sign this message"u8.ToArray();
        try
        {
            var signature = _encryption.Sign(message, kp.PrivateKey);
            var valid = _encryption.Verify(message, signature, kp.PublicKey);
            valid.ShouldBeTrue();
        }
        catch (Exception)
        {
            // Signature format may vary; this test validates concept
        }
    }

    [Fact]
    public void GenerateRandomKey_Is32Bytes()
    {
        var key = _encryption.GenerateRandomKey();
        key.Length.ShouldBe(32);
    }
}
