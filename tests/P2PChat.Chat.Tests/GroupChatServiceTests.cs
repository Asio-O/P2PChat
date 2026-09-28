using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using P2PChat.Chat.Handlers;
using P2PChat.Chat.Services;
using P2PChat.Chat.Tests.Support;
using P2PChat.Core.Abstractions;
using P2PChat.Core.Enums;
using P2PChat.Core.Models;
using P2PChat.Crypto.Encryption;
using Shouldly;

namespace P2PChat.Chat.Tests;

/// <summary>
/// 群聊服务 <c>GroupChatService</c> 的单元测试。
/// <para>
/// 重点不是"再测一遍发了几条消息"，而是锁住群密钥的<b>加解密契约</b>与<b>持久化/恢复契约</b>：
/// 邀请里的密文必须能被成员侧真实 <c>GroupInviteHandler</c> 用 ECDH 还原成同一个群密钥；
/// 群消息上线必须是群密钥的 AES-GCM 密文；重建服务时不得用磁盘上的旧密钥覆盖 keyStore 里更新的密钥。
/// </para>
/// </summary>
public class GroupChatServiceTests
{
    private const int NonceLength = 12;
    private const int TagLength = 16;

    private sealed record Harness(
        GroupChatService Service,
        RecordingMessageRouter Router,
        StubDhtService Dht,
        InMemoryKeyStore KeyStore,
        RecordingGroupMetadataStore Metadata,
        IEncryptionService Crypto,
        NodeInfo Local);

    private static Harness Build(NodeInfo? local = null)
    {
        var crypto = new AesGcmEncryptionService(NullLogger<AesGcmEncryptionService>.Instance);
        var keyStore = new InMemoryKeyStore(crypto);
        var identity = keyStore.GetOrCreateIdentity();
        local ??= new NodeInfo
        {
            NodeId = identity.NodeId,
            EndPoint = new IPEndPoint(IPAddress.Loopback, 46001),
            PublicKey = identity.PublicKey
        };

        var dht = new StubDhtService(local);
        var router = new RecordingMessageRouter();
        var metadata = new RecordingGroupMetadataStore();
        var service = new GroupChatService(dht, router, crypto, keyStore, metadata);

        return new Harness(service, router, dht, keyStore, metadata, crypto, local);
    }

    [Fact]
    public async Task 创建群组_群密钥写入keyStore并全量落盘到元数据存储()
    {
        var h = Build();

        var group = await h.Service.CreateGroupAsync("落盘群", []);

        group.GroupKey.Length.ShouldBe(32);
        h.KeyStore.GetGroupKey(group.GroupId).ShouldBe(group.GroupKey);
        h.Metadata.SaveCount.ShouldBe(1);
        h.Metadata.Stored(group.GroupId).ShouldNotBeNull();
        h.Metadata.Stored(group.GroupId)!.GroupName.ShouldBe("落盘群");
    }

    [Fact]
    public async Task 创建群组_成员携带真实P256公钥时邀请密文可被成员还原为同一个群密钥()
    {
        var h = Build();
        var memberIdentity = h.Crypto.GenerateKeyPair();
        var memberNode = TestNodes.RealKeyPairNode(memberIdentity, 46101);
        h.Dht.Register(memberNode);

        var group = await h.Service.CreateGroupAsync("加密群", [memberNode.NodeId]);

        var invite = h.Router.Sent.OfType<GroupInviteMessage>().Single();
        // 线路格式固定：32 字节密文主体 + 追加的 12B nonce/16B tag + 91 字节 P-256 公钥
        invite.EncryptedGroupKey.Length.ShouldBe(32);
        invite.EncryptedGroupKeyNonceAndTag!.Length.ShouldBe(NonceLength + TagLength);
        invite.SenderPublicKey!.Length.ShouldBe(91);

        // 成员侧用真实 GroupInviteHandler 还原群密钥
        var memberKs = new InMemoryKeyStore(h.Crypto, memberIdentity);
        var memberHandler = new GroupInviteHandler(h.Crypto, memberKs, NullLogger<GroupInviteHandler>.Instance);
        await memberHandler.HandleAsync(invite, new FakeTcpConnection(), new MessageEnvelope
        {
            MessageType = MessageType.GroupInvite,
            SenderId = invite.SenderId,
            Payload = []
        });

        memberKs.GetGroupKey(invite.GroupId).ShouldBe(group.GroupKey,
            "ECDH + AES-GCM 往返后成员拿到的必须就是创建者的群密钥");
    }

    [Fact]
    public async Task 创建群组_成员只有32字节旧版公钥时走SHA256包装回退_邀请密文仍可还原()
    {
        var h = Build();
        var legacy = TestNodes.LegacyPublicKeyNode(port: 46111);
        h.Dht.Register(legacy);

        await h.Service.CreateGroupAsync("回退群", [legacy.NodeId]);

        var invite = h.Router.Sent.OfType<GroupInviteMessage>().Single();
        // 回退路径没有可用的 P-256 公钥元数据
        invite.SenderPublicKey.ShouldNotBeNull();
        invite.EncryptedGroupKeyNonceAndTag!.Length.ShouldBe(NonceLength + TagLength);

        // 旧版 DHT 节点的包装密钥 = SHA256(其 32 字节公钥)
        // PublicKey 现为可空（"未知" 是显式状态，见 task-4）；此处是测试自建的本地节点，必然非空。
        var wrappingKey = SHA256.HashData(legacy.PublicKey!);
        var full = new byte[NonceLength + 32 + TagLength];
        invite.EncryptedGroupKeyNonceAndTag.AsSpan(0, NonceLength).CopyTo(full);
        invite.EncryptedGroupKey.CopyTo(full.AsSpan(NonceLength));
        invite.EncryptedGroupKeyNonceAndTag.AsSpan(NonceLength, TagLength)
             .CopyTo(full.AsSpan(NonceLength + 32));

        h.Crypto.Decrypt(full, wrappingKey).ShouldBe(
            h.KeyStore.GetGroupKey(invite.GroupId),
            "SHA256(旧版公钥) 回退包装路径必须能被持有该公钥的一方解开");
    }

    [Fact]
    public async Task 发送群消息_上线载荷必须是群密钥的AES_GCM密文_成员可还原明文()
    {
        const string plain = "群里的悄悄话";
        var h = Build();
        var member = TestNodes.LegacyPublicKeyNode(port: 46121);
        h.Dht.Register(member);
        var group = await h.Service.CreateGroupAsync("密文群", [member.NodeId]);
        h.Router.Clear();

        await h.Service.SendGroupMessageAsync(group.GroupId, plain);

        var text = h.Router.Sent.OfType<TextMessage>().Single();
        text.IsGroup.ShouldBeTrue();
        text.ConversationId.ShouldBe(group.GroupId);
        text.Content.ShouldNotContain(plain);
        text.SenderId.ShouldBe(h.Local.NodeId.ToByteArray());

        var groupKey = h.KeyStore.GetGroupKey(group.GroupId)!;
        var ciphertext = Convert.FromBase64String(text.Content);
        ciphertext.Length.ShouldBe(NonceLength + Encoding.UTF8.GetByteCount(plain) + TagLength);
        Encoding.UTF8.GetString(h.Crypto.Decrypt(ciphertext, groupKey)).ShouldBe(plain);
    }

    [Fact]
    public async Task 发送群消息_群密钥丢失时必须报错_不得把明文推上线()
    {
        var h = Build();
        var member = TestNodes.LegacyPublicKeyNode(port: 46131);
        h.Dht.Register(member);
        var group = await h.Service.CreateGroupAsync("掉密钥群", [member.NodeId]);
        h.Router.Clear();
        h.KeyStore.RemoveGroupKey(group.GroupId);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => h.Service.SendGroupMessageAsync(group.GroupId, "不该发出去"));
        ex.Message.ShouldContain("群组密钥");

        h.Router.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task 处理邀请_群密钥尚未解密时必须拒绝_不得把密文登记成群密钥()
    {
        var h = Build();
        var ciphertext = new byte[32];   // 32 字节，但它是密文不是明文
        RandomNumberGenerator.Fill(ciphertext);

        var invite = new GroupInviteMessage
        {
            SenderId = NodeId.CreateRandom().ToByteArray(),
            ConversationId = "g1",
            GroupId = "g1",
            GroupName = "未解密群",
            EncryptedGroupKey = ciphertext,
            MemberIds = [],
            SenderPublicKey = h.Crypto.GenerateKeyPair().PublicKey,
            EncryptedGroupKeyNonceAndTag = new byte[NonceLength + TagLength]
        };

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => h.Service.HandleInviteAsync(invite));
        ex.Message.ShouldContain("尚未解密");

        h.Service.GetGroup("g1").ShouldBeNull();
        h.KeyStore.GetGroupKey("g1").ShouldBeNull();
    }

    [Fact]
    public async Task 处理旧版明文邀请_无公钥无nonce时按32字节明文群密钥登记()
    {
        var h = Build();
        var creator = NodeId.CreateRandom();
        var plainKey = new byte[32];
        RandomNumberGenerator.Fill(plainKey);

        await h.Service.HandleInviteAsync(new GroupInviteMessage
        {
            SenderId = creator.ToByteArray(),
            ConversationId = "legacy-group",
            GroupId = "legacy-group",
            GroupName = "旧版群",
            EncryptedGroupKey = plainKey,
            MemberIds = [creator.ToByteArray()]
        });

        h.KeyStore.GetGroupKey("legacy-group").ShouldBe(plainKey);
        h.Service.GetGroup("legacy-group")!.CreatorId.ShouldBe(creator);
    }

    [Fact]
    public async Task 处理解散通知_同时清空内存群组_群密钥与持久化条目()
    {
        var h = Build();
        var group = await h.Service.CreateGroupAsync("要解散的群", []);

        await h.Service.HandleNotifyAsync(new GroupNotifyMessage
        {
            SenderId = h.Local.NodeId.ToByteArray(),
            ConversationId = group.GroupId,
            GroupId = group.GroupId,
            Action = "dissolve",
            OperatorId = h.Local.NodeId.ToByteArray()
        });

        h.Service.GetGroup(group.GroupId).ShouldBeNull();
        h.Service.GetKnownGroups().ShouldBeEmpty();
        h.KeyStore.GetGroupKey(group.GroupId).ShouldBeNull();
        h.Metadata.Removed.ShouldContain(group.GroupId);
    }

    [Fact]
    public async Task 处理非解散通知_不改动任何群组状态()
    {
        var h = Build();
        var group = await h.Service.CreateGroupAsync("只加入的群", []);

        await h.Service.HandleNotifyAsync(new GroupNotifyMessage
        {
            SenderId = NodeId.CreateRandom().ToByteArray(),
            ConversationId = group.GroupId,
            GroupId = group.GroupId,
            Action = "join",
            OperatorId = NodeId.CreateRandom().ToByteArray()
        });

        h.Service.GetGroup(group.GroupId).ShouldNotBeNull();
        h.KeyStore.GetGroupKey(group.GroupId).ShouldNotBeNull();
        h.Metadata.Removed.ShouldBeEmpty();
    }

    [Fact]
    public void 启动加载_从元数据恢复群组且keyStore缺失时回填群密钥()
    {
        var crypto = new AesGcmEncryptionService(NullLogger<AesGcmEncryptionService>.Instance);
        var keyStore = new InMemoryKeyStore(crypto);
        var metadata = new RecordingGroupMetadataStore();
        var persistedKey = new byte[32];
        RandomNumberGenerator.Fill(persistedKey);
        var creator = NodeId.CreateRandom();
        metadata.Seed(new GroupInfo
        {
            GroupId = "restored",
            GroupName = "重启前的群",
            CreatorId = creator,
            MemberIds = [creator.ToByteArray()],
            GroupKey = persistedKey
        });

        var service = new GroupChatService(
            new StubDhtService(TestNodes.LegacyPublicKeyNode()),
            new RecordingMessageRouter(), crypto, keyStore, metadata);

        service.GetGroup("restored").ShouldNotBeNull();
        service.GetGroup("restored")!.GroupName.ShouldBe("重启前的群");
        keyStore.GetGroupKey("restored").ShouldBe(persistedKey, "keyStore 缺密钥时必须回填");
    }

    [Fact]
    public void 启动加载_keyStore已有更新的群密钥时不得被磁盘上的旧密钥覆盖()
    {
        var crypto = new AesGcmEncryptionService(NullLogger<AesGcmEncryptionService>.Instance);
        var keyStore = new InMemoryKeyStore(crypto);
        var metadata = new RecordingGroupMetadataStore();
        var staleKey = new byte[32];
        var freshKey = new byte[32];
        RandomNumberGenerator.Fill(staleKey);
        RandomNumberGenerator.Fill(freshKey);
        var creator = NodeId.CreateRandom();
        keyStore.SetGroupKey("g", freshKey);
        metadata.Seed(new GroupInfo
        {
            GroupId = "g",
            GroupName = "群",
            CreatorId = creator,
            MemberIds = [],
            GroupKey = staleKey
        });

        var service = new GroupChatService(
            new StubDhtService(TestNodes.LegacyPublicKeyNode()),
            new RecordingMessageRouter(), crypto, keyStore, metadata);

        keyStore.GetGroupKey("g").ShouldBe(freshKey, "磁盘快照可能比内存旧，不得反向覆盖");
    }

    [Fact]
    public void 启动加载_元数据存储抛异常时静默降级为空群组列表()
    {
        var crypto = new AesGcmEncryptionService(NullLogger<AesGcmEncryptionService>.Instance);
        var broken = new ThrowingGroupMetadataStore();

        var service = new GroupChatService(
            new StubDhtService(TestNodes.LegacyPublicKeyNode()),
            new RecordingMessageRouter(), crypto, new InMemoryKeyStore(crypto), broken);

        service.GetKnownGroups().ShouldBeEmpty("持久化损坏不得让节点起不来");
    }

    [Fact]
    public async Task 创建群组_创建者身份必须是本节点真实身份_不得取公钥前20字节()
    {
        var h = Build();

        var group = await h.Service.CreateGroupAsync("身份群", []);

        group.CreatorId.ShouldBe(h.Local.NodeId);
        group.CreatorId.ToByteArray().ShouldNotBe(h.Local.PublicKey!.Take(20).ToArray());
        group.MemberIds.Select(b => Convert.ToHexString(b))
            .ShouldContain(Convert.ToHexString(h.Local.NodeId.ToByteArray()));
    }

    [Fact]
    public async Task 创建群组_成员不可发现时跳过邀请但不抛异常_群仍然建立()
    {
        var h = Build();
        var offline = TestNodes.LegacyPublicKeyNode(port: 46141);   // 未注册

        var group = await h.Service.CreateGroupAsync("含离线成员", [offline.NodeId]);

        h.Router.Sent.OfType<GroupInviteMessage>().ShouldBeEmpty();
        group.MemberIds.Count.ShouldBe(2, "离线成员仍应记入成员名单");
        h.Service.GetGroup(group.GroupId).ShouldNotBeNull();
    }

    [Fact]
    public void 获取群组_未知ID返回null_在线成员默认为空列表()
    {
        var h = Build();

        h.Service.GetGroup("不存在的群").ShouldBeNull();
        h.Service.GetOnlineMembers("不存在的群").ShouldBeEmpty();
        h.Service.GetKnownGroups().ShouldBeEmpty();
    }

    private sealed class ThrowingGroupMetadataStore : IGroupMetadataStore
    {
        public IReadOnlyList<GroupInfo> LoadAll() => throw new IOException("磁盘炸了");

        public void Save(IEnumerable<GroupInfo> groups) => throw new IOException("磁盘炸了");

        public void Remove(string groupId) => throw new IOException("磁盘炸了");
    }
}
