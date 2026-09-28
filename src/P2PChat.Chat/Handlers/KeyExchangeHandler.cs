using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using P2PChat.Chat.Routing;
using P2PChat.Core.Abstractions;
using P2PChat.Core.Enums;
using P2PChat.Core.Extensions;
using P2PChat.Core.Models;

namespace P2PChat.Chat.Handlers;

/// <summary>
/// 进程内「对端长期公钥」登记表 —— 记录密钥交换时从**已验签的信封**里学到的对端长期公钥。
/// <para>
/// 为什么不需要在线路上新增字段：<see cref="MessageEnvelope.SenderPublicKey"/> 本来就随每条
/// 消息上线，并且被 <see cref="MessageRouter.ComputeSignedBytes"/> 纳入 ECDSA 签名覆盖范围，
/// <see cref="MessageRouter.VerifyEnvelope"/> 又强制 <c>NodeId.FromPublicKey(SenderPublicKey) == SenderId</c>。
/// 也就是说信封里的长期公钥已经「可验签 + 与节点ID绑定」，比在 KeyExchangeMessage 载荷里新增一个
/// 无独立绑定的字段**更强**（后者还需要自己再做一次派生校验）。
/// </para>
/// <para>
/// 写入与读取都会重新校验 <c>SHA-1(公钥) == 登记的 NodeId</c>，因此表内不可能出现
/// 「用 A 的 NodeId 查到 B 的公钥」这种会导致群密钥被包装给错误接收方的条目。
/// </para>
/// </summary>
public sealed class PeerPublicKeyRegistry
{
    /// <summary>P-256 SubjectPublicKeyInfo (DER) 的长度，91 字节。</summary>
    public const int P256SubjectPublicKeyInfoLength = 91;

    /// <summary>
    /// 进程级默认实例。生产代码（<c>Program.cs</c> / <c>ChatServiceCollectionExtensions</c>）没有为
    /// 本类注册 DI，因此在构造函数里取默认单例；测试可显式注入独立实例以避免跨用例污染。
    /// </summary>
    public static PeerPublicKeyRegistry Shared { get; } = new();

    private readonly ConcurrentDictionary<string, byte[]> _keys = new(StringComparer.Ordinal);

    /// <summary>
    /// 取对端长期公钥。返回的副本可安全跨线程使用；表内条目若与登记的 NodeId 不再自洽会被丢弃。
    /// </summary>
    public bool TryGet(NodeId peerId, out byte[] longTermPublicKey)
    {
        longTermPublicKey = Array.Empty<byte>();
        if (!_keys.TryGetValue(peerId.ToHexString(), out var stored))
            return false;

        if (stored is null || stored.Length != P256SubjectPublicKeyInfoLength ||
            !DerivesNodeId(stored, peerId))
        {
            // 自洽性被破坏（理论上不会发生）→ 当作未命中并清掉，避免把错误公钥用于包装群密钥。
            _keys.TryRemove(peerId.ToHexString(), out _);
            return false;
        }

        longTermPublicKey = stored.ToArray();
        return true;
    }

    /// <summary>
    /// 登记对端长期公钥。仅当公钥长度合法、且 <c>SHA-1(公钥)</c> 等于 <paramref name="claimedNodeId"/>
    /// 时才写入（公钥能派生出自己的节点ID，因此不需要信任调用方给的 ID）。
    /// </summary>
    /// <returns>是否成功登记。</returns>
    public bool Record(byte[]? claimedNodeId, byte[]? longTermPublicKey)
    {
        if (longTermPublicKey is null || longTermPublicKey.Length != P256SubjectPublicKeyInfoLength)
            return false;
        if (claimedNodeId is null || claimedNodeId.Length != NodeId.Size)
            return false;

        var peerId = new NodeId(claimedNodeId);
        if (!DerivesNodeId(longTermPublicKey, peerId))
            return false;

        _keys[peerId.ToHexString()] = longTermPublicKey.ToArray();
        return true;
    }

    private static bool DerivesNodeId(byte[] publicKey, NodeId peerId)
    {
        try
        {
            return NodeId.FromPublicKey(publicKey).Equals(peerId);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// ECDH密钥交换消息处理器
/// </summary>
public class KeyExchangeHandler : IMessageHandler<KeyExchangeMessage>
{
    private readonly IEncryptionService _encryption;
    private readonly IKeyStore _keyStore;
    private readonly IDhtService _dht;
    private readonly ILogger<KeyExchangeHandler> _logger;
    private readonly Channel<KeyExchangeMessage> _exchangeChannel;
    private readonly ISerializer _serializer;
    private readonly PeerPublicKeyRegistry _peerPublicKeys;
    private uint _responseSequence;

    public MessageType MessageType => MessageType.KeyExchange;
    public IAsyncEnumerable<KeyExchangeMessage> OnKeyExchangeReceived => _exchangeChannel.Reader.ReadAllAsync();

    public KeyExchangeHandler(
        IEncryptionService encryption,
        IKeyStore keyStore,
        ILogger<KeyExchangeHandler> logger,
        IDhtService dht,
        PeerPublicKeyRegistry? peerPublicKeys = null)
    {
        _encryption = encryption;
        _keyStore = keyStore;
        _logger = logger;
        // 必填，不给默认值：漏注入会让「/connect 变双向」这条能力静默失效，而失效时
        // 用户看到的症状是「对方连不上我」——极难归因。与 IReplayGuard 同一个纪律。
        // 依赖方向：IDhtService 在 Core.Abstractions，本类在 Chat；Chat → Core，无循环。
        _dht = dht ?? throw new ArgumentNullException(nameof(dht));
        _exchangeChannel = Channel.CreateUnbounded<KeyExchangeMessage>();
        _serializer = new MessagePackSerializer();
        _peerPublicKeys = peerPublicKeys ?? PeerPublicKeyRegistry.Shared;
    }

    public async Task HandleAsync(
        KeyExchangeMessage message,
        ITcpConnection senderConnection,
        MessageEnvelope envelope,
        CancellationToken ct = default)
    {
        // ── 对端身份必须来自**信封**，不能来自载荷 ──────────────────────────────
        // 载荷里的 message.SenderId 是对端完全可控的输入：攻击者可以填任意 20 字节。
        // MessageRouter 在派发本处理器**之前**已完成 ECDSA 验签，并强制
        // NodeId.FromPublicKey(envelope.SenderPublicKey) == envelope.SenderId，
        // 因此 envelope.SenderId 是「持有对应私钥的一方」的自述身份。
        //
        // 为什么这里不只是安全、还关乎**可用性**：这个值是 SetSessionKey 的键。
        // 一旦用错，后续所有针对该对端的私聊都会去查一个空的会话槽位而静默失败 ——
        // 报文发出去了、对端也在收，但界面永远没内容，且现场几乎无迹可循。
        // （历史上本方法就出现过「相距 6 行，一个信载荷一个信封」的自相矛盾，已修正。）
        var senderNodeId = TrySenderNodeId(envelope);
        if (senderNodeId is null)
        {
            _logger.LogWarning(
                "密钥交换消息的发送方身份无法从信封确定（缺少或非法 SenderId），已丢弃: Peer={Peer}",
                TryShortNodeId(message.SenderId));
            return;
        }

        // 登记对端长期公钥（无论请求还是响应都要登记）。
        // 取值同样来自信封：载荷里的 SenderId 是对端可控输入，不能用它给公钥定身份。
        // 返回值表示「公钥与身份自洽」，后续登记静态对端以它为前提。
        var publicKeyBound = RecordPeerPublicKey(message, envelope);

        if (!message.IsResponse)
        {
            // 收到密钥交换请求: 生成自己的临时密钥对并响应
            var ourEphemeral = _encryption.GenerateKeyPair();
            var sharedSecret = _encryption.DeriveSharedSecret(ourEphemeral.PrivateKey, message.EphemeralPublicKey);
            var sessionKey = _encryption.DeriveSessionKey(sharedSecret);
            _keyStore.SetSessionKey(senderNodeId.Value, sessionKey);

            _logger.LogDebug("密钥交换请求已处理: {Sender}, 会话密钥已建立", senderNodeId.Value.ToHexString()[..8]);

            // 让 /connect 变双向：发起方知道我是谁、却不知道我在哪（典型场景三：
            // 局域网内一台能连公网、一台不能，不能连公网的那台永远查不到对方）。
            // 这里用「已验签的 envelope.SenderId」+「TCP 连接的源端点」把发起方登记为静态对端，
            // 于是发起方不必再 /add 或反向 /connect 一次。
            RegisterInboundPeer(senderNodeId.Value, message, envelope, publicKeyBound);

            // 双向握手的响应必须沿收到请求的同一TCP连接返回；只写入事件通道不会让发起方收到公钥。
            var identity = _keyStore.GetOrCreateIdentity();
            var response = new KeyExchangeMessage
            {
                // 必须是本节点真实身份（SHA-1(公钥)），发起方据此把会话密钥登记到正确的对端键下。
                SenderId = identity.NodeId.ToByteArray(),
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
            _logger.LogDebug("收到密钥交换响应: {Sender}, 由发起方完成会话密钥派生", senderNodeId.Value.ToHexString()[..8]);
        }
    }

    /// <summary>
    /// 把对端长期公钥登记进 <see cref="PeerPublicKeyRegistry"/>，供群邀请包装群密钥时使用。
    /// <para>
    /// 取值来自信封的 <see cref="MessageEnvelope.SenderPublicKey"/>：该字段被
    /// <see cref="MessageRouter.ComputeSignedBytes"/> 纳入 ECDSA 签名覆盖范围，且
    /// <see cref="MessageRouter.VerifyEnvelope"/> 已强制它派生出 <see cref="MessageEnvelope.SenderId"/>。
    /// 本方法再校验一次派生关系，是为了在本处理器被直接调用（绕过 MessageRouter 验签）时同样安全。
    /// </para>
    /// <para>
    /// 登记失败只告警不抛异常：密钥交换本身只用临时密钥，对端长期公钥缺失不应该中断会话建立。
    /// 真正需要公钥的群邀请路径会自行记录 Error 并跳过该成员，不再静默。
    /// </para>
    /// </summary>
    /// <returns>true 表示公钥与身份自洽且已登记；false 表示不可用（调用方**不得**据此登记静态对端）。</returns>
    private bool RecordPeerPublicKey(KeyExchangeMessage message, MessageEnvelope envelope)
    {
        var peerIdHex = TryShortNodeId(message.SenderId);

        if (envelope.SenderPublicKey is not { Length: > 0 } publicKey)
        {
            _logger.LogWarning(
                "密钥交换信封缺少发送方长期公钥，无法登记对端 {Sender}（群邀请时该对端将被显式跳过）",
                peerIdHex);
            return false;
        }

        if (!_peerPublicKeys.Record(envelope.SenderId, publicKey))
        {
            _logger.LogWarning(
                "密钥交换信封的长期公钥与发送方节点ID不自洽（长度 {Length}，期望 {Expected}），已拒绝登记对端 {Sender}",
                publicKey.Length, PeerPublicKeyRegistry.P256SubjectPublicKeyInfoLength, peerIdHex);
            return false;
        }

        return true;
    }

    /// <summary>
    /// 从**信封**取出对端身份。返回 null 表示信封里的 SenderId 缺失或长度非法。
    /// <para>
    /// 刻意不提供「从载荷取」的兜底：载荷的 SenderId 是对端可控输入，一旦在信封异常时
    /// 回退到它，就等于留了一条绕过验签的后门，而症状要等到会话错配时才显现。
    /// </para>
    /// </summary>
    private static NodeId? TrySenderNodeId(MessageEnvelope envelope)
        => envelope.SenderId is { } id && id.Length == NodeId.Size ? new NodeId(id) : null;

    /// <summary>
    /// 把「主动连进来的」对端登记为静态对端 —— 这让 <c>/connect &lt;ip:port&gt;</c> 成为**双向**：
    /// 发起方一条命令，双方互相可达。
    /// <para>
    /// 动机（部署场景三）：局域网内一台能连公网、一台不能。不能连公网的那台永远查不到
    /// 对方（连 <c>get_peers</c> 都发不出去），而此前只有发起方拿到对方身份，
    /// 被连的这一侧「知道对方是谁，却不知道对方在哪」，无法回话，必须让用户再手工
    /// <c>/add</c> 或反向 <c>/connect</c> 一次。
    /// </para>
    /// <para>
    /// <b>⚠️ 端点只能来自权威来源：对方在 hello 载荷里自报的监听端点</b>。
    /// 本方法最初按「用 <c>senderConnection.RemoteEndPoint</c> 登记」实现，
    /// 结果把既有的双向私聊测试打红了 3 条（<c>SocketException: 连接被拒</c>）。根因：
    /// <para>
    /// <c>TcpTransport.ConnectAsync</c> 用的是普通 <c>Socket.ConnectAsync</c>，本地端口由操作系统
    /// <b>临时分配</b>。因此**被连这一侧看到的 <c>RemoteEndPoint</c> 是对端的临时源端口，
    /// 不是它的监听端口</b> —— 拿它当 <c>NodeInfo.EndPoint</c> 登记，回连必然被拒。
    /// （反过来，<b>发起侧</b> 的 <c>connection.RemoteEndPoint</c> 才是对端真正的监听端点 ——
    /// 这正是 <c>/connect</c> 一直能工作的原因。）
    /// </para>
    /// <para>
    /// 更糟的是副作用：<c>FindNodeAsync</c> <b>优先</b>返回静态对端表，于是一条
    /// <c>对端IP:临时端口</c> 的错误条目会<b>永久遮蔽</b>该对端的 DHT 解析路径 ——
    /// 在「同一局域网 / 各自家宽」这两个本来能正常发现对方的场景里反而会把发现能力弄坏。
    /// </para>
    /// <para>
    /// 结论：监听端口这个信息在应答侧<b>根本不存在</b>（三次握手不携带对端监听端口），
    /// 只能由发起方自己说 —— 即 <see cref="KeyExchangeMessage.SenderListenEndPoint"/>。
    /// 该字段位于被签名整体覆盖的 Payload 内，故「A 声称自己监听 P」是<b>可归因</b>的。
    /// </para>
    /// </para>
    /// <para>
    /// 安全约束（供将来实现时保留，一条都不能省）：
    /// <list type="number">
    ///   <item>身份用 <b>已验签的 <c>envelope.SenderId</c></b>，不用载荷；</item>
    ///   <item>公钥用 <b>已验签的 <c>envelope.SenderPublicKey</c></b>，<b>绝不用本机公钥</b> ——
    ///         静态对端层的防冒名判定就是 <c>NodeId.FromPublicKey(node.PublicKey)</c>，
    ///         填错等于让那一层守卫被绕过；</item>
    ///   <item><paramref name="publicKeyBound"/> 为 false 时<b>不登记</b>：那时 NodeId 与公钥
    ///         不自洽，写进静态对端表就是一颗毒丸；</item>
    ///   <item><b>不得覆盖已存在的条目</b>：已登记的端点可能比新观测到的更可信。</item>
    /// </list>
    /// 注意：本处理器<b>不</b>因为「对方是自己连进来的」而跳过任何检查 —— 它在
    /// <c>MessageRouter.RouteIncomingAsync</c> 中已被验签与重放防护各筛过一遍；
    /// <paramref name="publicKeyBound"/> 则是本方法自身对绑定关系的独立复核，
    /// 使本方法在被直接调用时同样安全。
    /// </para>
    /// </summary>
    private void RegisterInboundPeer(
        NodeId senderNodeId, KeyExchangeMessage message, MessageEnvelope envelope, bool publicKeyBound)
    {
        var peer = senderNodeId.ToHexString()[..8];

        if (!publicKeyBound || envelope.SenderPublicKey is not { Length: > 0 } publicKey)
        {
            _logger.LogWarning(
                "对端 {Sender} 的公钥与身份不自洽，已跳过反向登记（不写入静态对端表，避免污染防冒名判定）",
                peer);
            return;
        }

        // ── 端点必须来自**权威来源**：对方在 hello 载荷里自报的监听端点 ──────────
        // 绝不使用 connection.RemoteEndPoint —— 出站连接的本地端口是内核分配的
        // ephemeral 临时端口，登记后回连必然被拒；更糟的是 FindNodeAsync 优先查静态对端表，
        // 一条「对端IP:临时端口」的废记录会永久遮蔽该对端的 DHT 解析路径。
        //
        // 降级语义：字段缺失（旧版对端）或解析失败时，**只登记身份与公钥、不碰静态对端表**。
        // 为什么退到 PeerPublicKeyRegistry 而不是静态对端表：
        //   静态对端表的语义是「我知道它**在哪**」，而 NodeInfo.EndPoint 是 required ——
        //   在当前模型下无法表达「身份已知、端点未知」的条目。
        //   若将来要让静态对端表容纳这种条目，必须先把 NodeInfo.EndPoint 改成可空，
        //   那是一次影响面很大的模型变更（波及 FindNodeAsync / 连接池 / 群消息扇出每一处），
        //   不是顺手能做的。写清楚代价，下一个人才不会以为只是漏写。

        if (string.IsNullOrWhiteSpace(message.SenderListenEndPoint))
        {
            _logger.LogInformation(
                "对端 {Sender} 未自报监听端点（可能是旧版节点）：已登记其身份与公钥，" +
                "但不写入静态对端表 —— 该对端暂时无法回话，需 /add 或反向 /connect。" +
                "身份与公钥见对端长期公钥登记表。",
                peer);
            return;
        }

        if (!Core.Extensions.EndpointText.TryParse(message.SenderListenEndPoint, out var listenEndPoint))
        {
            _logger.LogWarning(
                "对端 {Sender} 自报的监听端点无法解析（{Value}），已降级为只登记身份与公钥、不写入静态对端表",
                peer, message.SenderListenEndPoint);
            return;
        }

        try
        {
            _dht.RegisterStaticPeer(new NodeInfo
            {
                NodeId = senderNodeId,
                EndPoint = listenEndPoint,
                // 已验签、与 senderNodeId 绑定；不是载荷值，更不是本机公钥。
                PublicKey = publicKey,
                LastSeen = DateTime.UtcNow
            });

            _logger.LogInformation(
                "已登记反向可达的对端（对方 /connect 而来，监听端点由对方自报且已被签名覆盖）: " +
                "Peer={Peer} @ {EndPoint}",
                peer, listenEndPoint);
        }
        catch (Exception ex)
        {
            // 登记失败不得影响密钥交换本身（会话密钥已建立），但必须留痕 ——
            // 否则用户看到的症状是「对方连不上我」，而日志里一行都没有。
            _logger.LogWarning(ex,
                "登记反向对端失败，该对端仍可能无法回话: Peer={Peer} @ {EndPoint}",
                peer, listenEndPoint);
        }
    }

    private static string TryShortNodeId(byte[]? nodeIdBytes)
    {
        if (nodeIdBytes is null || nodeIdBytes.Length != NodeId.Size)
            return "<malformed>";

        return new NodeId(nodeIdBytes).ToHexString()[..8];
    }

    /// <summary>
    /// 直接构造与 MessageRouter 相同的信封（带签名），处理器没有路由器依赖时仍可把响应写回当前TCP连接。
    /// </summary>
    private async Task SendResponseAsync(
        KeyExchangeMessage response,
        ITcpConnection connection,
        CancellationToken ct)
    {
        var identity = _keyStore.GetOrCreateIdentity();

        var unsigned = new MessageEnvelope
        {
            Version = 1,
            MessageType = MessageType.KeyExchange,
            SequenceNumber = Interlocked.Increment(ref _responseSequence),
            SenderId = identity.NodeId.ToByteArray(),
            MessageId = response.MessageId,
            Timestamp = response.Timestamp,
            SenderPublicKey = identity.PublicKey,
            Payload = _serializer.Serialize<Message>(response)
        };

        // KeyExchangeHandler 没有 MessageRouter 依赖（设计选择：避免循环依赖并保留
        // "在收到请求的同一 TCP 连接上直接回写响应" 的能力），但签名规则必须与
        // MessageRouter.SendViaConnectionAsync 完全一致 —— 静态方法 SignEnvelope 把规则
        // 集中在一处，KeyExchangeHandler 与 MessageRouter 都从这里调用。
        var signed = MessageRouter.SignEnvelope(unsigned, identity, _encryption);
        var data = MessageRouter.SerializeEnvelope(signed);
        await connection.SendAsync(data, ct);
    }
}
