namespace P2PChat.Core.Abstractions;

/// <summary>
/// 加密服务 — ECDH密钥协商、AES-256-GCM加解密、数字签名
/// </summary>
public interface IEncryptionService
{
    /// <summary>
    /// 生成ECDH密钥对 (X25519)
    /// </summary>
    KeyPair GenerateKeyPair();

    /// <summary>
    /// 派生共享密钥: 己方私钥 + 对方公钥 → 共享密钥
    /// </summary>
    byte[] DeriveSharedSecret(byte[] localPrivateKey, byte[] remotePublicKey);

    /// <summary>
    /// 从共享密钥派生会话密钥 (HKDF)
    /// </summary>
    byte[] DeriveSessionKey(byte[] sharedSecret, byte[]? salt = null, byte[]? info = null);

    /// <summary>
    /// AES-256-GCM加密
    /// </summary>
    /// <param name="plaintext">明文</param>
    /// <param name="key">256位密钥</param>
    /// <returns>[12字节nonce] + [密文] + [16字节tag]</returns>
    byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key);

    /// <summary>
    /// AES-256-GCM解密
    /// </summary>
    /// <param name="ciphertext">[12字节nonce] + [密文] + [16字节tag]</param>
    /// <param name="key">256位密钥</param>
    byte[] Decrypt(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> key);

    /// <summary>
    /// Ed25519签名 (或ECDsa回退)
    /// </summary>
    byte[] Sign(ReadOnlySpan<byte> data, ReadOnlySpan<byte> privateKey);

    /// <summary>
    /// Ed25519验签 (或ECDsa回退)
    /// </summary>
    bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> publicKey);

    /// <summary>
    /// 生成随机密钥 (256位)
    /// </summary>
    byte[] GenerateRandomKey();
}

/// <summary>
/// 密钥对
/// </summary>
public record KeyPair
{
    public required byte[] PublicKey { get; init; }
    public required byte[] PrivateKey { get; init; }
}
