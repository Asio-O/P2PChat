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
/// Mainline DHT 服务 (BitTorrent Kademlia KRPC协议)
/// 使用 bencode 编码 + UDP 传输, 兼容公开DHT引导节点
/// </summary>
public class MainlineDhtService : IDhtService, IDisposable
{
    private readonly IRoutingTable _routingTable;
    private readonly IUdpTransport _transport;
    private readonly ILogger<MainlineDhtService> _logger;
    private readonly Channel<PeerDiscoveryEventArgs> _peerChannel;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Dictionary<string, object>>> _pending = new();
    private readonly List<IPEndPoint> _bootstrapNodes;
    private readonly int _alpha;
    private int _txCounter;
    private bool _disposed;

    public NodeInfo LocalNode { get; }
    public IAsyncEnumerable<PeerDiscoveryEventArgs> OnPeerDiscovered => _peerChannel.Reader.ReadAllAsync();

    public MainlineDhtService(
        NodeInfo localNode,
        IRoutingTable routingTable,
        IUdpTransport transport,
        ILogger<MainlineDhtService> logger,
        IReadOnlyList<IPEndPoint> bootstrapNodes,
        int alpha = 3)
    {
        LocalNode = localNode;
        _routingTable = routingTable;
        _transport = transport;
        _logger = logger;
        _bootstrapNodes = bootstrapNodes.ToList();
        _alpha = alpha;
        _peerChannel = Channel.CreateUnbounded<PeerDiscoveryEventArgs>();
    }

    /// <inheritdoc />
    public async Task BootstrapAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Mainline DHT引导开始, {Count}个引导节点", _bootstrapNodes.Count);
        _routingTable.AddOrUpdate(LocalNode);

        foreach (var ep in _bootstrapNodes)
        {
            try
            {
                _logger.LogDebug("PING引导节点: {Ep}", ep);
                var bootstrapNode = await KrpcPingAsync(ep, ct);
                if (bootstrapNode is not null)
                {
                    // PING响应中的id是引导节点的真实NodeId；先入表，后续FIND_NODE才能从已知节点开始。
                    _routingTable.AddOrUpdate(bootstrapNode);
                    _logger.LogInformation("引导节点 {Ep} 响应成功", ep);
                    var contacts = await IterativeFindNodeAsync(LocalNode.NodeId, ct);
                    _logger.LogInformation("引导完成, 发现 {Count} 个节点", contacts.Count);
                    foreach (var n in contacts)
                    {
                        _routingTable.AddOrUpdate(n);
                        await NotifyAsync(n, "Bootstrap");
                    }
                    break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "引导节点 {Ep} 无响应 (可能协议不同)", ep);
            }
        }

        _logger.LogInformation("DHT引导完成, 路由表: {Count}节点", _routingTable.GetAllContacts().Count);
        _ = RefreshLoopAsync(ct);
    }

    /// <inheritdoc />
    public async Task<NodeInfo?> FindNodeAsync(NodeId targetId, CancellationToken ct = default)
    {
        var contacts = await IterativeFindNodeAsync(targetId, ct);
        return contacts.FirstOrDefault(c => c.NodeId.Equals(targetId));
    }

    /// <inheritdoc />
    public Task StoreAsync(byte[] key, byte[] value, CancellationToken ct = default)
    {
        // Mainline DHT的store用于announce_peer, 我们使用自定义TCP协议存储
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<byte[]?> FindValueAsync(byte[] key, CancellationToken ct = default)
    {
        // 由上层TCP协议处理值查找
        return Task.FromResult<byte[]?>(null);
    }

    /// <inheritdoc />
    public async Task<bool> PingAsync(NodeInfo node, CancellationToken ct = default)
    {
        try
        {
            var respondingNode = await KrpcPingAsync(node.EndPoint, ct);
            if (respondingNode is null) return false;

            // 标准Kademlia行为：成功探测的已知节点也要刷新/加入路由表。
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
    /// 开始接收DHT消息循环
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
                _logger.LogError(ex, "DHT接收异常");
            }
        }
    }

    #region KRPC 协议实现

    private async Task<NodeInfo?> KrpcPingAsync(IPEndPoint ep, CancellationToken ct)
    {
        var response = await KrpcQueryAsync(ep, "ping", new Dictionary<string, object>(), ct);
        if (response is null || !response.TryGetValue("id", out var idObj) || idObj is not byte[] idBytes)
            return null;

        return CreateRemoteNode(ep, idBytes);
    }

    private async Task<IReadOnlyList<NodeInfo>> KrpcFindNodeAsync(IPEndPoint ep, NodeId target, CancellationToken ct)
    {
        var args = new Dictionary<string, object>
        {
            ["id"] = LocalNode.NodeId.ToByteArray(),
            ["target"] = target.ToByteArray()
        };
        var response = await KrpcQueryAsync(ep, "find_node", args, ct);
        if (response == null) return [];

        // 解析nodes字段 (紧凑格式)
        if (response.TryGetValue("nodes", out var nodesObj) && nodesObj is byte[] nodesData)
        {
            return Bencode.ParseCompactNodes(nodesData)
                .Select(n => CreateRemoteNode(n.Ip is null ? null : new IPEndPoint(n.Ip, n.Port), n.NodeId))
                .OfType<NodeInfo>()
                .ToList();
        }

        return [];
    }

    private async Task<Dictionary<string, object>?> KrpcQueryAsync(
        IPEndPoint ep, string query, Dictionary<string, object> args, CancellationToken ct)
    {
        var txId = NextTxId();
        args["id"] = LocalNode.NodeId.ToByteArray();

        var msg = new Dictionary<string, object>
        {
            ["t"] = Encoding.UTF8.GetBytes(txId),
            ["y"] = Encoding.UTF8.GetBytes("q"),
            ["q"] = Encoding.UTF8.GetBytes(query),
            ["a"] = args
        };

        var tcs = new TaskCompletionSource<Dictionary<string, object>>();
        _pending[txId] = tcs;

        await _transport.SendAsync(Bencode.Encode(msg), ep, ct);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            return await tcs.Task.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogTrace("KRPC超时: {Query} -> {Ep}", query, ep);
            return null;
        }
        finally
        {
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
                // 响应消息: 通知等待者
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
            _logger.LogTrace(ex, "解析KRPC消息失败 来自 {Ep}", remoteEp);
        }
    }

    private async Task HandleQueryAsync(string query, Dictionary<string, object> args,
        string txId, IPEndPoint remoteEp, CancellationToken ct)
    {
        // 获取发送者节点ID
        byte[]? senderId = null;
        if (args.TryGetValue("id", out var idObj) && idObj is byte[] idBytes && idBytes.Length == 20)
            senderId = idBytes;

        var remoteNode = CreateRemoteNode(remoteEp, senderId);
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
                break; // 只需返回id

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
                // 我们不是BitTorrent客户端, 返回最近节点即可
                if (args.TryGetValue("info_hash", out var ihObj) && ihObj is byte[] ihBytes && ihBytes.Length == 20)
                {
                    var ih = new NodeId(ihBytes);
                    var closest = _routingTable.GetClosestContacts(ih, 8);
                    var nodesBytes = closest
                        .Select(n => Bencode.EncodeCompactNode(
                            n.NodeId.ToByteArray(), n.EndPoint.Address, n.EndPoint.Port))
                        .SelectMany(b => b)
                        .ToArray();
                    response["nodes"] = nodesBytes;
                    response["token"] = Encoding.UTF8.GetBytes("p2pchat");
                }
                break;

            case "announce_peer":
                // 不实现, 但返回成功响应避免被拉黑
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

    #region 迭代查找 (与KademliaDhtService共用算法)

    private async Task<IReadOnlyList<NodeInfo>> IterativeFindNodeAsync(NodeId targetId, CancellationToken ct)
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
                    PublicKey = Array.Empty<byte>()
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
                try { return await KrpcFindNodeAsync(node.EndPoint, targetId, ct); }
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

    #endregion

    #region 辅助方法

    private NodeInfo? CreateRemoteNode(IPEndPoint? ep, byte[]? nodeIdBytes)
    {
        if (ep is null || nodeIdBytes is null || nodeIdBytes.Length != NodeId.Size ||
            nodeIdBytes.All(static b => b == 0))
            return null;

        var nodeId = new NodeId(nodeIdBytes);
        if (nodeId.Equals(LocalNode.NodeId) || IsLocalEndpoint(ep))
            return null;

        return new NodeInfo
        {
            NodeId = nodeId,
            EndPoint = ep,
            PublicKey = Array.Empty<byte>(),
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

    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                var target = _routingTable.GetStaleBucketTarget();
                if (target.HasValue)
                    await IterativeFindNodeAsync(target.Value, ct);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "刷新循环异常"); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _peerChannel.Writer.Complete();
    }

    #endregion
}
