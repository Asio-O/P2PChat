using MessagePack;

namespace P2PChat.Core.Models;

/// <summary>
/// ECDH(nistP256) 临时密钥交换消息 —— 用于首次通信前协商会话密钥
/// </summary>
[MessagePackObject]
public record KeyExchangeMessage : Message
{
    /// <summary>
    /// 发送方临时公钥：ECDH nistP256 的 SubjectPublicKeyInfo (DER)。
    /// 注意不是 X25519 —— 32 字节固定长度的写法在此不适用。
    /// </summary>
    [Key(10)]
    public required byte[] EphemeralPublicKey { get; init; }

    /// <summary>是否为响应 (true=响应, false=请求)</summary>
    [Key(11)]
    public bool IsResponse { get; init; }
}
