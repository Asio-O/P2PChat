using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using P2PChat.Core.Abstractions;
using P2PChat.Core.Models;

namespace P2PChat.Chat.Mesh;

/// <summary>
/// mesh 拓扑维护 —— 全连接 mesh 的连接维持循环。
/// <para>
/// 数据平面从「按需直连」改为「网状常驻」：每轮把<b>已知但无活跃连接</b>的节点补建 TCP 连接，
/// 让消息泛洪始终有邻居可走。连接本身复用 <see cref="IMessageRouter.GetOrCreateConnectionAsync"/>
/// （幂等：池中已有活跃连接直接返回），断线由下一轮维护自然重连。
/// </para>
/// <para>
/// 不做握手：mesh 连接只是 TCP 通道，会话密钥仍在发送消息时按需协商（ChatService.EnsureSessionKeyAsync）。
/// 不做发现：节点候选来自注入的静态种子与 DHT 路由表，发现平面（Mainline DHT）保持不变。
/// </para>
/// <para>
/// 见 2026-10-09-mesh-topology-and-flooding。
/// </para>
/// </summary>
public sealed class MeshTopologyService
{
    /// <summary>单轮内并发建连上限 —— 防止首轮对全部已知节点同时建连（建连风暴）。</summary>
    private const int MaxConcurrentConnects = 4;

    private readonly IDhtService _dht;
    private readonly IMessageRouter _router;
    private readonly IReadOnlyList<NodeInfo> _seedPeers;
    private readonly TimeSpan _interval;
    private readonly ILogger<MeshTopologyService> _logger;

    /// <summary>上一轮建连失败的对端 → 失败时间。失败者跳到下一轮才重试（轮间隔即退避粒度）。</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastFailed = new(StringComparer.Ordinal);

    /// <param name="seedPeers">
    /// 启动时已知的静态候选（配置引导节点之外、如手工登记的联系人端点）。
    /// 运行期新增的静态对端不在此列 —— 它们仍走按需直连，入站方向由
    /// MessageRouter 的入站邻居登记接管；见 ADR 的 Deferred 一节。
    /// </param>
    public MeshTopologyService(
        IDhtService dht,
        IMessageRouter router,
        IReadOnlyList<NodeInfo> seedPeers,
        MeshOptions options,
        ILogger<MeshTopologyService> logger)
    {
        _dht = dht ?? throw new ArgumentNullException(nameof(dht));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _seedPeers = seedPeers ?? throw new ArgumentNullException(nameof(seedPeers));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (options.MaintainIntervalSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), options.MaintainIntervalSeconds,
                "mesh 维护轮间隔必须为正秒数");
        _interval = TimeSpan.FromSeconds(options.MaintainIntervalSeconds);
    }

    /// <summary>
    /// 启动维护循环：立即跑一轮，然后每 <see cref="_interval"/> 一轮，直到取消。
    /// 取消是正常停机路径，不作为异常逃逸。
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        try
        {
            // 启动即首轮：不等一个轮间隔才建第一批连接。
            await TickAsync(ct);

            using var timer = new PeriodicTimer(_interval);
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    await TickAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 单轮失败（如 DHT 瞬时不可用）不结束循环 —— 维护的契约是持续尽力，不是一次到位。
                    _logger.LogWarning(ex, "mesh 维护轮异常，下一轮继续");
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("mesh 维护循环已停止");
        }
    }

    /// <summary>
    /// 跑一轮维护：收集候选节点，对无活跃连接者补建连接（并发上限
    /// <see cref="MaxConcurrentConnects"/>，上一轮失败者在下一轮前被跳过）。
    /// 公开为普通方法以便测试不经真实等待直接驱动。
    /// </summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        var localKey = Convert.ToHexString(_dht.LocalNode.NodeId.ToByteArray());

        // 种子（启动期静态候选）+ DHT 路由表（运行期发现），按池键去重、排除本机。
        var candidates = _seedPeers
            .Concat(_dht.GetAllKnownNodes())
            .Where(n => n.NodeId.ToByteArray().Length == NodeId.Size)
            .GroupBy(n => Convert.ToHexString(n.NodeId.ToByteArray()))
            .Where(g => g.Key != localKey)
            .Select(g => g.First())
            .ToList();

        var now = DateTimeOffset.UtcNow;
        var due = new List<NodeInfo>();
        foreach (var node in candidates)
        {
            var key = Convert.ToHexString(node.NodeId.ToByteArray());
            if (_lastFailed.TryGetValue(key, out var failedAt) && now - failedAt < _interval)
                continue; // 上轮刚失败，退避到下一轮。
            due.Add(node);
        }

        if (due.Count == 0)
            return;

        _logger.LogDebug("mesh 维护轮: 候选={Candidates}, 待建连={Due}", candidates.Count, due.Count);

        using var gate = new SemaphoreSlim(MaxConcurrentConnects, MaxConcurrentConnects);
        await Task.WhenAll(due.Select(node => ConnectOneAsync(node, gate, ct)));
    }

    private async Task ConnectOneAsync(NodeInfo node, SemaphoreSlim gate, CancellationToken ct)
    {
        var key = Convert.ToHexString(node.NodeId.ToByteArray());
        await gate.WaitAsync(ct);
        try
        {
            // GetOrCreateConnectionAsync 幂等：已有活跃连接直接返回，不会重复建连。
            await _router.GetOrCreateConnectionAsync(node, ct);
            _lastFailed.TryRemove(key, out _);
            _logger.LogDebug("mesh 连接就绪: Peer={Peer} @ {EndPoint}",
                key[..8], node.EndPoint);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _lastFailed[key] = DateTimeOffset.UtcNow;
            _logger.LogDebug(ex, "mesh 建连失败（下一轮重试）: Peer={Peer} @ {EndPoint}",
                key[..8], node.EndPoint);
        }
        finally
        {
            gate.Release();
        }
    }
}
