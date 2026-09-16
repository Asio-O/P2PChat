using System.Buffers.Binary;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using P2PChat.Core.Abstractions;
using P2PChat.Core.Enums;
using P2PChat.Core.Extensions;
using P2PChat.Core.Models;

namespace P2PChat.Chat.Handlers;

/// <summary>
/// ECDH密钥交换消息处理器
/// </summary>
public class KeyExchangeHandler : IMessageHandler<KeyExchangeMessage>
{
    private readonly IEncryptionService _encryption;
    private readonly IKeyStore _keyStore;
    private readonly ILogger<KeyExchangeHandler> _logger;
    private readonly Channel<KeyExchangeMessage> _exchangeChannel;
    private readonly ISerializer _serializer;
    private uint _responseSequence;

    public MessageType MessageType => MessageType.KeyExchange;
    public IAsyncEnumerable<KeyExchangeMessage> OnKeyExchangeReceived => _exchangeChannel.Reader.ReadAllAsync();

    public KeyExchangeHandler(
        IEncryptionService encryption,
        IKeyStore keyStore,
        ILogger<KeyExchangeHandler> logger)
    {
        _encryption = encryption;
        _keyStore = keyStore;
        _logger = logger;
        _exchangeChannel = Channel.CreateUnbounded<KeyExchangeMessage>();
        _serializer = new MessagePackSerializer();
    }

    public async Task HandleAsync(
        KeyExchangeMessage message,
        ITcpConnection senderConnection,
        MessageEnvelope envelope,
        CancellationToken ct = default)
    {
        var senderNodeId = new NodeId(message.SenderId);

        if (!message.IsResponse)
        {
            // 收到密钥交换请求: 生成自己的临时密钥对并响应
            var ourEphemeral = _encryption.GenerateKeyPair();
            var sharedSecret = _encryption.DeriveSharedSecret(ourEphemeral.PrivateKey, message.EphemeralPublicKey);
            var sessionKey = _encryption.DeriveSessionKey(sharedSecret);
            _keyStore.SetSessionKey(senderNodeId, sessionKey);

            _logger.LogDebug("密钥交换请求已处理: {Sender}, 会话密钥已建立", senderNodeId.ToHexString()[..8]);

            // 双向握手的响应必须沿收到请求的同一TCP连接返回；只写入事件通道不会让发起方收到公钥。
            var identity = _keyStore.GetOrCreateIdentity();
            var response = new KeyExchangeMessage
            {
                SenderId = identity.PublicKey.Take(NodeId.Size).ToArray(),
                ConversationId = message.ConversationId,
                EphemeralPublicKey = ourEphemeral.PublicKey,
                IsResponse = true
            };
            await SendResponseAsync(response, senderConnection, ct);

            // 保留事件流供上层观测请求，但不再依赖它驱动响应发送。
            await _exchangeChannel.Writer.WriteAsync(message, ct);
        }
        else
        {
            // 发起方 ChatService 在发送请求的同一出站连接上消费响应，并持有本方临时私钥。
            // 这里不能从 keyStore 读取临时私钥：keyStore 中只允许保存 32 字节会话密钥。
            _logger.LogDebug("收到密钥交换响应: {Sender}, 由发起方完成会话密钥派生", senderNodeId.ToHexString()[..8]);
        }
    }

    /// <summary>
    /// 直接构造与 MessageRouter 相同的信封。处理器没有路由器依赖时，仍可把响应写回当前TCP连接。
    /// </summary>
    private async Task SendResponseAsync(
        KeyExchangeMessage response,
        ITcpConnection connection,
        CancellationToken ct)
    {
        var payload = _serializer.Serialize<Message>(response);
        var headerLength = 1 + 1 + 4 + NodeId.Size + 16 + 8;
        var data = new byte[headerLength + payload.Length];
        var offset = 0;

        data[offset++] = 1;
        data[offset++] = (byte)MessageType.KeyExchange;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset, 4), Interlocked.Increment(ref _responseSequence));
        offset += 4;
        response.SenderId.AsSpan().CopyTo(data.AsSpan(offset, NodeId.Size));
        offset += NodeId.Size;
        response.MessageId.ToByteArray().AsSpan().CopyTo(data.AsSpan(offset, 16));
        offset += 16;
        BinaryPrimitives.WriteInt64BigEndian(data.AsSpan(offset, 8), response.Timestamp);
        offset += 8;
        payload.AsSpan().CopyTo(data.AsSpan(offset));

        await connection.SendAsync(data, ct);
    }
}
