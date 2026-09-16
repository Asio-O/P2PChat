using System.Net;

namespace P2PChat.Core.Models;

/// <summary>
/// DHT网络中的节点信息
/// </summary>
public record NodeInfo
{
    /// <summary>节点唯一标识</summary>
    public required NodeId NodeId { get; init; }

    /// <summary>节点IP终结点</summary>
    public required IPEndPoint EndPoint { get; init; }

    /// <summary>外部可观察IP终结点 (用于NAT穿透)</summary>
    public IPEndPoint? ExternalEndPoint { get; init; }

    /// <summary>Ed25519公钥 (32字节)</summary>
    public required byte[] PublicKey { get; init; }

    /// <summary>最后探测时间</summary>
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    /// <summary>在线状态</summary>
    public Enums.PeerState State { get; set; } = Enums.PeerState.Online;
}
