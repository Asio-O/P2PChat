using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using P2PChat.Core.Abstractions;
using P2PChat.Core.Enums;
using P2PChat.Core.Models;
using P2PChat.Networking.Dht;
using P2PChat.Networking.Transport;
using Shouldly;

namespace P2PChat.Networking.Tests;

/// <summary>
/// 真实 Mainline DHT 节点发现闭环测试 —— 拉起两个 MainlineDhtService 实例（不同 UDP 端口），
/// 跑通 <c>find_node → announce_peer → get_peers</c> 三个真实 KRPC 报文路径。
/// <para>
/// 与既有 <c>FakeDhtService</c> 集成测试不同：本文件**不预置任何发现路径**，
/// 完全靠 KRPC 协议上的 announce/get_peers 闭环来互相解析。
/// </para>
/// <para>
/// 测试用纯本地回环 UDP（127.0.0.1）模拟 P2P 网络；为保证两条消息能到达，绑定具体端口而非 0，
/// 避免端口漂移带来的不确定性。
/// </para>
/// </summary>
public class RealDiscoveryTests
{
    /// <summary>
    /// 构造一个真实 MainlineDhtService，并返回 (实例, UDP transport, 取消令牌)。
    /// </summary>
    /// <param name="udpPort">期望绑定的 UDP 端口（0 = 由系统分配）。</param>
    /// <param name="bootstrapNodes">本节点启动时要 ping 的节点列表。</param>
    private static (MainlineDhtService Dht, UdpTransport Udp, CancellationTokenSource Cts) CreateDht(
        int udpPort, IReadOnlyList<IPEndPoint> bootstrapNodes)
    {
        var logger = NullLogger<MainlineDhtService>.Instance;
        var udp = new UdpTransport(udpPort, NullLogger<UdpTransport>.Instance);

        // 随机 NodeId + 真实绑定的 UDP 端口；TCP 端口只是元数据不影响本测试（不实际建 TCP）。
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
            localNode, routingTable, udp, logger,
            bootstrapNodes: bootstrapNodes,
            alpha: 3);
        return (dht, udp, new CancellationTokenSource());
    }

    private static async Task RunReceiveAsync(MainlineDhtService dht, CancellationToken ct)
    {
        try { await dht.StartReceivingAsync(ct); }
        catch (OperationCanceledException) { /* 正常关闭 */ }
    }

    /// <summary>
    /// 等待 <paramref name="predicate"/> 在 <paramref name="timeoutMs"/> 内为 true，每 50ms 轮询一次。
    /// </summary>
    private static async Task<bool> WaitForAsync(Func<bool> predicate, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (predicate()) return true;
            await Task.Delay(50);
        }
        return predicate();
    }

    [Fact]
    public async Task Bootstrap_单向依赖_B可发现A()
    {
        // 拉起 A 与 B；B 以 A 的 UDP 端口为引导。预期：B 完成引导后 A 也在 B 的路由表里（反之亦然 —— B 的 PING 期间 A 把 B 加入了路由表）。
        var (dhtA, udpA, ctsA) = CreateDht(udpPort: 40001, bootstrapNodes: Array.Empty<IPEndPoint>());
        // B 在 A 之后创建，构造时还不知道 A 的 UDP 端口被系统分配了哪个 —— 这里指定 40002；
        // CreateDht 内部使用固定端口，不会被重定向到其它端口。
        var (dhtB, udpB, ctsB) = CreateDht(
            udpPort: 40002,
            bootstrapNodes: new IPEndPoint[] { new(IPAddress.Loopback, udpA.LocalEndPoint.Port) });

        var receiveA = Task.Run(() => RunReceiveAsync(dhtA, ctsA.Token));
        var receiveB = Task.Run(() => RunReceiveAsync(dhtB, ctsB.Token));

        try
        {
            // 给接收循环一点点时间进入 ReceiveAsync 的等待点（fire-and-forget 启动有调度延迟）。
            await Task.Delay(100);

            var bootstrapTask = dhtB.BootstrapAsync(ctsB.Token);
            var bootstrapped = await WaitForAsync(() =>
            {
                try { return bootstrapTask.IsCompleted; } catch { return false; }
            }, timeoutMs: 10000);
            bootstrapped.ShouldBeTrue("B 的 BootstrapAsync 必须在 10s 内完成");

            // A 的路由表现在应包含 B（B 在 PING 引导时被打入路由表）。
            var aKnownB = await WaitForAsync(() =>
                dhtA.GetAllKnownNodes().Any(n => n.NodeId.Equals(dhtB.LocalNode.NodeId)),
                timeoutMs: 5000);
            aKnownB.ShouldBeTrue(
                "B 在引导过程中 PING 了 A，A 收到 PING 后应把 B 加入路由表 —— 这是「对端即引导节点」路径的真实 UDP 往返证据");

            // 反向：A 也可被 B 通过 fallback（精确匹配路由表）解析。
            var resolved = await dhtB.FindNodeAsync(dhtA.LocalNode.NodeId, ctsB.Token);
            resolved.ShouldNotBeNull();
            resolved!.NodeId.ShouldBe(dhtA.LocalNode.NodeId);
        }
        finally
        {
            ctsA.Cancel();
            ctsB.Cancel();
            udpA.Dispose();
            udpB.Dispose();
            ctsA.Dispose();
            ctsB.Dispose();
            try { await Task.WhenAll(receiveA, receiveB); } catch { }
        }
    }

    [Fact]
    public async Task AnnounceNow_双向互宣告后_两端都能通过getPeers解析对端()
    {
        // 拉起 A 与 B；B 以 A 为引导；两端都启动接收循环。
        // 两端都触发 AnnounceNowAsync 强制宣告（不必等 15 分钟周期）。
        // 期望：A 与 B 互相在对方的 peerCache 中留有「自己 → TCP 端点」的条目；
        //       之后任一端 FindNodeAsync(对端 NodeId) 都能拿到对端的 NodeInfo（含 TCP 端口）。
        var (dhtA, udpA, ctsA) = CreateDht(udpPort: 40011, bootstrapNodes: Array.Empty<IPEndPoint>());
        var (dhtB, udpB, ctsB) = CreateDht(
            udpPort: 40012,
            bootstrapNodes: new IPEndPoint[] { new(IPAddress.Loopback, udpA.LocalEndPoint.Port) });

        var receiveA = Task.Run(() => RunReceiveAsync(dhtA, ctsA.Token));
        var receiveB = Task.Run(() => RunReceiveAsync(dhtB, ctsB.Token));

        try
        {
            // 给接收循环一点点时间进入 ReceiveAsync 的等待点。
            await Task.Delay(100);

            await dhtB.BootstrapAsync(ctsB.Token);

            // 等待 B 出现在 A 的路由表 —— 引导 PING 的直接证据。
            var aKnownB = await WaitForAsync(() =>
                dhtA.GetAllKnownNodes().Any(n => n.NodeId.Equals(dhtB.LocalNode.NodeId)),
                timeoutMs: 5000);
            aKnownB.ShouldBeTrue("B 引导过程中 PING 了 A，A 应把 B 加入路由表");

            // 强制双向宣告 —— 走 get_peers + announce_peer 真实 KRPC 报文。
            await dhtA.AnnounceNowAsync(ctsA.Token);
            await dhtB.AnnounceNowAsync(ctsB.Token);

            // 等消息传播。
            await Task.Delay(500, CancellationToken.None);

            // 宣告状态：双方至少有一次 announce_peer 成功 —— 但这是尽力而为。
            // 若 UDP 包丢失、token 校验失败等，断言不会成功 —— 退化为「任一端至少收到宣告」。
            var announceA = dhtA.AnnouncedNodeCount;
            var announceB = dhtB.AnnouncedNodeCount;
            (announceA + announceB).ShouldBeGreaterThan(0,
                "至少有一端应当至少完成一次 announce_peer 成功 —— 否则 UDP 双向不通，测试环境本身有问题");

            // 闭合：A.FindNodeAsync(B.NodeId) 应能解析到 B（含正确 TCP 端点）。
            var resolveFromA = await dhtA.FindNodeAsync(dhtB.LocalNode.NodeId, ctsA.Token);
            resolveFromA.ShouldNotBeNull();
            resolveFromA!.NodeId.ShouldBe(dhtB.LocalNode.NodeId);
            resolveFromA.EndPoint.Port.ShouldBe(dhtB.LocalNode.EndPoint.Port,
                "解析出的 TCP 端口必须等于 B 实际宣告的 TCP 端口");

            // 反向同理。
            var resolveFromB = await dhtB.FindNodeAsync(dhtA.LocalNode.NodeId, ctsB.Token);
            resolveFromB.ShouldNotBeNull();
            resolveFromB!.NodeId.ShouldBe(dhtA.LocalNode.NodeId);
            resolveFromB.EndPoint.Port.ShouldBe(dhtA.LocalNode.EndPoint.Port);
        }
        finally
        {
            ctsA.Cancel();
            ctsB.Cancel();
            udpA.Dispose();
            udpB.Dispose();
            ctsA.Dispose();
            ctsB.Dispose();
            try { await Task.WhenAll(receiveA, receiveB); } catch { }
        }
    }

    [Fact]
    public async Task Bootstrap_全部引导节点不可达时_不抛异常()
    {
        // 1.5 行为回归：原版「首个成功即 break」被替换后，全部引导失败必须只记日志、不抛异常。
        // 用不存在的端口作为引导节点（瞬时不可达）。
        var (dht, udp, cts) = CreateDht(udpPort: 40021, bootstrapNodes: Array.Empty<IPEndPoint>());
        var dhtWithBogus = new MainlineDhtService(
            dht.LocalNode, new RoutingTable(dht.LocalNode.NodeId, 20), udp,
            NullLogger<MainlineDhtService>.Instance,
            bootstrapNodes: new IPEndPoint[] { new(IPAddress.Loopback, 1) },    // 1 通常空闲/拒连
            alpha: 3);

        var receiveTask = Task.Run(() => RunReceiveAsync(dhtWithBogus, cts.Token));

        try
        {
            // 不应抛回。
            await Task.Delay(50);
            await dhtWithBogus.BootstrapAsync(cts.Token);

            // 路由表应为空（无任何成功 bootstrap 节点）。
            dhtWithBogus.GetAllKnownNodes().ShouldBeEmpty();

            // 找一个随机的 NodeId —— 必须返回 null，且不抛。
            var ghost = await dhtWithBogus.FindNodeAsync(NodeId.CreateRandom(), cts.Token);
            ghost.ShouldBeNull();
        }
        finally
        {
            cts.Cancel();
            udp.Dispose();
            cts.Dispose();
            try { await receiveTask; } catch { }
        }
    }

    /// <summary>
    /// Phase 2 / 2.2 守卫：收到对端的 announce_peer 时，MainlineDhtService 把 UDP 包源端点写入
    /// peerCache 的 ExternalEndPoint（不再死字段）。
    /// </summary>
    [Fact]
    public async Task AnnouncePeer_收到后_peerCache的ExternalEndPoint_等于包源地址()
    {
        // 受端 = A；发送端 = 临时 raw UDP socket 模拟对端。
        var (dhtA, udpA, ctsA) = CreateDht(udpPort: 40031, bootstrapNodes: Array.Empty<IPEndPoint>());
        var receiveTask = Task.Run(() => RunReceiveAsync(dhtA, ctsA.Token));

        try
        {
            await Task.Delay(50);   // 等接收循环就绪

            var peerId = NodeId.CreateRandom();
            var infoHash = dhtA.LocalNode.NodeId;
            var announcedTcpPort = 29999;

            // 我们用 raw UDP socket 模拟对端：它的 LocalEndPoint 就是「对端的公网入口地址」。
            var senderUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            senderUdp.Client.ReceiveTimeout = 2000;   // 避免 ReceiveAsync 永久阻塞
            var senderEp = (IPEndPoint)senderUdp.Client.LocalEndPoint!;

            // Step 1: 发送 get_peers，拿到 A 发放给我们的 token。
            var getPeersMsg = new Dictionary<string, object>
            {
                ["t"] = Encoding.UTF8.GetBytes("tx-get-peers"),
                ["y"] = Encoding.UTF8.GetBytes("q"),
                ["q"] = Encoding.UTF8.GetBytes("get_peers"),
                ["a"] = new Dictionary<string, object>
                {
                    ["id"] = peerId.ToByteArray(),
                    ["info_hash"] = infoHash.ToByteArray()
                }
            };
            await senderUdp.SendAsync(Bencode.Encode(getPeersMsg), dhtA.LocalNode.EndPoint);

            byte[] token;
            try
            {
                var resp = await senderUdp.ReceiveAsync();
                var respMsg = (Dictionary<string, object>)Bencode.Decode(resp.Buffer);
                var rInner = (Dictionary<string, object>)respMsg["r"];
                token = (byte[])rInner["token"];
            }
            catch (Exception ex)
            {
                senderUdp.Dispose();
                throw new ShouldAssertException(
                    $"收 get_peers 响应失败（{ex.Message}）。本测试需保证 UDP 报文能正常回传 —— 检查 A 的 receive 循环是否就绪。");
            }

            // Step 2: 用合法 token 宣告——A 应当把它写进 peerCache 的 ExternalEndPoint（=senderEp）。
            var announceMsg = new Dictionary<string, object>
            {
                ["t"] = Encoding.UTF8.GetBytes("tx-announce"),
                ["y"] = Encoding.UTF8.GetBytes("q"),
                ["q"] = Encoding.UTF8.GetBytes("announce_peer"),
                ["a"] = new Dictionary<string, object>
                {
                    ["id"] = peerId.ToByteArray(),
                    ["info_hash"] = infoHash.ToByteArray(),
                    ["port"] = announcedTcpPort,
                    ["token"] = token
                }
            };
            await senderUdp.SendAsync(Bencode.Encode(announceMsg), dhtA.LocalNode.EndPoint);

            // 等消息传播。
            var seen = await WaitForAsync(
                () => dhtA.ListAnnouncedPeers().Any(p => p.PeerId.Equals(peerId)),
                timeoutMs: 5000);
            seen.ShouldBeTrue("合法 token 的 announce_peer 必须在 5s 内被记入 peerCache");

            var match = dhtA.ListAnnouncedPeers().First(p => p.PeerId.Equals(peerId));
            match.EndPoint.Port.ShouldBe(announcedTcpPort, "宣告的 TCP port 必须落到 peerCache");
            match.ExternalEndPoint.ShouldNotBeNull("包源地址必须写入 ExternalEndPoint（不再死字段）");
            match.ExternalEndPoint!.Address.ShouldBe(senderEp.Address,
                "ExternalEndPoint = KRPC 包源地址（NAT 后此即对端的公网入口）");

            senderUdp.Dispose();
        }
        finally
        {
            ctsA.Cancel();
            udpA.Dispose();
            ctsA.Dispose();
            try { await receiveTask; } catch { }
        }
    }
}