using P2PChat.Core.Abstractions;
using P2PChat.Core.Models;

namespace P2PChat.Networking.Dht;

/// <summary>
/// Kademlia路由表 — 160个k-bucket, 按距离组织
/// </summary>
public class RoutingTable : IRoutingTable
{
    private readonly KBucket[] _buckets;
    private readonly int _bucketCount = 160; // SHA-1 = 160 bits

    public NodeId LocalNodeId { get; }
    public int BucketSize { get; }

    public RoutingTable(NodeId localNodeId, int bucketSize = 20)
    {
        LocalNodeId = localNodeId;
        BucketSize = bucketSize;
        _buckets = new KBucket[_bucketCount];
        for (int i = 0; i < _bucketCount; i++)
            _buckets[i] = new KBucket(i, bucketSize);
    }

    /// <inheritdoc />
    public void AddOrUpdate(NodeInfo node)
    {
        if (node.NodeId.Equals(LocalNodeId)) return;

        var bucketIndex = GetBucketIndex(node.NodeId);
        _ = _buckets[bucketIndex].AddOrMoveToTailAsync(node);
    }

    /// <inheritdoc />
    public IReadOnlyList<NodeInfo> GetClosestContacts(NodeId target, int count)
    {
        var allContacts = new List<NodeInfo>();
        foreach (var bucket in _buckets)
        {
            var contacts = bucket.GetAllContactsAsync().GetAwaiter().GetResult();
            allContacts.AddRange(contacts);
        }

        // 按XOR距离排序
        var targetBytes = target.ToByteArray();
        return allContacts
            .OrderBy(c =>
            {
                var xor = c.NodeId.XorDistanceTo(target);
                // 将XOR距离转为BigInteger排序
                return Convert.ToHexString(xor);
            })
            .Take(count)
            .ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<NodeInfo> GetAllContacts()
    {
        var allContacts = new List<NodeInfo>();
        foreach (var bucket in _buckets)
        {
            var contacts = bucket.GetAllContactsAsync().GetAwaiter().GetResult();
            allContacts.AddRange(contacts);
        }
        return allContacts;
    }

    /// <inheritdoc />
    public void Remove(NodeId nodeId)
    {
        var bucketIndex = GetBucketIndex(nodeId);
        _ = _buckets[bucketIndex].RemoveAsync(nodeId);
    }

    /// <inheritdoc />
    public NodeId? GetStaleBucketTarget()
    {
        var now = DateTime.UtcNow;
        KBucket? stalestBucket = null;
        DateTime stalestTime = DateTime.MaxValue;

        foreach (var bucket in _buckets)
        {
            if (bucket.Count > 0 && bucket.LastChanged < stalestTime)
            {
                stalestTime = bucket.LastChanged;
                stalestBucket = bucket;
            }
        }

        if (stalestBucket != null)
        {
            var contact = stalestBucket.GetOldestAsync().GetAwaiter().GetResult();
            return contact?.NodeId ?? GenerateRandomIdInBucket(stalestBucket.BucketIndex);
        }

        return null;
    }

    /// <summary>
    /// 计算目标节点所属的桶索引 (公共前缀位数)
    /// </summary>
    public int GetBucketIndex(NodeId nodeId)
    {
        var prefixLen = LocalNodeId.CommonPrefixLength(nodeId);
        return Math.Min(prefixLen, _bucketCount - 1);
    }

    /// <summary>
    /// 生成指定桶范围内的随机ID
    /// </summary>
    private NodeId GenerateRandomIdInBucket(int bucketIndex)
    {
        var localBytes = LocalNodeId.ToByteArray();
        // 取本地ID前 (bucketIndex/8) 个字节，第 (bucketIndex/8) 位翻转
        var randomId = new byte[NodeId.Size];
        Array.Copy(localBytes, randomId, NodeId.Size);

        int byteIndex = bucketIndex / 8;
        int bitIndex = 7 - (bucketIndex % 8);
        if (byteIndex < NodeId.Size)
            randomId[byteIndex] ^= (byte)(1 << bitIndex);

        return new NodeId(randomId);
    }

    /// <summary>
    /// 统计路由表总节点数
    /// </summary>
    public int Count()
    {
        int count = 0;
        foreach (var bucket in _buckets)
            count += bucket.Count;
        return count;
    }
}
