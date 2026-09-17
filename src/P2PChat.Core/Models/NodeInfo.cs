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

    /// <summary>
    /// 节点身份公钥 — ECDH nistP256，格式为 SubjectPublicKeyInfo (DER，91字节)。
    /// </summary>
    /// <remarks>
    /// 注意：这是 ECDH P-256 公钥，<b>不是</b> Ed25519 公钥——历史上本注释曾误写为 Ed25519。
    /// NodeId 由该公钥经 SHA-1 派生 (见 <see cref="NodeId.FromPublicKey"/>)。
    /// </remarks>
    public required byte[] PublicKey { get; init; }

    /// <summary>最后探测时间</summary>
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    /// <summary>在线状态</summary>
    public Enums.PeerState State { get; set; } = Enums.PeerState.Online;
}
