using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using P2PChat.Core.Abstractions;

namespace P2PChat.Crypto.Encryption;

/// <summary>
/// AES-256-GCM 加密服务实现
/// ECDH(X25519)密钥协商 + HKDF-SHA256密钥派生 + AES-256-GCM加解密
/// </summary>
public class AesGcmEncryptionService : IEncryptionService
{
    private readonly ILogger<AesGcmEncryptionService> _logger;

    public AesGcmEncryptionService(ILogger<AesGcmEncryptionService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public KeyPair GenerateKeyPair()
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = ecdh.ExportSubjectPublicKeyInfo();
        var privateKey = ecdh.ExportECPrivateKey();
        _logger.LogDebug("生成新的ECDH密钥对 (P-256)");
        return new KeyPair { PublicKey = publicKey, PrivateKey = privateKey };
    }

    /// <inheritdoc />
    public byte[] DeriveSharedSecret(byte[] localPrivateKey, byte[] remotePublicKey)
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        ecdh.ImportECPrivateKey(localPrivateKey, out _);

        using var remoteEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        remoteEcdh.ImportSubjectPublicKeyInfo(remotePublicKey, out _);
        var remotePubKey = remoteEcdh.PublicKey;

        var sharedSecret = ecdh.DeriveKeyMaterial(remotePubKey);
        _logger.LogTrace("派生ECDH共享密钥, 长度={Len}字节", sharedSecret.Length);
        return sharedSecret;
    }

    /// <inheritdoc />
    public byte[] DeriveSessionKey(byte[] sharedSecret, byte[]? salt = null, byte[]? info = null)
    {
        salt ??= new byte[32]; // 空盐
        info ??= "P2PChat-session-key"u8.ToArray();

        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, salt, info);
        _logger.LogTrace("HKDF派生会话密钥, 长度={Len}字节", key.Length);
        return key;
    }

    /// <inheritdoc />
    public byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key)
    {
        if (key.Length != 32)
            throw new ArgumentException("密钥必须是256位(32字节)", nameof(key));

        var nonce = new byte[12]; // AES-GCM标准nonce大小
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16]; // AES-GCM认证标签

        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        // 输出格式: [12字节nonce] + [密文] + [16字节tag]
        var result = new byte[nonce.Length + ciphertext.Length + tag.Length];
        Array.Copy(nonce, 0, result, 0, nonce.Length);
        Array.Copy(ciphertext, 0, result, nonce.Length, ciphertext.Length);
        Array.Copy(tag, 0, result, nonce.Length + ciphertext.Length, tag.Length);

        return result;
    }

    /// <inheritdoc />
    public byte[] Decrypt(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> key)
    {
        if (key.Length != 32)
            throw new ArgumentException("密钥必须是256位(32字节)", nameof(key));
        if (ciphertext.Length < 28) // 12(nonce) + 0(最少) + 16(tag)
            throw new ArgumentException("密文太短", nameof(ciphertext));

        var nonce = ciphertext[..12];
        var tag = ciphertext[^16..];
        var encryptedData = ciphertext[12..^16];

        var plaintext = new byte[encryptedData.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, encryptedData, tag, plaintext);

        return plaintext;
    }

    /// <inheritdoc />
    public byte[] Sign(ReadOnlySpan<byte> data, ReadOnlySpan<byte> privateKey)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ecdsa.ImportECPrivateKey(privateKey, out _);
        var signature = ecdsa.SignData(data, HashAlgorithmName.SHA256);
        _logger.LogTrace("ECDSA签名, 长度={Len}字节", signature.Length);
        return signature;
    }

    /// <inheritdoc />
    public bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> publicKey)
    {
        try
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out _);
            return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "签名验证失败");
            return false;
        }
    }

    /// <inheritdoc />
    public byte[] GenerateRandomKey()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        return key;
    }
}
