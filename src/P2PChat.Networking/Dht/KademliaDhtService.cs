using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using MessagePack;
using Microsoft.Extensions.Logging;
using P2PChat.Core.Abstractions;
using P2PChat.Core.Enums;
using P2PChat.Core.Models;
using P2PChat.Networking.Transport;

namespace P2PChat.Networking.Dht;

/// <summary>
/// Kademlia DHT 服务实现
/// 实现 IDhtService: Bootstrap、FindNode、Store、FindValue、Ping、节点发现事件
/// </summary>
public class KademliaDhtService : IDhtService, IDisposable
{
    private readonly IRoutingTable _routingTable;
    private readonly IUdpTransport _transport;
    private readonly ISerializer _serializer;
    private readonly ILogger<KademliaDhtService> _logger;
    private readonly Channel<PeerDiscoveryEventArgs> _peerChannel;
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<DhtRpcMessage>> _pendingRequests = new();
    /// <summary>静态对端（手工添加的联系人）—— 端点已知，优先于 DHT 查找。</summary>
    private readonly ConcurrentDictionary<string, NodeInfo> _staticPeers = new();
    private readonly List<IPEndPoint> _bootstrapNodes;
    private readonly int _alpha; // 并行度
    private uint _requestIdCounter;
    private bool _disposed;

    public NodeInfo LocalNode { get; }
    public IAsyncEnumerable<PeerDiscoveryEventArgs> OnPeerDiscovered => _peerChannel.Reader.ReadAllAsync();

    public KademliaDhtService(
        NodeInfo localNode,
        IRoutingTable routingTable,
        IUdpTransport transport,
        ISerializer serializer,
        ILogger<KademliaDhtService> logger,
        IReadOnlyList<IPEndPoint> bootstrapNodes,
        int alpha = 3)
    {
        LocalNode = localNode;
        _routingTable = routingTable;
        _transport = transport;
        _serializer = serializer;
        _logger = logger;
        _bootstrapNodes = bootstrapNodes.ToList();
        _alpha = alpha;
        _peerChannel = Channel.CreateUnbounded<PeerDiscoveryEventArgs>();
    }

    /// <inheritdoc />
    public async Task BootstrapAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("开始DHT引导, 引导节点数: {Count}", _bootstrapNodes.Count);

        // 将自己加入路由表
        _routingTable.AddOrUpdate(LocalNode);

        // 并发的FIND_NODE向引导节点查找自己
        foreach (var bootstrapEp in _bootstrapNodes)
        {
            try
            {
                _logger.LogDebug("向引导节点 {Endpoint} 发送FIND_NODE", bootstrapEp);
                var contacts = await IterativeFindNodeAsync(LocalNode.NodeId, ct);
                _logger.LogInformation("引导完成, 发现 {Count} 个节点", contacts.Count);

                foreach (var node in contacts)
                {
                    _routingTable.AddOrUpdate(node);
                    await NotifyPeerDiscoveredAsync(node, "Bootstrap");
                }
                break; // 引导成功
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "引导节点 {Endpoint} 连接失败", bootstrapEp);
            }
        }

        _logger.LogInformation("DHT引导完成, 路由表节点数: {Count}", _routingTable.GetAllContacts().Count);

        // 启动定期刷新
        _ = RefreshLoopAsync(ct);
    }

    /// <inheritdoc />
    public async Task<NodeInfo?> FindNodeAsync(NodeId targetId, CancellationToken ct = default)
    {
        // 静态对端优先（手工添加的联系人），与 MainlineDhtService 语义一致。
        if (_staticPeers.TryGetValue(targetId.ToHexString(), out var known))
        {
            _logger.LogDebug("命中静态对端: {NodeId} @ {EndPoint}",
                targetId.ToHexString()[..8], known.EndPoint);
            return known;
        }

        var contacts = await IterativeFindNodeAsync(targetId, ct);
        return contacts.FirstOrDefault(c => c.NodeId.Equals(targetId));
    }

    /// <inheritdoc />
    public void RegisterStaticPeer(NodeInfo node)
    {
        ArgumentNullException.ThrowIfNull(node);

        _staticPeers[node.NodeId.ToHexString()] = node;
        _routingTable.AddOrUpdate(node);
        _logger.LogInformation("登记静态对端: {NodeId} @ {EndPoint}",
            node.NodeId.ToHexString()[..8], node.EndPoint);
    }

    /// <inheritdoc />
    public async Task StoreAsync(byte[] key, byte[] value, CancellationToken ct = default)
    {
        var keyNodeId = new NodeId(key.Take(NodeId.Size).ToArray());
        var closestNodes = _routingTable.GetClosestContacts(keyNodeId, _alpha);

        _logger.LogDebug("STORE 键 {Key} 到 {Count} 个最近节点", Convert.ToHexString(key).ToLower(), closestNodes.Count);

        var tasks = closestNodes.Select(node => SendStoreAsync(node, key, value, ct));
        await Task.WhenAll(tasks);
    }

    /// <inheritdoc />
    public async Task<byte[]?> FindValueAsync(byte[] key, CancellationToken ct = default)
    {
        var keyNodeId = new NodeId(key.Take(NodeId.Size).ToArray());
        var closestNodes = _routingTable.GetClosestContacts(keyNodeId, _alpha);

        foreach (var node in closestNodes)
        {
            try
            {
                var response = await SendFindValueAsync(node, key, ct);
                // 如果响应中包含值 (而非联系人列表)
                var value = response.Payload;
                if (value != null && value.Length > 0 && response.Payload.Length > 0)
                {
                    // 检查第一个字节标记是否有值: 1=value, 0=contacts
                    if (value[0] == 1)
                        return value[1..];
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "FIND_VALUE 失败: {Node}", node.EndPoint);
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<bool> PingAsync(NodeInfo node, CancellationToken ct = default)
    {
        var requestId = NextRequestId();
        try
        {
            var request = new DhtRpcMessage
            {
                MessageType = DhtMessageType.Ping,
                RequestId = requestId,
                SenderId = LocalNode.NodeId.ToByteArray(),
                Payload = Array.Empty<byte>()
            };

            var tcs = new TaskCompletionSource<DhtRpcMessage>();
            _pendingRequests[requestId] = tcs;

            await _transport.SendAsync(request.ToBytes(), node.EndPoint, ct);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            try
            {
                await tcs.Task.WaitAsync(linkedCts.Token);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PING 失败: {Node}", node.EndPoint);
            return false;
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<NodeInfo> GetAllKnownNodes() => _routingTable.GetAllContacts();

    /// <summary>
    /// 开始接收DHT消息的后台循环
    /// </summary>
    public async Task StartReceivingAsync(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var packet = await _transport.ReceiveAsync(ct);
                _ = HandleIncomingRpcAsync(packet.Data, packet.RemoteEndPoint, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException ex) when (UdpSocketErrorClassifier.IsExpectedPeerError(ex))
            {
                // UDP 收到 ICMP port-unreachable 可能表现为 ConnectionReset (10054)。
                // 这是正常的对端不可达反馈，降级记录并保持接收循环运行。
                _logger.LogDebug(ex, "DHT UDP 对端不可达，继续接收: {ErrorCode}", ex.SocketErrorCode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DHT接收循环异常");
            }
        }
    }

    #region 内部方法

    /// <summary>
    /// Kademlia迭代节点查找算法
    /// </summary>
    private async Task<IReadOnlyList<NodeInfo>> IterativeFindNodeAsync(NodeId targetId, CancellationToken ct)
    {
        var queriedNodes = new HashSet<NodeId>();
        var kClosest = _routingTable.GetClosestContacts(targetId, _alpha).ToList();

        if (kClosest.Count == 0)
        {
            // 路由表为空, 尝试引导节点
            foreach (var ep in _bootstrapNodes)
            {
                kClosest.Add(new NodeInfo
                {
                    NodeId = NodeId.CreateRandom(), // 暂时随机
                    EndPoint = ep,
                    // 引导节点是外部数据，长期公钥未知 → 显式 null
                    PublicKey = null
                });
            }
        }

        while (true)
        {
            // 取alpha个未查询的节点 (排除本地端点)
            var toQuery = kClosest
                .Where(n => !queriedNodes.Contains(n.NodeId))
                .Where(n => !IsLocalEndpoint(n.EndPoint))
                .Take(_alpha).ToList();
            if (toQuery.Count == 0) break;

            var results = await Task.WhenAll(toQuery.Select(async node =>
            {
                try { return await SendFindNodeAsync(node, targetId, ct); }
                catch { return new List<NodeInfo>(); }
            }));

            var contacted = false;
            foreach (var resultNodes in results)
            {
                foreach (var foundNode in resultNodes)
                {
                    if (!queriedNodes.Contains(foundNode.NodeId) && !foundNode.NodeId.Equals(LocalNode.NodeId))
                    {
                        contacted = true;
                        _routingTable.AddOrUpdate(foundNode);
                        await NotifyPeerDiscoveredAsync(foundNode, "FindNode");
                    }
                }
            }

            foreach (var node in toQuery)
                queriedNodes.Add(node.NodeId);

            if (!contacted) break;

            // 更新k个最近的节点
            kClosest = _routingTable.GetClosestContacts(targetId, _alpha).ToList();
        }

        return _routingTable.GetClosestContacts(targetId, _alpha);
    }

    private async Task<IReadOnlyList<NodeInfo>> SendFindNodeAsync(NodeInfo node, NodeId targetId, CancellationToken ct)
    {
        var payload = _serializer.Serialize(targetId.ToByteArray());
        var response = await SendRpcAsync(node, DhtMessageType.FindNode, payload, ct);
        return _serializer.Deserialize<List<NodeInfoDto>>(response.Payload)
            .Select(dto => new NodeInfo
            {
                NodeId = new NodeId(dto.NodeId),
                EndPoint = new IPEndPoint(IPAddress.Parse(dto.IpAddress), dto.Port),
                PublicKey = dto.PublicKey,
                LastSeen = DateTime.UtcNow
            })
            .ToList();
    }

    private async Task SendStoreAsync(NodeInfo node, byte[] key, byte[] value, CancellationToken ct)
    {
        var payload = new StoreRequest { Key = key, Value = value };
        var payloadBytes = _serializer.Serialize(payload);
        await SendRpcAsync(node, DhtMessageType.Store, payloadBytes, ct);
    }

    private async Task<DhtRpcMessage> SendFindValueAsync(NodeInfo node, byte[] key, CancellationToken ct)
    {
        var payload = _serializer.Serialize(key);
        return await SendRpcAsync(node, DhtMessageType.FindValue, payload, ct);
    }

    private async Task<DhtRpcMessage> SendRpcAsync(NodeInfo node, DhtMessageType messageType, byte[] payload, CancellationToken ct)
    {
        var requestId = NextRequestId();
        var request = new DhtRpcMessage
        {
            MessageType = messageType,
            RequestId = requestId,
            SenderId = LocalNode.NodeId.ToByteArray(),
            Payload = payload
        };

        var tcs = new TaskCompletionSource<DhtRpcMessage>();
        _pendingRequests[requestId] = tcs;

        await _transport.SendAsync(request.ToBytes(), node.EndPoint, ct);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            var response = await tcs.Task.WaitAsync(linkedCts.Token);
            return response;
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    private async Task HandleIncomingRpcAsync(byte[] data, IPEndPoint remoteEp, CancellationToken ct)
    {
        try
        {
            var message = DhtRpcMessage.FromBytes(data);

            // 更新发送者路由信息
            var remoteNodeId = new NodeId(message.SenderId);
            var remoteNode = new NodeInfo
            {
                NodeId = remoteNodeId,
                EndPoint = remoteEp,
                PublicKey = null,   // DHT 层不关心公钥：KRPC 应答里拿不到，显式 null 表示未知
                LastSeen = DateTime.UtcNow
            };
            _routingTable.AddOrUpdate(remoteNode);

            // 检查是否是待处理请求的响应 — 是则直接返回，不做请求处理
            if (_pendingRequests.TryGetValue(message.RequestId, out var tcs))
            {
                tcs.TrySetResult(message);
                return;
            }

            // 否则作为新请求处理
            switch (message.MessageType)
            {
                case DhtMessageType.Ping:
                    await HandlePingAsync(message, remoteEp, ct);
                    break;
                case DhtMessageType.FindNode:
                    await HandleFindNodeAsync(message, remoteEp, ct);
                    break;
                case DhtMessageType.Store:
                    await HandleStoreAsync(message, remoteEp, ct);
                    break;
                case DhtMessageType.FindValue:
                    await HandleFindValueAsync(message, remoteEp, ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "处理DHT RPC消息异常 来自 {Endpoint}", remoteEp);
        }
    }

    private Task HandlePingAsync(DhtRpcMessage request, IPEndPoint remoteEp, CancellationToken ct)
    {
        var response = new DhtRpcMessage
        {
            MessageType = DhtMessageType.Ping,
            RequestId = request.RequestId,
            SenderId = LocalNode.NodeId.ToByteArray(),
            Payload = Array.Empty<byte>()
        };
        return _transport.SendAsync(response.ToBytes(), remoteEp, ct);
    }

    private async Task HandleFindNodeAsync(DhtRpcMessage request, IPEndPoint remoteEp, CancellationToken ct)
    {
        var targetIdBytes = _serializer.Deserialize<byte[]>(request.Payload);
        var targetId = new NodeId(targetIdBytes);
        var closest = _routingTable.GetClosestContacts(targetId, _alpha);

        var dtos = closest.Select(n => new NodeInfoDto
        {
            NodeId = n.NodeId.ToByteArray(),
            IpAddress = n.EndPoint.Address.ToString(),
            Port = n.EndPoint.Port,
            PublicKey = n.PublicKey
        }).ToList();

        var response = new DhtRpcMessage
        {
            MessageType = DhtMessageType.FindNode,
            RequestId = request.RequestId,
            SenderId = LocalNode.NodeId.ToByteArray(),
            Payload = _serializer.Serialize(dtos)
        };
        await _transport.SendAsync(response.ToBytes(), remoteEp, ct);
    }

    private Task HandleStoreAsync(DhtRpcMessage request, IPEndPoint remoteEp, CancellationToken ct)
    {
        // 简单实现: 存储到本地内存字典
        var storeRequest = _serializer.Deserialize<StoreRequest>(request.Payload);
        StoredValues[Convert.ToHexString(storeRequest.Key)] = storeRequest.Value;

        var response = new DhtRpcMessage
        {
            MessageType = DhtMessageType.Store,
            RequestId = request.RequestId,
            SenderId = LocalNode.NodeId.ToByteArray(),
            Payload = Array.Empty<byte>()
        };
        return _transport.SendAsync(response.ToBytes(), remoteEp, ct);
    }

    private Task HandleFindValueAsync(DhtRpcMessage request, IPEndPoint remoteEp, CancellationToken ct)
    {
        var keyBytes = _serializer.Deserialize<byte[]>(request.Payload);
        var key = Convert.ToHexString(keyBytes);
        byte[] payload;

        if (StoredValues.TryGetValue(key, out var value))
        {
            // 有值: [1] + value
            payload = new byte[1 + value.Length];
            payload[0] = 1;
            Array.Copy(value, 0, payload, 1, value.Length);
        }
        else
        {
            // 无值: [0] + contacts
            var keyNodeId = new NodeId(keyBytes.Take(NodeId.Size).ToArray());
            var closest = _routingTable.GetClosestContacts(keyNodeId, _alpha);
            var dtos = closest.Select(n => new NodeInfoDto
            {
                NodeId = n.NodeId.ToByteArray(),
                IpAddress = n.EndPoint.Address.ToString(),
                Port = n.EndPoint.Port,
                PublicKey = n.PublicKey
            }).ToList();
            var contactsData = _serializer.Serialize(dtos);
            payload = new byte[1 + contactsData.Length];
            payload[0] = 0;
            Array.Copy(contactsData, 0, payload, 1, contactsData.Length);
        }

        var response = new DhtRpcMessage
        {
            MessageType = DhtMessageType.FindValue,
            RequestId = request.RequestId,
            SenderId = LocalNode.NodeId.ToByteArray(),
            Payload = payload
        };
        return _transport.SendAsync(response.ToBytes(), remoteEp, ct);
    }

    private async Task NotifyPeerDiscoveredAsync(NodeInfo node, string method)
    {
        var args = new PeerDiscoveryEventArgs { NodeInfo = node, DiscoveryMethod = method };
        await _peerChannel.Writer.WriteAsync(args);
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
                {
                    _logger.LogDebug("刷新桶节点...");
                    await IterativeFindNodeAsync(target.Value, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "刷新循环异常");
            }
        }
    }

    private uint NextRequestId() => Interlocked.Increment(ref _requestIdCounter);

    private bool IsLocalEndpoint(System.Net.IPEndPoint ep) =>
        ep.Equals(LocalNode.EndPoint) || ep.Equals(_transport.LocalEndPoint);

    private ConcurrentDictionary<string, byte[]> StoredValues { get; } = new();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _peerChannel.Writer.Complete();
    }

    #endregion
}

/// <summary>
/// DHT节点信息DTO (用于序列化)
/// </summary>
[MessagePackObject]
public record NodeInfoDto
{
    [Key(0)] public required byte[] NodeId { get; init; }
    [Key(1)] public required string IpAddress { get; init; }
    [Key(2)] public int Port { get; init; }
    /// <remarks>set 而非 init：init 属性 + 初始化器会在反序列化时被重置为 default(MsgPack017)。</remarks>
    // 可空：NodeInfo.PublicKey 现在用 null 显式表示「未知」。DHT 应答里本来就拿不到长期公钥，
    // 用 byte[] 强制非空会逼调用点编造一个空数组（那会让 NodeId.FromPublicKey 算出错误结果）。
    [Key(3)] public byte[]? PublicKey { get; set; }
}

/// <summary>
/// DHT STORE请求
/// </summary>
[MessagePackObject]
public record StoreRequest
{
    [Key(0)] public required byte[] Key { get; init; }
    [Key(1)] public required byte[] Value { get; init; }
}
