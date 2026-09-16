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
/// 负责: 处理器注册、入站消息分发、出站消息发送、连接池管理
/// </summary>
public class MessageRouter : IMessageRouter
{
    private readonly ITcpTransport _tcpTransport;
    private readonly ISerializer _serializer;
    private readonly ILogger<MessageRouter> _logger;
    private readonly ConcurrentDictionary<MessageType, IMessageHandler> _handlers = new();
    private readonly ConcurrentDictionary<string, ITcpConnection> _connectionPool = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<Message>> _pendingResponses = new();
    private const int MaxConnectionsPerPeer = 3;
    private uint _seqCounter;

    public MessageRouter(
        ITcpTransport tcpTransport,
        ISerializer serializer,
        ILogger<MessageRouter> logger)
    {
        _tcpTransport = tcpTransport;
        _serializer = serializer;
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
        var envelope = new MessageEnvelope
        {
            MessageType = GetMessageType(message),
            SequenceNumber = NextSeq(),
            SenderId = message.SenderId,
            MessageId = message.MessageId,
            Timestamp = message.Timestamp,
            Payload = _serializer.Serialize(message)
        };

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

    private byte[] SerializeEnvelope(MessageEnvelope envelope)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(envelope.Version);
        ms.WriteByte((byte)envelope.MessageType);
        var seqBytes = BitConverter.GetBytes(envelope.SequenceNumber);
        if (BitConverter.IsLittleEndian) Array.Reverse(seqBytes);
        ms.Write(seqBytes);
        ms.Write(envelope.SenderId);
        var msgIdBytes = envelope.MessageId.ToByteArray();
        ms.Write(msgIdBytes);
        var tsBytes = BitConverter.GetBytes(envelope.Timestamp);
        if (BitConverter.IsLittleEndian) Array.Reverse(tsBytes);
        ms.Write(tsBytes);
        ms.Write(envelope.Payload);
        return ms.ToArray();
    }

    private static MessageEnvelope DeserializeEnvelope(ReadOnlyMemory<byte> rawData)
    {
        var data = rawData.Span;
        int offset = 0;
        var version = data[offset++];
        var msgType = (MessageType)data[offset++];
        uint seq = (uint)((data[offset++] << 24) | (data[offset++] << 16) | (data[offset++] << 8) | data[offset++]);
        var senderId = data[offset..(offset + NodeId.Size)].ToArray();
        offset += NodeId.Size;
        var messageId = new Guid(data[offset..(offset + 16)]);
        offset += 16;
        long ts = ((long)data[offset++] << 56) | ((long)data[offset++] << 48) |
                  ((long)data[offset++] << 40) | ((long)data[offset++] << 32) |
                  ((long)data[offset++] << 24) | ((long)data[offset++] << 16) |
                  ((long)data[offset++] << 8) | data[offset++];
        var payload = data[offset..].ToArray();

        return new MessageEnvelope
        {
            Version = version,
            MessageType = msgType,
            SequenceNumber = seq,
            SenderId = senderId,
            MessageId = messageId,
            Timestamp = ts,
            Payload = payload
        };
    }

    private uint NextSeq() => Interlocked.Increment(ref _seqCounter);

    #endregion
}
