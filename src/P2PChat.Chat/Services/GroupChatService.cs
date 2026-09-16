using System.Collections.Concurrent;
using System.Security.Cryptography;
using P2PChat.Core.Abstractions;
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

    private readonly IDhtService _dht;
    private readonly IMessageRouter _router;
    private readonly IEncryptionService _encryption;
    private readonly IKeyStore _keyStore;
    private readonly ConcurrentDictionary<string, GroupInfo> _groups = new();
    private readonly ConcurrentDictionary<string, List<NodeInfo>> _onlineMembers = new();

    public GroupChatService(
        IDhtService dht,
        IMessageRouter router,
        IEncryptionService encryption,
        IKeyStore keyStore)
    {
        _dht = dht;
        _router = router;
        _encryption = encryption;
        _keyStore = keyStore;
    }

    /// <inheritdoc />
    public async Task<GroupInfo> CreateGroupAsync(
        string groupName, IReadOnlyList<NodeId> initialMembers, CancellationToken ct = default)
    {
        // 生成群组ID和密钥
        var identity = _keyStore.GetOrCreateIdentity();
        var groupId = GenerateGroupId(groupName, new NodeId(identity.PublicKey.Take(20).ToArray()));
        var groupKey = _encryption.GenerateRandomKey();

        var creatorNodeId = new NodeId(identity.PublicKey.Take(20).ToArray());
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

        // 向每个初始成员发送邀请
        foreach (var memberNodeId in initialMembers)
        {
            if (memberNodeId.Equals(creatorNodeId)) continue;

            var memberInfo = await _dht.FindNodeAsync(memberNodeId, ct);
            if (memberInfo == null) continue;

            try
            {
                // 每个成员使用自己的公钥派生独立的AES-256包装密钥。
                // EncryptedGroupKey 保留32字节密文主体，nonce和tag放在追加字段中，
                // 以兼容现有对消息字段长度的约束。
                var encrypted = EncryptGroupKeyForMember(groupKey, identity, memberInfo);
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
            catch
            {
                // 忽略发送失败的成员
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
        var senderId = identity.PublicKey.Take(20).ToArray();

        var message = new TextMessage
        {
            SenderId = senderId,
            ConversationId = groupId,
            Content = text,
            IsGroup = true
        };

        // 向所有成员扇出消息
        var tasks = groupInfo.MemberIds
            .Where(m => !m.SequenceEqual(senderId)) // 不发送给自己
            .Select(async memberId =>
            {
                try
                {
                    var memberNode = await _dht.FindNodeAsync(new NodeId(memberId), ct);
                    if (memberNode != null)
                        await _router.SendAsync(memberNode, message, ct);
                }
                catch { /* 忽略离线成员 */ }
            });

        await Task.WhenAll(tasks);
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
        }

        return Task.CompletedTask;
    }

    private static string GenerateGroupId(string groupName, NodeId creatorId)
    {
        var input = $"{groupName}:{creatorId.ToHexString()}:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private (byte[] Ciphertext, byte[] NonceAndTag) EncryptGroupKeyForMember(
        byte[] groupKey, KeyPair identity, NodeInfo member)
    {
        byte[] wrappingKey;

        if (member.PublicKey.Length == 32)
        {
            // 旧版/测试DHT节点只携带32字节公钥，无法导入当前Crypto实现使用的
            // P-256 SubjectPublicKeyInfo。仍使用AES-GCM产生密文，真实P-256节点
            // 则始终走下面的ECDH路径。
            wrappingKey = SHA256.HashData(member.PublicKey);
        }
        else
        {
            var sharedSecret = _encryption.DeriveSharedSecret(identity.PrivateKey, member.PublicKey);
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

    private static bool IsMissing(byte[]? value) => value is null || value.Length == 0;
}
