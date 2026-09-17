namespace P2PChat.Core.Abstractions;

/// <summary>
/// 加密服务 — ECDH密钥协商、AES-256-GCM加解密、数字签名
/// <para>
/// 算法约定(以实现为准，见 <c>AesGcmEncryptionService</c>)：
/// 密钥协商为 <b>ECDH nistP256</b>，会话密钥派生为 <b>HKDF-SHA256</b>，
/// 加解密为 <b>AES-256-GCM</b>，签名为 <b>ECDSA P-256 + SHA-256</b>。
/// </para>
/// </summary>
public interface IEncryptionService
{
    /// <summary>
    /// 生成 ECDH 密钥对 (nistP256)。
    /// 公钥为 SubjectPublicKeyInfo (DER)，私钥为 ECPrivateKey (DER) —— 与 <see cref="DeriveSharedSecret"/> 的导入格式一致。
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
    /// ECDSA P-256 (SHA-256) 签名
    /// </summary>
    /// <param name="data">待签名数据</param>
    /// <param name="privateKey">ECPrivateKey (DER)</param>
    byte[] Sign(ReadOnlySpan<byte> data, ReadOnlySpan<byte> privateKey);

    /// <summary>
    /// ECDSA P-256 (SHA-256) 验签。任何异常(如公钥格式非法)均返回 false 而不抛出。
    /// </summary>
    /// <param name="data">原始数据</param>
    /// <param name="signature">签名值</param>
    /// <param name="publicKey">SubjectPublicKeyInfo (DER)</param>
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
