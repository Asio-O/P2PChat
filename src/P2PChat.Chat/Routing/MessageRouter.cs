using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Threading.Channels;
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
    public MessageRouter(
        ITcpTransport tcpTransport,
        ISerializer serializer,
        IEncryptionService encryption,
        IKeyStore keyStore,
        IReplayGuard replayGuard,
        ILogger<MessageRouter> logger)
    {
        _tcpTransport = tcpTransport;
        _serializer = serializer;
        _encryption = encryption;
        _keyStore = keyStore;
        _replayGuard = replayGuard ?? throw new ArgumentNullException(nameof(replayGuard));
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

        var envelope = SignEnvelope(unsigned, identity, _encryption);

        var data = SerializeEnvelope(envelope);
        await connection.SendAsync(data, ct);
        _logger.LogTrace("发送消息: Type={Type}, Seq={Seq}",
            envelope.MessageType, envelope.SequenceNumber);
    }

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