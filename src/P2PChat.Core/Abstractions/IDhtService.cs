using System.Net;
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

    /// <summary>
    /// 累计成功完成对外宣告（announce_peer）的对端节点数。
    /// </summary>
    /// <remarks>
    /// 仅 <c>MainlineDhtService</c> 实现真实的 <c>announce_peer</c> 协议路径；其它实现的默认值为 0。
    /// 由 <c>P2PCHAT_SELFTEST</c> 输出，供无人值守断言读取。
    /// </remarks>
    int AnnouncedPeerCount => 0;

    /// <summary>
    /// 最近一次完成 announce_peer 批量宣告的时间（UTC）。未宣告过则为 <see cref="DateTime.MinValue"/>。
    /// </summary>
    /// <remarks>语义见 <see cref="AnnouncedPeerCount"/>。</remarks>
    DateTime LastAnnounceUtc => DateTime.MinValue;

    /// <summary>
    /// NAT 穿透（UPnP / NAT-PMP）状态：是否成功把本机端口暴露到公网。
    /// </summary>
    /// <remarks>
    /// 默认 <see cref="NatMappingState.NotAttempted"/>；仅 <c>MainlineDhtService</c> 装配 UPnP 后会更新。
    /// TUI 状态栏与 <c>P2PCHAT_SELFTEST</c> 用此字段提示用户可达性范围。
    /// </remarks>
    NatMappingState NatMappingState => NatMappingState.NotAttempted;

    /// <summary>UPnP 探测到的本机公网入口；未成功时为 null。</summary>
    IPEndPoint? LocalExternalEndPoint => null;
}

/// <summary>NAT 穿透（UPnP / NAT-PMP）状态。</summary>
public enum NatMappingState
{
    /// <summary>尚未尝试 UPnP 映射。</summary>
    NotAttempted,
    /// <summary>UPnP 映射成功，本机端口已对外暴露。</summary>
    Mapped,
    /// <summary>无 UPnP / 路由器不支持 / 映射失败 —— 本机只对同网段/已有连接可达。</summary>
    Unavailable
}
