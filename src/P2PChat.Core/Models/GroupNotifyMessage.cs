using MessagePack;

namespace P2PChat.Core.Models;

/// <summary>
/// 群组操作通知 (加入/离开/解散)
/// </summary>
[MessagePackObject]
public record GroupNotifyMessage : Message
{
    /// <summary>群组ID</summary>
    [Key(10)]
    public required string GroupId { get; init; }

    /// <summary>操作类型: join, leave, dissolve</summary>
    [Key(11)]
    public required string Action { get; init; }

    /// <summary>操作者节点ID</summary>
    [Key(12)]
    public required byte[] OperatorId { get; init; }
}
