using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using P2PChat.Core.Enums;
using P2PChat.Core.Models;
using P2PChat.Networking.Dht;
using P2PChat.Networking.Transport;
using Shouldly;
using Xunit.Abstractions;

namespace P2PChat.Networking.Tests;

/// <summary>
/// 「公共 DHT 能否自动发现 P2PChat 节点」的**实测**证据（task-32）。
/// <para>
/// 结构性论证（doc-writer / task-24）：公共 BitTorrent 节点返回的 <c>get_peers</c> 响应里
/// 永远没有可用的 P2PChat 节点 —— 标准 <c>values</c> 只带 6 字节 IP+port，而
/// <c>MainlineDhtService</c> 把这些条目盖上<strong>响应者（公共节点）自己</strong>的 NodeId
/// （<c>MainlineDhtService.cs:406</c>），解析端又要求 <c>peerId.Equals(targetId)</c>
/// （<c>:711</c>）。两者相加，命中恒为假。
/// </para>
/// <para>
/// 本文件不靠"读代码觉得不可能"，而是<b>拉起真实 KRPC 报文</b>来量化：所有用例都跑真实 UDP、
/// 真实 bencode、真实的 <c>MainlineDhtService</c>，不预置静态对端、不预置 contacts.json。
/// </para>
/// <para>
/// 关键设计：<see cref="FakePublicDhtNode"/> 是一个<b>仿真公共 BitTorrent 节点</b>，它按 BEP-5/BEP-44
/// 应答（<c>ping</c> / <c>find_node</c> / <c>get_peers</c> / <c>announce_peer</c>，带 token 校验），
/// 真实记录谁向它宣告了「谁 → 哪个 TCP 端点」。之所以要仿真，是因为本机<b>出网 UDP 被封</b>
/// （实测 4 个公共 DHT 引导节点 send 即失败），无法直接打真实公网 DHT；而仿真恰好让实验
/// <b>可重复</b>，并且能给出比公网更慷慨的对照。
/// </para>
/// </summary>
public class PublicDhtDiscoveryEmpiricsTests
{
    /// <summary>每个场景的重复次数。</summary>
    private const int Iterations = 10;

    /// <summary>单次「是否发现对方」的判定时限（毫秒）。</summary>
    private const int DiscoveryBudgetMs = 30000;

    private readonly ITestOutputHelper _output;
    public PublicDhtDiscoveryEmpiricsTests(ITestOutputHelper output) => _output = output;

    #region 仿真公共 BitTorrent DHT 节点

    /// <summary>
    /// 仿真公共 BitTorrent DHT 节点 —— 行为严格按 BEP-5 / BEP-44，<b>只认标准 6 字节 values</b>。
    /// <para>
    /// 这是公共节点的真实能力边界：<c>announce_peer</c> 里有 20 字节 <c>id</c>，但协议只允许它
    /// 通过 <c>values</c> 把 6 字节 IP+port 还回去 —— <b>宣告方的 NodeId 无法被查询方拿回</b>。
    /// </para>
    /// </summary>
    private sealed class FakePublicDhtNode : IDisposable
    {
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly NodeId _id = NodeId.CreateRandom();
        private readonly byte[] _token = new byte[8];
        private readonly CancellationTokenSource _cts = new();

        /// <summary>info_hash(hex) → 已宣告的 (宣告方 NodeId, 源 IP, 宣告端口)。</summary>
        private readonly ConcurrentDictionary<string, List<(byte[] AnnouncerId, IPAddress Ip, int Port)>> _store
            = new(StringComparer.Ordinal);

        /// <summary>
        /// true = 按标准返回 6 字节 <c>values</c>（真实公共节点行为）。
        /// false = 返回 26 字节 <c>values</c>（<b>比真实公共节点更慷慨</b>：额外捎带宣告方真实 NodeId）。
        /// 用来把失败原因唯一地钉在「:406 覆盖 NodeId + :711 等值检查」上。
        /// </summary>
        public bool StandardSixByteValues { get; set; } = true;

        public IPEndPoint EndPoint => (IPEndPoint)_udp.Client.LocalEndPoint!;
        public NodeId NodeId => _id;
        public int AnnounceCount => _store.Values.Sum(v => v.Count);

        public FakePublicDhtNode()
        {
            Random.Shared.NextBytes(_token);
            _ = Task.Run(ServeAsync);
        }

        private async Task ServeAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var r = await _udp.ReceiveAsync(_cts.Token);
                    _ = Task.Run(() => HandleAsync(r.Buffer, r.RemoteEndPoint));
                }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { /* 对端不可达反馈（ICMP），继续服务 */ }
                catch (Exception) { /* 单条报文出错不影响服务循环 */ }
            }
        }

        private async Task HandleAsync(byte[] data, IPEndPoint remote)
        {
            Dictionary<string, object> msg;
            try { msg = (Dictionary<string, object>)Bencode.Decode(data); }
            catch { return; }

            if (!msg.TryGetValue("y", out var yObj) || yObj is not byte[] yBytes
                || Encoding.UTF8.GetString(yBytes) != "q")
                return;

            var q = msg.TryGetValue("q", out var qObj) && qObj is byte[] qBytes
                ? Encoding.UTF8.GetString(qBytes) : "";
            var a = msg.TryGetValue("a", out var aObj) && aObj is Dictionary<string, object> aDict
                ? aDict : new Dictionary<string, object>();
            var t = msg.TryGetValue("t", out var tObj) && tObj is byte[] tBytes ? tBytes : new byte[2];

            var r = new Dictionary<string, object> { ["id"] = _id.ToByteArray() };

            switch (q)
            {
                case "ping":
                    break;

                case "find_node":
                    // 公共节点确实会回自己知道的节点；这里回空列表，
                    // 以便把实验聚焦在「get_peers 能否解析出目标对端」这一条路径上。
                    r["nodes"] = Array.Empty<byte>();
                    break;

                case "get_peers":
                    r["token"] = _token;
                    if (a.TryGetValue("info_hash", out var ihObj) && ihObj is byte[] ih && ih.Length == 20
                        && _store.TryGetValue(Convert.ToHexString(ih).ToLowerInvariant(), out var announced)
                        && announced.Count > 0)
                    {
                        var values = new List<object>();
                        foreach (var (announcerId, ip, port) in announced)
                            values.Add(StandardSixByteValues ? Encode6(ip, port) : Bencode.EncodeCompactNode(announcerId, ip, port));
                        r["values"] = values;
                    }
                    break;

                case "announce_peer":
                    if (a.TryGetValue("info_hash", out var ahObj) && ahObj is byte[] ah && ah.Length == 20
                        && a.TryGetValue("id", out var idObj) && idObj is byte[] idBytes && idBytes.Length == 20
                        && a.TryGetValue("port", out var pObj)
                        && a.TryGetValue("token", out var tkObj) && tkObj is byte[] tk && tk.AsSpan().SequenceEqual(_token))
                    {
                        var key = Convert.ToHexString(ah).ToLowerInvariant();
                        var entry = (idBytes, remote.Address, Bencode.B2I(pObj));
                        _store.AddOrUpdate(key,
                            _ => new List<(byte[], IPAddress, int)> { entry },
                            (_, list) => { list.Add(entry); return list; });
                    }
                    break;
            }

            var reply = new Dictionary<string, object>
            {
                ["t"] = t,
                ["y"] = Encoding.UTF8.GetBytes("r"),
                ["r"] = r
            };
            try { await _udp.SendAsync(Bencode.Encode(reply), remote); }
            catch (SocketException) { /* 查询方可能已关闭 */ }
        }

        /// <summary>BEP-44 标准的 6 字节 values 条目：[4B IPv4][2B port BE]，无 NodeId。</summary>
        private static byte[] Encode6(IPAddress ip, int port)
        {
            var e = new byte[6];
            var ipBytes = ip.GetAddressBytes();
            Buffer.BlockCopy(ipBytes, 0, e, 0, 4);
            e[4] = (byte)(port >> 8);
            e[5] = (byte)port;
            return e;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _udp.Dispose();
            _cts.Dispose();
        }
    }

    #endregion

    #region P2PChat 真实节点

    /// <summary>拉起一个真实 <see cref="MainlineDhtService"/>（UDP 端口 0 = 系统分配），并启动接收循环。</summary>
    private static (MainlineDhtService Dht, UdpTransport Udp, CancellationTokenSource Cts, Task Receive) CreateP2pNode(
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
        var dht = new MainlineDhtService(
            localNode, new RoutingTable(nodeId, 20), udp,
            NullLogger<MainlineDhtService>.Instance,
            bootstrapNodes: bootstrapNodes, alpha: 3);

        var cts = new CancellationTokenSource();
        var receive = Task.Run(async () =>
        {
            try { await dht.StartReceivingAsync(cts.Token); } catch (OperationCanceledException) { }
        });
        return (dht, udp, cts, receive);
    }

    private static async Task ShutdownAsync(params (MainlineDhtService Dht, UdpTransport Udp, CancellationTokenSource Cts, Task Receive)[] nodes)
    {
        foreach (var n in nodes)
        {
            n.Cts.Cancel();
            n.Udp.Dispose();
            n.Dht.Dispose();
            n.Cts.Dispose();
            try { await n.Receive; } catch { /* 关闭期异常忽略 */ }
        }
    }

    /// <summary>向系统申请 N 个当前空闲的 UDP 端口并立即释放 —— 得到「几乎肯定无人监听」的端口。</summary>
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
        finally { foreach (var s in holders) s.Dispose(); }
        return ports;
    }

    /// <summary>
    /// 在 <see cref="DiscoveryBudgetMs"/> 预算内判断 <paramref name="seeker"/> 能否解析出 <paramref name="target"/>。
    /// 返回 (是否成功, 解析到的 TCP 端口或 -1, 耗时)。
    /// </summary>
    private static async Task<(bool Ok, int Port, long ElapsedMs)> TryResolveAsync(
        MainlineDhtService seeker, NodeId target, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var found = await seeker.FindNodeAsync(target, ct);
        sw.Stop();
        return (found is not null, found?.EndPoint.Port ?? -1, sw.ElapsedMilliseconds);
    }

    private static void Report(string group, int success, int total, long totalMs, int samplePort)
    {
        // 供下一轮 doc-writer / note-writer 直接引用的实测数字。
        // 断言本身已经把这些数字钉死（0/N 与 N/N），这里只是让明细在详细日志里可见。
        System.Console.WriteLine(
            $"[EMPIRICS] {group}: {success}/{total} 成功, 合计 {totalMs}ms, 单次均 {totalMs / (double)total:F0}ms, 命中端口样例={samplePort}");
    }

    #endregion

    #region 场景 A：冷启动（双方互不知情）

    /// <summary>
    /// **场景 A —— 冷启动自动发现**：两个全新 P2PChat 实例，互不知情（不预置静态对端、
    /// 不预置 contacts.json、引导列表只有公共 DHT 节点），各自向对方发起 get_peers / find_node。
    /// 预期 <b>0 / N</b>。
    /// </summary>
    [Fact]
    public async Task 冷启动_双方互不知情_无法通过DHT发现对方()
    {
        var closed = ReserveClosedUdpPorts(4);
        var publicLike = closed.Select(p => new IPEndPoint(IPAddress.Loopback, p)).ToArray();

        var results = new (bool Ok, int Port, long Ms)[Iterations];

        // 每一轮冷启动都要等满「不可达引导节点」的 KRPC 通用超时（迭代 find_node 回退路径 ~10s），
        // 串行跑完要 4 分多钟。各轮之间没有任何共享状态（独立节点 / 独立路由表 / 独立端口），
        // 因此限并发跑，把墙钟压到 1/5 而不影响结论。
        await Parallel.ForEachAsync(
            Enumerable.Range(0, Iterations),
            new ParallelOptions { MaxDegreeOfParallelism = 5 },
            async (i, _) =>
            {
                var a = CreateP2pNode(publicLike);
                var b = CreateP2pNode(publicLike);
                try
                {
                    await Task.Delay(50);
                    await a.Dht.BootstrapAsync(a.Cts.Token);
                    await b.Dht.BootstrapAsync(b.Cts.Token);

                    // 双向各问一次（get_peers → find_node 由 FindNodeAsync 内部串联）
                    var r1 = await TryResolveAsync(a.Dht, b.Dht.LocalNode.NodeId, a.Cts.Token);
                    var r2 = await TryResolveAsync(b.Dht, a.Dht.LocalNode.NodeId, b.Cts.Token);

                    results[i] = (r1.Ok || r2.Ok, r1.Ok ? r1.Port : r2.Port, r1.ElapsedMs + r2.ElapsedMs);
                }
                finally { await ShutdownAsync(a, b); }
            });

        var success = results.Count(r => r.Ok);
        var totalMs = results.Sum(r => r.Ms);
        var samplePort = results.FirstOrDefault(r => r.Ok).Port;

        Report("场景A 冷启动·互不知情", success, Iterations, totalMs, samplePort);
        success.ShouldBe(0, "双方互不知情且公共 DHT 不可达时，自动发现不可能成功（结构性 + 环境性）");
    }

    #endregion

    #region 场景 B：对照组（手动 /connect 预置）

    /// <summary>
    /// **场景 B —— 对照组（手动 /connect 等价）**：B 以 A 的真实端点为引导（等价于用户
    /// 执行过一次 <c>/connect</c>、双方互知端点），随后 B 向 A 发起 get_peers / find_node。
    /// 预期 <b>N / N</b> —— 它证明本套件<b>有能力测出「发现成功」</b>，
    /// 从而让场景 C/D 的 0/N 有意义。
    /// </summary>
    [Fact]
    public async Task 对照组_手动预置对端_必定能发现对方()
    {
        int success = 0;
        long totalMs = 0;
        int samplePort = -1;

        for (int i = 0; i < Iterations; i++)
        {
            var a = CreateP2pNode(Array.Empty<IPEndPoint>());
            var b = CreateP2pNode(new IPEndPoint[] { new(IPAddress.Loopback, a.Udp.LocalEndPoint.Port) });
            try
            {
                await Task.Delay(50);
                await a.Dht.BootstrapAsync(a.Cts.Token);
                await b.Dht.BootstrapAsync(b.Cts.Token);

                var r = await TryResolveAsync(b.Dht, a.Dht.LocalNode.NodeId, b.Cts.Token);
                totalMs += r.ElapsedMs;
                if (r.Ok && r.Port == a.Dht.LocalNode.EndPoint.Port)
                {
                    success++;
                    if (samplePort < 0) samplePort = r.Port;
                }
            }
            finally { await ShutdownAsync(a, b); }
        }

        Report("场景B 对照组·手动预置", success, Iterations, totalMs, samplePort);
        success.ShouldBe(Iterations,
            "对照组必须 N/N 成功 —— 否则说明本套件测不出「发现成功」，场景 C/D 的 0/N 就失去对照意义");
        samplePort.ShouldBeGreaterThan(0, "解析出的 TCP 端口应等于 A 实际宣告的端口");
    }

    #endregion

    #region 场景 C：公共 DHT 按标准返回 6 字节 values（核心实验）

    /// <summary>
    /// **场景 C —— 公共节点按 BEP-44 标准应答（核心实验）**：
    /// A 真实向仿真公共节点 P 宣告自己；B 以 P 为引导，随后向 P 查询 A 的 NodeId。
    /// P 手里**确实有 A 的正确 IP+port**（它就是靠 A 的 announce_peer 记下来的），
    /// 并按标准以 6 字节 <c>values</c> 如实归还。
    /// <para>预期 <b>0 / N</b>：公共节点明明把地址交出来了，查询方仍然解析不出 A。</para>
    /// </summary>
    [Fact]
    public async Task 公共DHT按标准返回values_查询方仍无法解析出目标对端()
    {
        int success = 0;
        long totalMs = 0;
        int samplePort = -1;
        int announceOk = 0;

        for (int i = 0; i < Iterations; i++)
        {
            using var p = new FakePublicDhtNode { StandardSixByteValues = true };
            var a = CreateP2pNode(new IPEndPoint[] { p.EndPoint });
            var b = CreateP2pNode(new IPEndPoint[] { p.EndPoint });
            try
            {
                await Task.Delay(50);

                // A 先加入并向公共节点宣告自己（真实的 get_peers 取 token → announce_peer）
                await a.Dht.BootstrapAsync(a.Cts.Token);
                await a.Dht.AnnounceNowAsync(a.Cts.Token);
                if (p.AnnounceCount > 0) announceOk++;

                // B 独立加入同一公共节点，然后去查 A
                await b.Dht.BootstrapAsync(b.Cts.Token);
                var r = await TryResolveAsync(b.Dht, a.Dht.LocalNode.NodeId, b.Cts.Token);
                totalMs += r.ElapsedMs;
                if (r.Ok)
                {
                    success++;
                    if (samplePort < 0) samplePort = r.Port;
                }
            }
            finally { await ShutdownAsync(a, b); }
        }

        Report("场景C 公共DHT·标准6字节values", success, Iterations, totalMs, samplePort);
        announceOk.ShouldBe(Iterations, "前置条件：公共节点必须真的收到了 A 的 announce_peer（否则本组是空跑）");
        success.ShouldBe(0,
            "公共节点已按标准归还 A 的 6 字节 IP+port，查询方仍应解析不出（NodeId 被盖成公共节点的，:711 等值检查恒假）");
    }

    #endregion

    #region 场景 D：公共节点额外捎带真实 NodeId（钉死归因）

    /// <summary>
    /// **场景 D —— 归因实验**：同一个仿真公共节点，改为在 <c>values</c> 里返回 26 字节条目，
    /// <b>额外捎带 A 的真实 NodeId</b>（比任何真实公共节点都慷慨）。
    /// <para>
    /// 若失败仅源于「6 字节格式解析不了」，这里应该成功；它仍然失败，就说明失败点是
    /// <c>:406</c> 把已解析出的 NodeId 覆盖成响应者的 NodeId，配合 <c>:711</c> 的等值检查
    /// —— 两者相加，命中恒为假。预期 <b>0 / N</b>。
    /// </para>
    /// </summary>
    [Fact]
    public async Task 公共DHT在values里额外带上真实NodeId_仍然无法解析出目标对端()
    {
        int success = 0;
        long totalMs = 0;
        int samplePort = -1;
        int announceOk = 0;

        for (int i = 0; i < Iterations; i++)
        {
            using var p = new FakePublicDhtNode { StandardSixByteValues = false };
            var a = CreateP2pNode(new IPEndPoint[] { p.EndPoint });
            var b = CreateP2pNode(new IPEndPoint[] { p.EndPoint });
            try
            {
                await Task.Delay(50);

                await a.Dht.BootstrapAsync(a.Cts.Token);
                await a.Dht.AnnounceNowAsync(a.Cts.Token);
                if (p.AnnounceCount > 0) announceOk++;

                await b.Dht.BootstrapAsync(b.Cts.Token);
                var r = await TryResolveAsync(b.Dht, a.Dht.LocalNode.NodeId, b.Cts.Token);
                totalMs += r.ElapsedMs;
                if (r.Ok)
                {
                    success++;
                    if (samplePort < 0) samplePort = r.Port;
                }
            }
            finally { await ShutdownAsync(a, b); }
        }

        Report("场景D 公共DHT·values带真实NodeId", success, Iterations, totalMs, samplePort);
        announceOk.ShouldBe(Iterations, "前置条件：公共节点必须真的收到了 A 的 announce_peer");
        success.ShouldBe(0,
            "即便公共节点把目标对端的真实 NodeId 一并给出，仍解析不出 —— 归因于 :406 覆盖 + :711 等值检查");
    }

    #endregion
}
