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
    private readonly ILogger<MessageRouter> _logger;
    private readonly ConcurrentDictionary<MessageType, IMessageHandler> _handlers = new();
    private readonly ConcurrentDictionary<string, ITcpConnection> _connectionPool = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<Message>> _pendingResponses = new();
    private const int MaxConnectionsPerPeer = 3;
    private uint _seqCounter;

    public MessageRouter(
        ITcpTransport tcpTransport,
        ISerializer serializer,
        IEncryptionService encryption,
        IKeyStore keyStore,
        ILogger<MessageRouter> logger)
    {
        _tcpTransport = tcpTransport;
        _serializer = serializer;
        _encryption = encryption;
        _keyStore = keyStore;
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

        if (!_handlers.TryGetValue(envelope.MessageType, out var handler))
        {
            _logger.LogWarning("未注册的消息处理器: {Type}", envelope.MessageType);
            return;
        }

        try
        {
            var message = _serializer.Deserialize<Message>(envelope.Payload);

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

    private static MessageType GetMessageType(Message message) => message switch
    {
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
    /// </summary>
    public static bool VerifyEnvelope(MessageEnvelope envelope, IEncryptionService encryption, out string? failureReason)
    {
        ArgumentNullException.ThrowIfNull(encryption);
        return VerifyEnvelopeCore(envelope, encryption, out failureReason);
    }

    private static bool VerifyEnvelopeCore(MessageEnvelope envelope, IEncryptionService encryption, out string? failureReason)
    {
        if (envelope.Signature == null || envelope.Signature.Length == 0)
        {
            failureReason = "缺少签名";
            return false;
        }
        if (envelope.SenderPublicKey == null || envelope.SenderPublicKey.Length == 0)
        {
            failureReason = "缺少发送方公钥";
            return false;
        }

        // 校验 SenderId 必须等于公钥派生的 NodeId —— 阻止 SenderId 冒名
        try
        {
            var derivedNodeId = NodeId.FromPublicKey(envelope.SenderPublicKey).ToByteArray();
            if (!derivedNodeId.AsSpan().SequenceEqual(envelope.SenderId))
            {
                failureReason = "SenderId 与 SenderPublicKey 不匹配";
                return false;
            }
        }
        catch (Exception ex)
        {
            failureReason = "SenderPublicKey 解析失败: " + ex.Message;
            return false;
        }

        var data = EnvelopeCodec.ComputeSignedBytes(envelope);
        if (!encryption.Verify(data, envelope.Signature, envelope.SenderPublicKey))
        {
            failureReason = "ECDSA 验签失败";
            return false;
        }

        failureReason = null;
        return true;
    }

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