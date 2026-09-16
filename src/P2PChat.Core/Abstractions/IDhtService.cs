using P2PChat.Core.Models;

namespace P2PChat.Core.Abstractions;

/// <summary>
/// Kademlia DHT 服务接口 — 对等节点发现、路由、键值存储
/// </summary>
public interface IDhtService
{
    /// <summary>本地节点信息</summary>
    NodeInfo LocalNode { get; }

    /// <summary>
    /// 引导加入DHT网络
    /// </summary>
    Task BootstrapAsync(CancellationToken ct = default);

    /// <summary>
    /// 在DHT中查找指定节点ID对应的节点信息
    /// </summary>
    Task<NodeInfo?> FindNodeAsync(NodeId targetId, CancellationToken ct = default);

    /// <summary>
    /// 向DHT存储键值对 (会复制到k个最近的节点)
    /// </summary>
    Task StoreAsync(byte[] key, byte[] value, CancellationToken ct = default);

    /// <summary>
    /// 从DHT查找键对应的值
    /// </summary>
    Task<byte[]?> FindValueAsync(byte[] key, CancellationToken ct = default);

    /// <summary>
    /// PING节点检测存活
    /// </summary>
    Task<bool> PingAsync(NodeInfo node, CancellationToken ct = default);

    /// <summary>
    /// 获取路由表中所有已知节点
    /// </summary>
    IReadOnlyList<NodeInfo> GetAllKnownNodes();

    /// <summary>
    /// 节点发现事件流
    /// </summary>
    IAsyncEnumerable<PeerDiscoveryEventArgs> OnPeerDiscovered { get; }
}
