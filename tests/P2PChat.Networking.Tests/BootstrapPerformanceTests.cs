using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using P2PChat.Core.Abstractions;
using P2PChat.Core.Enums;
using P2PChat.Core.Models;
using P2PChat.Networking.Dht;
using P2PChat.Networking.Transport;
using Shouldly;
using Xunit.Abstractions;

namespace P2PChat.Networking.Tests;

/// <summary>
/// 引导耗时回归测试 —— 锁死 REPAIR-PLAN 阶段 1.5 的两条语义边界：
/// <list type="number">
///   <item>
///     **不得退回「首个成功即 break」**：所有可达的引导节点都必须被 PING 且进入路由表，
///     否则公共 DHT 可达时无法真正加入（阶段 1 要修的正是这个问题）。
///   </item>
///   <item>
///     **引导总耗时不随引导节点个数线性累加**：全部引导节点并发探测 + 引导阶段短超时，
///     「N 个不可达公网节点」的代价应是 ~1 个超时，而不是 N 个超时。
///   </item>
/// </list>
/// 两条红线同时由 <see cref="Bootstrap_多个可达引导节点_全部进入路由表"/> 覆盖：
/// 它既断言路由表节点数（不 break），也断言总耗时上界（并发）。
/// </summary>
public class BootstrapPerformanceTests
{
    private readonly ITestOutputHelper _output;

    public BootstrapPerformanceTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// 构造一个真实 MainlineDhtService（UDP 端口 0 = 系统分配），返回实例与传输层。
    /// </summary>
    private static (MainlineDhtService Dht, UdpTransport Udp, CancellationTokenSource Cts) CreateDht(
        IReadOnlyList<IPEndPoint> bootstrapNodes)
    {
        var udp = new UdpTransport(0, NullLogger<UdpTransport>.Instance);
        var nodeId = NodeId.CreateRandom();
        var localNode = new NodeInfo
        {
            NodeId = nodeId,
            EndPoint = new IPEndPoint(IPAddress.Loopback, udp.LocalEndPoint.Port),
            PublicKey = new byte[91],
            State = PeerState.Online
        };
        var routingTable = new RoutingTable(nodeId, 20);
        var dht = new MainlineDhtService(
            localNode, routingTable, udp,
            NullLogger<MainlineDhtService>.Instance,
            bootstrapNodes: bootstrapNodes,
            alpha: 3);
        return (dht, udp, new CancellationTokenSource());
    }

    private static Task RunReceiveAsync(MainlineDhtService dht, CancellationToken ct) =>
        Task.Run(async () =>
        {
            try { await dht.StartReceivingAsync(ct); }
            catch (OperationCanceledException) { /* 正常关闭 */ }
        });

    /// <summary>
    /// 向系统申请 <paramref name="count"/> 个当前空闲的 UDP 端口并立即释放 ——
    /// 得到的是「几乎肯定无人监听」的端口，用作不可达引导节点（比写死端口更不易撞占用）。
    /// </summary>
    private static int[] ReserveClosedUdpPorts(int count)
    {
        var holders = new List<UdpClient>(count);
        var ports = new int[count];
        try
        {
            for (int i = 0; i < count; i++)
            {
                var s = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                holders.Add(s);
                ports[i] = ((IPEndPoint)s.Client.LocalEndPoint!).Port;
            }
        }
        finally
        {
            foreach (var s in holders) s.Dispose();
        }
        return ports;
    }

    /// <summary>
    /// 全部引导节点不可达时，引导总耗时必须接近「单个节点的超时」而不是 N 倍之和。
    /// <para>
    /// 旧实现（串行 PING + KRPC 通用 10s 超时）在 4 个节点上要 ~40s；
    /// 并发 + 引导阶段短超时后应 ~3s。此处上界取 8s，对慢 CI 留足余量，
    /// 又远低于旧实现的 40s —— 足以在回归时立刻失败。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Bootstrap_全部引导节点不可达_总耗时不随节点数线性累加()
    {
        const int nodeCount = 4;
        var closedPorts = ReserveClosedUdpPorts(nodeCount);
        var unreachable = closedPorts.Select(p => new IPEndPoint(IPAddress.Loopback, p)).ToArray();

        var (dht, udp, cts) = CreateDht(unreachable);
        var receiveTask = RunReceiveAsync(dht, cts.Token);

        try
        {
            await Task.Delay(100);   // 等接收循环进入 ReceiveAsync

            var sw = Stopwatch.StartNew();
            await dht.BootstrapAsync(cts.Token);
            sw.Stop();
            _output.WriteLine(
                $"[baseline/opt] 引导节点 {nodeCount} 个（全部不可达）耗时 {sw.Elapsed.TotalSeconds:F2}s");

            // 语义不变：全部失败只记日志、不抛，路由表为空（只有自己，且自己不入表）。
            dht.GetAllKnownNodes().ShouldBeEmpty();

            sw.Elapsed.ShouldBeLessThan(
                TimeSpan.FromSeconds(8),
                "引导节点并发探测 + 引导阶段短超时后，4 个不可达引导节点不应耗掉 4 个超时");
        }
        finally
        {
            cts.Cancel();
            udp.Dispose();
            cts.Dispose();
            try { await receiveTask; } catch { /* 忽略关闭期异常 */ }
        }
    }

    /// <summary>
    /// 红线守卫：**所有**可达引导节点都必须进路由表。
    /// <para>
    /// 拉起 A / C / B 三个真实 DHT 节点，B 以 [A, C] 为引导。
    /// 若实现退化为「首个成功即 break」，B 只会 PING 到其中一个（另一个在 B 的表里不可见），
    /// 断言 <c>路由表同时含 A 与 C</c> 必然失败；D7 修复（引导 PING 成功节点入表）也在此覆盖。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Bootstrap_多个可达引导节点_全部进入路由表()
    {
        var (dhtA, udpA, ctsA) = CreateDht(Array.Empty<IPEndPoint>());
        var (dhtC, udpC, ctsC) = CreateDht(Array.Empty<IPEndPoint>());
        var (dhtB, udpB, ctsB) = CreateDht(new IPEndPoint[]
        {
            new(IPAddress.Loopback, udpA.LocalEndPoint.Port),
            new(IPAddress.Loopback, udpC.LocalEndPoint.Port),
        });

        var receiveA = RunReceiveAsync(dhtA, ctsA.Token);
        var receiveC = RunReceiveAsync(dhtC, ctsC.Token);
        var receiveB = RunReceiveAsync(dhtB, ctsB.Token);

        try
        {
            await Task.Delay(100);   // 等三个接收循环就绪

            var sw = Stopwatch.StartNew();
            await dhtB.BootstrapAsync(ctsB.Token);
            sw.Stop();
            _output.WriteLine(
                $"[baseline/opt] 引导节点 2 个（均可达，本地回环）耗时 {sw.Elapsed.TotalSeconds:F2}s");

            // A17 等价断言：引导完成后路由表至少 1 个节点；此处进一步要求「>= 引导节点数」。
            var known = dhtB.GetAllKnownNodes();
            _output.WriteLine(
                $"[baseline/opt] B 引导后路由表: {string.Join(", ", known.Select(n => n.EndPoint))}");

            known.ShouldNotBeEmpty("引导完成且至少有一个引导节点响应成功 —— 路由表不能为空");
            known.Count.ShouldBeGreaterThanOrEqualTo(2,
                "两个可达引导节点都必须被 PING 并入表 —— 若只有 1 个，说明退化成「首个成功即 break」");

            known.Any(n => n.NodeId.Equals(dhtA.LocalNode.NodeId)).ShouldBeTrue(
                "D7 修复：引导阶段 PING 成功的节点必须进入路由表");
            known.Any(n => n.NodeId.Equals(dhtC.LocalNode.NodeId)).ShouldBeTrue(
                "D7 修复：引导阶段 PING 成功的节点必须进入路由表（第二个引导节点同样适用）");

            sw.Elapsed.ShouldBeLessThan(
                TimeSpan.FromSeconds(5),
                "本地回环上的引导节点应在亚秒级完成引导，不应被引导阶段的超时预算拖住");
        }
        finally
        {
            ctsA.Cancel();
            ctsC.Cancel();
            ctsB.Cancel();
            udpA.Dispose();
            udpC.Dispose();
            udpB.Dispose();
            ctsA.Dispose();
            ctsC.Dispose();
            try { await Task.WhenAll(receiveA, receiveC, receiveB); } catch { /* 忽略关闭期异常 */ }
        }
    }

    /// <summary>
    /// e2e 场景形状的守卫：一个可达的本地节点 + 4 个不可达公网节点。
    /// <para>
    /// 这是 <c>scripts/e2e-verify.ps1</c> 阶段A 启动时的真实形状
    /// （引导列表 = 本地对端 + 4 个公共 DHT 节点）。修复前它要串行等满每个不可达公网节点
    /// （约 30~40s），现在应在引导超时预算内收敛。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Bootstrap_一个可达加四个不可达_总耗时有界()
    {
        var closedPorts = ReserveClosedUdpPorts(4);
        var unreachable = closedPorts.Select(p => new IPEndPoint(IPAddress.Loopback, p)).ToArray();

        var (dhtA, udpA, ctsA) = CreateDht(Array.Empty<IPEndPoint>());
        var (dhtB, udpB, ctsB) = CreateDht(
            new[] { new IPEndPoint(IPAddress.Loopback, udpA.LocalEndPoint.Port) }
                .Concat(unreachable).ToArray());

        var receiveA = RunReceiveAsync(dhtA, ctsA.Token);
        var receiveB = RunReceiveAsync(dhtB, ctsB.Token);

        try
        {
            await Task.Delay(100);

            var sw = Stopwatch.StartNew();
            await dhtB.BootstrapAsync(ctsB.Token);
            sw.Stop();
            _output.WriteLine(
                $"[baseline/opt] 引导节点 5 个（1 可达 + 4 不可达）耗时 {sw.Elapsed.TotalSeconds:F2}s");

            // 可达的那个必须入表（D7），不可达的不影响引导完成。
            var known = dhtB.GetAllKnownNodes();
            known.Any(n => n.NodeId.Equals(dhtA.LocalNode.NodeId)).ShouldBeTrue(
                "可达的引导节点必须在引导完成时已经在路由表里（e2e 断言 A17）");
            known.Count.ShouldBeLessThanOrEqualTo(2, "不可达节点不应凭空入表");

            sw.Elapsed.ShouldBeLessThan(
                TimeSpan.FromSeconds(10),
                "4 个不可达公网节点不应把启动拖到 4 倍 KRPC 通用超时");
        }
        finally
        {
            ctsA.Cancel();
            ctsB.Cancel();
            udpA.Dispose();
            udpB.Dispose();
            ctsA.Dispose();
            ctsB.Dispose();
            try { await Task.WhenAll(receiveA, receiveB); } catch { /* 忽略关闭期异常 */ }
        }
    }

    /// <summary>
    /// 引导阶段的单节点超时必须短于 KRPC 通用超时（10s）——
    /// 否则受限网络下「等满所有引导节点」依旧会把启动拖到 10s × N。
    /// 通过构造参数注入一个极短超时来验证该参数确实作用到引导 PING 上。
    /// </summary>
    [Fact]
    public async Task Bootstrap_自定义引导超时_生效且不影响入表语义()
    {
        var closedPorts = ReserveClosedUdpPorts(3);
        var unreachable = closedPorts.Select(p => new IPEndPoint(IPAddress.Loopback, p)).ToArray();

        var udp = new UdpTransport(0, NullLogger<UdpTransport>.Instance);
        var nodeId = NodeId.CreateRandom();
        var localNode = new NodeInfo
        {
            NodeId = nodeId,
            EndPoint = new IPEndPoint(IPAddress.Loopback, udp.LocalEndPoint.Port),
            PublicKey = new byte[91],
            State = PeerState.Online
        };
        using var cts = new CancellationTokenSource();
        var dht = new MainlineDhtService(
            localNode, new RoutingTable(nodeId, 20), udp,
            NullLogger<MainlineDhtService>.Instance,
            bootstrapNodes: unreachable,
            alpha: 3,
            bootstrapQueryTimeout: TimeSpan.FromMilliseconds(400));

        var receiveTask = RunReceiveAsync(dht, cts.Token);
        try
        {
            await Task.Delay(100);
            var sw = Stopwatch.StartNew();
            await dht.BootstrapAsync(cts.Token);
            sw.Stop();
            _output.WriteLine($"[baseline/opt] 引导超时 400ms × 3 个不可达节点耗时 {sw.Elapsed.TotalSeconds:F2}s");

            dht.GetAllKnownNodes().ShouldBeEmpty();
            sw.Elapsed.ShouldBeLessThan(
                TimeSpan.FromSeconds(2),
                "引导阶段超时必须可配置且真正作用于 PING（3 × 400ms ≈ 1.2s）");
        }
        finally
        {
            cts.Cancel();
            udp.Dispose();
            try { await receiveTask; } catch { /* 忽略关闭期异常 */ }
        }
    }
}
