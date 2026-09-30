using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using P2PChat.Core.Enums;
using P2PChat.Core.Models;
using P2PChat.Networking.Dht;
using P2PChat.Networking.Transport;
using Shouldly;

namespace P2PChat.Networking.Tests;

/// <summary>
/// 回归守卫 —— <c>MainlineDhtService.RegisterStaticPeer</c> 的「**冲突时保留已登记端点**」契约。
/// <para>
/// 历史缺陷：该方法原本是 <c>_staticPeers[key] = node</c> **无条件覆盖**，而「不得覆盖已存在的条目」
/// 这条约束只写在 <c>KeyExchangeHandler</c> 的 XML 文档里、且注明「一条都不能省」——
/// 即<b>注释声称的保证在当前路径上并不成立</b>。
/// </para>
/// <para>
/// 为什么旧值更可信：静态对端表有两种来源，可信度不对等。
/// <list type="bullet">
///   <item><b>手工登记</b>（contacts.json、/add）—— 用户带外的主动输入，攻击者拿不到。</item>
///   <item><b>自报端点</b>（KeyExchangeHandler 反向登记）—— 对端在签名覆盖的载荷里<b>自述</b>；
///         签名只能证明「这话是它说的」，无法判定它有没有撒谎。</item>
/// </list>
/// </para>
/// <para>
/// 这些用例在<b>真实</b> <see cref="MainlineDhtService"/> 上跑（不是替身）——该类只在本测试项目里被构造，
/// 集成测试用的 <c>FakeDhtService</c> 是另一个实现。替身已同步为同一契约，否则集成测试跑的是另一个世界。
/// </para>
/// </summary>
public class StaticPeerRegistrationTests
{
    /// <summary>
    /// 这些用例只走 <c>FindNodeAsync</c> 的「静态对端优先」短路分支（同步返回），
    /// 不会真的发网络报文；这里的令牌只是兜底，防止将来实现变化时无限挂住测试。
    /// </summary>
    private static CancellationToken Ct => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    // ------------------------------------------------------------ 1. 冲突保留 + LogWarning

    /// <summary>
    /// 回归守卫 S1 — 同一 NodeId、端点不同：<b>后调不覆盖前调</b>，并记一条 LogWarning。
    /// <para>
    /// 断言消息里<b>同时包含 Existing 与 New 两个端点</b>：这是产品事实，不是措辞。
    /// 下一个人若想改日志措辞让这条变绿，等于删掉「差异可查」这个能力。
    /// </para>
    /// </summary>
    [Fact]
    public async Task 同一NodeId端点不同_后调保留前调_且记LogWarning()
    {
        using var fixture = DhtFixture.Create();
        var peerId = NodeId.CreateRandom();
        var manual = new IPEndPoint(IPAddress.Loopback, 1111);      // 手工登记
        var selfReported = new IPEndPoint(IPAddress.Loopback, 2222); // 自报端点

        fixture.Dht.RegisterStaticPeer(Node(peerId, manual));
        fixture.Dht.RegisterStaticPeer(Node(peerId, selfReported));

        var resolved = await fixture.Dht.FindNodeAsync(peerId, Ct);
        resolved.ShouldNotBeNull();
        resolved!.EndPoint.ShouldBe(manual,
            "后调端点不得覆盖先登记的端点 —— 手工登记是用户带外输入，自报端点只是对端自述");

        var conflict = fixture.Log.Warnings
            .ShouldHaveSingleItem("端点冲突必须留下唯一的 LogWarning，不能静默丢弃");
        conflict.Contains(manual.ToString()).ShouldBeTrue(
            "告警必须给出被保留的端点（Existing），否则用户不知道自己登记的地址被谁顶掉了");
        conflict.Contains(selfReported.ToString()).ShouldBeTrue(
            "告警必须给出被拒绝的端点（New），否则无法排查是谁在顶替");
    }

    // ------------------------------------------------------------ 2. 同端点幂等

    /// <summary>
    /// 回归守卫 S2 — 同一 NodeId、端点相同：后调<b>不产生冲突</b>、不新建条目（幂等）。
    /// <para>
    /// 守卫只拦「端点不一致」这一种情况。端点一致时后调仍应正常生效（刷新 PublicKey/LastSeen），
    /// 因此<b>不应</b>产生任何冲突告警 —— 否则正常心跳会被刷成一片告警，告警也就失去意义。
    /// </para>
    /// </summary>
    [Fact]
    public async Task 同一NodeId端点相同_后调幂等不创建新条目()
    {
        using var fixture = DhtFixture.Create();
        var peerId = NodeId.CreateRandom();
        var endPoint = new IPEndPoint(IPAddress.Loopback, 3333);

        fixture.Dht.RegisterStaticPeer(Node(peerId, endPoint));
        fixture.Dht.RegisterStaticPeer(Node(peerId, endPoint));

        var resolved = await fixture.Dht.FindNodeAsync(peerId, Ct);
        resolved.ShouldNotBeNull();
        resolved!.EndPoint.ShouldBe(endPoint);
        fixture.Log.Warnings.ShouldBeEmpty(
            "端点一致不构成冲突，重复登记必须安静通过（幂等），否则正常心跳会刷满告警");
    }

    // ------------------------------------------------------------ 3. 不同 NodeId 互不影响

    /// <summary>
    /// 回归守卫 S3 — 不同 NodeId 互不影响：冲突判定只对<b>同一 NodeId</b> 生效。
    /// <para>
    /// 若实现误写成「只要表非空就拒绝」，这条立刻变红 —— 那会让第二个对端根本登记不上。
    /// </para>
    /// </summary>
    [Fact]
    public async Task 不同NodeId互不影响()
    {
        using var fixture = DhtFixture.Create();
        var idA = NodeId.CreateRandom();
        var idB = NodeId.CreateRandom();
        var epA = new IPEndPoint(IPAddress.Loopback, 4444);
        var epB = new IPEndPoint(IPAddress.Loopback, 5555);

        fixture.Dht.RegisterStaticPeer(Node(idA, epA));
        fixture.Dht.RegisterStaticPeer(Node(idB, epB));

        (await fixture.Dht.FindNodeAsync(idA, Ct))!.EndPoint.ShouldBe(epA);
        (await fixture.Dht.FindNodeAsync(idB, Ct))!.EndPoint.ShouldBe(epB);
        fixture.Log.Warnings.ShouldBeEmpty("不同 NodeId 之间不存在冲突，不应产生告警");
    }

    // ------------------------------------------------------------ 4. 重复自报不污染手工登记

    /// <summary>
    /// 回归守卫 S4 — 重复自报端点<b>不污染</b>手工登记条目，且<b>每一次</b>冲突都各记一条告警。
    /// <para>
    /// 与 S1 的差别在于「重复」：验证冲突不是只拦第一次，而是<b>每次</b>都比对、每次都留痕。
    /// 一个「只报首次、之后默默覆盖」的实现，S1 会绿而本条会红。
    /// </para>
    /// </summary>
    [Fact]
    public async Task 重复自报端点不污染手工登记条目()
    {
        using var fixture = DhtFixture.Create();
        var peerId = NodeId.CreateRandom();
        var manual = new IPEndPoint(IPAddress.Loopback, 6666);

        fixture.Dht.RegisterStaticPeer(Node(peerId, manual));
        fixture.Dht.RegisterStaticPeer(Node(peerId, new IPEndPoint(IPAddress.Loopback, 7777)));
        fixture.Dht.RegisterStaticPeer(Node(peerId, new IPEndPoint(IPAddress.Loopback, 8888)));

        var resolved = await fixture.Dht.FindNodeAsync(peerId, Ct);
        resolved!.EndPoint.ShouldBe(manual, "多次自报端点都不得顶掉手工登记的条目");

        fixture.Log.Warnings.Count.ShouldBe(2,
            "两次冲突必须各留一条痕 —— 只报首次会让后续顶替完全不可见");
    }

    // ------------------------------------------------------------------ 辅助

    private static NodeInfo Node(NodeId id, IPEndPoint endPoint) => new()
    {
        NodeId = id,
        EndPoint = endPoint,
        PublicKey = null,
        State = PeerState.Online
    };

    /// <summary>
    /// 真实 <see cref="MainlineDhtService"/> 的最小装置。
    /// <para>
    /// 绑 UDP 端口 0（由系统分配）以避免固定端口冲突；这些用例只走
    /// <c>FindNodeAsync</c> 的「静态对端优先」短路分支，<b>不发任何网络报文</b>。
    /// </para>
    /// </summary>
    private sealed class DhtFixture : IDisposable
    {
        private readonly UdpTransport _udp;

        private DhtFixture(MainlineDhtService dht, CapturingLogger<MainlineDhtService> log, UdpTransport udp)
        {
            Dht = dht; Log = log; _udp = udp;
        }

        public MainlineDhtService Dht { get; }
        public CapturingLogger<MainlineDhtService> Log { get; }

        public static DhtFixture Create()
        {
            var log = new CapturingLogger<MainlineDhtService>();
            var udp = new UdpTransport(0, NullLogger<UdpTransport>.Instance);
            var nodeId = NodeId.CreateRandom();
            var localNode = new NodeInfo
            {
                NodeId = nodeId,
                EndPoint = new IPEndPoint(IPAddress.Loopback, udp.LocalEndPoint.Port),
                PublicKey = new byte[91],
                State = PeerState.Online
            };
            var dht = new MainlineDhtService(
                localNode, new RoutingTable(nodeId, 20), udp, log,
                bootstrapNodes: Array.Empty<IPEndPoint>(),
                alpha: 3);
            return new DhtFixture(dht, log, udp);
        }

        public void Dispose() => _udp.Dispose();
    }

    /// <summary>记录已渲染日志文本的测试用 logger（渲染后消息里含结构化占位符的实际值）。</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<string> Warnings
        {
            get { lock (_entries) { return _entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList(); } }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            lock (_entries) _entries.Add((logLevel, message));
        }
    }
}
