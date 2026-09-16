using P2PChat.Core.Models;
using Shouldly;

namespace P2PChat.Core.Tests;

public class NodeIdTests
{
    [Fact]
    public void Create_ValidBytes_CreatesNodeId()
    {
        var bytes = new byte[20];
        Random.Shared.NextBytes(bytes);
        var nodeId = new NodeId(bytes);
        nodeId.ToByteArray().ShouldBe(bytes);
    }

    [Fact]
    public void Create_InvalidSize_Throws()
    {
        Should.Throw<ArgumentException>(() => new NodeId(new byte[10]));
    }

    [Fact]
    public void XorDistance_Symmetric()
    {
        var a = NodeId.CreateRandom();
        var b = NodeId.CreateRandom();
        var dist1 = a.XorDistanceTo(b);
        var dist2 = b.XorDistanceTo(a);
        dist1.ShouldBe(dist2);
    }

    [Fact]
    public void XorDistance_Self_IsZero()
    {
        var a = NodeId.CreateRandom();
        var dist = a.XorDistanceTo(a);
        foreach (var b in dist)
            b.ShouldBe((byte)0);
    }

    [Fact]
    public void FromPublicKey_Produces160Bit()
    {
        var pubKey = new byte[32];
        Random.Shared.NextBytes(pubKey);
        var nodeId = NodeId.FromPublicKey(pubKey);
        nodeId.ToByteArray().Length.ShouldBe(20);
    }

    [Fact]
    public void CommonPrefixLength_WithSelf_Returns160()
    {
        var a = NodeId.CreateRandom();
        a.CommonPrefixLength(a).ShouldBe(160);
    }
}
