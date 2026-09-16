using P2PChat.Core.Models;

namespace P2PChat.Networking.Dht;

/// <summary>
/// Kademlia k-bucket — 维护最多k个节点的有序列表
/// 按最近联系时间排序 (尾部=最近)
/// </summary>
public class KBucket
{
    private readonly LinkedList<NodeInfo> _contacts = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public int BucketIndex { get; }
    public int MaxSize { get; }
    public int Count => _contacts.Count;
    public DateTime LastChanged { get; private set; } = DateTime.UtcNow;

    public KBucket(int bucketIndex, int maxSize = 20)
    {
        BucketIndex = bucketIndex;
        MaxSize = maxSize;
    }

    /// <summary>
    /// 添加节点或将已存在节点移到尾部
    /// </summary>
    public async Task<bool> AddOrMoveToTailAsync(NodeInfo node)
    {
        await _lock.WaitAsync();
        try
        {
            // 检查是否已存在
            var existing = _contacts.First;
            while (existing != null)
            {
                if (existing.Value.NodeId.Equals(node.NodeId))
                {
                    var contact = existing.Value;
                    _contacts.Remove(existing);
                    // 更新最后看到时间
                    contact.LastSeen = node.LastSeen;
                    contact.State = node.State;
                    _contacts.AddLast(contact);
                    LastChanged = DateTime.UtcNow;
                    return false; // 已存在,未新增
                }
                existing = existing.Next;
            }

            // 新节点
            if (_contacts.Count >= MaxSize)
            {
                // 检查最老节点是否仍存活 (头部=最久未联系)
                var oldest = _contacts.First!.Value;
                // 此处不主动PING，由外部调度
                return false; // 桶满，未插入
            }

            _contacts.AddLast(node);
            LastChanged = DateTime.UtcNow;
            return true; // 新插入
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 移除指定节点
    /// </summary>
    public async Task<bool> RemoveAsync(NodeId nodeId)
    {
        await _lock.WaitAsync();
        try
        {
            var node = _contacts.First;
            while (node != null)
            {
                if (node.Value.NodeId.Equals(nodeId))
                {
                    _contacts.Remove(node);
                    LastChanged = DateTime.UtcNow;
                    return true;
                }
                node = node.Next;
            }
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 获取桶中所有节点 (线程安全的快照)
    /// </summary>
    public async Task<IReadOnlyList<NodeInfo>> GetAllContactsAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return _contacts.ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 获取桶中最久未联系的节点 (头部)
    /// </summary>
    public async Task<NodeInfo?> GetOldestAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return _contacts.First?.Value;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 检查节点是否在桶中
    /// </summary>
    public async Task<bool> ContainsAsync(NodeId nodeId)
    {
        await _lock.WaitAsync();
        try
        {
            return _contacts.Any(c => c.NodeId.Equals(nodeId));
        }
        finally
        {
            _lock.Release();
        }
    }
}
