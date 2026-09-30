using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using P2PChat.Core.Abstractions;
using P2PChat.Core.Models;
using P2PChat.Networking.Transport;

namespace P2PChat.Networking.Dht;

/// <summary>
/// Mainline DHT 服务 (BitTorrent Kademlia KRPC 协议)
/// <para>
/// 本实现除了标准的 ping / find_node / get_peers / announce_peer 应答之外，
/// 额外用两个非标准字段支撑「通过公共 DHT 互相宣告 + 互相解析」：
/// </para>
/// <list type="bullet">
///   <item>
///     <c>p2pc_peers</c> —— 在 <c>get_peers</c> 应答中携带 P2PChat 节点列表，
///     每条目 26 字节：<c>[20B NodeId][4B IPv4][2B TCP Port BE]</c>
///     （与 BitTorrent 标准 <c>values</c> 的 6 字节格式不同 —— 多出的 20B NodeId
///     用于让对端把 6 字节 IP+port 映射回具体 NodeId）。
///   </item>
///   <item>
///     <c>p2pc_id</c> —— 在 <c>announce_peer</c> 请求中冗余带上声明方的 NodeId，
///     与标准字段 <c>id</c> 一致，但显式表明「我是 P2PChat 节点，请用本协议存我」。
///   </item>
/// </list>
/// <para>
/// 因此「宣告」/「解析」都依赖对端是 P2PChat 节点。公网引导节点不解析这两个字段，
/// 仅作为回包中转 —— 它们的路由表里不会有 P2PChat 节点。
/// </para>
/// </summary>
public class MainlineDhtService : IDhtService, IDisposable
{
    private readonly IRoutingTable _routingTable;
    private readonly IUdpTransport _transport;
    private readonly ILogger<MainlineDhtService> _logger;
    private readonly Channel<PeerDiscoveryEventArgs> _peerChannel;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Dictionary<string, object>>> _pending = new();

    /// <summary>
    /// 静态对端（手工添加的联系人）—— 端点已知，不依赖 DHT 发现，也不受路由表淘汰影响。
    /// </summary>
    private readonly ConcurrentDictionary<string, NodeInfo> _staticPeers = new();

    /// <summary>
    /// 对端终结点（hex）→ 我们为其发放的 token。BitTorrent 协议要求 token 是该对端「曾被发放过的」字符串，
    /// 本实现用随机 8 字节，长期有效（不实现失效期）。
    /// </summary>
    private readonly ConcurrentDictionary<string, byte[]> _issuedTokens = new(StringComparer.Ordinal);

    /// <summary>
    /// P2PChat 节点宣告记录。Key:info_hash（hex，20 字节），Value:（NodeId, IPEndPoint）列表，
    /// 仅保留最近一次宣告，便于 get_peers 应答直接回送。
    /// </summary>
    private readonly ConcurrentDictionary<string, List<AnnouncedPeer>> _peerCache = new(StringComparer.Ordinal);

    private readonly List<IPEndPoint> _bootstrapNodes;
    private readonly int _alpha;

    /// <summary>
    /// 引导阶段单次 KRPC 查询的超时预算（默认 3s）。
    /// <para>
    /// 引导阶段的语义是「尽快确认哪些引导节点活着」—— 迟到 3s 的应答对本轮加入 DHT 已无价值，
    /// 却在受限/离线网络里把每个不可达节点的代价从 10s 抬到整轮启动的超时预算。
    /// </para>
    /// <para>
    /// 运行期（迭代查找 / 宣告 / 15 分钟周期刷新）仍用 KRPC 通用超时 <see cref="KrpcQueryTimeout"/>，
    /// 不受此预算影响。
    /// </para>
    /// </summary>
    private readonly TimeSpan _bootstrapQueryTimeout;

    /// <summary>KRPC 查询的通用超时（10s，Kademlia 常规值）—— 运行期查询与宣告使用。</summary>
    private static readonly TimeSpan KrpcQueryTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 引导阶段并发探测的引导节点数上限。引导列表 = 配置项 + 已知对端 + 4 个公共 DHT 节点，
    /// 通常不到 10 个；设上限是为了对端文件异常膨胀时不至于一次性打满本机 UDP 发送队列。
    /// </summary>
    private const int BootstrapProbeConcurrency = 8;

    /// <summary>每次宣告取的最近 K 个节点（BEP-44 推荐 8）。</summary>
    private const int AnnounceK = 8;
    private int _txCounter;
    private bool _disposed;

    private readonly object _announceLock = new();
    private DateTime _lastAnnounceUtc = DateTime.MinValue;
    private int _announcedNodeCount;

    public NodeInfo LocalNode { get; }
    public IAsyncEnumerable<PeerDiscoveryEventArgs> OnPeerDiscovered => _peerChannel.Reader.ReadAllAsync();

    /// <summary>最近一次完成 announce_peer 批量的时间（UTC）；未宣告过则为 <see cref="DateTime.MinValue"/>。</summary>
    public DateTime LastAnnounceUtc { get { lock (_announceLock) return _lastAnnounceUtc; } }

    /// <summary>累计成功完成 announce_peer 的对端节点数（启动至今不清零）。</summary>
    public int AnnouncedNodeCount { get { lock (_announceLock) return _announcedNodeCount; } }

    /// <summary>
    /// <see cref="IDhtService.AnnouncedPeerCount"/> 的实现。
    /// <para>
    /// <b>必须显式覆写。</b>接口上它是默认实现 <c>int AnnouncedPeerCount =&gt; 0;</c>，
    /// 而本类只暴露了另一个名字（<see cref="AnnouncedNodeCount"/>）—— 名字对不上导致
    /// <c>P2PChatTui</c> 自检里读到的「已宣告节点数」<b>恒为 0</b>，无论宣告是否真的成功。
    /// 那等于把阶段 1 最核心的能力变成了不可观测（e2e 也无法据此断言）。
    /// </para>
    /// </summary>
    public int AnnouncedPeerCount => AnnouncedNodeCount;

    public MainlineDhtService(
        NodeInfo localNode,
        IRoutingTable routingTable,
        IUdpTransport transport,
        ILogger<MainlineDhtService> logger,
        IReadOnlyList<IPEndPoint> bootstrapNodes,
        int alpha = 3,
        TimeSpan? bootstrapQueryTimeout = null)
    {
        LocalNode = localNode;
        _routingTable = routingTable;
        _transport = transport;
        _logger = logger;
        _bootstrapNodes = bootstrapNodes.ToList();
        _alpha = alpha;
        _bootstrapQueryTimeout = bootstrapQueryTimeout ?? TimeSpan.FromSeconds(3);
        _peerChannel = Channel.CreateUnbounded<PeerDiscoveryEventArgs>();
    }

    /// <inheritdoc />
    public async Task BootstrapAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Mainline DHT 引导开始, {Count} 个引导节点", _bootstrapNodes.Count);
        _routingTable.AddOrUpdate(LocalNode);

        // 1.5 行为（不可退让）：**所有**引导节点都会被 PING —— 任一成功即触发一次迭代查询，
        // 失败只记日志、不阻塞其它节点。历史缺陷是「首个成功即 break」，会让公共 DHT
        // 排在自定义引导节点之后时完全轮不到，从而无法真正加入公共 DHT。
        //
        // 性能改造（同一语义、更短墙钟）：探测**并发**发起，且只花引导阶段短超时
        // （默认 3s 而非 KRPC 通用 10s）。于是「N 个不可达公网节点」的代价从 N×10s 降到 ~3s。
        using var probeGate = new SemaphoreSlim(BootstrapProbeConcurrency);
        var probes = _bootstrapNodes
            .Select(ep => ProbeBootstrapNodeAsync(ep, probeGate, ct))
            .ToArray();

        var anySuccess = false;
        var findNodeStarted = false;
        foreach (var probe in probes)
        {
            // 全部探测此刻已在并发飞行中，逐个 await 只是**取结果**，不会额外拉长总耗时
            // （第 k 个探测早在前面几个被消费时就已经完成或超时了）。
            if (!await probe) continue;

            anySuccess = true;
            // 迭代查询只在首个成功后启动一次；其余成功节点的入表已在探测内部完成（D7）。
            if (findNodeStarted) continue;
            findNodeStarted = true;

            var contacts = await IterativeFindNodeAsync(LocalNode.NodeId, ct, _bootstrapQueryTimeout);
            _logger.LogInformation("引导完成, 发现 {Count} 个节点", contacts.Count);
            foreach (var n in contacts)
            {
                _routingTable.AddOrUpdate(n);
                await NotifyAsync(n, "Bootstrap");
            }
        }

        if (!anySuccess)
        {
            _logger.LogWarning("所有引导节点均无响应，路由表为空");

            // 面向用户的操作指引（不是给排障者看的日志）。
            // 场景三「局域网内一台能连公网、一台完全不能」的用户，收到的正是上面这句，
            // 而「路由表为空」对他毫无帮助 —— 他需要知道下一步敲什么。
            // 本块整体在 if (!anySuccess) 内，因此**只在引导确实失败时出现**，不会每次启动刷屏。
            _logger.LogWarning(
                "无法加入公共 DHT：如果你和对端在同一个局域网，或本机访问不了公网 DHT，"
                + "请不要依赖自动发现，改用下面两条命令之一（两者都完全绕开 DHT）：");
            _logger.LogWarning(
                "  /add <对方节点ID> <局域网IP>:<对方TCP端口>      已知对方节点 ID 时");
            _logger.LogWarning(
                "  /connect <局域网IP>:<对方TCP端口>               不知道节点 ID 时（hello 握手自动获取）");
            _logger.LogWarning(
                "对方在对方程序里敲 /id 可同时看到它的节点 ID 与 TCP 端点（局域网地址要手动替换成内网 IP）");
        }

        // 到这里所有探测都已结束 —— 每个 PING 成功的引导节点都已在 ProbeBootstrapNodeAsync 内入表，
        // 因此这一行日志反映的是完整结果（e2e 断言 A17 依赖这一点）。
        _logger.LogInformation("DHT 引导完成, 路由表: {Count} 节点", _routingTable.GetAllContacts().Count);

        // 引导完成后立刻宣告一次（不等 15 分钟），减少首启→首启密闭时间。
        // 失败也只记录日志 —— 公共 DHT 是尽力而为存储，「未宣告」属正常结果。
        // 这里用引导阶段的短超时：宣告本身是尽力而为，晚到的响应 15 分钟后的周期刷新还能补上。
        await TryAnnounceAsync(ct, _bootstrapQueryTimeout);

        _ = RefreshLoopAsync(ct);
    }

    /// <summary>
    /// 探测单个引导节点：PING → 成功则把对端加入路由表并记「响应成功」日志。
    /// </summary>
    /// <remarks>
    /// D7 修复（必须保留）：引导阶段 PING 成功响应的节点要进路由表，
    /// 否则「引导完成」时路由表为空，节点自身的 DHT 查找能力为 0。
    /// 成功/超时/异常都不外抛，只以返回值汇报 —— 引导不是关键路径，不能让单个节点拖垮整轮。
    /// </remarks>
    /// <param name="gate">并发闸门，限制同时在飞的引导探测数量。</param>
    private async Task<bool> ProbeBootstrapNodeAsync(IPEndPoint ep, SemaphoreSlim gate, CancellationToken ct)
    {
        try
        {
            await gate.WaitAsync(ct);
            try
            {
                _logger.LogDebug("PING 引导节点: {Ep}", ep);
                var bootstrapNode = await KrpcPingAsync(ep, ct, _bootstrapQueryTimeout);
                if (bootstrapNode is null) return false;

                _routingTable.AddOrUpdate(bootstrapNode);   // D7
                _logger.LogInformation("引导节点 {Ep} 响应成功", ep);
                return true;
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "引导节点 {Ep} 无响应 (可能协议不同)", ep);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<NodeInfo?> FindNodeAsync(NodeId targetId, CancellationToken ct = default)
    {
        // 1. 静态对端优先 —— 手工添加的联系人无需 DHT 发现（公共 DHT 上无人宣告任意 NodeId→端点）。
        if (_staticPeers.TryGetValue(targetId.ToHexString(), out var known))
        {
            _logger.LogDebug("命中静态对端: {NodeId} @ {EndPoint}",
                targetId.ToHexString()[..8], known.EndPoint);
            return known;
        }

        // 2. DHT get_peers(info_hash=targetId) —— P2PChat 节点之间通过 p2pc_peers 字段互找。
        var resolved = await ResolveViaGetPeersAsync(targetId, ct);
        if (resolved is not null)
            return resolved;

        // 3. 路由表精确匹配兜底 —— 处理「静态对端未登记 + DHT 暂未宣告/暂未过期」的情况。
        var contacts = await IterativeFindNodeAsync(targetId, ct);
        return contacts.FirstOrDefault(c => c.NodeId.Equals(targetId));
    }

    /// <inheritdoc />
    public void RegisterStaticPeer(NodeInfo node)
    {
        ArgumentNullException.ThrowIfNull(node);

        var key = node.NodeId.ToHexString();

        // ── 冲突时**保留已登记的端点**，不覆盖 ─────────────────────────────────
        // 这条约束此前只写在 KeyExchangeHandler 的 XML 文档里（「不得覆盖已存在的条目」，
        // 且注明「一条都不能省」），而这里原本是 `_staticPeers[key] = node` 无条件覆盖 ——
        // 即**注释声称的保证在当前路径上并不成立**。
        //
        // 为什么旧值更可信：静态对端表有两种来源，可信度不对等。
        //   · 手工登记（contacts.json、/add）：用户带外的主动输入，攻击者拿不到。
        //   · 自报端点（KeyExchangeHandler 的反向登记）：对端在签名覆盖的载荷里**自述**。
        //     签名只能证明「这话是它说的」，**无法判定它有没有撒谎**。
        // 因此冲突时以已登记的为准，并把差异显式记 LogWarning —— 静默丢弃等于让用户
        // 永远不知道自己登记的端点被谁顶掉了。
        if (_staticPeers.TryGetValue(key, out var existing) && !Equals(existing.EndPoint, node.EndPoint))
        {
            _logger.LogWarning(
                "静态对端端点冲突，已保留已登记的端点（不覆盖）: NodeId={NodeId} Existing={Existing} New={New}",
                key[..8], existing.EndPoint, node.EndPoint);
            return;
        }

        _staticPeers[key] = node;
        // 同时入路由表，使 find_node 应答能把该对端报给其他节点（提升整体可发现性）。
        _routingTable.AddOrUpdate(node);
        _logger.LogInformation("登记静态对端: {NodeId} @ {EndPoint}",
            key[..8], node.EndPoint);
    }

    /// <inheritdoc />
    public Task StoreAsync(byte[] key, byte[] value, CancellationToken ct = default)
    {
        // Mainline DHT 的 store 用于 announce_peer，我们使用自定义协议 + 自定义 peerCache。
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<byte[]?> FindValueAsync(byte[] key, CancellationToken ct = default)
    {
        // 改由 FindNodeAsync 的 get_peers 路径覆盖，FindValueAsync 在此仍为占位。
        return Task.FromResult<byte[]?>(null);
    }

    /// <inheritdoc />
    public async Task<bool> PingAsync(NodeInfo node, CancellationToken ct = default)
    {
        try
        {
            var respondingNode = await KrpcPingAsync(node.EndPoint, ct);
            if (respondingNode is null) return false;

            _routingTable.AddOrUpdate(
                respondingNode.NodeId.Equals(node.NodeId)
                    ? node with { LastSeen = respondingNode.LastSeen }
                    : respondingNode);
            return true;
        }
        catch { return false; }
    }

    /// <inheritdoc />
    public IReadOnlyList<NodeInfo> GetAllKnownNodes() => _routingTable.GetAllContacts();

    /// <summary>
    /// 开始接收 DHT 消息循环
    /// </summary>
    public async Task StartReceivingAsync(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var packet = await _transport.ReceiveAsync(ct);
                _ = HandleKrpcMessageAsync(packet.Data, packet.RemoteEndPoint, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException ex) when (UdpSocketErrorClassifier.IsExpectedPeerError(ex))
            {
                // Windows 收到 ICMP port-unreachable 时，UDP ReceiveFromAsync 会抛 ConnectionReset (10054)。
                // 这是对端没有监听端口的正常网络反馈，不应终止接收循环或记录为 ERROR。
                _logger.LogDebug(ex, "DHT UDP 对端不可达，继续接收: {ErrorCode}", ex.SocketErrorCode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DHT 接收异常");
            }
        }
    }

    #region KRPC 协议实现

    private async Task<NodeInfo?> KrpcPingAsync(IPEndPoint ep, CancellationToken ct, TimeSpan? timeout = null)
    {
        var response = await KrpcQueryAsync(ep, "ping", new Dictionary<string, object>(), ct, timeout);
        if (response is null || !response.TryGetValue("id", out var idObj) || idObj is not byte[] idBytes)
            return null;

        return CreateRemoteNode(ep, idBytes, ep);
    }

    private async Task<IReadOnlyList<NodeInfo>> KrpcFindNodeAsync(
        IPEndPoint ep, NodeId target, CancellationToken ct, TimeSpan? timeout = null)
    {
        var args = new Dictionary<string, object>
        {
            ["id"] = LocalNode.NodeId.ToByteArray(),
            ["target"] = target.ToByteArray()
        };
        var response = await KrpcQueryAsync(ep, "find_node", args, ct, timeout);
        if (response == null) return [];

        // 解析 nodes 字段 (紧凑格式)
        if (response.TryGetValue("nodes", out var nodesObj) && nodesObj is byte[] nodesData)
        {
            return Bencode.ParseCompactNodes(nodesData)
                .Select(n => CreateRemoteNode(
                    n.Ip is null ? null : new IPEndPoint(n.Ip, n.Port),
                    n.NodeId,
                    ep))    // KRPC 源端点 = peer 的 UDP 包源地址
                .OfType<NodeInfo>()
                .ToList();
        }

        return [];
    }

    /// <summary>
    /// 向对端发起 <c>get_peers</c>，返回 (token, p2pc_peers)。
    /// </summary>
    private async Task<KrpcGetPeersResult?> KrpcGetPeersAsync(
        IPEndPoint ep, NodeId infoHash, CancellationToken ct, TimeSpan? timeout = null)
    {
        var args = new Dictionary<string, object>
        {
            ["id"] = LocalNode.NodeId.ToByteArray(),
            ["info_hash"] = infoHash.ToByteArray()
        };
        var response = await KrpcQueryAsync(ep, "get_peers", args, ct, timeout);
        if (response is null) return null;

        byte[]? token = null;
        if (response.TryGetValue("token", out var tObj) && tObj is byte[] tBytes)
            token = tBytes;

        List<(byte[]? NodeId, IPAddress Ip, int Port)>? peers = null;
        if (response.TryGetValue("p2pc_peers6", out var pp6Obj) && pp6Obj is List<object> pp6List)
        {
            // IPv6 扩展条目（38B）：NodeId 就在条目里，可直接与查询目标比对。
            peers = FlattenByteList(pp6List, Bencode.CompactPeerV6Size);
        }
        else if (response.TryGetValue("p2pc_peers", out var ppObj) && ppObj is List<object> ppList)
        {
            // p2pc_peers 是 bencode list<bytes> —— 解析为连续 26 字节空间。
            peers = FlattenByteList(ppList, Bencode.CompactPeerV4Size);
        }
        else if (response.TryGetValue("values", out var vObj) && vObj is List<object> vList)
        {
            // 公共节点按 BitTorrent 标准返回 6 字节 values：仅 IP+port，无 NodeId。
            // 我们用查询对端的 NodeId 作为这些 6 字节条目的 NodeId（peer-as-bootstrap 路径的自然延续）。
            var flat = FlattenValuesList(vList);
            if (flat.Count > 0)
            {
                byte[]? responderId = null;
                if (response.TryGetValue("id", out var idObj) && idObj is byte[] idBytes && idBytes.Length == 20)
                    responderId = idBytes;
                if (responderId is not null)
                {
                    // 显式标注元素类型为 byte[]?：标准 values 条目本身不含 NodeId，
                    // 这里补的是「响应者的」，所以结果集的 NodeId 一律可能为 null（见 Bencode.ParseCompactValues6）。
                    peers = flat.Select(p => ((byte[]?)responderId, p.Ip, p.Port)).ToList();
                }
            }
        }

        return new KrpcGetPeersResult(token, peers);
    }

    private readonly record struct KrpcGetPeersResult(byte[]? Token, List<(byte[]? NodeId, IPAddress Ip, int Port)>? Peers);

    /// <summary>
    /// 宣告本节点的 NodeId→TCP 端口映射到给定对端。
    /// </summary>
    private async Task<bool> KrpcAnnouncePeerAsync(
        IPEndPoint ep, NodeId infoHash, byte[] token, int tcpPort, CancellationToken ct, TimeSpan? timeout = null)
    {
        var args = new Dictionary<string, object>
        {
            ["id"] = LocalNode.NodeId.ToByteArray(),
            ["info_hash"] = infoHash.ToByteArray(),
            ["port"] = tcpPort,
            ["token"] = token,
            ["p2pc_id"] = LocalNode.NodeId.ToByteArray()
        };
        var response = await KrpcQueryAsync(ep, "announce_peer", args, ct, timeout);
        return response is not null;
    }

    private async Task<Dictionary<string, object>?> KrpcQueryAsync(
        IPEndPoint ep, string query, Dictionary<string, object> args,
        CancellationToken ct, TimeSpan? timeout = null)
    {
        args["id"] = LocalNode.NodeId.ToByteArray();

        // 并发查询下「算 txId → 登记等待句柄」可能被别的查询抢先，因此用「TryAdd 成功才算拿到 txId」
        // 的方式保证 txId 与自己的等待句柄一一对应（否则回包可能被投递给错误的等待者）。
        string txId;
        TaskCompletionSource<Dictionary<string, object>> tcs;
        while (true)
        {
            txId = NextTxId();
            tcs = new TaskCompletionSource<Dictionary<string, object>>();
            if (_pending.TryAdd(txId, tcs)) break;
        }

        var msg = new Dictionary<string, object>
        {
            ["t"] = Encoding.UTF8.GetBytes(txId),
            ["y"] = Encoding.UTF8.GetBytes("q"),
            ["q"] = Encoding.UTF8.GetBytes(query),
            ["a"] = args
        };

        try
        {
            await _transport.SendAsync(Bencode.Encode(msg), ep, ct);

            using var timeoutCts = new CancellationTokenSource(timeout ?? KrpcQueryTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            return await tcs.Task.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogTrace("KRPC 超时: {Query} -> {Ep}", query, ep);
            return null;
        }
        finally
        {
            // 发送失败也必须清理 —— 否则这个 txId 会被永久占用（并发查询下会影响 txId 的唯一性判定）。
            _pending.TryRemove(txId, out _);
        }
    }

    private async Task HandleKrpcMessageAsync(byte[] data, IPEndPoint remoteEp, CancellationToken ct)
    {
        try
        {
            var msg = (Dictionary<string, object>)Bencode.Decode(data);

            var y = msg.TryGetValue("y", out var yObj) && yObj is byte[] yBytes
                ? Encoding.UTF8.GetString(yBytes) : "";
            var txId = msg.TryGetValue("t", out var tObj) && tObj is byte[] tBytes
                ? Encoding.UTF8.GetString(tBytes) : "";

            if (y == "r")
            {
                if (!string.IsNullOrEmpty(txId) && _pending.TryGetValue(txId, out var tcs))
                {
                    var r = msg.TryGetValue("r", out var rObj) && rObj is Dictionary<string, object> rDict
                        ? rDict : new Dictionary<string, object>();
                    tcs.TrySetResult(r);
                }
            }
            else if (y == "q")
            {
                var q = msg.TryGetValue("q", out var qObj) && qObj is byte[] qBytes
                    ? Encoding.UTF8.GetString(qBytes) : "";
                var a = msg.TryGetValue("a", out var aObj) && aObj is Dictionary<string, object> aDict
                    ? aDict : new Dictionary<string, object>();

                await HandleQueryAsync(q, a, txId, remoteEp, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "解析 KRPC 消息失败 来自 {Ep}", remoteEp);
        }
    }

    private async Task HandleQueryAsync(string query, Dictionary<string, object> args,
        string txId, IPEndPoint remoteEp, CancellationToken ct)
    {
        byte[]? senderId = null;
        if (args.TryGetValue("id", out var idObj) && idObj is byte[] idBytes && idBytes.Length == 20)
            senderId = idBytes;

        // 1.4: 创建 remoteNode 时同时填入 EndPoint (= DHT 源端点 + 公开宣告 TCP 端口（未声明时取源端 UDP 端口作回退）)
        // 与 DhtEndPoint (= KRPC 包源地址)。MessageRouter 仍用 EndPoint 建 TCP 连接 —— 我们这里 EndPoint 即源地址，
        // 远端真要在 NAT 后被连入需要 Phase 2 的 ExternalEndPoint 回填（task-4）。
        var remoteNode = CreateRemoteNode(remoteEp, senderId, remoteEp);
        if (remoteNode is not null)
        {
            _routingTable.AddOrUpdate(remoteNode);
        }

        var response = new Dictionary<string, object>
        {
            ["id"] = LocalNode.NodeId.ToByteArray()
        };

        switch (query)
        {
            case "ping":
                break; // 只需返回 id

            case "find_node":
                if (args.TryGetValue("target", out var targetObj) && targetObj is byte[] targetBytes && targetBytes.Length == 20)
                {
                    var targetId = new NodeId(targetBytes);
                    var closest = _routingTable.GetClosestContacts(targetId, 8);
                    var nodesBytes = closest
                        .Select(n => Bencode.EncodeCompactNode(
                            n.NodeId.ToByteArray(), n.EndPoint.Address, n.EndPoint.Port))
                        .SelectMany(b => b)
                        .ToArray();
                    response["nodes"] = nodesBytes;
                }
                break;

            case "get_peers":
                // 我们不是 BitTorrent 客户端 —— 若 info_hash 命中我们的 peerCache 就回 p2pc_peers；
                // 否则回 closest nodes 的 26 字节紧凑格式 + 一个 token（满足 BEP-44）。
                if (args.TryGetValue("info_hash", out var ihObj) && ihObj is byte[] ihBytes && ihBytes.Length == 20)
                {
                    var ih = new NodeId(ihBytes);
                    IssueToken(remoteEp);

                    var closest = _routingTable.GetClosestContacts(ih, 8);
                    var nodesBytes = closest
                        .Select(n => Bencode.EncodeCompactNode(
                            n.NodeId.ToByteArray(), n.EndPoint.Address, n.EndPoint.Port))
                        .SelectMany(b => b)
                        .ToArray();
                    response["nodes"] = nodesBytes;
                    response["token"] = MakeTokenString(remoteEp);

                    if (_peerCache.TryGetValue(ih.ToHexString(), out var peers))
                    {
                        // 按地址族分流到两个字段：`p2pc_peers`（26B，IPv4，**字节级不变**）
                        // 与 `p2pc_peers6`（38B，IPv6）。
                        //
                        // ⚠️ 旧实现对每条都用固定 4 字节 BlockCopy 拷地址 ——
                        // IPv6 地址会被**静默截断成毫不相干的 IPv4 地址**，不抛异常、不记日志。
                        // 现在改由 Bencode.SerializeCompactPeer 按地址族选格式并对未知族抛异常。
                        var ppList = new List<object>();
                        var pp6List = new List<object>();
                        foreach (var p in peers)
                        {
                            byte[] entry;
                            try
                            {
                                entry = Bencode.SerializeCompactPeer(p.NodeId.ToByteArray(), p.EndPoint);
                            }
                            catch (ArgumentException ex)
                            {
                                // 缓存里出现了本实现无法编码的地址族：跳过并留痕，
                                // 绝不静默塞一个错的地址出去。
                                _logger.LogWarning(ex,
                                    "地址表条目地址族无法编码，已跳过该条目: NodeId={Peer} EndPoint={EndPoint}",
                                    p.NodeId.ToHexString()[..8], p.EndPoint);
                                continue;
                            }

                            if (entry.Length == Bencode.CompactPeerV6Size) pp6List.Add(entry);
                            else ppList.Add(entry);
                        }
                        if (ppList.Count > 0) response["p2pc_peers"] = ppList;
                        if (pp6List.Count > 0) response["p2pc_peers6"] = pp6List;
                    }
                }
                break;

            case "announce_peer":
                // 1.1 / 1.4: 验证 token 后把 (id, 端点) 记入 peerCache。包源端点 = 远端的 DHT 地址，
                // 远端宣告的 TCP 端口 = 我们应当用 EndPoint 连入的端口。
                if (args.TryGetValue("info_hash", out var aIhObj) && aIhObj is byte[] aIhBytes && aIhBytes.Length == 20 &&
                    args.TryGetValue("port", out var aPortObj) &&
                    args.TryGetValue("token", out var aTokenObj) && aTokenObj is byte[] aTokenBytes &&
                    senderId is not null)
                {
                    if (ValidateToken(remoteEp, aTokenBytes))
                    {
                        var announcedPort = Convert.ToInt32((long)aPortObj);
                        var ih = new NodeId(aIhBytes);
                        var tcpEp = new IPEndPoint(remoteEp.Address, announcedPort);
                        RecordAnnounce(ih, senderId, tcpEp, remoteEp, remoteEp);   // Phase 2 / 2.2: 包源地址 = 对端的 NAT 后入口
                        _logger.LogDebug("收到 announce_peer: NodeId={NodeId} @ {Tcp} (DHT src={Dht})",
                            Convert.ToHexString(senderId).ToLower()[..8], tcpEp, remoteEp);
                    }
                    else
                    {
                        _logger.LogDebug("announce_peer token 校验失败: from {Ep}", remoteEp);
                    }
                }
                break;
        }

        var reply = new Dictionary<string, object>
        {
            ["t"] = Encoding.UTF8.GetBytes(txId),
            ["y"] = Encoding.UTF8.GetBytes("r"),
            ["r"] = response
        };
        await _transport.SendAsync(Bencode.Encode(reply), remoteEp, ct);
    }

    #endregion

    #region 迭代查找 (与 KademliaDhtService 共用算法)

    private async Task<IReadOnlyList<NodeInfo>> IterativeFindNodeAsync(
        NodeId targetId, CancellationToken ct, TimeSpan? queryTimeout = null)
    {
        var queried = new HashSet<IPEndPoint>();
        var kClosest = _routingTable.GetClosestContacts(targetId, _alpha).ToList();

        if (kClosest.Count == 0)
        {
            foreach (var ep in _bootstrapNodes)
            {
                kClosest.Add(new NodeInfo
                {
                    NodeId = NodeId.CreateRandom(),
                    EndPoint = ep,
                    // 引导节点是外部数据，长期公钥未知 → 显式 null
                    PublicKey = null
                });
            }
        }

        while (true)
        {
            var toQuery = kClosest
                .Where(n => !queried.Contains(n.EndPoint))
                .Where(n => !IsLocalEndpoint(n.EndPoint))
                .Take(_alpha).ToList();

            if (toQuery.Count == 0) break;

            var results = await Task.WhenAll(toQuery.Select(async node =>
            {
                try { return await KrpcFindNodeAsync(node.EndPoint, targetId, ct, queryTimeout); }
                catch { return new List<NodeInfo>(); }
            }));

            var contacted = false;
            foreach (var nodes in results)
            {
                foreach (var n in nodes)
                {
                    if (!queried.Contains(n.EndPoint) && !IsLocalEndpoint(n.EndPoint) &&
                        !n.NodeId.Equals(LocalNode.NodeId))
                    {
                        contacted = true;
                        _routingTable.AddOrUpdate(n);
                        await NotifyAsync(n, "FindNode");
                    }
                }
            }

            foreach (var n in toQuery)
                queried.Add(n.EndPoint);

            if (!contacted) break;
            kClosest = _routingTable.GetClosestContacts(targetId, _alpha).ToList();
        }

        return _routingTable.GetClosestContacts(targetId, _alpha);
    }

    /// <summary>
    /// 1.3: 对路由表最近 K 个节点发 get_peers(info_hash=targetId)，
    /// 任一应答含匹配 NodeId 的 p2pc_peers/values 条目即返回 NodeInfo。
    /// </summary>
    private async Task<NodeInfo?> ResolveViaGetPeersAsync(NodeId targetId, CancellationToken ct)
    {
        var closest = _routingTable.GetClosestContacts(targetId, AnnounceK);
        if (closest.Count == 0) return null;

        foreach (var n in closest)
        {
            if (IsLocalEndpoint(n.EndPoint)) continue;
            try
            {
                var resp = await KrpcGetPeersAsync(n.EndPoint, targetId, ct);
                if (resp is null) continue;
                var peers = resp.Value.Peers;
                if (peers is null) continue;
                foreach (var (peerIdBytes, ip, port) in peers)
                {
                    // 标准 6 字节 values 不含 NodeId —— 那是「响应者的 NodeId」而非
                    // 「该条目的 NodeId」，因此这里必须显式跳过，不能拿它当身份用。
                    // 扩展字段（p2pc_peers / p2pc_peers6）的条目才自带真实 NodeId。
                    if (peerIdBytes is null || peerIdBytes.Length != NodeId.Size) continue;
                    var peerId = new NodeId(peerIdBytes);
                    if (!peerId.Equals(targetId)) continue;
                    var tcpEp = new IPEndPoint(ip, port);
                    _logger.LogInformation("DHT 解析命中: {NodeId} @ {Tcp} (via {Via})",
                        targetId.ToHexString()[..8], tcpEp, n.EndPoint);
                    return new NodeInfo
                    {
                        NodeId = peerId,
                        EndPoint = tcpEp,
                        DhtEndPoint = n.EndPoint,
                        // 来自 get_peers 应答的外部数据，长期公钥未知 → 显式 null
                        PublicKey = null,
                        LastSeen = DateTime.UtcNow
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "get_peers 解析失败: via {Ep}", n.EndPoint);
            }
        }

        return null;
    }

    /// <summary>
    /// 1.1: 向最近 K 个节点宣告本机的 NodeId → TCP 端口映射。
    /// 每节点：先 get_peers 取 token，再 announce_peer 报本地 TCP 端口。
    /// </summary>
    /// <remarks>
    /// 公开此方法便于测试断言（不必等 15 分钟周期），生产路径由 <see cref="BootstrapAsync"/>
    /// 与 <see cref="RefreshLoopAsync"/> 驱动。
    /// </remarks>
    public async Task AnnounceNowAsync(CancellationToken ct = default) => await TryAnnounceAsync(ct);

    /// <param name="queryTimeout">
    /// 单次 KRPC 超时；为 null 时用 KRPC 通用超时。
    /// 引导阶段传入引导阶段的短超时（宣告是尽力而为，迟到的响应 15 分钟周期刷新会补上）。
    /// </param>
    private async Task TryAnnounceAsync(CancellationToken ct, TimeSpan? queryTimeout = null)
    {
        var infoHash = LocalNode.NodeId;
        var tcpPort = LocalNode.EndPoint.Port;
        var announceHex = infoHash.ToHexString()[..8];

        var closest = _routingTable.GetClosestContacts(infoHash, AnnounceK);
        if (closest.Count == 0)
        {
            _logger.LogDebug("无可宣告节点（路由表为空），跳过 announce_peer");
            return;
        }

        int successCount = 0;
        var tasks = closest.Select(async n =>
        {
            if (IsLocalEndpoint(n.EndPoint)) return;
            try
            {
                // 1) 取 token（同一个对端同一 info_hash 多次宣告应缓存 token，
                //    但此处简化：每次重取。BEP-44 不强制 token 重用）。
                var resp = await KrpcGetPeersAsync(n.EndPoint, infoHash, ct, queryTimeout);
                if (resp is null || resp.Value.Token is null)
                {
                    _logger.LogDebug("announce: 取 token 失败: via {Ep}", n.EndPoint);
                    return;
                }

                // 2) announce_peer
                var ok = await KrpcAnnouncePeerAsync(n.EndPoint, infoHash, resp.Value.Token, tcpPort, ct, queryTimeout);
                if (ok)
                {
                    successCount++;
                    _logger.LogDebug("announce_peer OK: {NodeId} @ {Ep}", announceHex, n.EndPoint);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "announce 失败: via {Ep}", n.EndPoint);
            }
        });

        await Task.WhenAll(tasks);

        lock (_announceLock)
        {
            if (successCount > 0)
            {
                _lastAnnounceUtc = DateTime.UtcNow;
                _announcedNodeCount += successCount;
            }
        }

        if (successCount > 0)
            _logger.LogInformation("宣告完成: {Success}/{Total} 个节点接受了 announce_peer",
                successCount, closest.Count);
        else
            _logger.LogDebug("宣告本轮全部失败: 共尝试 {Total} 个节点", closest.Count);
    }

    #endregion

    #region 辅助方法

    /// <summary>
    /// 构造远端 NodeInfo。
    /// </summary>
    /// <param name="dhtEp">从 KRPC 包源观察到的端点（写入 <see cref="NodeInfo.DhtEndPoint"/>）。</param>
    /// <param name="endPoint">对端用于 TCP 连接的端点（写入 <see cref="NodeInfo.EndPoint"/>）。
    /// 当前 KRPC 路径下与 dhtEp 相同；静态对端/已解析对端走其它路径构造 NodeInfo。</param>
    /// <param name="nodeIdBytes">对端的 20 字节 NodeId。</param>
    private NodeInfo? CreateRemoteNode(IPEndPoint? dhtEp, byte[]? nodeIdBytes, IPEndPoint? endPoint = null)
    {
        if (dhtEp is null || nodeIdBytes is null || nodeIdBytes.Length != NodeId.Size ||
            nodeIdBytes.All(static b => b == 0))
            return null;

        var nodeId = new NodeId(nodeIdBytes);
        if (nodeId.Equals(LocalNode.NodeId) || IsLocalEndpoint(dhtEp))
            return null;

        return new NodeInfo
        {
            NodeId = nodeId,
            EndPoint = endPoint ?? dhtEp,
            DhtEndPoint = dhtEp,
            // KRPC 应答里只有 NodeId + 端点，长期公钥**拿不到**。显式 null 表示「未知」，
            // 而不是用空数组假装「已知的空公钥」—— 后者会让任何
            // NodeId.FromPublicKey(PublicKey) 算出一个看似合法、实则错误的结果。
            // 对端的真实公钥由 GroupChatService 主动握手从已验签的响应信封取回。
            PublicKey = null,
            LastSeen = DateTime.UtcNow
        };
    }

    private string NextTxId()
    {
        var n = Interlocked.Increment(ref _txCounter);
        return $"{(char)('a' + (n / 26 % 26))}{(char)('a' + (n % 26))}";
    }

    private bool IsLocalEndpoint(IPEndPoint ep) =>
        ep.Equals(LocalNode.EndPoint) || ep.Equals(_transport.LocalEndPoint);

    private async Task NotifyAsync(NodeInfo node, string method)
    {
        await _peerChannel.Writer.WriteAsync(
            new PeerDiscoveryEventArgs { NodeInfo = node, DiscoveryMethod = method });
    }

    /// <summary>
    /// 15 分钟周期：桶刷新 + 重新宣告。本地路由表被刷新后，
    /// 上次宣告过的「最近 K 节点」可能已变，需要重新选择并宣告。
    /// </summary>
    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        // 第一次 WaitForNextTick 等待一个完整周期；
        // 引导后的首次宣告由 BootstrapAsync 同步触发。
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                var target = _routingTable.GetStaleBucketTarget();
                if (target.HasValue)
                    await IterativeFindNodeAsync(target.Value, ct);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "刷新循环异常（桶刷新）"); }

            try
            {
                await TryAnnounceAsync(ct);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "刷新循环异常（announce）"); }
        }
    }

    private string EndpointKey(IPEndPoint ep) => $"{ep.Address}:{ep.Port}";

    private void IssueToken(IPEndPoint ep)
    {
        _issuedTokens[EndpointKey(ep)] = RandomToken();
    }

    private byte[] MakeTokenString(IPEndPoint ep)
    {
        if (!_issuedTokens.TryGetValue(EndpointKey(ep), out var t))
        {
            t = RandomToken();
            _issuedTokens[EndpointKey(ep)] = t;
        }
        return t;
    }

    private bool ValidateToken(IPEndPoint ep, byte[] presented)
    {
        return _issuedTokens.TryGetValue(EndpointKey(ep), out var expected)
            && expected.AsSpan().SequenceEqual(presented);
    }

    private static byte[] RandomToken()
    {
        var t = new byte[8];
        Random.Shared.NextBytes(t);
        return t;
    }

    private void RecordAnnounce(NodeId infoHash, byte[] nodeIdBytes, IPEndPoint tcpEp, IPEndPoint dhtEp, IPEndPoint externalEp)
    {
        if (nodeIdBytes.Length != NodeId.Size) return;
        var peerId = new NodeId(nodeIdBytes);
        var key = infoHash.ToHexString();
        _peerCache.AddOrUpdate(key,
            _ => new List<AnnouncedPeer> { new(peerId, tcpEp, dhtEp, externalEp) },
            (_, list) =>
            {
                // 同一对端重复宣告：仅替换；不同对端：追加。
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].NodeId.Equals(peerId))
                    {
                        list[i] = new AnnouncedPeer(peerId, tcpEp, dhtEp, externalEp);
                        return list;
                    }
                }
                list.Add(new AnnouncedPeer(peerId, tcpEp, dhtEp, externalEp));
                return list;
            });
    }

    /// <summary>
    /// 列出当前已收到的对外宣告（按 info_hash 分组聚合）。
    /// 主要供 TUI 状态栏与 SELFTEST 观察使用；不属于 hot-path。
    /// </summary>
    public IReadOnlyList<(NodeId PeerId, IPEndPoint EndPoint, IPEndPoint DhtEndPoint, IPEndPoint? ExternalEndPoint)> ListAnnouncedPeers()
    {
        var result = new List<(NodeId, IPEndPoint, IPEndPoint, IPEndPoint?)>();
        foreach (var kv in _peerCache)
        {
            foreach (var p in kv.Value)
                result.Add((p.NodeId, p.EndPoint, p.DhtEndPoint, p.ExternalEndPoint));
        }
        return result;
    }

    /// <summary>
    /// 将 bencode 列表的 byte[] 条目拼接成一个连续字节数组，按 <paramref name="entrySize"/> 切分。
    /// <para>
    /// <b>必须真的按 <paramref name="entrySize"/> 切分。</b>
    /// 此前本方法收下 <c>entrySize</c> 却无条件调用按 26 字节切分的解析器 ——
    /// 形参在签名里，行为里没有。6 字节的 <c>values</c> 因此被切成错位条目。
    /// 它之所以长期没暴露，是因为那条路径的产物无论如何都过不了「NodeId 必须等于查询目标」
    /// 那一关而被下游丢弃 —— <b>被掩盖的错位解析仍然是错位解析</b>。
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="entrySize"/> 不是受支持的条目长度时抛出。
    /// <b>刻意抛而不是猜</b>：猜错就是静默产出错条目，而这正是本函数要消灭的失败模式。
    /// </exception>
    private static List<(byte[]? NodeId, IPAddress Ip, int Port)> FlattenByteList(List<object> list, int entrySize)
    {
        var flat = FlattenRaw(list);
        return entrySize switch
        {
            Bencode.CompactPeerV4Size => Bencode.ParseCompactPeers26(flat)
                .Select(p => ((byte[]?)p.NodeId, p.Ip, p.Port)).ToList(),
            Bencode.CompactPeerV6Size => Bencode.ParseCompactPeers38(flat)
                .Select(p => ((byte[]?)p.NodeId, p.Ip, p.Port)).ToList(),
            _ => throw new ArgumentException(
                $"不受支持的扩展条目长度 {entrySize}；已知为 {Bencode.CompactPeerV4Size}（IPv4）或 " +
                $"{Bencode.CompactPeerV6Size}（IPv6）。标准的 {Bencode.CompactPeerValueSize} 字节 values " +
                "不含 NodeId，请改用 FlattenValuesList。", nameof(entrySize))
        };
    }

    /// <summary>
    /// 标准 <c>values</c>（6 字节，<b>不含 NodeId</b>）的拼接与解析。
    /// 与 <see cref="FlattenByteList"/> 分开，因为它连字段布局都不同 ——
    /// 放进同一个方法里正是当初那个被忽略的 <c>entrySize</c> 形参的来源。
    /// </summary>
    private static List<(byte[]? NodeId, IPAddress Ip, int Port)> FlattenValuesList(List<object> list)
        => Bencode.ParseCompactValues6(FlattenRaw(list));

    /// <summary>把 bencode list 里各条 <c>byte[]</c> 的内容按顺序拼成一个连续数组。</summary>
    private static byte[] FlattenRaw(List<object> list)
    {
        // Bencode 把每个 byte[] 单独编码；list<object> 里每个 object 都是 byte[]。
        var totalLen = 0;
        foreach (var item in list)
        {
            if (item is byte[] b) totalLen += b.Length;
        }
        var flat = new byte[totalLen];
        int offset = 0;
        foreach (var item in list)
        {
            if (item is byte[] b)
            {
                Buffer.BlockCopy(b, 0, flat, offset, b.Length);
                offset += b.Length;
            }
        }
        return flat;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _peerChannel.Writer.Complete();
    }

    private readonly record struct AnnouncedPeer(NodeId NodeId, IPEndPoint EndPoint, IPEndPoint DhtEndPoint, IPEndPoint? ExternalEndPoint);

    /// <summary>
    /// 本机对外可见的公网入口（由 UPnP / NAT-PMP 探测填充）。无 NAT 或 UPnP 失败时为 null。
    /// </summary>
    private IPEndPoint? _localExternalEndPoint;

    /// <summary>当前 NAT 映射状态；程序启动时由 <c>Program.cs</c> 通过 <see cref="ApplyMapping"/> 设置。</summary>
    public NatMappingState NatMappingState { get; private set; } = NatMappingState.NotAttempted;

    /// <summary>UPnP 探测到的本机公网入口；未成功时为 null。</summary>
    public IPEndPoint? LocalExternalEndPoint => _localExternalEndPoint;

    /// <summary>
    /// 由 <c>Program.cs</c> 在启动阶段调用：把 UPnP 探测结果应用到本机节点，
    /// 并在状态栏 / SELFTEST 输出反映出来。重复调用覆盖（热重载场景）。
    /// </summary>
    public void ApplyMapping(UpnpMapping? mapping)
    {
        if (mapping is null)
        {
            _localExternalEndPoint = null;
            NatMappingState = NatMappingState.Unavailable;
            return;
        }
        _localExternalEndPoint = mapping.ExternalEndPoint;
        NatMappingState = NatMappingState.Mapped;
        // UPnP 成功后立刻补一次宣告 —— 让公共 DHT 上的对端尽快看到我们的公网入口。
        _ = AnnounceNowAsync(CancellationToken.None);
    }

    #endregion
}