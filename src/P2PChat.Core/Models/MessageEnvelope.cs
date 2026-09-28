using P2PChat.Core.Enums;

namespace P2PChat.Core.Models;

/// <summary>
/// 消息信封 — 网络传输的包装层，包含路由头信息
/// </summary>
public record MessageEnvelope
{
    /// <summary>协议版本</summary>
    public byte Version { get; init; } = 1;

    /// <summary>消息类型</summary>
    public required MessageType MessageType { get; init; }

    /// <summary>序列号</summary>
    public uint SequenceNumber { get; init; }

    /// <summary>发送者节点ID</summary>
    public required byte[] SenderId { get; init; }

    /// <summary>消息ID</summary>
    public Guid MessageId { get; init; } = Guid.NewGuid();

    /// <summary>UTC毫秒时间戳</summary>
    public long Timestamp { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// 发送方长期身份公钥（P-256 SubjectPublicKeyInfo，约 91 字节）。
    /// <para>
    /// 接收端必须用此公钥验签 <see cref="Signature"/>；本字段为可选——若未携带则视作未签名消息，
    /// <c>MessageRouter.RouteIncomingAsync</c> 入口直接拒绝。
    /// </para>
    /// <para>
    /// 见 <c>notes/implemented/bug-fix/2026-09-21-message-signing</c>。
    /// </para>
    /// </summary>
    public byte[]? SenderPublicKey { get; init; }

    /// <summary>
    /// ECDSA P-256 / SHA-256 签名（~64 字节），覆盖除本字段外整个信封。
    /// <para>
    /// 若未携带，<c>MessageRouter.RouteIncomingAsync</c> 入口拒绝并告警。
    /// </para>
    /// </summary>
    public byte[]? Signature { get; init; }

    /// <summary>负载数据 (MessagePack序列化后的Message子类)</summary>
    public required byte[] Payload { get; init; }
}
