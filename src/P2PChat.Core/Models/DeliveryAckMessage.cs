using MessagePack;

namespace P2PChat.Core.Models;

/// <summary>
/// 消息送达确认
/// </summary>
[MessagePackObject]
public record DeliveryAckMessage : Message
{
    /// <summary>被确认的消息ID</summary>
    [Key(10)]
    public Guid AcknowledgedMessageId { get; init; }

    /// <summary>送达状态</summary>
    [Key(11)]
    public required string Status { get; init; } // "delivered", "error"
}
