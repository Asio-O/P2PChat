using P2PChat.Core.Models;

namespace P2PChat.Core.Abstractions;

/// <summary>
/// Kademlia路由表 — k-bucket组织
/// </summary>
public interface IRoutingTable
{
    /// <summary>本地节点ID</summary>
    NodeId LocalNodeId { get; }

    /// <summary>bucket数量 (k参数)</summary>
    int BucketSize { get; }

    /// <summary>
    /// 添加或更新节点 (移到桶尾部表示最近联系)
    /// </summary>
    void AddOrUpdate(NodeInfo node);

    /// <summary>
    /// 获取距离目标节点最近的k个节点
    /// </summary>
    IReadOnlyList<NodeInfo> GetClosestContacts(NodeId target, int count);

    /// <summary>
    /// 获取所有已知节点
    /// </summary>
    IReadOnlyList<NodeInfo> GetAllContacts();

    /// <summary>
    /// 移除指定节点
    /// </summary>
    void Remove(NodeId nodeId);

    /// <summary>
    /// 获取需要刷新的桶（最久未更新的桶）
    /// </summary>
    NodeId? GetStaleBucketTarget();
}
