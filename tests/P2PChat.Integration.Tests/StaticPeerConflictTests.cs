using System.Net;
using P2PChat.Core.Models;
using P2PChat.Integration.Tests.Support;
using Shouldly;

namespace P2PChat.Integration.Tests;

/// <summary>
/// 端到端回归 —— 「**手工登记的对端不会被自报端点顶替**」覆盖整条真实链路。
/// <para>
/// 与 <c>Networking.Tests/StaticPeerRegistrationTests</c> 的分工：那边在真实
/// <c>MainlineDhtService</c> 上验证「登记契约」本身；这里验证**真实链路上确实会走到那个冲突**——
/// 真实 TCP + 真实签名 + 真实密钥交换，触发 B 的 <c>KeyExchangeHandler</c> 把 A 的
/// <b>自报监听端点</b> 反向登记进 B 的静态对端表，而那条登记正好撞上 B 早先的**手工条目**。
/// </para>
/// <para>
/// 没有这条，端到端世界就缺一环：基础用例只说「产品会拒绝冲突」，
/// 这里要证明「现实中真的会产生这种冲突，且拒绝后手工条目仍在」。
/// </para>
/// </summary>
public class StaticPeerConflictTests
{
    /// <summary>
    /// 回归守卫 E1 — B 手工登记过 A（且端点已过期）后，A 发起会话触发自报端点反向登记，
    /// B 的手工条目必须**原封不动**。
    /// <para>
    /// 为什么这个场景真实存在：手工登记是用户带外输入，可能是**过期**的（对方换了端口、
    /// 用户当初手输错了）。自报端点则是对端在签名载荷里的自述 —— 签名只能证明「这话是它说的」，
    /// 无法判定它有没有撒谎。若让自报端点无条件覆盖，用户手工登记的地址会被静默顶掉，
    /// 而且静态对端表优先于 DHT 解析（<c>FindNodeAsync</c> 第一步就查它），
    /// 一条错误条目会**永久遮蔽**该对端的发现能力。
    /// </para>
    /// </summary>
    [Fact]
    public async Task 手工登记的对端_被自报端点覆盖时以手工为准_端到端()
    {
        await using var alice = NodeHarness.Start("alice");
        await using var bob = NodeHarness.Start("bob");

        // B 此前手工登记过 A，但记的是一个**过期端点**（用户当初手输的端口，对方早已不用）。
        // 端口 1 在本机几乎必然连不上，正好代表「过期且不可用」。
        var stale = new IPEndPoint(IPAddress.Loopback, 1);
        bob.Dht.RegisterStaticPeer(new NodeInfo
        {
            NodeId = alice.LocalNode.NodeId,
            EndPoint = stale,
            PublicKey = null
        });

        // A 侧登记 B，随后发起真实会话 —— 没有会话键 ⇒ 触发真实密钥交换 ⇒
        // B 的 KeyExchangeHandler 收到 hello，把 A 的自报监听端点反向登记进 B 的 dht。
        alice.Dht.RegisterStaticPeer(new NodeInfo
        {
            NodeId = bob.LocalNode.NodeId,
            EndPoint = bob.LocalNode.EndPoint,
            PublicKey = null
        });
        await alice.Chat.SendPrivateMessageAsync(bob.LocalNode.NodeId, "触发密钥交换与反向登记");

        // 先证明链路真的通了 —— 否则下面的断言可能只是「冲突压根没发生」的恒真。
        var atBob = await ReceiveOneAsync(bob.IncomingMessages);
        atBob.Content.ShouldBe("触发密钥交换与反向登记",
            "必须先证明真实通信发生过，否则「手工条目未变」可能只是因为反向登记从未触发");

        var resolved = await bob.Dht.FindNodeAsync(alice.LocalNode.NodeId);
        resolved.ShouldNotBeNull();
        resolved!.EndPoint.ShouldBe(stale,
            "自报端点不得顶替手工登记的条目 —— 手工是用户带外输入，自报端点只是对端自述");
    }

    private static async Task<ChatMessageEvent> ReceiveOneAsync(
        IAsyncEnumerable<ChatMessageEvent> stream, int timeoutMs = 20000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        await foreach (var evt in stream.WithCancellation(cts.Token))
            return evt;
        throw new TimeoutException($"等待消息超时 ({timeoutMs}ms)");
    }
}
