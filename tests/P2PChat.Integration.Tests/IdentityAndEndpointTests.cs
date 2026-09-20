using System.Net;
using P2PChat.Core.Enums;
using P2PChat.Core.Models;
using P2PChat.Integration.Tests.Support;
using Shouldly;

namespace P2PChat.Integration.Tests;

/// <summary>
/// 身份标识与显式端点的回归守卫。
/// <para>
/// 这里每一条断言都对应一个曾让软件在两台设备上**完全不可用**、而既有测试**结构性测不出来**的缺陷。
/// 改动 <c>SenderId</c> 派生方式、会话键构造方式或端点解析优先级时，本文件必须继续为绿。
/// </para>
/// </summary>
public class IdentityAndEndpointTests
{
    private static async Task<ChatMessageEvent> ReceiveOneAsync(
        IAsyncEnumerable<ChatMessageEvent> stream, int timeoutMs = 20000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        await foreach (var evt in stream.WithCancellation(cts.Token))
            return evt;
        throw new TimeoutException($"等待消息超时 ({timeoutMs}ms)");
    }

    private static NodeInfo StaticPeer(NodeInfo peer) => new()
    {
        NodeId = peer.NodeId,
        EndPoint = peer.EndPoint,
        PublicKey = [],
        State = PeerState.Online
    };

    [Fact]
    public async Task 身份_两个节点的SenderId必须不同且等于各自节点ID()
    {
        await using var alice = NodeHarness.Start("alice");
        await using var bob = NodeHarness.Start("bob");

        alice.SenderId.ShouldBe(alice.LocalNode.NodeId.ToByteArray());
        bob.SenderId.ShouldBe(bob.LocalNode.NodeId.ToByteArray());

        alice.SenderId.ShouldNotBe(bob.SenderId,
            "两个节点必须有各自不同的身份标识");
    }

    [Fact]
    public async Task 身份_SenderId不得取公钥前20字节()
    {
        await using var alice = NodeHarness.Start("alice");
        await using var bob = NodeHarness.Start("bob");

        // 直接钉死历史缺陷的机制：公钥是 P-256 SubjectPublicKeyInfo (DER)，
        // 前 27 字节是与密钥内容无关的固定算法头，所以「前 20 字节」对每个节点都是同一串字节。
        var alicePrefix = alice.Identity.PublicKey.Take(NodeId.Size).ToArray();
        var bobPrefix = bob.Identity.PublicKey.Take(NodeId.Size).ToArray();

        alicePrefix.ShouldBe(bobPrefix,
            "前提断言：不同节点的公钥前 20 字节确实相同（这正是该写法致命的证据）");

        alice.SenderId.ShouldNotBe(alicePrefix,
            "SenderId 绝不能是公钥前 20 字节 —— 那会让所有节点的身份标识退化成同一个常量");
    }

    [Fact]
    public async Task 静态对端_登记后无需DHT发现即可被FindNodeAsync命中()
    {
        await using var alice = NodeHarness.Start("alice");
        await using var bob = NodeHarness.Start("bob");

        // 不调用 Discover —— 模拟真实环境：公共 DHT 上无人为我们宣告对端，迭代 find_node 命中不了。
        (await alice.Dht.FindNodeAsync(bob.LocalNode.NodeId)).ShouldBeNull(
            "未登记静态对端时，不应凭空发现对端");

        alice.Dht.RegisterStaticPeer(StaticPeer(bob.LocalNode));

        var resolved = await alice.Dht.FindNodeAsync(bob.LocalNode.NodeId);
        resolved.ShouldNotBeNull("静态对端必须能被 FindNodeAsync 直接命中");
        resolved!.EndPoint.ShouldBe(bob.LocalNode.EndPoint);
    }

    [Fact]
    public async Task 静态对端_仅凭显式端点即可完成双向加密私聊()
    {
        await using var alice = NodeHarness.Start("alice");
        await using var bob = NodeHarness.Start("bob");

        // 全程不调用 Discover：完全模拟「两台设备之间没有任何发现机制」的真实环境。
        alice.Dht.RegisterStaticPeer(StaticPeer(bob.LocalNode));
        bob.Dht.RegisterStaticPeer(StaticPeer(alice.LocalNode));

        await alice.Chat.SendPrivateMessageAsync(bob.LocalNode.NodeId, "A->B 仅凭显式端点");
        var atBob = await ReceiveOneAsync(bob.PrivateHandler.OnMessageReceived);

        atBob.Content.ShouldBe("A->B 仅凭显式端点");
        atBob.SenderId.ToByteArray().ShouldBe(alice.SenderId,
            "接收端必须看到发送方的真实身份，否则联系人别名解析不到");

        // 会话键必须方向无关 —— 否则消息落进 UI 永远选不中的桶
        atBob.ConversationId.ShouldBe(
            ConversationId.ForPrivate(alice.LocalNode.NodeId, bob.LocalNode.NodeId));

        await bob.Chat.SendPrivateMessageAsync(alice.LocalNode.NodeId, "B->A 回复");
        var atAlice = await ReceiveOneAsync(alice.PrivateHandler.OnMessageReceived);

        atAlice.Content.ShouldBe("B->A 回复");
        atAlice.ConversationId.ShouldBe(atBob.ConversationId,
            "同一会话在两个方向上必须得到同一个会话键");
    }

    [Fact]
    public async Task 会话键_对调参数顺序结果必须相同()
    {
        await using var alice = NodeHarness.Start("alice");
        await using var bob = NodeHarness.Start("bob");

        ConversationId.ForPrivate(alice.LocalNode.NodeId, bob.LocalNode.NodeId)
            .ShouldBe(ConversationId.ForPrivate(bob.LocalNode.NodeId, alice.LocalNode.NodeId));
    }

    [Fact]
    public async Task 会话密钥_双方必须登记在对方真实节点ID之下()
    {
        await using var alice = NodeHarness.Start("alice");
        await using var bob = NodeHarness.Start("bob");
        alice.Discover(bob);
        bob.Discover(alice);

        await alice.Chat.SendPrivateMessageAsync(bob.LocalNode.NodeId, "触发密钥交换");
        await ReceiveOneAsync(bob.PrivateHandler.OnMessageReceived);

        // 发送方把会话密钥登记在「对端节点ID」下
        alice.KeyStore.GetSessionKey(bob.LocalNode.NodeId).ShouldNotBeNull(
            "发送方必须把会话密钥登记在对端的真实节点ID之下");

        // 接收方从 KeyExchange 的 SenderId 推导出对端身份，也必须落在同一个真实节点ID之下。
        // 历史缺陷：SenderId 是常量，于是所有对端共用同一个密钥槽位，第三个节点接入即互相覆盖。
        bob.KeyStore.GetSessionKey(alice.LocalNode.NodeId).ShouldNotBeNull(
            "接收方必须把会话密钥登记在发送方的真实节点ID之下");
    }
}
