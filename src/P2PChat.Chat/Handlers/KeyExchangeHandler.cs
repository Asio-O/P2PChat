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
        PeerPublicKeyRegistry? peerPublicKeys = null)
    {
        _encryption = encryption;
        _keyStore = keyStore;
        _logger = logger;
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
        var senderNodeId = new NodeId(message.SenderId);

        // 登记对端长期公钥（无论请求还是响应都要登记）。
        // 取值来自信封而非载荷：MessageRouter 已在派发前完成 ECDSA 验签，并强制
        // NodeId.FromPublicKey(envelope.SenderPublicKey) == envelope.SenderId；
        // 载荷里的 SenderId 是对端可控输入，不能用它给公钥定身份。
        RecordPeerPublicKey(message, envelope);

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
            _logger.LogDebug("收到密钥交换响应: {Sender}, 由发起方完成会话密钥派生", senderNodeId.ToHexString()[..8]);
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
    private void RecordPeerPublicKey(KeyExchangeMessage message, MessageEnvelope envelope)
    {
        var peerIdHex = TryShortNodeId(message.SenderId);

        if (envelope.SenderPublicKey is not { Length: > 0 } publicKey)
        {
            _logger.LogWarning(
                "密钥交换信封缺少发送方长期公钥，无法登记对端 {Sender}（群邀请时该对端将被显式跳过）",
                peerIdHex);
            return;
        }

        if (!_peerPublicKeys.Record(envelope.SenderId, publicKey))
        {
            _logger.LogWarning(
                "密钥交换信封的长期公钥与发送方节点ID不自洽（长度 {Length}，期望 {Expected}），已拒绝登记对端 {Sender}",
                publicKey.Length, PeerPublicKeyRegistry.P256SubjectPublicKeyInfoLength, peerIdHex);
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
