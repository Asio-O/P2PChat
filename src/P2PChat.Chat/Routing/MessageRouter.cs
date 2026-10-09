using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using P2PChat.Core.Abstractions;
using P2PChat.Core.Enums;
using P2PChat.Core.Extensions;
using P2PChat.Core.Models;

namespace P2PChat.Chat.Routing;

/// <summary>
/// 消息路由器 — 插件式消息分发中枢
/// 负责: 处理器注册、入站消息分发、出站消息发送、连接池管理、出站签名 + 入站验签
/// </summary>
public class MessageRouter : IMessageRouter
{
    private readonly ITcpTransport _tcpTransport;
    private readonly ISerializer _serializer;
    private readonly IEncryptionService _encryption;
    private readonly IKeyStore _keyStore;
    private readonly IReplayGuard _replayGuard;
    private readonly bool _floodingEnabled;
    private readonly ILogger<MessageRouter> _logger;
    private readonly ConcurrentDictionary<MessageType, IMessageHandler> _handlers = new();
    private readonly ConcurrentDictionary<string, ITcpConnection> _connectionPool = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<Message>> _pendingResponses = new();

    // 历史遗留：这里曾有一个 `private const int MaxConnectionsPerPeer = 3;`，
    // 但**从未有任何逻辑读取它** —— 「每对端最多 3 条连接」在今天并不成立，
    // 留着它只会误导下一个人以为该上限真实存在（Agent.md 也曾照抄这段）。
    // 真要实现连接上限应当作为独立任务设计（淘汰哪一条、如何通知在途请求），
    // 不能靠一个从不生效的常量假装已实现，故删除。

    private uint _seqCounter;

    /// <param name="replayGuard">
    /// 重放防护，<b>必填</b>。刻意不给默认值：写成 <c>IReplayGuard? replayGuard = null</c> 会让
    /// 「忘记注入」静默等于「关闭防护」—— 这是安全陷阱，必须让漏配变成编译错误。
    /// </param>
    /// <param name="floodingEnabled">
    /// mesh 泛洪开关（P2PChat:Mesh:FloodingEnabled，默认开）。关闭时入站消息只做本地处理、
    /// 不向其它邻居转发 —— 作为行为对照与回退阀，不是安全边界。
    /// </param>
    public MessageRouter(
        ITcpTransport tcpTransport,
        ISerializer serializer,
        IEncryptionService encryption,
        IKeyStore keyStore,
        IReplayGuard replayGuard,
        ILogger<MessageRouter> logger,
        bool floodingEnabled = true)
    {
        _tcpTransport = tcpTransport;
        _serializer = serializer;
        _encryption = encryption;
        _keyStore = keyStore;
        _replayGuard = replayGuard ?? throw new ArgumentNullException(nameof(replayGuard));
        _floodingEnabled = floodingEnabled;
        _logger = logger;
    }

    /// <inheritdoc />
    public void RegisterHandler<T>(IMessageHandler<T> handler) where T : Message
    {
        _handlers[handler.MessageType] = handler;
        _logger.LogInformation("注册消息处理器: {Type} -> {Handler}",
            handler.MessageType, handler.GetType().Name);
    }

    /// <inheritdoc />
    public void UnregisterHandler(MessageType messageType)
    {
        _handlers.TryRemove(messageType, out _);
        _logger.LogInformation("移除消息处理器: {Type}", messageType);
    }

    /// <inheritdoc />
    public async Task RouteIncomingAsync(MessageEnvelope envelope, ITcpConnection sender, CancellationToken ct = default)
    {
        _logger.LogTrace("入站消息: Type={Type}, From={Sender}",
            envelope.MessageType, Convert.ToHexString(envelope.SenderId).ToLower()[..8]);

        // 签名/身份校验：见 2026-09-21-message-signing。
        // 1) 缺签名/缺公钥 → 拒绝
        // 2) ECDSA 验签失败 → 拒绝
        // 3) 公钥派生的 NodeId 与 SenderId 不一致 → 拒绝（防 SenderId 冒名）
        // 全部通过才进入正常 handler 派发。
        if (!VerifyEnvelope(envelope, _encryption, out var failureReason))
        {
            _logger.LogWarning("消息验签失败，已丢弃: Type={Type}, Reason={Reason}, From={Sender}",
                envelope.MessageType, failureReason,
                Convert.ToHexString(envelope.SenderId).ToLower()[..8]);
            return;
        }

        // 注意：入站连接**不**写进连接池。池的契约是「本机主动建立的出站连接」——
        // 每条出站连接由发起方写、接收方读；发起方对这条连接没有持久读循环
        // （握手响应由 ChatService 直接读，握手结束后无人读它）。若把入站连接混进池，
        // 泛洪/转发就会「反向复用」一条发起方已停止读取的连接，消息静默丢失。
        // 双向通信靠每对节点各建一条出站连接（mesh 维护循环保证），不走入站连接反向。

        // 重放防护（独立步骤，不并入 VerifyEnvelope）。
        // 验签只证明「来自持私钥的一方」，不证明「不是重放的旧包」——攻击者录下一条合法信封
        // 反复重放，仍能通过上面全部三道关卡。此处按 MessageId 去重 + 时间新鲜度判定。
        // 放在验签**之后**是为了不对攻击者可控的数据做无谓工作；放在 handler 派发**之前**
        // 是为了保证重放的消息不会产生任何副作用。
        // 明因（过旧/超前/重复）由 IReplayGuard 记 LogWarning，这里不再重复打一条，
        // 避免同一事件在日志里出现两行。
        if (!_replayGuard.TryAccept(envelope, out var replayReason))
        {
            _logger.LogTrace("消息被重放防护拒绝: Type={Type}, Reason={ReplayReason}",
                envelope.MessageType, replayReason);
            return;
        }

        if (!_handlers.TryGetValue(envelope.MessageType, out var handler))
        {
            _logger.LogWarning("未注册的消息处理器: {Type}", envelope.MessageType);
            return;
        }

        Message message;
        try
        {
            message = _serializer.Deserialize<Message>(envelope.Payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "消息载荷反序列化失败，已丢弃: Type={Type}", envelope.MessageType);
            return;
        }

        // 载荷 SenderId 与信封 SenderId 必须一致（反序列化之后、handler 派发之前）。
        //
        // 为什么要有这道检查：`Message.SenderId` 这个字段**存在但不是权威** ——
        // 权威是 `envelope.SenderId`（已被 ECDSA 签名覆盖，且被验签强制与公钥派生一致）。
        // 历史缺陷正是因此产生的：KeyExchangeHandler 曾用载荷的 SenderId 去写会话密钥槽位，
        // 而同一方法里另几行又正确地用了信封 —— 同一个方法、相距 6 行，一个信载荷一个信封。
        // 只要「两者可以不一致」这件事被允许，下一个读 message.SenderId 的人就会踩同一个坑，
        // 而且没有任何编译期或测试期的提示。加了检查之后，该字段才成为「构造上可信」的东西。
        //
        // 不一致只可能来自：发送方把载荷 SenderId 填错（实现 bug），
        // 或对端刻意构造（此时它已通过验签，说明对端在用合法私钥说一个自相矛盾的话）。
        // 两种情况都没有任何合理用途，一律拒绝。
        if (message is { SenderId: not null } payload && !SameSender(envelope.SenderId, payload.SenderId))
        {
            _logger.LogWarning(
                "载荷 SenderId 与信封 SenderId 不一致，已丢弃（载荷身份字段不是权威）: " +
                "Type={Type}, Payload={PayloadSender}, Envelope={EnvelopeSender}",
                envelope.MessageType,
                DescribeNodeId(payload.SenderId),
                DescribeNodeId(envelope.SenderId));
            return;
        }

        try
        {
            // 通过非泛型 IMessageHandler 接口分发 (默认接口方法转发到强类型重载)。
            // 不再使用 reflection GetMethod/Invoke: Native-AOT 下反射目标缺少静态调用点
            // 会被 ILC 裁剪，导致处理器静默失效 (原先会产生 IL2075)。
            await handler.HandleAsync(message, sender, envelope, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "消息路由处理异常");
        }

        // mesh 泛洪：只有走到这里的消息（验签 + 重放防护都已通过，即「本节点首次见到」）
        // 才转发 —— 同一 MessageId 从其它路径再到达时会被上面的 TryAccept 拦截，
        // 因此每个节点对每条消息最多转发一次，环路自动终止，无需 TTL。
        // 转发保留原信封（原签名、原 Timestamp），接收方验的是原始发送者。
        // 放在 handler 之后：本地处理优先于转发职责。
        if (_floodingEnabled && IsFloodable(envelope.MessageType))
        {
            await ForwardAsync(envelope, sender, ct);
        }
    }

    /// <inheritdoc />
    public async Task SendAsync(NodeInfo recipient, Message message, CancellationToken ct = default)
    {
        var connection = await GetOrCreateConnectionAsync(recipient, ct);
        await SendViaConnectionAsync(connection, message, ct);
    }

    /// <inheritdoc />
    public async Task SendViaConnectionAsync(ITcpConnection connection, Message message, CancellationToken ct = default)
    {
        var envelope = BuildSignedEnvelope(message);
        var data = SerializeEnvelope(envelope);
        await connection.SendAsync(data, ct);
        _logger.LogTrace("发送消息: Type={Type}, Seq={Seq}",
            envelope.MessageType, envelope.SequenceNumber);
    }

    /// <inheritdoc />
    public async Task<int> FloodAsync(Message message, CancellationToken ct = default)
    {
        // 签名一次，同一信封复用到全部邻居：泛洪 N 个邻居时签名计算是 O(1) 而不是 O(N)。
        var envelope = BuildSignedEnvelope(message);
        var data = SerializeEnvelope(envelope);

        var sent = 0;
        foreach (var pair in _connectionPool)
        {
            if (!pair.Value.IsConnected)
                continue;

            try
            {
                await pair.Value.SendAsync(data, ct);
                sent++;
            }
            catch (Exception ex)
            {
                // 单个邻居失败不中断其余邻居 —— 泛洪的契约是尽力送达而非全有全无。
                _logger.LogWarning(ex, "泛洪发送失败: Peer={Peer}", DescribeKey(pair.Key));
            }
        }

        _logger.LogTrace("泛洪消息: Type={Type}, 邻居={Total}, 送达={Sent}",
            envelope.MessageType, _connectionPool.Count, sent);
        return sent;
    }

    /// <summary>
    /// 把一条<b>已验签的入站信封</b>原样转发给全部活跃出站邻居（mesh 泛洪的转发半边）。
    /// <para>
    /// 不重签名、不改 SequenceNumber / Timestamp —— 接收方验签与重放防护针对的都是原始发送者。
    /// 序列化一次，同一字节串复用到全部邻居；单个邻居失败只记日志。
    /// </para>
    /// <para>
    /// 排除两类连接：<b>原始发送者</b>（<c>envelope.SenderId</c> 对应的出站连接 —— 回发给源头
    /// 必被其重放防护丢弃，纯属浪费）与<b>来源连接</b>（防御性：正常情况下来源是入站连接、不在池中）。
    /// 中间转发者（多跳时把消息交给本节点的那个邻居）无法仅凭信封识别，回发给它的浪费由
    /// 对端重放防护兜底 —— 全连接小规模网络下可接受。
    /// </para>
    /// </summary>
    public async Task<int> ForwardAsync(MessageEnvelope envelope, ITcpConnection? exclude, CancellationToken ct = default)
    {
        var data = SerializeEnvelope(envelope);
        var senderKey = envelope.SenderId.Length == NodeId.Size
            ? Convert.ToHexString(envelope.SenderId)
            : null;

        var sent = 0;
        foreach (var pair in _connectionPool)
        {
            if (!pair.Value.IsConnected)
                continue;
            if (exclude is not null && pair.Value.ConnectionId == exclude.ConnectionId)
                continue;
            if (senderKey is not null && string.Equals(pair.Key, senderKey, StringComparison.Ordinal))
                continue;

            try
            {
                await pair.Value.SendAsync(data, ct);
                sent++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "转发失败: Peer={Peer}", DescribeKey(pair.Key));
            }
        }

        return sent;
    }

    /// <summary>构造并签名一条本机新发消息的信封（SendViaConnectionAsync 与 FloodAsync 共用）。</summary>
    private MessageEnvelope BuildSignedEnvelope(Message message)
    {
        var identity = _keyStore.GetOrCreateIdentity();

        var unsigned = new MessageEnvelope
        {
            MessageType = GetMessageType(message),
            SequenceNumber = NextSeq(),
            SenderId = identity.NodeId.ToByteArray(),   // 强制覆盖：发送方真实身份（不再让 Message.SenderId 决定）
            MessageId = message.MessageId,
            Timestamp = message.Timestamp,
            SenderPublicKey = identity.PublicKey,
            Payload = _serializer.Serialize(message)
        };

        return SignEnvelope(unsigned, identity, _encryption);
    }

    /// <summary>
    /// 只有聊天载荷才泛洪：控制消息是请求-响应配对（KeyExchange 的 pendingResponses 按
    /// MessageId 等待单一响应，泛洪会让第三方响应污染配对），文件消息是大流量点对点，
    /// 群成员管理（Invite/Notify）与送达确认（DeliveryAck）都是定向语义。
    /// </summary>
    private static bool IsFloodable(MessageType messageType)
        => messageType is MessageType.PrivateText or MessageType.GroupText;

    /// <summary>日志用的池键短标签；池键恒为 40 位 hex，防御性兜底不抛异常。</summary>
    private static string DescribeKey(string poolKey)
        => poolKey.Length >= 8 ? poolKey[..8] : poolKey;

    /// <inheritdoc />
    public async Task<ITcpConnection> GetOrCreateConnectionAsync(NodeInfo node, CancellationToken ct = default)
    {
        var key = Convert.ToHexString(node.NodeId.ToByteArray());

        if (_connectionPool.TryGetValue(key, out var existing) && existing.IsConnected)
            return existing;

        var connection = await _tcpTransport.ConnectAsync(node.EndPoint, ct);
        _connectionPool[key] = connection;
        _logger.LogDebug("建立TCP连接: {Endpoint}", node.EndPoint);
        return connection;
    }

    /// <inheritdoc />
    public async Task CloseConnectionAsync(byte[] nodeId)
    {
        var key = Convert.ToHexString(nodeId);
        if (_connectionPool.TryRemove(key, out var connection))
        {
            await connection.DisposeAsync();
            _logger.LogDebug("关闭TCP连接: {NodeId}", key[..8]);
        }
    }

    /// <summary>
    /// 处理传入TCP连接的消息
    /// </summary>
    public async Task ProcessIncomingConnectionAsync(ITcpConnection connection, CancellationToken ct = default)
    {
        while (connection.IsConnected && !ct.IsCancellationRequested)
        {
            try
            {
                var data = await connection.ReceiveMessageAsync(ct);
                var envelope = DeserializeEnvelope(data);
                await RouteIncomingAsync(envelope, connection, ct);
            }
            catch (InvalidOperationException) // 连接关闭
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // 取消是正常停机路径（进程退出时 appCts.Cancel()），不是错误。
                // mesh 维护循环会让对端主动连入，退出时这类连接正阻塞在读取上，
                // 取消会在这里以 OperationCanceledException 冒出来 —— 必须静默 break，
                // 不能记成「处理传入消息异常」。
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理传入消息异常");
                break;
            }
        }
    }

    #region 辅助方法

    /// <summary>两个节点 ID 字节数组是否表示同一身份（长度不等直接判否，避免 <c>AsSpan().SequenceEqual</c> 抛异常）。</summary>
    private static bool SameSender(byte[]? envelopeSenderId, byte[] payloadSenderId)
    {
        if (envelopeSenderId is null || envelopeSenderId.Length != payloadSenderId.Length)
            return false;

        return envelopeSenderId.AsSpan().SequenceEqual(payloadSenderId);
    }

    /// <summary>日志用的短身份标签；长度非法时如实标注，不抛异常（日志路径不应成为故障源）。</summary>
    private static string DescribeNodeId(byte[]? nodeId)
    {
        if (nodeId is null) return "<null>";
        return nodeId.Length == NodeId.Size
            ? Convert.ToHexString(nodeId).ToLowerInvariant()[..8]
            : $"<malformed:{nodeId.Length}B>";
    }

    private static MessageType GetMessageType(Message message) => message switch    {
        TextMessage m => m.IsGroup ? MessageType.GroupText : MessageType.PrivateText,
        FileMetaMessage => MessageType.FileMeta,
        FileChunkMessage => MessageType.FileChunk,
        FileAckMessage => MessageType.FileAck,
        KeyExchangeMessage => MessageType.KeyExchange,
        GroupInviteMessage => MessageType.GroupInvite,
        GroupNotifyMessage => MessageType.GroupNotify,
        DeliveryAckMessage => MessageType.DeliveryAck,
        _ => MessageType.Unknown
    };

    /// <summary>
    /// 用发送方长期私钥计算签名，写回 <see cref="MessageEnvelope.Signature"/> 字段，返回新信封。
    /// 调用方负责把 SenderId / SenderPublicKey / Payload / 其他字段已正确填充。
    /// </summary>
    public static MessageEnvelope SignEnvelope(MessageEnvelope envelope, KeyPair identity, IEncryptionService encryption)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(encryption);

        if (envelope.SenderPublicKey == null || envelope.SenderPublicKey.Length == 0)
            throw new ArgumentException("SenderPublicKey 必须在签名前填充", nameof(envelope));
        if (identity.PrivateKey == null || identity.PrivateKey.Length == 0)
            throw new ArgumentException("identity.PrivateKey 为空", nameof(identity));

        var data = EnvelopeCodec.ComputeSignedBytes(envelope);
        var signature = encryption.Sign(data, identity.PrivateKey);
        return envelope with { Signature = signature };
    }

    /// <summary>
    /// 暴露给外部（如 KeyExchangeHandler）做签名；用法与 SignEnvelope 等价但不依赖 router 实例。
    /// </summary>
    public MessageEnvelope SignEnvelopeWith(MessageEnvelope envelope)
    {
        var identity = _keyStore.GetOrCreateIdentity();
        var filled = envelope with
        {
            SenderId = identity.NodeId.ToByteArray(),
            SenderPublicKey = identity.PublicKey
        };
        return SignEnvelope(filled, identity, _encryption);
    }

    /// <summary>
    /// 验签并返回是否通过；若失败给出原因。
    /// <para>
    /// 实现已收敛到 <see cref="EnvelopeVerifier"/>（Core 层唯一真相源）。
    /// 本方法保留为薄封装，供既有调用方与测试继续使用。
    /// <para>
    /// 收敛的理由与 <see cref="EnvelopeCodec"/> 完全相同：UI 层不引用 Chat 层，
    /// 此前 <c>P2PChatTui.ReadHelloResponseAsync</c> 因此只能<b>完全不验签</b>，
    /// 让 <c>/connect</c> 对任意应答主机无条件信任。唯一正确的修法是把验签下沉到 Core，
    /// 而不是让 UI 再抄一份（抄一份 = 制造下一个漂移点）。
    /// </para>
    /// </summary>
    public static bool VerifyEnvelope(MessageEnvelope envelope, IEncryptionService encryption, out string? failureReason)
        => EnvelopeVerifier.Verify(envelope, encryption, out failureReason);

    private static bool VerifyEnvelopeCore(MessageEnvelope envelope, IEncryptionService encryption, out string? failureReason)
        => EnvelopeVerifier.Verify(envelope, encryption, out failureReason);

    /// <summary>
    /// 序列化整条信封到字节：固定头 + 长度前缀 SenderPublicKey + 长度前缀 Signature + Payload。
    /// <para>
    /// 实现已收敛到 <see cref="EnvelopeCodec"/>（Core 层唯一真相源）。
    /// 本方法保留为薄封装，供既有调用方与测试继续使用。
    /// </para>
    /// </summary>
    public static byte[] SerializeEnvelope(MessageEnvelope envelope) => EnvelopeCodec.Serialize(envelope);

    /// <summary>
    /// 反序列化整条信封。
    /// <para>
    /// 实现已收敛到 <see cref="EnvelopeCodec"/>（Core 层唯一真相源）。
    /// 该实现带有完整的长度边界校验，畸形帧一律抛 <see cref="InvalidDataException"/>。
    /// </para>
    /// <para>
    /// UI 层（<c>P2PChatTui.ReadHelloResponseAsync</c>）也必须调用同一份实现：
    /// 阶段 3.2 引入签名后载荷前多了「4+N 公钥长度前缀」与「4+M 签名前缀」，
    /// 任何按旧 50 字节固定头直接切 <c>Payload</c> 的副本都会解析失败。
    /// </para>
    /// </summary>
    public static MessageEnvelope DeserializeEnvelope(ReadOnlyMemory<byte> rawData)
        => EnvelopeCodec.Deserialize(rawData);

    private uint NextSeq() => Interlocked.Increment(ref _seqCounter);

    #endregion
}