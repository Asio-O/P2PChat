namespace P2PChat.Core.Models;

/// <summary>
/// 联系人状态变更事件
/// </summary>
public record ContactStatusEvent
{
    /// <summary>联系人节点ID</summary>
    public required NodeId NodeId { get; init; }

    /// <summary>是否在线</summary>
    public bool IsOnline { get; init; }

    /// <summary>新状态</summary>
    public Enums.PeerState State { get; init; }
}
