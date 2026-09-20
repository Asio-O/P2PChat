using System.Text;
using P2PChat.Core.Models;
using P2PChat.Integration.Tests.Support;
using Shouldly;

namespace P2PChat.Integration.Tests;

/// <summary>
/// 两节点真实 TCP 连接 + 私聊端到端验证。
/// 使用真实 TcpTransport / MessageRouter / MessagePack 序列化 / ECDH 密钥交换 / handlers，
/// 只有 DHT 节点发现被预置（真实 DHT 发现需要公网）。
/// </summary>
public class TwoNodeChatIntegrationTests
{
    private static async Task<ChatMessageEvent> ReceiveOneAsync(
        IAsyncEnumerable<ChatMessageEvent> stream, int timeoutMs = 20000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        await foreach (var evt in stream.WithCancellation(cts.Token))
            return evt;
        throw new TimeoutException($"等待消息超时 ({timeoutMs}ms)");
    }

    [Fact]
    public async Task 两节点_真实TCP连接_可建立并标记为已连接()
    {
        await using var alice = NodeHarness.Start("alice");
        await using var bob = NodeHarness.Start("bob");

        var connection = await alice.Router.GetOrCreateConnectionAsync(bob.LocalNode);

        connection.IsConnected.ShouldBeTrue();
        connection.RemoteEndPoint.Port.ShouldBe(bob.Port);
    }

    [Fact]
    public async Task 两节点_加密私聊往返_接收端解密后内容一致()
    {
        await using var alice = NodeHarness.Start("alice");
        await using var bob = NodeHarness.Start("bob");
        alice.Discover(bob);
        bob.Discover(alice);

        const string text = "端到端加密私聊：你好 B！🚀 内容必须一致";
        await alice.Chat.SendPrivateMessageAsync(bob.LocalNode.NodeId, text);

        var received = await ReceiveOneAsync(bob.PrivateHandler.OnMessageReceived);

        received.Content.ShouldBe(text);
        received.IsOutgoing.ShouldBeFalse();
        received.IsGroup.ShouldBeFalse();
        received.SenderId.ToByteArray().ShouldBe(alice.SenderId);

        // 会话键必须方向无关：接收端用「对端（= 发送方）节点ID」必须能定位到同一个会话桶，
        // 否则消息虽到达却落进一个 UI 永远选不中的桶（历史缺陷：ConversationId 用的是收件人 ID，
        // 于是接收端拿到的是自己的 NodeId，UI 会话列表里永远看不到这条消息）。
        var expectedConversationId = ConversationId.ForPrivate(bob.LocalNode.NodeId, alice.LocalNode.NodeId);
        received.ConversationId.ShouldBe(expectedConversationId);
        received.ConversationId.ShouldBe(
            ConversationId.ForPrivate(alice.LocalNode.NodeId, bob.LocalNode.NodeId),
            "A 与 B 两侧必须算出同一个会话键");
    }

    [Fact]
    public async Task 两节点_双向私聊_B与A都能收到对方消息()
    {
        await using var alice = NodeHarness.Start("alice");
        await using var bob = NodeHarness.Start("bob");
        alice.Discover(bob);
        bob.Discover(alice);

        await alice.Chat.SendPrivateMessageAsync(bob.LocalNode.NodeId, "A->B 第一条");
        var atBob = await ReceiveOneAsync(bob.PrivateHandler.OnMessageReceived);
        atBob.Content.ShouldBe("A->B 第一条");

        await bob.Chat.SendPrivateMessageAsync(alice.LocalNode.NodeId, "B->A 回复");
        var atAlice = await ReceiveOneAsync(alice.PrivateHandler.OnMessageReceived);
        atAlice.Content.ShouldBe("B->A 回复");
    }

    [Fact]
    public async Task 两节点_连续多条私聊_按发送顺序到达且内容不串()
    {
        await using var alice = NodeHarness.Start("alice");
        await using var bob = NodeHarness.Start("bob");
        alice.Discover(bob);

        var expected = Enumerable.Range(1, 20).Select(i => $"消息 #{i} - payload {new string('x', i * 7)}").ToList();
        foreach (var text in expected)
            await alice.Chat.SendPrivateMessageAsync(bob.LocalNode.NodeId, text);

        var received = new List<string>();
        using var cts = new CancellationTokenSource(20000);
        await foreach (var evt in bob.PrivateHandler.OnMessageReceived.WithCancellation(cts.Token))
        {
            received.Add(evt.Content);
            if (received.Count == expected.Count) break;
        }

        received.ShouldBe(expected);
    }

    [Fact]
    public async Task 两节点_超过单次缓冲的大消息_内容完整不截断()
    {
        await using var alice = NodeHarness.Start("alice");
        await using var bob = NodeHarness.Start("bob");
        alice.Discover(bob);

        // 200KB 文本（远超 TCP 单次读取缓冲），验证长度前缀成帧的正确性
        var text = new string('A', 200_000);
        await alice.Chat.SendPrivateMessageAsync(bob.LocalNode.NodeId, text);

        var received = await ReceiveOneAsync(bob.PrivateHandler.OnMessageReceived);

        received.Content.Length.ShouldBe(200_000);
        received.Content.ShouldBe(text);
    }

    [Fact]
    public async Task 两节点_UTF8多语言内容_往返字节完全一致()
    {
        await using var alice = NodeHarness.Start("alice");
        await using var bob = NodeHarness.Start("bob");
        alice.Discover(bob);

        const string text = "中文 / English / 日本語 / 한국어 / العربية / русский / emoji 🎉🔒";
        await alice.Chat.SendPrivateMessageAsync(bob.LocalNode.NodeId, text);

        var received = await ReceiveOneAsync(bob.PrivateHandler.OnMessageReceived);

        received.Content.ShouldBe(text);
        Encoding.UTF8.GetBytes(received.Content).ShouldBe(Encoding.UTF8.GetBytes(text));
    }

    [Fact]
    public async Task 两节点_密钥交换后_接收端持有发送方会话密钥()
    {
        await using var alice = NodeHarness.Start("alice");
        await using var bob = NodeHarness.Start("bob");
        alice.Discover(bob);

        bob.KeyStore.GetSessionKey(new NodeId(alice.SenderId)).ShouldBeNull("交换前不应有会话密钥");

        await alice.Chat.SendPrivateMessageAsync(bob.LocalNode.NodeId, "触发密钥交换");
        await ReceiveOneAsync(bob.PrivateHandler.OnMessageReceived);

        var bobSideKey = bob.KeyStore.GetSessionKey(new NodeId(alice.SenderId));
        bobSideKey.ShouldNotBeNull("接收端处理 KeyExchange 后应持有会话密钥");
        bobSideKey!.Length.ShouldBe(32, "会话密钥应为 HKDF 派生的 256 位密钥");
    }

    [Fact]
    public async Task 两节点_连接被复用_多条消息只建立一条TCP连接()
    {
        await using var alice = NodeHarness.Start("alice");
        await using var bob = NodeHarness.Start("bob");
        alice.Discover(bob);

        var c1 = await alice.Router.GetOrCreateConnectionAsync(bob.LocalNode);
        var c2 = await alice.Router.GetOrCreateConnectionAsync(bob.LocalNode);

        c1.ConnectionId.ShouldBe(c2.ConnectionId, "同一对端应复用连接池中的连接");
    }

    [Fact]
    public async Task 两节点_目标节点不可发现时_发送私聊必须报错()
    {
        await using var alice = NodeHarness.Start("alice");

        var unknown = NodeId.CreateRandom();

        await Should.ThrowAsync<InvalidOperationException>(
            () => alice.Chat.SendPrivateMessageAsync(unknown, "发不出去"));
    }
}
