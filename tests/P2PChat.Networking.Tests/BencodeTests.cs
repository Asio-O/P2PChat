using P2PChat.Networking.Dht;
using Shouldly;
using System.Text;

namespace P2PChat.Networking.Tests;

public class BencodeTests
{
    [Fact]
    public void EncodeDecode_String_RoundTrip()
    {
        var encoded = Bencode.Encode("hello");
        var decoded = Bencode.Decode(encoded);
        var str = Encoding.UTF8.GetString((byte[])decoded);
        str.ShouldBe("hello");
    }

    [Fact]
    public void EncodeDecode_Integer_RoundTrip()
    {
        var encoded = Bencode.Encode(42);
        var decoded = Bencode.Decode(encoded);
        decoded.ShouldBe(42L);
    }

    [Fact]
    public void EncodeDecode_List_RoundTrip()
    {
        var list = new List<object> { "spam", 42L };
        var encoded = Bencode.Encode(list.Select(x => (object)x).ToList());
        var decoded = (List<object>)Bencode.Decode(encoded);
        decoded.Count.ShouldBe(2);
        Encoding.UTF8.GetString((byte[])decoded[0]).ShouldBe("spam");
        decoded[1].ShouldBe(42L);
    }

    [Fact]
    public void EncodeDecode_Dict_RoundTrip()
    {
        var dict = new Dictionary<string, object>
        {
            ["key"] = "value",
            ["num"] = 42L
        };
        var encoded = Bencode.Encode(dict);
        var decoded = (Dictionary<string, object>)Bencode.Decode(encoded);
        Encoding.UTF8.GetString((byte[])decoded["key"]).ShouldBe("value");
        decoded["num"].ShouldBe(42L);
    }

    [Fact]
    public void Encode_Dict_SortsKeys()
    {
        var dict = new Dictionary<string, object>
        {
            ["z"] = "last",
            ["a"] = "first"
        };
        var encoded = Bencode.Encode(dict);
        var str = Encoding.UTF8.GetString(encoded);
        // 'a' 应该在 'z' 之前
        str.IndexOf("a").ShouldBeLessThan(str.IndexOf("z"));
    }

    [Fact]
    public void ParseCompactNodes_Valid()
    {
        var data = new byte[26 * 2];
        // 节点1: NodeId全0, IP=1.2.3.4, Port=9001
        data[20] = 1; data[21] = 2; data[22] = 3; data[23] = 4;
        data[24] = 0x23; data[25] = 0x29; // 9001
        // 节点2: NodeId全0, IP=5.6.7.8, Port=9002
        data[46] = 5; data[47] = 6; data[48] = 7; data[49] = 8;
        data[50] = 0x23; data[51] = 0x2A; // 9002

        var nodes = Bencode.ParseCompactNodes(data);
        nodes.Count.ShouldBe(2);
        nodes[0].Ip.ToString().ShouldBe("1.2.3.4");
        nodes[0].Port.ShouldBe(9001);
        nodes[1].Ip.ToString().ShouldBe("5.6.7.8");
        nodes[1].Port.ShouldBe(9002);
    }

    [Fact]
    public void ParseCompactPeers26_RoundTrip()
    {
        // 构造两条目：NodeId 全 0x10 字节 + IP=10.0.0.1 / Port=6881 与 IP=10.0.0.2 / Port=6882。
        // 用 NodeId.FromPublicKey 输入合法 P-256 公钥更稳妥——但本测试只关心 26 字节布局，
        // 直接按 26 字节组填充字节数组即可。
        var data = new byte[26 * 2];
        // NodeId 1 = 全 0x10
        for (int i = 0; i < 20; i++) data[i] = 0x10;
        data[20] = 10; data[21] = 0; data[22] = 0; data[23] = 1;
        data[24] = 0x1A; data[25] = 0xE1; // 6881
        // NodeId 2 = 全 0x20
        for (int i = 26; i < 46; i++) data[i] = 0x20;
        data[46] = 10; data[47] = 0; data[48] = 0; data[49] = 2;
        data[50] = 0x1A; data[51] = 0xE2; // 6882

        var peers = Bencode.ParseCompactPeers26(data);

        peers.Count.ShouldBe(2);
        peers[0].Ip.ToString().ShouldBe("10.0.0.1");
        peers[0].Port.ShouldBe(6881);
        peers[1].Ip.ToString().ShouldBe("10.0.0.2");
        peers[1].Port.ShouldBe(6882);
        // NodeId 必须从字节数组原样取出
        peers[0].NodeId.Length.ShouldBe(20);
        peers[0].NodeId[0].ShouldBe((byte)0x10);
        peers[1].NodeId[19].ShouldBe((byte)0x20);
    }

    [Fact]
    public void ParseCompactPeers26_尾部不足26字节被截断丢弃()
    {
        // 28 字节：1 个完整条目 + 2 字节尾巴
        var data = new byte[28];
        for (int i = 0; i < 20; i++) data[i] = 0x33;
        data[20] = 192; data[21] = 168; data[22] = 1; data[23] = 1;
        data[24] = 0x1A; data[25] = 0xE2;
        data[26] = 0xFF; data[27] = 0xFF; // 不完整尾部

        var peers = Bencode.ParseCompactPeers26(data);

        peers.Count.ShouldBe(1, "不足 26 字节的尾部必须被丢弃，不能误解析为第 2 条目");
        peers[0].Port.ShouldBe(6882);
    }

    [Fact]
    public void ParseCompactPeers26_空输入返回空列表()
    {
        Bencode.ParseCompactPeers26([]).ShouldBeEmpty();
        Bencode.ParseCompactPeers26(new byte[10]).ShouldBeEmpty();
    }
}
