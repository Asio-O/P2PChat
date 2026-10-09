using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using P2PChat.Chat.Handlers;
using P2PChat.Chat.Routing;
using P2PChat.Core.Abstractions;
using P2PChat.Core.Enums;
using P2PChat.Core.Extensions;
using P2PChat.Core.Models;

namespace P2PChat.Chat.Services;

/// <summary>
/// 群聊服务 — 群组创建、成员管理、群消息收发
/// </summary>
public class GroupChatService : IGroupChatService
{
    private const int AesGcmNonceLength = 12;
    private const int AesGcmTagLength = 16;
    private const int GroupKeyMetadataLength = AesGcmNonceLength + AesGcmTagLength;

    /// <summary>
    /// 历史/测试 DHT 节点只携带 32 字节公钥，无法导入当前 Crypto 实现使用的 P-256 SPKI。
    /// 识别为「非 P-256」后走 SHA256(公钥) 的兼容包装路径（见 <see cref="EncryptGroupKeyForMember"/>）。
    /// </summary>
    private const int LegacyPublicKeyLength = 32;

    /// <summary>主动握手取回对端长期公钥的超时。超时按「取不到公钥」处理并跳过该成员。</summary>
    private static readonly TimeSpan PublicKeyProbeTimeout = TimeSpan.FromSeconds(5);

    private static readonly ISerializer ProbeSerializer = new MessagePackSerializer();

    private readonly IDhtService _dht;
    private readonly IMessageRouter _router;
    private readonly IEncryptionService _encryption;
    private readonly IKeyStore _keyStore;
    private readonly IGroupMetadataStore _metadataStore;
    private readonly ILogger<GroupChatService>? _logger;
    private readonly PeerPublicKeyRegistry _peerPublicKeys;
    private readonly ConcurrentDictionary<string, GroupInfo> _groups = new();
    private readonly ConcurrentDictionary<string, List<NodeInfo>> _onlineMembers = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _probeLocks = new(StringComparer.Ordinal);

    public GroupChatService(
        IDhtService dht,
        IMessageRouter router,
        IEncryptionService encryption,
        IKeyStore keyStore,
        IGroupMetadataStore metadataStore,
        ILogger<GroupChatService>? logger = null,
        PeerPublicKeyRegistry? peerPublicKeys = null)
    {
        _dht = dht;
        _router = router;
        _encryption = encryption;
        _keyStore = keyStore;
        _metadataStore = metadataStore;
        _logger = logger;
        _peerPublicKeys = peerPublicKeys ?? PeerPublicKeyRegistry.Shared;

        // 启动加载：把磁盘上的群组元数据恢复进内存，同时确保 keyStore 也持有相应群密钥。
        // 见 2026-09-21-group-metadata-persistence。
        LoadPersistedGroups();
    }

    /// <summary>
    /// 启动时加载磁盘上的群组元数据。两条一致性保证：
    /// (1) 仅当 keyStore 缺失对应群密钥时才用元数据里的明文密钥回填（避免覆盖更新的会话密钥）；
    /// (2) 加载失败一律静默吞并返回空集合（沿用 <see cref="FileBackedKeyStore.LoadAll"/> 的容错约定）。
    /// </summary>
    private void LoadPersistedGroups()
    {
        IReadOnlyList<GroupInfo> persisted;
        try
        {
            persisted = _metadataStore.LoadAll();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "加载群组元数据失败，群组列表为空");
            return;
        }

        foreach (var g in persisted)
        {
            _groups[g.GroupId] = g;
            if (_keyStore.GetGroupKey(g.GroupId) == null)
                _keyStore.SetGroupKey(g.GroupId, g.GroupKey);
        }
        _logger?.LogInformation("已从持久化恢复 {Count} 个群组", persisted.Count);
    }

    /// <inheritdoc />
    public async Task<GroupInfo> CreateGroupAsync(
        string groupName, IReadOnlyList<NodeId> initialMembers, CancellationToken ct = default)
    {
        // 生成群组ID和密钥
        // 创建者身份必须是本节点真实身份（SHA-1(公钥)），不能用 PublicKey.Take(20)
        // ——那是 P-256 SPKI DER 的固定算法头，对每个节点都相同。
        var identity = _keyStore.GetOrCreateIdentity();
        var creatorNodeId = identity.NodeId;
        var groupId = GenerateGroupId(groupName, creatorNodeId);
        var groupKey = _encryption.GenerateRandomKey();

        var memberIds = initialMembers.Select(m => m.ToByteArray()).ToList();
        if (!memberIds.Any(id => id.SequenceEqual(creatorNodeId.ToByteArray())))
            memberIds.Add(creatorNodeId.ToByteArray());

        var groupInfo = new GroupInfo
        {
            GroupId = groupId,
            GroupName = groupName,
            CreatorId = creatorNodeId,
            MemberIds = memberIds,
            GroupKey = groupKey
        };

        _groups[groupId] = groupInfo;
        _keyStore.SetGroupKey(groupId, groupKey);
        PersistGroups();

        // 向每个初始成员发送邀请。
        // 注意：单个成员失败**不得**中断建群（历史行为），但任何失败都必须留下日志 ——
        // 静默吞掉是实质缺陷：用户看到「/group create 没反应」，日志里一行都没有，
        // 既定位不了也区分不了「对端离线」和「群密钥没送出去」。
        // 群链路「拿不到就显式记 Error 并跳过该成员」的现行约定见 <c>Agent.md</c> §3.4
        // （成员公钥三级解析）；该事故的由来见归档快照
        // <c>.agents/notes/archived/process/2026-09-20-p2pchat-repair-plan.md</c>。
        foreach (var memberNodeId in initialMembers)
        {
            if (memberNodeId.Equals(creatorNodeId)) continue;

            var memberShortId = memberNodeId.ToHexString()[..8];

            NodeInfo? memberInfo;
            try
            {
                memberInfo = await _dht.FindNodeAsync(memberNodeId, ct);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex,
                    "查询初始成员失败，该成员未收到群邀请: {Member}, 群组={GroupId}",
                    memberShortId, groupId);
                continue;
            }

            if (memberInfo == null)
            {
                _logger?.LogWarning(
                    "初始成员无法定位（未登记显式端点且 DHT 无结果），该成员未收到群邀请: {Member}, 群组={GroupId}",
                    memberShortId, groupId);
                continue;
            }

            try
            {
                // 群密钥必须用「成员自己的长期公钥」做 ECDH 包装；静态登记（/add）与盲连接
                // （/connect）得到的 NodeInfo 的 PublicKey 为空，主线路径也一律返回空 ——
                // 因此这里先解析出真实公钥，拿不到就显式跳过并记 Error。
                var memberPublicKey = await ResolveMemberPublicKeyAsync(memberNodeId, memberInfo, ct);
                if (memberPublicKey == null)
                {
                    _logger?.LogError(
                        "无法取得成员 {Member} 的长期公钥（登记记录为空且主动密钥交换失败），" +
                        "群密钥无法为其包装，该成员未收到群邀请: EndPoint={EndPoint}, 群组={GroupId}。" +
                        "提示：对该节点执行一次 /msg 或 /connect 可建立连接后重试。",
                        memberShortId, memberInfo.EndPoint, groupId);
                    continue;
                }

                // 每个成员使用自己的公钥派生独立的AES-256包装密钥。
                // EncryptedGroupKey 保留32字节密文主体，nonce和tag放在追加字段中，
                // 以兼容现有对消息字段长度的约束。
                var encrypted = EncryptGroupKeyForMember(groupKey, identity, memberPublicKey);
                var invite = new GroupInviteMessage
                {
                    SenderId = creatorNodeId.ToByteArray(),
                    ConversationId = groupId,
                    GroupId = groupId,
                    GroupName = groupName,
                    EncryptedGroupKey = encrypted.Ciphertext,
                    MemberIds = memberIds,
                    SenderPublicKey = identity.PublicKey.ToArray(),
                    EncryptedGroupKeyNonceAndTag = encrypted.NonceAndTag
                };

                await _router.SendAsync(memberInfo, invite, ct);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex,
                    "向成员 {Member} 发送群邀请失败，群密钥未送达该成员: EndPoint={EndPoint}, 群组={GroupId}",
                    memberShortId, memberInfo.EndPoint, groupId);
            }
        }

        return groupInfo;
    }

    /// <inheritdoc />
    public async Task SendGroupMessageAsync(string groupId, string text, CancellationToken ct = default)
    {
        if (!_groups.TryGetValue(groupId, out var groupInfo))
            throw new InvalidOperationException("群组不存在: " + groupId);

        var identity = _keyStore.GetOrCreateIdentity();
        var senderId = identity.NodeId.ToByteArray();

        // 群消息必须走 AES-256-GCM 加密（与私聊 ChatService.SendPrivateMessageAsync:71 同源）。
        // 见 2026-09-21-group-message-encryption：之前 Content 是明文，README 端到端加密对群聊不成立。
        var groupKey = _keyStore.GetGroupKey(groupId)
            ?? throw new InvalidOperationException("群组密钥缺失: " + groupId);

        var ciphertext = _encryption.Encrypt(System.Text.Encoding.UTF8.GetBytes(text), groupKey);

        var message = new TextMessage
        {
            SenderId = senderId,
            ConversationId = groupId,
            Content = Convert.ToBase64String(ciphertext),  // 12B nonce + ct + 16B tag
            IsGroup = true
        };

        // mesh 泛洪发送：不再逐成员解析端点、逐个建连 —— 消息泛洪给全部 mesh 邻居，
        // 成员过滤由接收端承担（群消息以群密钥加密，非成员解密失败即丢弃，
        // GroupMessageHandler 已如此）。群密钥仍经 GroupInvite 定向分发，不走泛洪。
        // 见 2026-10-09-mesh-topology-and-flooding。
        var delivered = await _router.FloodAsync(message, ct);
        if (delivered == 0)
        {
            _logger?.LogWarning(
                "群消息泛洪无任何活跃邻居可达，消息未离开本机: 群组={GroupId}",
                groupId[..Math.Min(8, groupId.Length)]);
        }
    }

    /// <inheritdoc />
    public GroupInfo? GetGroup(string groupId)
    {
        return _groups.TryGetValue(groupId, out var g) ? g : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<GroupInfo> GetKnownGroups()
    {
        return _groups.Values.ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<NodeInfo> GetOnlineMembers(string groupId)
    {
        return _onlineMembers.TryGetValue(groupId, out var members) ? members : [];
    }

    /// <inheritdoc />
    public Task HandleInviteAsync(GroupInviteMessage invite, CancellationToken ct = default)
    {
        // GroupInviteHandler 已经完成解密并把明文群密钥放入 keyStore。
        // 这里读取该密钥，不能再把 EncryptedGroupKey 的密文主体当作群密钥。
        var key = _keyStore.GetGroupKey(invite.GroupId);
        if (key == null)
        {
            // 兼容旧版直接传递32字节明文群密钥的本地调用；线上新消息必须
            // 先经过 GroupInviteHandler 解密，否则不能把密文登记为群密钥。
            if (invite.EncryptedGroupKey.Length == 32 &&
                IsMissing(invite.SenderPublicKey) &&
                IsMissing(invite.EncryptedGroupKeyNonceAndTag))
            {
                key = invite.EncryptedGroupKey.ToArray();
            }
            else
            {
                throw new InvalidOperationException("群组邀请的群密钥尚未解密");
            }
        }

        if (key.Length != 32)
            throw new ArgumentException("群组密钥必须是256位(32字节)", nameof(invite));

        key = key.ToArray();

        // 存储群组信息
        var groupInfo = new GroupInfo
        {
            GroupId = invite.GroupId,
            GroupName = invite.GroupName,
            CreatorId = new NodeId(invite.SenderId),
            MemberIds = invite.MemberIds,
            GroupKey = key
        };
        _groups[invite.GroupId] = groupInfo;

        _keyStore.SetGroupKey(invite.GroupId, key);
        PersistGroups();

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task HandleNotifyAsync(GroupNotifyMessage notify, CancellationToken ct = default)
    {
        // 处理群组通知: 加入/离开/解散
        if (notify.Action == "dissolve" && _groups.TryGetValue(notify.GroupId, out var group))
        {
            _keyStore.RemoveGroupKey(notify.GroupId);
            _groups.TryRemove(notify.GroupId, out _);
            _metadataStore.Remove(notify.GroupId);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 把当前所有群组全量写入持久化层。沿用 <see cref="FileBackedKeyStore"/>
    /// 「只在变更时落盘」的写文件模式：调用方在变更点调用即可，无需额外去重。
    /// </summary>
    private void PersistGroups()
    {
        try
        {
            _metadataStore.Save(_groups.Values);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "群组元数据持久化失败");
        }
    }

    private static string GenerateGroupId(string groupName, NodeId creatorId)
    {
        var input = $"{groupName}:{creatorId.ToHexString()}:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private (byte[] Ciphertext, byte[] NonceAndTag) EncryptGroupKeyForMember(
        byte[] groupKey, KeyPair identity, byte[] memberPublicKey)
    {
        byte[] wrappingKey;

        if (memberPublicKey.Length == LegacyPublicKeyLength)
        {
            // 旧版/测试DHT节点只携带32字节公钥，无法导入当前Crypto实现使用的
            // P-256 SubjectPublicKeyInfo。仍使用AES-GCM产生密文，真实P-256节点
            // 则始终走下面的ECDH路径。
            wrappingKey = SHA256.HashData(memberPublicKey);
        }
        else
        {
            var sharedSecret = _encryption.DeriveSharedSecret(identity.PrivateKey, memberPublicKey);
            wrappingKey = _encryption.DeriveSessionKey(sharedSecret);
        }

        var encrypted = _encryption.Encrypt(groupKey, wrappingKey);
        if (encrypted.Length != groupKey.Length + GroupKeyMetadataLength)
            throw new CryptographicException("群组密钥密文格式无效");

        var ciphertext = encrypted.AsSpan(AesGcmNonceLength,
            encrypted.Length - GroupKeyMetadataLength).ToArray();
        var nonceAndTag = new byte[GroupKeyMetadataLength];
        encrypted.AsSpan(0, AesGcmNonceLength).CopyTo(nonceAndTag);
        encrypted.AsSpan(encrypted.Length - AesGcmTagLength, AesGcmTagLength)
            .CopyTo(nonceAndTag.AsSpan(AesGcmNonceLength));

        return (ciphertext, nonceAndTag);
    }

    /// <summary>
    /// 取出成员的长期公钥（用于 ECDH 包装群密钥）。三级来源，逐级降级：
    /// <list type="number">
    ///   <item>登记信息（DHT / 静态对端）里自带的公钥；</item>
    ///   <item>密钥交换阶段登记进 <see cref="PeerPublicKeyRegistry"/> 的对端公钥；</item>
    ///   <item>都没有 → 主动发一次密钥交换请求，从对端<b>已验签的响应信封</b>里取它的长期公钥。</item>
    /// </list>
    /// 三级都拿不到才返回 null，由调用方显式记 Error 并跳过该成员。
    /// </summary>
    private async Task<byte[]?> ResolveMemberPublicKeyAsync(
        NodeId memberNodeId, NodeInfo memberInfo, CancellationToken ct)
    {
        if (IsUsablePublicKey(memberInfo.PublicKey))
            return memberInfo.PublicKey;

        if (_peerPublicKeys.TryGet(memberNodeId, out var registered))
            return registered;

        return await ProbeMemberPublicKeyAsync(memberNodeId, memberInfo, ct);
    }

    /// <summary>
    /// 主动密钥交换一次以取得对端长期公钥。
    /// <para>
    /// 这是「静态/盲连接对端也能被邀请进群」的关键：这类对端的 <see cref="NodeInfo.PublicKey"/>
    /// 必然为空（手工 <c>/add</c> 只能给出 ip:port，<c>MainlineDhtService</c> 的每条解析路径也都返回
    /// <c>Array.Empty&lt;byte&gt;</c>），但对端一旦接受 TCP 连接就一定会用签名信封回一条密钥交换响应，
    /// 而信封里的 <see cref="MessageEnvelope.SenderPublicKey"/> 已被路由器验签并与 SenderId 绑定。
    /// </para>
    /// <para>
    /// 这里刻意**不新增线路字段**：信封早已携带同一个公钥且签名覆盖它，新增载荷字段反而需要
    /// 自己再做一次身份绑定，且会改变被签名的载荷字节。
    /// </para>
    /// </summary>
    private async Task<byte[]?> ProbeMemberPublicKeyAsync(
        NodeId memberNodeId, NodeInfo memberInfo, CancellationToken ct)
    {
        var memberShortId = memberNodeId.ToHexString()[..8];

        // 同一节点的探测会共用路由器连接池里那条连接，必须串行化，否则两个调用方会互相
        // 抢读对方的信封（与 ChatService 的 _exchangeLocks 同一理由）。用完整 hex 作键，
        // 避免 8 位前缀碰撞把两个不相关节点的探测串到同一把锁上。
        var gate = _probeLocks.GetOrAdd(memberNodeId.ToHexString(), static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // 排队期间可能已被其它调用方解析出来。
            if (_peerPublicKeys.TryGet(memberNodeId, out var cached))
                return cached;

            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(PublicKeyProbeTimeout);

            var identity = _keyStore.GetOrCreateIdentity();
            var ephemeral = _encryption.GenerateKeyPair();
            var request = new KeyExchangeMessage
            {
                SenderId = identity.NodeId.ToByteArray(),
                ConversationId = memberNodeId.ToHexString(),
                EphemeralPublicKey = ephemeral.PublicKey,
                // 自报本机监听端点，让成员也能把我们登记为静态对端（见 KeyExchangeMessage.SenderListenEndPoint）。
                SenderListenEndPoint = EndpointText.Format(_dht.LocalNode.EndPoint),
                IsResponse = false
            };

            var connection = await _router.GetOrCreateConnectionAsync(memberInfo, probeCts.Token);
            await _router.SendViaConnectionAsync(connection, request, probeCts.Token);

            var peerPublicKey = await ReadPeerPublicKeyFromResponseAsync(connection, memberNodeId, probeCts.Token);
            if (peerPublicKey == null)
                return null;

            if (!_peerPublicKeys.Record(memberNodeId.ToByteArray(), peerPublicKey))
            {
                _logger?.LogError(
                    "主动密钥交换取回的公钥与成员 {Member} 的节点ID不自洽，已拒绝使用该公钥包装群密钥",
                    memberShortId);
                return null;
            }

            _logger?.LogInformation(
                "已通过主动密钥交换取得成员 {Member} 的长期公钥（登记记录中原本为空）: EndPoint={EndPoint}",
                memberShortId, memberInfo.EndPoint);
            return peerPublicKey;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "主动密钥交换失败，无法取得成员 {Member} 的长期公钥: EndPoint={EndPoint}",
                memberShortId, memberInfo.EndPoint);
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 在同一条连接上等对端的密钥交换<b>响应</b>，取出其签名信封里的长期公钥。
    /// </summary>
    private async Task<byte[]?> ReadPeerPublicKeyFromResponseAsync(
        ITcpConnection connection, NodeId expectedPeer, CancellationToken ct)
    {
        var expectedShortId = expectedPeer.ToHexString()[..8];

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var raw = await connection.ReceiveMessageAsync(ct);
            if (raw.Length == 0)
                throw new InvalidOperationException("连接已关闭，对端未返回密钥交换响应");

            var envelope = MessageRouter.DeserializeEnvelope(raw);

            if (envelope.MessageType != MessageType.KeyExchange)
            {
                // 同一连接上若先到达其它消息，交回正常路由，继续等待本次握手响应。
                await _router.RouteIncomingAsync(envelope, connection, ct);
                continue;
            }

            // 逐条校验 IsResponse：同一条连接上对端也可能主动发来密钥交换**请求**。
            if (ProbeSerializer.Deserialize<Message>(envelope.Payload) is not KeyExchangeMessage { IsResponse: true })
            {
                await _router.RouteIncomingAsync(envelope, connection, ct);
                continue;
            }

            // 必须验签。我们要取的正是信封里的公钥，不验签等于允许任意对端指定任意公钥。
            if (!MessageRouter.VerifyEnvelope(envelope, _encryption, out var failureReason))
            {
                _logger?.LogError("密钥交换响应验签失败，已丢弃: Reason={Reason}", failureReason);
                return null;
            }

            if (!new NodeId(envelope.SenderId).Equals(expectedPeer))
            {
                _logger?.LogError(
                    "密钥交换响应来自非预期节点: Actual={Actual}, Expected={Expected}",
                    new NodeId(envelope.SenderId).ToHexString()[..8], expectedShortId);
                return null;
            }

            var peerPublicKey = envelope.SenderPublicKey;
            if (!IsUsablePublicKey(peerPublicKey))
            {
                _logger?.LogError(
                    "密钥交换响应的长期公钥不可用: Length={Length}", peerPublicKey?.Length ?? 0);
                return null;
            }

            return peerPublicKey;
        }
    }

    /// <summary>
    /// 公钥是否可用于包装群密钥：P-256 SPKI（91 字节）或历史 32 字节格式。
    /// 长度不对的公钥一律视为「未知」，宁可显式失败也不能拿去 ECDH ——
    /// 导入失败会抛异常，而「悄悄换一条路径」会让群密钥被包装给错误的接收方。
    /// </summary>
    private static bool IsUsablePublicKey(byte[]? publicKey)
        => publicKey is { Length: LegacyPublicKeyLength }
        || publicKey is { Length: PeerPublicKeyRegistry.P256SubjectPublicKeyInfoLength };

    private static bool IsMissing(byte[]? value) => value is null || value.Length == 0;
}
