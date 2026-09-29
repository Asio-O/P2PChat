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

    /// <summary>
    /// 密钥交换应答的最长等待时间。
    /// <para>
    /// 存在的理由不是「优雅」，是<b>避免整个 TUI 卡死</b>：发送私聊是单线程 await，
    /// 而 <c>ReadKeyExchangeResponseAsync</c> 在对端只发非应答帧时会在循环里一直等。
    /// 同族两处同类等待都已有上限（<c>GroupChatService.PublicKeyProbeTimeout</c> 5s、
    /// <c>P2PChatTui</c> 的 /connect 10s），本值取 10s 与后两者对齐。
    /// </para>
    /// <para>
    /// <b>internal 且可写，仅为测试可覆盖</b>（<c>InternalsVisibleTo</c> 只开给 Chat.Tests）。
    /// 真等 10 秒会让测试套件平白多花 10 秒，而这条分支恰恰最该被自动化覆盖。
    /// <para>
    /// <b>是实例属性而不是静态常量</b>：静态可变状态会在 xUnit 的并行执行下串到别的测试类
    /// （<c>ChatServiceTests</c> 同样会做密钥交换）。实例属性天然按实例隔离，
    /// 也不动构造函数签名 —— <c>ChatService</c> 是用 <c>AddSingleton&lt;ChatService&gt;()</c>
    /// 容器激活注册的，多加一个构造参数就得往 DI 里注册一个 <c>TimeSpan</c>，
    /// 那会让容器里任何一处 TimeSpan 依赖都被悄悄满足，是比这条超时更危险的坑。
    /// </para>
    /// </summary>
    internal TimeSpan KeyExchangeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public IAsyncEnumerable<ChatMessageEvent> OnMessageReceived => _messageChannel.Reader.ReadAllAsync();

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
        // 必填，不给默认值：本类的密钥交换**应答**读取路径走不到 MessageRouter.RouteIncomingAsync，
        // 因而不受那里的验签与重放防护保护 —— 漏注入会让「入站有防护、出站握手没防护」这条不一致
        // 静默复现，且症状是「会话密钥被中间人换掉」，日志里一行都没有。
        // 与 MessageRouter 的 replayGuard 形参同一个纪律，理由见那里的注释。
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
    /// <para>
    /// 本方法是<b>自动发现场景下用户发第一条私聊时必经的路径</b>（没有会话键时
    /// <see cref="EnsureSessionKeyAsync"/> 会走这里），也就是说它的对端身份来源是
    /// DHT 解析 / 联系人记录，而不是用户刚刚亲手 <c>/connect</c> 过的端点 ——
    /// 因此应答侧的校验强度必须与 <c>/connect</c> 那条路径<b>一致</b>，不能因为
    /// 「对端是系统自己找的」就当作可信。
    /// </para>
    /// </summary>
    private async Task PerformKeyExchangeAsync(NodeInfo recipient, KeyPair identity, CancellationToken ct)
    {
        _logger.LogDebug("开始密钥交换: {Recipient}", recipient.NodeId.ToHexString()[..8]);

        var ephemeralKey = _encryption.GenerateKeyPair();

        // 一次性关联标识（nonce）。**刻意不用恒定的会话键**：应答必须原样回显它，
        // 攻击者录下的旧应答回显的是上一次那串值，对不上 —— 这是无状态地挡住
        // 「录一条合法应答重放给另一个受害者」的唯一手段（重放防护是本地状态，
        // 受害节点从未见过那条 MessageId）。
        // 旧值 recipient.NodeId.ToHexString() 恒定，验回显等于没验。
        var correlationId = $"kx-{Guid.NewGuid():N}";

        var request = new KeyExchangeMessage
        {
            SenderId = identity.NodeId.ToByteArray(),
            ConversationId = correlationId,
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

        // 握手必须有超时。对端「连得上但不回应」是很现实的一种情况（半开的连接、
        // 老版本节点、中间的 NAT 设备），而 TUI 的发送路径是单线程 await ——
        // 没有超时就是**整个界面卡死**：用户既看不到消息、也看不到任何错误。
        // 同族两处都已有超时（GroupChatService 的公钥探测 5s、TUI /connect 10s），这里是唯一漏掉的。
        KeyExchangeMessage response;
        using (var exchangeCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            exchangeCts.CancelAfter(KeyExchangeTimeout);
            try
            {
                response = await ReadKeyExchangeResponseAsync(
                    connection, recipient.NodeId, correlationId, exchangeCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 只在「我们自己没取消」时归因为超时；调用方主动取消要原样往上抛。
                _logger.LogWarning("密钥交换超时（{Seconds}s 内对端未回应）: {Recipient}",
                    KeyExchangeTimeout.TotalSeconds, recipient.NodeId.ToHexString()[..8]);
                throw new KeyExchangeRejectedException(
                    $"对端在 {KeyExchangeTimeout.TotalSeconds:0} 秒内没有回应密钥交换，消息未发送。" +
                    "请确认对方节点正在运行、且该地址可达。");
            }
        }

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

    /// <summary>
    /// 在同一条出站连接上等待密钥交换**应答**，四道判据全部通过才返回。
    /// <para>
    /// <b>为什么这里必须自己校验，而不能指望 <c>MessageRouter.RouteIncomingAsync</c></b>：
    /// 那条路径会验签并过重放防护，但本方法读的是<b>本进程主动发起</b>的握手应答，
    /// 它直接消费连接上的字节流，<b>走不到路由器</b>。本方法此前只
    /// <c>Deserialize</c> 就取载荷使用，<b>全程无验签</b> ——
    /// 净效果是「能应答这个端点的主机可以单方面决定会话密钥」，
    /// 且会话密钥被记在<b>用户以为的那个对端</b>名下，攻击者据此可解密该会话后续全部私聊。
    /// 症状：一条日志都没有，消息照发照收，只是对攻击者是明文。
    /// </para>
    /// <para>
    /// <b>四道判据（顺序固定）</b>：
    /// <list type="number">
    ///   <item><b>回显本次的关联标识</b> —— 挡「录下旧的合法应答重放给另一个受害者」。
    ///         正常对端必回显（<c>KeyExchangeHandler</c> 原样回传），故无误杀。</item>
    ///   <item><b>ECDSA 验签</b>（<see cref="EnvelopeVerifier.Verify"/>）—— 挡伪造与篡改。
    ///         验签已强制 <c>NodeId.FromPublicKey(公钥) == SenderId</c>，公钥与身份强绑定。</item>
    ///   <item><b>签名者必须就是我们本想联系的那个节点</b> —— 挡「攻击者用自己的合法密钥
    ///         完整签一个包」。这是本路径<b>强于</b> <c>/connect</c> 的地方：那里只能 TOFU
    ///         （用户连的是未知端点），而这里 <paramref name="expectedPeerId"/> 来自
    ///         <c>FindNodeAsync</c> / 联系人，是<b>已知的预期身份</b>，因此这里能拒、那里不能。</item>
    ///   <item><b>重放防护</b> —— 与入站消息通道同强度，消除「两套强度」不一致。</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>证明什么 / 不证明什么</b>：它保证「应答由 <b>预期对端</b> 持有其私钥的一方签发、
    /// 是本次交换的新鲜应答」。它<b>不</b>保证对端机器本身没被攻陷，也不保护
    /// <c>FindNodeAsync</c> 返回的<b>端点</b>是否被指向了错误主机 —— 那属于发现层的信任问题。
    /// 用户提示不得把它表述成「已验证对端可信」。
    /// </para>
    /// </summary>
    /// <param name="connection">本次交换所用的出站连接。</param>
    /// <param name="expectedPeerId">本机<b>预期</b>与之交换的对端身份（来自 DHT 解析 / 联系人）。</param>
    /// <param name="expectedCorrelationId">本次请求发出时使用的一次性关联标识。</param>
    /// <param name="ct">取消标记。</param>
    private async Task<KeyExchangeMessage> ReadKeyExchangeResponseAsync(
        ITcpConnection connection,
        NodeId expectedPeerId,
        string expectedCorrelationId,
        CancellationToken ct)
    {
        var expectedShortId = expectedPeerId.ToHexString()[..8];

        while (true)
        {
            var rawData = await connection.ReceiveMessageAsync(ct);
            var envelope = DeserializeEnvelope(rawData);
            var message = ExchangeSerializer.Deserialize<Message>(envelope.Payload);

            if (envelope.MessageType != MessageType.KeyExchange ||
                message is not KeyExchangeMessage { IsResponse: true } response)
            {
                // 同一连接上若先到达其它消息（含对端主动发来的交换**请求**），交回正常路由，
                // 继续等待本次握手响应。
                await _router.RouteIncomingAsync(envelope, connection, ct);
                continue;
            }

            // ── 判据 ①：回显本次的一次性关联标识 ───────────────────────────────
            // 攻击者录下的**旧**应答回显的是上一次那串值，对不上；而它自己的应答
            // （无论真伪）无法预知本次随机值。
            if (!string.Equals(response.ConversationId, expectedCorrelationId, StringComparison.Ordinal))
            {
                throw Reject(expectedPeerId,
                    $"密钥交换响应的关联标识不匹配（期望 {expectedCorrelationId}，实际 {response.ConversationId}）" +
                    "——疑似重放了一条旧的合法应答");
            }

            // ── 判据 ②：ECDSA 验签 ───────────────────────────────────────────
            // 唯一实现是 Core.Extensions.EnvelopeVerifier（收敛到 Core 的那一份）；
            // 绝不在本文件再抄一份解析/验签逻辑。
            if (!EnvelopeVerifier.Verify(envelope, _encryption, out var failureReason))
            {
                throw Reject(expectedPeerId, $"密钥交换响应验签失败: {failureReason}");
            }

            // ── 判据 ③：签名者必须就是预期对端 ───────────────────────────────
            // 验签已保证 SenderPublicKey 非空且能派生出 SenderId，故这里可安全派生。
            // 绝不比较载荷里的 SenderId —— 那是对端完全可控的输入。
            var signerNodeId = NodeId.FromPublicKey(envelope.SenderPublicKey!);
            if (!signerNodeId.Equals(expectedPeerId))
            {
                throw Reject(expectedPeerId,
                    $"密钥交换响应来自非预期节点（应答方 {signerNodeId.ToHexString()[..8]}，" +
                    $"预期 {expectedShortId}）—— 该端点可能不是你要联系的对端");
            }

            // ── 判据 ④：重放防护 ────────────────────────────────────────────
            // 与入站消息通道同强度。明因（重复/过旧/超前）由 IReplayGuard 自己记 LogWarning。
            if (!_replayGuard.TryAccept(envelope, out var replayReason))
            {
                throw Reject(expectedPeerId, $"密钥交换响应未通过重放防护: {replayReason}");
            }

            return response;
        }
    }

    private KeyExchangeRejectedException Reject(NodeId expectedPeer, string reason)
    {
        // 记 Error 而不是 Debug：本条是**安全事件**，静默失败过一次（症状是对话被中间人接管）。
        _logger.LogError("密钥交换被拒绝，未建立会话密钥: Expected={Expected}, Reason={Reason}",
            expectedPeer.ToHexString()[..8], reason);
        return new KeyExchangeRejectedException(
            $"无法与对端建立可信会话（{reason}）。消息未发送。请确认对方节点 ID 与网络环境后重试。");
    }

    private static MessageEnvelope DeserializeEnvelope(ReadOnlyMemory<byte> rawData)
    {
        // 与 MessageRouter.SerializeEnvelope 对称：固定头 + 长度前缀 SenderPublicKey + 长度前缀 Signature + Payload。
        // 直接复用 MessageRouter.DeserializeEnvelope，避免两条反序列化路径走偏。
        return MessageRouter.DeserializeEnvelope(rawData);
    }
}
