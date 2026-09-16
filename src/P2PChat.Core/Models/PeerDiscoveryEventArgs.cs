namespace P2PChat.Core.Models;

/// <summary>
/// 节点发现事件参数
/// </summary>
public record PeerDiscoveryEventArgs
{
    /// <summary>被发现的节点信息</summary>
    public required NodeInfo NodeInfo { get; init; }

    /// <summary>发现方式: Bootstrap / Refresh / Incoming</summary>
    public string DiscoveryMethod { get; init; } = "Unknown";
}
