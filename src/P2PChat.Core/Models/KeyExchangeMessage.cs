using MessagePack;

namespace P2PChat.Core.Models;

/// <summary>
/// ECDH密钥交换消息
/// </summary>
[MessagePackObject]
public record KeyExchangeMessage : Message
{
    /// <summary>发送方临时公钥 (X25519, 32字节)</summary>
    [Key(10)]
    public required byte[] EphemeralPublicKey { get; init; }

    /// <summary>是否为响应 (true=响应, false=请求)</summary>
    [Key(11)]
    public bool IsResponse { get; init; }
}
