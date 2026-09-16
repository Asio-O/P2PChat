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

    /// <summary>负载数据 (MessagePack序列化后的Message子类)</summary>
    public required byte[] Payload { get; init; }
}
