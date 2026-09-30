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
    private readonly IReplayGuard _replayGuard;
    private readonly ILogger<ChatService> _logger;
    private readonly Channel<ChatMessageEvent> _messageChannel;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _exchangeLocks = new();
    private static readonly ISerializer ExchangeSerializer = new MessagePackSerializer();

    public IAsyncEnumerable<ChatMessageEvent> OnMessageReceived => _messageChannel.Reader.ReadAllAsync();

    /// <param name="replayGuard">
    /// 入站重放防护，<b>必填</b>。本服务有一条**不走 <c>MessageRouter.RouteIncomingAsync</c>** 的读路径
    /// （<c>ReadKeyExchangeResponseAsync</c>：出站连接没有入站消费循环，握手响应必须在本连接上现读），
    /// 因此必须**自己**对那条路径的报文过重放防护 —— 否则「同一 MessageId 经不同连接重放」无人拦截。
    /// 刻意不给默认值：可选参数会让「忘记注入」静默等于「关闭防护」。
    /// </param>
    public ChatService(
        IDhtService dht,
        IMessageRouter router,
        IEncryptionService encryption,
        IKeyStore keyStore,
        IReplayGuard replayGuard,
        ILogger<ChatService> logger)
    {
        _dht = dht;
        _router = router;
        _encryption = encryption;
        _keyStore = keyStore;
        _replayGuard = replayGuard ?? throw new ArgumentNullException(nameof(replayGuard));
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
        var (response, peerNodeId) = await ReadKeyExchangeResponseAsync(connection, recipient.NodeId, ct);

        var sharedSecret = _encryption.DeriveSharedSecret(
            ephemeralKey.PrivateKey, response.EphemeralPublicKey);
        var sessionKey = _encryption.DeriveSessionKey(sharedSecret);
        // 键用**已验签的**对端身份（与「我方拨号的节点」在 ReadKeyExchangeResponseAsync 里已校验一致），
        // 绝不用载荷里的 message.SenderId —— 那是对端可控输入。
        _keyStore.SetSessionKey(peerNodeId, sessionKey);

        _logger.LogDebug("密钥交换已完成: {Recipient}, 会话密钥长度={Length}",
            peerNodeId.ToHexString()[..8], sessionKey.Length);
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

    /// <summary>
    /// 在出站连接上直接读取密钥交换<b>响应</b>。
    /// <para>
    /// <b>为什么这条路径必须自己过防护</b>：它<b>不</b>经过 <c>MessageRouter.RouteIncomingAsync</c>
    /// （出站连接没有独立的入站消费循环，握手响应必须在本连接上现读），因此
    /// <c>EnvelopeVerifier</c> 与 <c>IReplayGuard</c> 两道关卡<b>都被绕过</b> ——
    /// 而它恰好服务于 <c>/connect</c>，即<b>唯一不需预知对端身份</b>的接入方式，攻击面最大。
    /// 此前它只 <c>Deserialize</c> 就直接使用载荷：载荷篡改、签名无效、公钥与身份不自洽、
    /// 以及经不同连接重放同一条 MessageId，全都能通过。
    /// </para>
    /// <para>
    /// <b>顺序不变量：先验签、后去重</b>。先拒伪造，再拒合法包的重放；若先去重，
    /// 伪造包会先写进去重状态（把一个从未真正到达过的 MessageId 标记成「已见」），
    /// 反而污染了真正的去重表。
    /// </para>
    /// <para>
    /// <b>本方法不产出 <see cref="ChatMessageEvent"/></b>：它只接握手响应，不承载聊天内容。
    /// 聊天事件有且只有 <c>IChatEventPublisher</c> 一条路径（见类注释的「唯一来源铁律」）。
    /// </para>
    /// </summary>
    /// <param name="connection">握手所用的那条出站连接。</param>
    /// <param name="expectedPeer">
    /// 我方主动拨号的预期对端。响应自称的身份必须与之一致，否则拒绝 ——
    /// 否则会话密钥会被记到一个我方并未拨号的 NodeId 名下。
    /// </param>
    /// <param name="ct">取消标记。</param>
    private async Task<(KeyExchangeMessage Message, NodeId PeerNodeId)> ReadKeyExchangeResponseAsync(
        ITcpConnection connection,
        NodeId expectedPeer,
        CancellationToken ct)
    {
        while (true)
        {
            var rawData = await connection.ReceiveMessageAsync(ct);
            var envelope = DeserializeEnvelope(rawData);
            var message = ExchangeSerializer.Deserialize<Message>(envelope.Payload);

            if (envelope.MessageType != MessageType.KeyExchange ||
                message is not KeyExchangeMessage { IsResponse: true } response)
            {
                // 同一连接上若先到达其它消息，交回正常路由，继续等待本次握手响应。
                await _router.RouteIncomingAsync(envelope, connection, ct);
                continue;
            }

            // ── ① 验签：收敛到 Core 的唯一实现，不在这里另抄一份 ──────────────────
            if (!EnvelopeVerifier.Verify(envelope, _encryption, out var verifyReason))
            {
                throw new InvalidOperationException(
                    $"密钥交换响应验签失败，拒绝继续（{verifyReason}）");
            }

            // 身份取自**信封**（已验签、与公钥强绑定），不是载荷里对端可控的 SenderId。
            var peerNodeId = new NodeId(envelope.SenderId);

            // ── ② 去重：必须在验签之后，否则伪造包会污染去重表 ──────────────────
            if (!_replayGuard.TryAccept(envelope, out var replayReason))
            {
                throw new InvalidOperationException(
                    $"密钥交换响应被重放防护拒绝（{replayReason}）");
            }

            // ── ③ 响应自称的身份必须就是我方拨号的那个 ────────────────────────────
            if (!peerNodeId.Equals(expectedPeer))
            {
                throw new InvalidOperationException(
                    $"密钥交换响应来自非预期节点（实际 {peerNodeId.ToHexString()[..8]}，" +
                    $"预期 {expectedPeer.ToHexString()[..8]}）");
            }

            _logger.LogDebug("密钥交换响应已通过验签与重放防护: {Peer}",
                peerNodeId.ToHexString()[..8]);

            return (response, peerNodeId);
        }
    }

    private static MessageEnvelope DeserializeEnvelope(ReadOnlyMemory<byte> rawData)
    {
        // 与 MessageRouter.SerializeEnvelope 对称：固定头 + 长度前缀 SenderPublicKey + 长度前缀 Signature + Payload。
        // 直接复用 MessageRouter.DeserializeEnvelope，避免两条反序列化路径走偏。
        return MessageRouter.DeserializeEnvelope(rawData);
    }
}
