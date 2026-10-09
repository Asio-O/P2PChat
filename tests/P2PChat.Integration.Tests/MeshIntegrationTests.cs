using P2PChat.Core.Models;
using P2PChat.Integration.Tests.Support;
using Shouldly;

namespace P2PChat.Integration.Tests;

/// <summary>
/// mesh 泛洪的三节点端到端验证（2026-10-09-mesh-topology-and-flooding）。
/// <para>
/// 两节点用例由既有 <see cref="GroupChatIntegrationTests"/> / <see cref="TwoNodeChatIntegrationTests"/>
/// 覆盖（泛洪在单邻居时与旧点对点路径等价）；这里锁的是<b>多邻居</b>下的差异：
/// 私聊泛洪只送达收件人（路过节点不产出事件）、群消息泛洪让全部成员都收到。
/// </para>
/// </summary>
public class MeshIntegrationTests
{
    private static async Task<ChatMessageEvent> ReceiveOneAsync(
        IAsyncEnumerable<ChatMessageEvent> stream, int timeoutMs = 20000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        await foreach (var evt in stream.WithCancellation(cts.Token))
            return evt;
        throw new TimeoutException($"等待消息超时 ({timeoutMs}ms)");
    }

    private static async Task<bool> SawAnyEventWithinAsync(
        IAsyncEnumerable<ChatMessageEvent> stream, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await foreach (var _ in stream.WithCancellation(cts.Token))
                return true;
        }
        catch (OperationCanceledException) { }
        return false;
    }

    [Fact]
    public async Task 三节点_私聊泛洪_只送达收件人_路过节点无事件()
    {
        await using var alice = NodeHarness.Start("alice");
        await using var bob = NodeHarness.Start("bob");
        await using var carol = NodeHarness.Start("carol");
        alice.Discover(bob);
        alice.Discover(carol);
        bob.Discover(alice);
        bob.Discover(carol);
        carol.Discover(alice);
        carol.Discover(bob);

        // 模拟全连接 mesh：alice 与 bob、carol 都有出站连接（真实环境由维护循环预建）。
        await alice.Router.GetOrCreateConnectionAsync(bob.LocalNode);
        await alice.Router.GetOrCreateConnectionAsync(carol.LocalNode);

        await alice.Chat.SendPrivateMessageAsync(bob.LocalNode.NodeId, "只给 bob 的私聊");

        var atBob = await ReceiveOneAsync(bob.IncomingMessages);
        atBob.Content.ShouldBe("只给 bob 的私聊");
        atBob.ConversationId.ShouldBe(ConversationId.ForPrivate(alice.LocalNode.NodeId, bob.LocalNode.NodeId));

        var carolSaw = await SawAnyEventWithinAsync(carol.IncomingMessages, 1500);
        carolSaw.ShouldBeFalse("私聊泛洪到 carol 后，carol 无 alice 的会话密钥，解密失败必须丢弃、不得上报事件");
    }

    [Fact]
    public async Task 三节点_群消息泛洪_全部成员均收到()
    {
        await using var alice = NodeHarness.Start("alice");
        await using var bob = NodeHarness.Start("bob");
        await using var carol = NodeHarness.Start("carol");
        alice.Discover(bob);
        alice.Discover(carol);
        bob.Discover(alice);
        bob.Discover(carol);
        carol.Discover(alice);
        carol.Discover(bob);

        var group = await alice.Group.CreateGroupAsync("三人群", [bob.LocalNode.NodeId, carol.LocalNode.NodeId]);

        // 邀请链路真实走点对点发送；等价状态是两侧都拿到群密钥。
        await Wait.UntilAsync(() => bob.KeyStore.GetGroupKey(group.GroupId) is not null, 10000);
        await Wait.UntilAsync(() => carol.KeyStore.GetGroupKey(group.GroupId) is not null, 10000);

        const string text = "泛洪到全员的群消息";
        await alice.Group.SendGroupMessageAsync(group.GroupId, text);

        var atBob = await ReceiveOneAsync(bob.IncomingMessages);
        atBob.Content.ShouldBe(text);
        atBob.IsGroup.ShouldBeTrue();

        var atCarol = await ReceiveOneAsync(carol.IncomingMessages);
        atCarol.Content.ShouldBe(text);
        atCarol.IsGroup.ShouldBeTrue();
    }
}
