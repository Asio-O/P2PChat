using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using P2PChat.Crypto.Encryption;
using Shouldly;

namespace P2PChat.Integration.Tests;

/// <summary>
/// AES-256-GCM 加解密 + ECDH(P-256) + HKDF + 签名 往返验证。
/// 密文格式契约: [12 字节 nonce] + [密文] + [16 字节 tag]。
/// </summary>
public class CryptoRoundTripTests
{
    private static readonly AesGcmEncryptionService Crypto =
        new(NullLogger<AesGcmEncryptionService>.Instance);

    private static byte[] RandomKey() => Crypto.GenerateRandomKey();

    public static TheoryData<int> PayloadSizes() => new() { 0, 1, 15, 16, 17, 1024, 65536, 1024 * 1024 };

    [Theory]
    [MemberData(nameof(PayloadSizes))]
    public void AESGCM_加密解密往返_明文完全一致(int size)
    {
        var plaintext = new byte[size];
        Random.Shared.NextBytes(plaintext);
        var key = RandomKey();

        var ciphertext = Crypto.Encrypt(plaintext, key);
        var decrypted = Crypto.Decrypt(ciphertext, key);

        decrypted.ShouldBe(plaintext);
        // 格式契约: 12 nonce + N 密文 + 16 tag
        ciphertext.Length.ShouldBe(12 + size + 16);
    }

    [Fact]
    public void AESGCM_中文与emoji文本往返一致()
    {
        var key = RandomKey();
        const string text = "端到端加密私聊：你好，世界！🚀🔐";
        var plaintext = Encoding.UTF8.GetBytes(text);

        var decrypted = Crypto.Decrypt(Crypto.Encrypt(plaintext, key), key);

        Encoding.UTF8.GetString(decrypted).ShouldBe(text);
    }

    [Fact]
    public void AESGCM_每次加密使用不同nonce_相同明文密文不同()
    {
        var key = RandomKey();
        var plaintext = Encoding.UTF8.GetBytes("same plaintext");

        var c1 = Crypto.Encrypt(plaintext, key);
        var c2 = Crypto.Encrypt(plaintext, key);

        c1.ShouldNotBe(c2);
        Crypto.Decrypt(c1, key).ShouldBe(Crypto.Decrypt(c2, key));
    }

    [Fact]
    public void AESGCM_密文被篡改时_解密必须抛异常()
    {
        var key = RandomKey();
        var ciphertext = Crypto.Encrypt(Encoding.UTF8.GetBytes("tamper me"), key);

        ciphertext[20] ^= 0xFF;   // 篡改密文正文

        Should.Throw<CryptographicException>(() => Crypto.Decrypt(ciphertext, key));
    }

    [Fact]
    public void AESGCM_认证标签被篡改时_解密必须抛异常()
    {
        var key = RandomKey();
        var ciphertext = Crypto.Encrypt(Encoding.UTF8.GetBytes("tamper me"), key);

        ciphertext[^1] ^= 0xFF;   // 篡改 tag

        Should.Throw<CryptographicException>(() => Crypto.Decrypt(ciphertext, key));
    }

    [Fact]
    public void AESGCM_使用错误密钥_解密必须抛异常()
    {
        var ciphertext = Crypto.Encrypt(Encoding.UTF8.GetBytes("secret"), RandomKey());

        Should.Throw<CryptographicException>(() => Crypto.Decrypt(ciphertext, RandomKey()));
    }

    [Fact]
    public void AESGCM_密钥长度不是32字节_必须拒绝()
    {
        Should.Throw<ArgumentException>(() => Crypto.Encrypt(new byte[4], new byte[16]));
        Should.Throw<ArgumentException>(() => Crypto.Decrypt(new byte[64], new byte[31]));
    }

    [Fact]
    public void AESGCM_密文过短_必须拒绝()
    {
        Should.Throw<ArgumentException>(() => Crypto.Decrypt(new byte[27], RandomKey()));
    }

    [Fact]
    public void ECDH_双方派生出相同共享密钥()
    {
        var alice = Crypto.GenerateKeyPair();
        var bob = Crypto.GenerateKeyPair();

        var sharedFromAlice = Crypto.DeriveSharedSecret(alice.PrivateKey, bob.PublicKey);
        var sharedFromBob = Crypto.DeriveSharedSecret(bob.PrivateKey, alice.PublicKey);

        sharedFromBob.ShouldBe(sharedFromAlice);
        sharedFromAlice.Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void ECDH_不同密钥对派生出不同共享密钥()
    {
        var alice = Crypto.GenerateKeyPair();
        var bob = Crypto.GenerateKeyPair();
        var eve = Crypto.GenerateKeyPair();

        var ab = Crypto.DeriveSharedSecret(alice.PrivateKey, bob.PublicKey);
        var ae = Crypto.DeriveSharedSecret(alice.PrivateKey, eve.PublicKey);

        ab.ShouldNotBe(ae);
    }

    [Fact]
    public void HKDF_相同输入产生相同会话密钥_且为32字节()
    {
        var shared = new byte[32];
        Random.Shared.NextBytes(shared);

        var k1 = Crypto.DeriveSessionKey(shared);
        var k2 = Crypto.DeriveSessionKey(shared);

        k1.ShouldBe(k2);
        k1.Length.ShouldBe(32);
    }

    [Fact]
    public void HKDF_不同salt产生不同会话密钥()
    {
        var shared = new byte[32];
        Random.Shared.NextBytes(shared);

        var k1 = Crypto.DeriveSessionKey(shared, Encoding.UTF8.GetBytes("salt-A"));
        var k2 = Crypto.DeriveSessionKey(shared, Encoding.UTF8.GetBytes("salt-B"));

        k1.ShouldNotBe(k2);
    }

    [Fact]
    public void 会话密钥可用于AESGCM加解密_即ECDH到加密的完整链路()
    {
        var alice = Crypto.GenerateKeyPair();
        var bob = Crypto.GenerateKeyPair();

        // 双方独立派生会话密钥
        var aliceKey = Crypto.DeriveSessionKey(Crypto.DeriveSharedSecret(alice.PrivateKey, bob.PublicKey));
        var bobKey = Crypto.DeriveSessionKey(Crypto.DeriveSharedSecret(bob.PrivateKey, alice.PublicKey));

        var plaintext = Encoding.UTF8.GetBytes("通过 ECDH 会话密钥加密的私聊内容");
        var ciphertext = Crypto.Encrypt(plaintext, aliceKey);

        Encoding.UTF8.GetString(Crypto.Decrypt(ciphertext, bobKey)).ShouldBe("通过 ECDH 会话密钥加密的私聊内容");
    }

    [Fact]
    public void 签名验签往返_篡改后验签失败()
    {
        var keys = Crypto.GenerateKeyPair();
        var data = Encoding.UTF8.GetBytes("需要签名的内容");

        var signature = Crypto.Sign(data, keys.PrivateKey);

        Crypto.Verify(data, signature, keys.PublicKey).ShouldBeTrue();
        Crypto.Verify(Encoding.UTF8.GetBytes("被篡改的内容"), signature, keys.PublicKey).ShouldBeFalse();
    }

    [Fact]
    public void GenerateRandomKey_每次生成不同的32字节密钥()
    {
        var k1 = Crypto.GenerateRandomKey();
        var k2 = Crypto.GenerateRandomKey();

        k1.Length.ShouldBe(32);
        k2.Length.ShouldBe(32);
        k1.ShouldNotBe(k2);
    }
}
