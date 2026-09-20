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
    /// 登记一个「静态对端」：端点已知、无需 DHT 查找。
    /// <para>
    /// 手工添加的联系人（<c>/add &lt;节点ID&gt; &lt;ip:port&gt;</c>）走这条路径。
    /// <see cref="FindNodeAsync"/> 必须**优先**返回静态对端，再退回 DHT 迭代查找，
    /// 且静态登记不受路由表淘汰影响。
    /// </para>
    /// <para>
    /// 存在理由：公共 Mainline DHT 上没有任何节点为我们宣告 <c>NodeId → 端点</c> 映射，
    /// 因此迭代 <c>find_node</c> 不可能命中对端；静态对端是当前唯一可靠的直连手段。
    /// </para>
    /// </summary>
    void RegisterStaticPeer(NodeInfo node);

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
