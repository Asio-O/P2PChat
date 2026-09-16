using System.Net;
using P2PChat.Core.Models;
using P2PChat.Integration.Tests.Support;
using P2PChat.Networking.Dht;
using Shouldly;
using Xunit.Abstractions;

namespace P2PChat.Integration.Tests;

/// <summary>
/// Kademlia 路由表 / k-bucket 行为验证。
/// </summary>
public class DhtRoutingTableTests(ITestOutputHelper output)
{
    private static NodeInfo MakeNode(NodeId id, int port = 40000)
        => new()
        {
            NodeId = id,
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            PublicKey = new byte[32]
        };

    private static int CompareDistance(byte[] a, byte[] b)
    {
        for (var i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return a[i] - b[i];
        return 0;
    }

    #region NodeId

    [Fact]
    public void NodeId_FromPublicKey_产生20字节且hex为40位小写()
    {
        var publicKey = new byte[91];
        Random.Shared.NextBytes(publicKey);

        var id = NodeId.FromPublicKey(publicKey);

        id.ToByteArray().Length.ShouldBe(20);
        id.ToHexString().Length.ShouldBe(40);
        id.ToHexString().ShouldBe(id.ToHexString().ToLowerInvariant());
    }

    [Fact]
    public void NodeId_长度不是20字节_必须拒绝()
    {
        Should.Throw<ArgumentException>(() => new NodeId(new byte[19]));
        Should.Throw<ArgumentException>(() => new NodeId(new byte[21]));
    }

    [Fact]
    public void NodeId_相同值的两个实例相等_且哈希一致()
    {
        var bytes = new byte[20];
        Random.Shared.NextBytes(bytes);

        var a = new NodeId(bytes);
        var b = new NodeId((byte[])bytes.Clone());

        a.Equals(b).ShouldBeTrue();
        a.GetHashCode().ShouldBe(b.GetHashCode());
        a.ToHexString().ShouldBe(b.ToHexString());
    }

    [Fact]
    public void NodeId_CommonPrefixLength_相同ID为160_仅末位不同为159()
    {
        var bytes = new byte[20];
        var a = new NodeId(bytes);
        a.CommonPrefixLength(a).ShouldBe(160);

        var last = (byte[])bytes.Clone();
        last[19] = 0x01;
        a.CommonPrefixLength(new NodeId(last)).ShouldBe(159);

        var first = (byte[])bytes.Clone();
        first[0] = 0x80;
        a.CommonPrefixLength(new NodeId(first)).ShouldBe(0);
    }

    [Fact]
    public void NodeId_XorDistance_对称且为零当且仅当相同()
    {
        var a = NodeId.CreateRandom();
        var b = NodeId.CreateRandom();

        a.XorDistanceTo(b).ShouldBe(b.XorDistanceTo(a));
        a.XorDistanceTo(a).ShouldBe(new byte[20]);
    }

    #endregion

    #region RoutingTable

    [Fact]
    public void RoutingTable_添加节点后可被查询到()
    {
        var localId = NodeId.CreateRandom();
        var table = new RoutingTable(localId, 20);
        var node = MakeNode(NodeId.CreateRandom());

        table.AddOrUpdate(node);

        table.GetAllContacts().Count.ShouldBe(1);
        table.GetAllContacts()[0].NodeId.ShouldBe(node.NodeId);
    }

    [Fact]
    public void RoutingTable_不把本地节点加入路由表()
    {
        var localId = NodeId.CreateRandom();
        var table = new RoutingTable(localId, 20);

        table.AddOrUpdate(MakeNode(localId));

        table.GetAllContacts().ShouldBeEmpty();
    }

    [Fact]
    public void RoutingTable_重复添加同一节点不产生重复项()
    {
        var table = new RoutingTable(NodeId.CreateRandom(), 20);
        var node = MakeNode(NodeId.CreateRandom());

        table.AddOrUpdate(node);
        table.AddOrUpdate(node);
        table.AddOrUpdate(MakeNode(node.NodeId, 41000));

        table.GetAllContacts().Count.ShouldBe(1);
    }

    [Fact]
    public void RoutingTable_GetBucketIndex_等于公共前缀长度()
    {
        var localId = NodeId.CreateRandom();
        var table = new RoutingTable(localId, 20);

        table.GetBucketIndex(localId).ShouldBe(159);   // 前缀160被夹到 bucketCount-1
        var near = NodeId.CreateRandom();
        table.GetBucketIndex(near).ShouldBe(localId.CommonPrefixLength(near));
    }

    [Fact]
    public void RoutingTable_GetClosestContacts_返回按XOR距离最近的k个()
    {
        var localId = NodeId.CreateRandom();
        // bucketSize 取足够大，避免 k-bucket 容量截断干扰本测试的排序验证
        var table = new RoutingTable(localId, 500);
        var nodes = Enumerable.Range(0, 100).Select(i => MakeNode(NodeId.CreateRandom(), 40000 + i)).ToList();

        foreach (var n in nodes) table.AddOrUpdate(n);
        table.GetAllContacts().Count.ShouldBe(100);

        var target = NodeId.CreateRandom();
        var actual = table.GetClosestContacts(target, 5);

        var expected = nodes
            .OrderBy(n => n.NodeId.XorDistanceTo(target), Comparer<byte[]>.Create(CompareDistance))
            .Take(5)
            .Select(n => n.NodeId.ToHexString())
            .ToList();

        actual.Count.ShouldBe(5);
        actual.Select(n => n.NodeId.ToHexString()).ShouldBe(expected);
    }

    [Fact]
    public void RoutingTable_Remove_移除指定节点()
    {
        var table = new RoutingTable(NodeId.CreateRandom(), 20);
        var node = MakeNode(NodeId.CreateRandom());
        table.AddOrUpdate(node);
        table.GetAllContacts().Count.ShouldBe(1);

        table.Remove(node.NodeId);

        table.GetAllContacts().ShouldBeEmpty();
    }

    [Fact]
    public void RoutingTable_GetStaleBucketTarget_空表返回null()
    {
        var table = new RoutingTable(NodeId.CreateRandom(), 20);

        table.GetStaleBucketTarget().ShouldBeNull();
    }

    [Fact]
    public async Task RoutingTable_并发添加_最终一致但可能暴露fire_and_forget未完成()
    {
        // AddOrUpdate 内部是 `_ = bucket.AddOrMoveToTailAsync(node)` (fire-and-forget)，
        // 而 GetAllContacts 是同步阻塞读取。锁争用下 AddOrUpdate 返回时 insertion 可能尚未完成。
        // bucketSize 取足够大，避免 k-bucket 容量截断干扰本测试。
        var table = new RoutingTable(NodeId.CreateRandom(), 500);
        var nodes = Enumerable.Range(0, 200).Select(i => MakeNode(NodeId.CreateRandom(), 40000 + i)).ToList();

        await Task.WhenAll(nodes.Select(n => Task.Run(() => table.AddOrUpdate(n))));

        var immediate = table.GetAllContacts().Count;
        var eventual = await Wait.UntilAsync(() => table.GetAllContacts().Count == 200, 10000);
        var afterWait = table.GetAllContacts().Count;

        output.WriteLine($"并发 AddOrUpdate 200 次：立即读取={immediate}，最终={afterWait}");
        eventual.ShouldBeTrue($"最终应达到 200，实际 {afterWait}");
    }

    #endregion

    #region KBucket

    [Fact]
    public async Task KBucket_超出MaxSize后不再插入()
    {
        var bucket = new KBucket(0, maxSize: 3);

        (await bucket.AddOrMoveToTailAsync(MakeNode(NodeId.CreateRandom(), 1))).ShouldBeTrue();
        (await bucket.AddOrMoveToTailAsync(MakeNode(NodeId.CreateRandom(), 2))).ShouldBeTrue();
        (await bucket.AddOrMoveToTailAsync(MakeNode(NodeId.CreateRandom(), 3))).ShouldBeTrue();
        (await bucket.AddOrMoveToTailAsync(MakeNode(NodeId.CreateRandom(), 4))).ShouldBeFalse();

        bucket.Count.ShouldBe(3);
    }

    [Fact]
    public async Task KBucket_重复添加已存在节点_移到尾部且不增加计数()
    {
        var bucket = new KBucket(0, maxSize: 3);
        var first = MakeNode(NodeId.CreateRandom(), 1);
        var second = MakeNode(NodeId.CreateRandom(), 2);
        await bucket.AddOrMoveToTailAsync(first);
        await bucket.AddOrMoveToTailAsync(second);

        var updated = MakeNode(first.NodeId, 3);
        (await bucket.AddOrMoveToTailAsync(updated)).ShouldBeFalse();

        var contacts = await bucket.GetAllContactsAsync();
        contacts.Count.ShouldBe(2);
        contacts[^1].NodeId.ShouldBe(first.NodeId);      // 已移到尾部
        (await bucket.GetOldestAsync())!.NodeId.ShouldBe(second.NodeId);
        // 注：KBucket 在更新已存在节点时只同步 LastSeen/State，不同步 EndPoint（NodeInfo.EndPoint 是 init-only，
        // 现有实现保留首次的 EndPoint），因此此处不断言端口更新。
    }

    [Fact]
    public async Task KBucket_Remove与Contains行为正确()
    {
        var bucket = new KBucket(0, maxSize: 3);
        var node = MakeNode(NodeId.CreateRandom(), 1);
        await bucket.AddOrMoveToTailAsync(node);

        (await bucket.ContainsAsync(node.NodeId)).ShouldBeTrue();
        (await bucket.RemoveAsync(node.NodeId)).ShouldBeTrue();
        (await bucket.ContainsAsync(node.NodeId)).ShouldBeFalse();
        (await bucket.RemoveAsync(node.NodeId)).ShouldBeFalse();
        bucket.Count.ShouldBe(0);
    }

    [Fact]
    public async Task KBucket_GetAllContactsAsync返回快照_后续修改不影响已取出的列表()
    {
        var bucket = new KBucket(0, maxSize: 10);
        await bucket.AddOrMoveToTailAsync(MakeNode(NodeId.CreateRandom(), 1));

        var snapshot = await bucket.GetAllContactsAsync();
        await bucket.AddOrMoveToTailAsync(MakeNode(NodeId.CreateRandom(), 2));

        snapshot.Count.ShouldBe(1);
        bucket.Count.ShouldBe(2);
    }

    #endregion
}
