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
}
