using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using P2PChat.Chat.Routing;
using P2PChat.Core.Abstractions;
using P2PChat.Core.Enums;
using P2PChat.Core.Extensions;
using P2PChat.Core.Models;

namespace P2PChat.Chat.Services;

/// <summary>
/// 聊天服务 — 私聊消息收发
/// <para>
/// 同时是整条聊天事件流的<b>唯一持有者与唯一出口</b>：出站消息的本地回显写进
/// <see cref="_messageChannel"/>，入站消息由 <c>PrivateMessageHandler</c> /
/// <c>GroupMessageHandler</c> 经 <see cref="IChatEventPublisher"/> 写进**同一个**通道。
/// 见 REPAIR-PLAN B3 —— 这两个写入口此前各自持有私有通道，导致「收到的消息永远不显示」。
/// </para>
/// </summary>
public class ChatService : IChatService, IChatEventPublisher
{
    private readonly IDhtService _dht;
    private readonly IMessageRouter _router;
    private readonly IEncryptionService _encryption;
    private readonly IKeyStore _keyStore;
    private readonly ILogger<ChatService> _logger;
    private readonly Channel<ChatMessageEvent> _messageChannel;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _exchangeLocks = new();
    private static readonly ISerializer ExchangeSerializer = new MessagePackSerializer();

    public IAsyncEnumerable<ChatMessageEvent> OnMessageReceived => _messageChannel.Reader.ReadAllAsync();

    public ChatService(
        IDhtService dht,
        IMessageRouter router,
        IEncryptionService encryption,
        IKeyStore keyStore,
        ILogger<ChatService> logger)
    {
        _dht = dht;
        _router = router;
        _encryption = encryption;
        _keyStore = keyStore;
        _logger = logger;
        _messageChannel = Channel.CreateUnbounded<ChatMessageEvent>();
    }

    /// <inheritdoc />
    public async Task SendPrivateMessageAsync(NodeId recipientId, string text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        // 1. 查找目标节点
        var recipient = await _dht.FindNodeAsync(recipientId, ct);
        if (recipient == null)
        {
            _logger.LogWarning("目标节点未找到: {NodeId}", recipientId.ToHexString()[..8]);
            throw new InvalidOperationException($"目标节点未找到: {recipientId.ToHexString()[..8]}");
        }

        // 2. 确保有会话密钥
        var identity = _keyStore.GetOrCreateIdentity();
        await EnsureSessionKeyAsync(recipientId, recipient, identity, ct);

        var sessionKey = _keyStore.GetSessionKey(recipientId);
        if (sessionKey is not { Length: 32 })
        {
            _logger.LogError("密钥交换完成后会话密钥无效: {Recipient}",
                recipientId.ToHexString()[..8]);
            throw new InvalidOperationException("无法建立有效的私聊会话密钥");
        }

        // 私聊线路契约：TextMessage.Content 只承载 AES-256-GCM 密文的 Base64，
        // 明文仅在本地事件和接收端解密后的 ChatMessageEvent 中出现。
        var encryptedContent = _encryption.Encrypt(Encoding.UTF8.GetBytes(text), sessionKey);

        // 会话键必须**方向无关**：发送方与接收方必须算出同一个字符串。
        // 历史上这里写 recipientId，导致接收方收到的键是它自己的 NodeId，
        // 而 UI 的会话桶以「对端 ID」为键 → 消息投进一个永远选不中的桶。
        var conversationId = ConversationId.ForPrivate(identity.NodeId, recipientId);

        // 3. 构建并发送消息
        // SenderId 必须是本节点的真实身份（SHA-1(公钥)）。
        // 不要退回 PublicKey.Take(20)：那是 P-256 SPKI DER 的固定算法头，对每个节点都相同。
        var message = new TextMessage
        {
            SenderId = identity.NodeId.ToByteArray(),
            ConversationId = conversationId,
            Content = Convert.ToBase64String(encryptedContent),
            IsGroup = false
        };

        await _router.SendAsync(recipient, message, ct);

        // 4. 生成本地消息事件 (显示在发送者UI)
        await _messageChannel.Writer.WriteAsync(new ChatMessageEvent
        {
            Content = text,
            SenderId = identity.NodeId,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(message.Timestamp).UtcDateTime,
            ConversationId = conversationId,
            IsGroup = false,
            IsOutgoing = true
        }, ct);

        _logger.LogInformation("私聊消息已发送: {Recipient} -> {Text}",
            recipientId.ToHexString()[..8], text[..Math.Min(20, text.Length)]);
    }

    /// <inheritdoc />
    public bool HasSessionKey(NodeId peerId)
    {
        return _keyStore.GetSessionKey(peerId) is { Length: 32 };
    }

    /// <summary>
    /// 发布聊天事件到事件流。
    /// <para>
    /// 这是 <see cref="IChatEventPublisher"/> 的唯一实现点，也是入站消息抵达 UI 的**唯一**路径。
    /// 此前本方法在 <c>src/**</c> 里零调用者 —— handler 把事件写进了自己的私有通道，
    /// 而 UI 读的是这里，于是「收到」与「看到」之间没有任何连线（REPAIR-PLAN B3）。
    /// 现在 <c>PrivateMessageHandler</c> / <c>GroupMessageHandler</c> 都注入
    /// <see cref="IChatEventPublisher"/> 并调用本方法。
    /// </para>
    /// </summary>
    public async Task PublishAsync(ChatMessageEvent chatEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chatEvent);
        await _messageChannel.Writer.WriteAsync(chatEvent, ct);
    }

    /// <summary>
    /// 执行ECDH密钥交换
    /// </summary>
    private async Task PerformKeyExchangeAsync(NodeInfo recipient, KeyPair identity, CancellationToken ct)
    {
        _logger.LogDebug("开始密钥交换: {Recipient}", recipient.NodeId.ToHexString()[..8]);

        var ephemeralKey = _encryption.GenerateKeyPair();

        var request = new KeyExchangeMessage
        {
            SenderId = identity.NodeId.ToByteArray(),
            ConversationId = recipient.NodeId.ToHexString(),
            EphemeralPublicKey = ephemeralKey.PublicKey,
            // 自报本机**监听**端点，让对端能把我们登记为静态对端（/connect 双向的一半）。
            // LocalNode.EndPoint 即本机监听端点（局域网 IP + TCP 监听端口），与 DHT 宣告同源。
            // 注意它对**跨 NAT** 对端不可达 —— 那种场景靠 DHT 解析拿到公网端点，不靠反向登记。
            SenderListenEndPoint = Core.Extensions.EndpointText.Format(_dht.LocalNode.EndPoint),
            IsResponse = false
        };

        // MessageRouter 的出站连接没有独立的入站消费循环，因此从同一连接读取响应。
        // 只有收到对端临时公钥后才把 ECDH/HKDF 结果写入 keyStore；临时私钥绝不作为会话密钥保存。
        var connection = await _router.GetOrCreateConnectionAsync(recipient, ct);
        await _router.SendViaConnectionAsync(connection, request, ct);
        var response = await ReadKeyExchangeResponseAsync(connection, ct);

        var sharedSecret = _encryption.DeriveSharedSecret(
            ephemeralKey.PrivateKey, response.EphemeralPublicKey);
        var sessionKey = _encryption.DeriveSessionKey(sharedSecret);
        _keyStore.SetSessionKey(recipient.NodeId, sessionKey);

        _logger.LogDebug("密钥交换已完成: {Recipient}, 会话密钥长度={Length}",
            recipient.NodeId.ToHexString()[..8], sessionKey.Length);
    }

    private async Task EnsureSessionKeyAsync(
        NodeId peerId,
        NodeInfo recipient,
        KeyPair identity,
        CancellationToken ct)
    {
        if (HasSessionKey(peerId))
            return;

        var gate = _exchangeLocks.GetOrAdd(peerId.ToHexString(), static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (!HasSessionKey(peerId))
                await PerformKeyExchangeAsync(recipient, identity, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<KeyExchangeMessage> ReadKeyExchangeResponseAsync(
        ITcpConnection connection,
        CancellationToken ct)
    {
        while (true)
        {
            var rawData = await connection.ReceiveMessageAsync(ct);
            var envelope = DeserializeEnvelope(rawData);
            var message = ExchangeSerializer.Deserialize<Message>(envelope.Payload);

            if (envelope.MessageType == MessageType.KeyExchange &&
                message is KeyExchangeMessage { IsResponse: true } response)
            {
                return response;
            }

            // 同一连接上若先到达其它消息，交回正常路由，继续等待本次握手响应。
            await _router.RouteIncomingAsync(envelope, connection, ct);
        }
    }

    private static MessageEnvelope DeserializeEnvelope(ReadOnlyMemory<byte> rawData)
    {
        // 与 MessageRouter.SerializeEnvelope 对称：固定头 + 长度前缀 SenderPublicKey + 长度前缀 Signature + Payload。
        // 直接复用 MessageRouter.DeserializeEnvelope，避免两条反序列化路径走偏。
        return MessageRouter.DeserializeEnvelope(rawData);
    }
}
