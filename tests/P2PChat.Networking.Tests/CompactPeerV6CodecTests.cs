using System.Net;
using System.Net.Sockets;
using P2PChat.Networking.Dht;
using Shouldly;

namespace P2PChat.Networking.Tests;

/// <summary>
/// <c>p2pc_peers6</c>（IPv6 扩展条目）的编解码契约。
/// <para>
/// <b>为什么另开一个字段而不是给 26 字节条目加宽度标记位</b>：决定性理由是
/// <b>老节点的失败模式</b>。bencode 字典里老节点找不到 <c>p2pc_peers6</c> ⇒ 它只会走
/// <c>values</c> 兜底而<b>得不到任何条目</b> ⇒ 干净降级。
/// 而「加标记位」那条路里，老节点遇到不认识的宽度会<b>按 26 字节错位切分</b> ⇒
/// 把新格式解析成错条目。<b>误解析远比缺失危险</b>
/// （同阶段 3.2 <c>EnvelopeCodec</c> 事故与 §8.1.2 假绿灯记录的同一个教训）。
/// </para>
/// <para>
/// <b>本文件锁住三件事</b>：① <c>p2pc_peers</c>（IPv4）<b>字节级不变</b>；
/// ② IPv6 条目能无损往返；③ <b>IPv6 地址绝不会被静默截断成 IPv4</b> —— 这一条是本轮
/// 修复的<b>核心缺陷</b>，它此前会安静产出一个语义上毫不相干的 IPv4 地址且不记日志。
/// </para>
/// </summary>
public class CompactPeerV6CodecTests
{
    private static byte[] NodeIdBytes(byte fill) => Enumerable.Repeat(fill, 20).ToArray();

    // ── ① IPv4 条目字节级不变（本轮不能动它，否则老节点全线错位） ──────────

    [Fact]
    public void 序列化IPv4端点_产出26字节且布局与旧实现逐字节相同()
    {
        var nodeId = NodeIdBytes(0x10);
        var ep = new IPEndPoint(IPAddress.Parse("10.0.0.1"), 6881);

        var entry = Bencode.SerializeCompactPeer(nodeId, ep);

        entry.Length.ShouldBe(Bencode.CompactPeerV4Size);
        entry.Length.ShouldBe(26, "IPv4 条目长度必须与旧实现完全一致 —— 这是不破坏老节点的前提");
        entry.Take(20).ShouldBe(nodeId);
        ((int)entry[20]).ShouldBe(10);
        ((int)entry[21]).ShouldBe(0);
        ((int)entry[22]).ShouldBe(0);
        ((int)entry[23]).ShouldBe(1);
        ((int)entry[24]).ShouldBe(0x1A);
        ((int)entry[25]).ShouldBe(0xE1);
    }

    [Fact]
    public void IPv4条目_经26字节解析器往返无损()
    {
        var nodeId = NodeIdBytes(0x33);
        var entry = Bencode.SerializeCompactPeer(nodeId, new IPEndPoint(IPAddress.Parse("192.168.1.7"), 20092));

        var parsed = Bencode.ParseCompactPeers26(entry);

        parsed.Count.ShouldBe(1);
        parsed[0].NodeId.ShouldBe(nodeId);
        parsed[0].Ip.ToString().ShouldBe("192.168.1.7");
        parsed[0].Port.ShouldBe(20092);
    }

    // ── ② IPv6 条目往返 ───────────────────────────────────────────────────

    [Fact]
    public void 序列化IPv6端点_产出38字节()
    {
        var entry = Bencode.SerializeCompactPeer(
            NodeIdBytes(0x44), new IPEndPoint(IPAddress.Parse("2001:db8::1"), 20091));

        entry.Length.ShouldBe(Bencode.CompactPeerV6Size);
        entry.Length.ShouldBe(38);
    }

    [Fact]
    public void IPv6条目_经38字节解析器往返无损()
    {
        var nodeId = NodeIdBytes(0x44);
        var address = IPAddress.Parse("2001:db8::dead:beef");
        const int port = 20091;

        var entry = Bencode.SerializeCompactPeer(nodeId, new IPEndPoint(address, port));
        var parsed = Bencode.ParseCompactPeers38(entry);

        parsed.Count.ShouldBe(1);
        parsed[0].NodeId.ShouldBe(nodeId);
        parsed[0].Ip.ToString().ShouldBe(address.ToString());
        parsed[0].Port.ShouldBe(port);
    }

    [Fact]
    public void IPv6回环地址_往返无损()
    {
        // 里程碑断言会用 [::1]，所以回环必须能被无歧义地编码与还原。
        var address = IPAddress.IPv6Loopback;
        var entry = Bencode.SerializeCompactPeer(NodeIdBytes(0x01), new IPEndPoint(address, 1234));

        var parsed = Bencode.ParseCompactPeers38(entry);

        parsed[0].Ip.ShouldBe(IPAddress.IPv6Loopback);
        parsed[0].Port.ShouldBe(1234);
    }

    [Fact]
    public void 两条IPv6条目拼接后_按38字节切分不串位()
    {
        // 串位是「误解析」的具体形态：宽度算错时，切出来的每一条都是垃圾地址。
        var a = Bencode.SerializeCompactPeer(
            NodeIdBytes(0x01), new IPEndPoint(IPAddress.Parse("2001:db8::1"), 1000));
        var b = Bencode.SerializeCompactPeer(
            NodeIdBytes(0x02), new IPEndPoint(IPAddress.Parse("2001:db8::2"), 2000));
        var flat = a.Concat(b).ToArray();

        var parsed = Bencode.ParseCompactPeers38(flat);

        parsed.Count.ShouldBe(2);
        parsed[0].Ip.ToString().ShouldBe("2001:db8::1");
        parsed[0].Port.ShouldBe(1000);
        parsed[1].Ip.ToString().ShouldBe("2001:db8::2");
        parsed[1].Port.ShouldBe(2000);
        ((int)parsed[0].NodeId[0]).ShouldBe(0x01);
        ((int)parsed[1].NodeId[0]).ShouldBe(0x02);
    }

    [Fact]
    public void 尾部不足38字节的残片被丢弃_不产生半条记录()
    {
        var a = Bencode.SerializeCompactPeer(
            NodeIdBytes(0x01), new IPEndPoint(IPAddress.Parse("2001:db8::1"), 1000));
        var flat = a.Concat(new byte[10]).ToArray();   // 48 字节：1 条完整 + 10 字节残片

        Bencode.ParseCompactPeers38(flat).Count.ShouldBe(1, "残片必须整条丢弃，不能解析出半个地址");
    }

    // ── ③ 核心缺陷：IPv6 绝不被静默截断 ───────────────────────────────────

    [Fact]
    public void IPv6地址_不再被截断成IPv4_而是产出38字节条目()
    {
        var nodeId = NodeIdBytes(0x55);
        var v6 = new IPEndPoint(IPAddress.Parse("2001:db8::1"), 1234);

        // ① 先钉住**旧行为确实产出垃圾地址** —— 不钉住它，本测试就无法证明修复有效。
        //    旧实现：固定 4 字节 BlockCopy ⇒ 2001:db8::1 的前 4 字节是 0x20 0x01 0x0d 0xb8
        //    ⇒ 得到 32.1.13.184 —— 一个语义上毫不相干的 IPv4 地址，**不抛异常、不记日志**。
        var oldStyle = new byte[26];
        Buffer.BlockCopy(v6.Address.GetAddressBytes(), 0, oldStyle, 20, 4);
        new IPAddress(oldStyle.Skip(20).Take(4).ToArray())
            .ToString().ShouldBe("32.1.13.184", "先钉住旧行为确实产出了垃圾地址");

        // ② 新实现：IPv6 走**自己的宽度**，不是被截断。
        //    这是与旧行为的关键区别 —— 截断的产物是 26 字节，正确产物是 38 字节。
        //    （不要试图用「地址前 4 字节」来区分：两种布局在偏移 20 处的头 4 字节
        //      本来就相同，2001:0db8::1 的开头正是 20 01 0d b8 —— 那条断言恒真，没有鉴别力。）
        var entry = Bencode.SerializeCompactPeer(nodeId, v6);
        entry.Length.ShouldBe(Bencode.CompactPeerV6Size,
            "IPv6 必须产出 38 字节；产出 26 字节就意味着又被截断了");
        entry.Length.ShouldNotBe(Bencode.CompactPeerV4Size);

        // ③ 端口在**条目末尾**（偏移 36），不是偏移 24 —— 布局随宽度一起平移，
        //    这正是「固定偏移写死」会出错的地方。
        ((int)entry[36]).ShouldBe((1234 >> 8) & 0xFF);
        ((int)entry[37]).ShouldBe(1234 & 0xFF);

        // ④ 16 字节地址原样往返。
        entry.Skip(20).Take(16).ShouldBe(v6.Address.GetAddressBytes());

        // ⑤ 解析回来必须是原地址。
        var parsed = Bencode.ParseCompactPeers38(entry);
        parsed[0].Ip.ToString().ShouldBe("2001:db8::1");
        parsed[0].Port.ShouldBe(1234);
    }

    [Fact]
    public void 节点ID长度不合法时抛异常_而不是写出错位条目()
    {
        // 同样是「宁可炸掉也不要静默产出错误字节」这条纪律。
        // 注意：**未知地址族那条分支在当前 .NET 上不可测** ——
        // IPAddress 只支持 InterNetwork / InterNetworkV6 两族，无法构造第三族来触发它。
        // 因此这里只锁住可测的那一半；不可测的一半**不假装被覆盖**。
        Should.Throw<ArgumentException>(
            () => Bencode.SerializeCompactPeer(new byte[19], new IPEndPoint(IPAddress.Loopback, 1)));
        Should.Throw<ArgumentException>(
            () => Bencode.SerializeCompactPeer(null!, new IPEndPoint(IPAddress.Loopback, 1)));
        Should.Throw<ArgumentException>(
            () => Bencode.SerializeCompactPeer(NodeIdBytes(1), null!));
    }

    // ── 标准 values：6 字节、不含 NodeId ───────────────────────────────────

    [Fact]
    public void 标准values条目_解析出IP与端口_且NodeId一律为null()
    {
        var flat = new byte[6 * 2];
        flat[0] = 10; flat[1] = 0; flat[2] = 0; flat[3] = 1;
        flat[4] = 0x1A; flat[5] = 0xE1;
        flat[6] = 10; flat[7] = 0; flat[8] = 0; flat[9] = 2;
        flat[10] = 0x1A; flat[11] = 0xE2;

        var parsed = Bencode.ParseCompactValues6(flat);

        parsed.Count.ShouldBe(2);
        parsed[0].Ip.ToString().ShouldBe("10.0.0.1");
        parsed[0].Port.ShouldBe(6881);
        parsed[1].Ip.ToString().ShouldBe("10.0.0.2");
        parsed[1].Port.ShouldBe(6882);
        // 标准 values 不含 NodeId —— 调用方必须自己补，解析器不得替它编一个。
        // （不用 ShouldAllBe：它把谓词编译成表达式树，容不下 is 模式。）
        (parsed.Count(p => p.NodeId is null) == parsed.Count).ShouldBeTrue(
            "标准 values 不含 NodeId —— 调用方必须自己补，解析器不得替它编一个");
    }

    [Fact]
    public void 标准values条目_不会被26字节解析器误切()
    {
        // 这条锁的是本轮顺带修掉的那个「形参在签名里、行为里没有」的缺陷：
        // 旧实现把 6 字节数据交给按 26 字节切分的解析器，切出来的是错位条目。
        var flat = new byte[6 * 4];   // 24 字节 = 4 个真实条目；按 26 切只够切出 0 条
        for (int i = 0; i < 4; i++)
        {
            flat[i * 6] = 10; flat[i * 6 + 1] = 0; flat[i * 6 + 2] = 0; flat[i * 6 + 3] = (byte)(i + 1);
            flat[i * 6 + 4] = 0x1A; flat[i * 6 + 5] = 0xE1;
        }

        Bencode.ParseCompactValues6(flat).Count.ShouldBe(4);
        Bencode.ParseCompactPeers26(flat).Count.ShouldBe(0,
            "按 26 字节切 24 字节数据只能切出 0 条 —— 这正说明两条路径必须各用自己的宽度");
    }

    [Fact]
    public void 空输入与短输入_返回空列表而不抛异常()
    {
        Bencode.ParseCompactPeers38([]).ShouldBeEmpty();
        Bencode.ParseCompactPeers38(new byte[20]).ShouldBeEmpty();
        Bencode.ParseCompactValues6([]).ShouldBeEmpty();
        Bencode.ParseCompactValues6(new byte[5]).ShouldBeEmpty();
    }
}
