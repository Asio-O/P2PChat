using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using P2PChat.Chat.Handlers;
using P2PChat.Chat.Routing;
using P2PChat.Chat.Tests.Support;
using P2PChat.Core.Abstractions;
using P2PChat.Core.Enums;
using P2PChat.Core.Extensions;
using P2PChat.Core.Models;
using P2PChat.Crypto.Encryption;
using Shouldly;

namespace P2PChat.Chat.Tests;

/// <summary>
/// 消息处理器的单元测试：私聊解密、群消息解密、群邀请解密、密钥交换响应。
/// <para>
/// 这些分支的共同价值是<b>「解密失败必须静默丢弃，绝不把明文/垃圾当成聊天内容上报」</b>；
/// 其中群消息与群邀请的加密是 2026-09-21 才补上的，历史上 Content 走明文，
/// 因此这里显式锁住"明文载荷会被拒"这一回归。
/// </para>
/// </summary>
public class MessageHandlerTests
{
    private static readonly IEncryptionService Crypto =
        new AesGcmEncryptionService(NullLogger<AesGcmEncryptionService>.Instance);

    private static readonly ISerializer Wire = new MessagePackSerializer();

    private static readonly byte[] GroupKey = CreateKey();
    private static readonly byte[] OtherKey = CreateKey();

    private static byte[] CreateKey()
    {
        var key = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(key);
        return key;
    }

    private static InMemoryKeyStore NewKeyStore() => new(Crypto);

    private static MessageEnvelope AnyEnvelope(NodeId sender, MessageType type = MessageType.PrivateText)
        => new()
        {
            MessageType = type,
            SenderId = sender.ToByteArray(),
            Payload = []
        };

    #region 私聊处理器

    [Fact]
    public async Task 私聊消息_会话密钥存在时解密并上报事件_标记为入站非群聊()
    {
        var ks = NewKeyStore();
        var sender = NodeId.CreateRandom();
        ks.SetSessionKey(sender, GroupKey);
        var events = new CapturingChatEventPublisher();
        var handler = new PrivateMessageHandler(Crypto, ks, events, NullLogger<PrivateMessageHandler>.Instance);

        const string plain = "你好，加密私聊";
        await handler.HandleAsync(new TextMessage
        {
            SenderId = sender.ToByteArray(),
            ConversationId = "conv-1",
            Content = Convert.ToBase64String(Crypto.Encrypt(Encoding.UTF8.GetBytes(plain), GroupKey)),
            IsGroup = false
        }, new FakeTcpConnection(), AnyEnvelope(sender));

        var evt = await AsyncStream.FirstAsync(events.Events);
        evt.Content.ShouldBe(plain);
        evt.SenderId.ShouldBe(sender);
        evt.ConversationId.ShouldBe("conv-1");
        evt.IsGroup.ShouldBeFalse();
        evt.IsOutgoing.ShouldBeFalse();
    }

    [Fact]
    public async Task 私聊消息_缺少会话密钥时丢弃_不上报任何事件()
    {
        var ks = NewKeyStore();
        var sender = NodeId.CreateRandom();
        var events = new CapturingChatEventPublisher();
        var handler = new PrivateMessageHandler(Crypto, ks, events, NullLogger<PrivateMessageHandler>.Instance);

        await handler.HandleAsync(new TextMessage
        {
            SenderId = sender.ToByteArray(),
            ConversationId = "conv-1",
            Content = Convert.ToBase64String(Crypto.Encrypt("密文"u8.ToArray(), GroupKey)),
            IsGroup = false
        }, new FakeTcpConnection(), AnyEnvelope(sender));

        events.Published.ShouldBeEmpty("缺少会话密钥的私聊不得投递任何事件 —— 否则用户会看到一条无法解密的垃圾");
    }

    [Fact]
    public async Task 私聊消息_会话密钥长度不是32字节时丢弃()
    {
        var ks = NewKeyStore();
        var sender = NodeId.CreateRandom();
        ks.SetSessionKey(sender, new byte[16]);
        var events = new CapturingChatEventPublisher();
        var handler = new PrivateMessageHandler(Crypto, ks, events, NullLogger<PrivateMessageHandler>.Instance);

        await handler.HandleAsync(new TextMessage
        {
            SenderId = sender.ToByteArray(),
            ConversationId = "conv-1",
            Content = Convert.ToBase64String(Crypto.Encrypt("密文"u8.ToArray(), GroupKey)),
            IsGroup = false
        }, new FakeTcpConnection(), AnyEnvelope(sender));

        events.Published.ShouldBeEmpty("会话密钥长度非法的私聊不得投递任何事件");
    }

    [Fact]
    public async Task 私聊消息_Content是明文时解密失败被丢弃_不得原样显示()
    {
        var ks = NewKeyStore();
        var sender = NodeId.CreateRandom();
        ks.SetSessionKey(sender, GroupKey);
        var events = new CapturingChatEventPublisher();
        var handler = new PrivateMessageHandler(Crypto, ks, events, NullLogger<PrivateMessageHandler>.Instance);

        await handler.HandleAsync(new TextMessage
        {
            SenderId = sender.ToByteArray(),
            ConversationId = "conv-1",
            Content = "这是一条没有加密的明文私聊",
            IsGroup = false
        }, new FakeTcpConnection(), AnyEnvelope(sender));

        events.Published.ShouldBeEmpty("Content 是明文的私聊解密必失败，必须丢弃且不得原样显示给用户");
    }

    #endregion

    #region 群消息处理器

    [Fact]
    public async Task 群消息_群密钥存在时解密并上报事件_标记为群聊入站()
    {
        var ks = NewKeyStore();
        var sender = NodeId.CreateRandom();
        ks.SetGroupKey("g-1", GroupKey);
        var events = new CapturingChatEventPublisher();
        var handler = new GroupMessageHandler(Crypto, ks, events, NullLogger<GroupMessageHandler>.Instance);

        const string plain = "群里的加密消息";
        await handler.HandleAsync(new TextMessage
        {
            SenderId = sender.ToByteArray(),
            ConversationId = "g-1",
            Content = Convert.ToBase64String(Crypto.Encrypt(Encoding.UTF8.GetBytes(plain), GroupKey)),
            IsGroup = true
        }, new FakeTcpConnection(), AnyEnvelope(sender));

        var evt = await AsyncStream.FirstAsync(events.Events);
        evt.Content.ShouldBe(plain);
        evt.SenderId.ShouldBe(sender);
        evt.ConversationId.ShouldBe("g-1");
        evt.IsGroup.ShouldBeTrue();
        evt.IsOutgoing.ShouldBeFalse();
    }

    [Fact]
    public async Task 群消息_Content是明文时必须丢弃_群聊同样要求端到端加密()
    {
        var ks = NewKeyStore();
        ks.SetGroupKey("g-1", GroupKey);
        var events = new CapturingChatEventPublisher();
        var handler = new GroupMessageHandler(Crypto, ks, events, NullLogger<GroupMessageHandler>.Instance);

        await handler.HandleAsync(new TextMessage
        {
            SenderId = NodeId.CreateRandom().ToByteArray(),
            ConversationId = "g-1",
            Content = "群里的明文消息",
            IsGroup = true
        }, new FakeTcpConnection(), AnyEnvelope(NodeId.CreateRandom()));

        events.Published.ShouldBeEmpty("Content 是明文的群消息解密必失败 —— 群聊同样要求端到端加密，不得明文显示");
    }

    [Fact]
    public async Task 群消息_密文由别的群密钥加密时认证失败被丢弃()
    {
        var ks = NewKeyStore();
        ks.SetGroupKey("g-1", GroupKey);
        var events = new CapturingChatEventPublisher();
        var handler = new GroupMessageHandler(Crypto, ks, events, NullLogger<GroupMessageHandler>.Instance);

        await handler.HandleAsync(new TextMessage
        {
            SenderId = NodeId.CreateRandom().ToByteArray(),
            ConversationId = "g-1",
            Content = Convert.ToBase64String(Crypto.Encrypt("串群密文"u8.ToArray(), OtherKey)),
            IsGroup = true
        }, new FakeTcpConnection(), AnyEnvelope(NodeId.CreateRandom()));

        events.Published.ShouldBeEmpty("用别的群密钥加密的群消息认证必失败，必须丢弃");
    }

    [Fact]
    public async Task 群消息_缺少群密钥时丢弃_不上报任何事件()
    {
        var ks = NewKeyStore();
        var events = new CapturingChatEventPublisher();
        var handler = new GroupMessageHandler(Crypto, ks, events, NullLogger<GroupMessageHandler>.Instance);

        await handler.HandleAsync(new TextMessage
        {
            SenderId = NodeId.CreateRandom().ToByteArray(),
            ConversationId = "没加入的群",
            Content = Convert.ToBase64String(Crypto.Encrypt("密文"u8.ToArray(), GroupKey)),
            IsGroup = true
        }, new FakeTcpConnection(), AnyEnvelope(NodeId.CreateRandom()));

        events.Published.ShouldBeEmpty("缺少群密钥的群消息不得投递任何事件");
    }

    #endregion

    #region 群邀请处理器

    /// <summary>
    /// 造一条"线路上合法"的群邀请：包装密钥按接收方视角真实地由 ECDH+HKDF 派生，
    /// 线路格式为 <c>[12B nonce][32B 密文主体][16B tag]</c>（nonce/tag 拆到追加字段）。
    /// </summary>
    private static GroupInviteMessage ValidInvite(
        KeyPair creatorKeys, KeyPair recipientKeys, byte[] groupKey,
        byte[]? overrideCiphertext = null)
    {
        var wrappingKey = Crypto.DeriveSessionKey(
            Crypto.DeriveSharedSecret(recipientKeys.PrivateKey, creatorKeys.PublicKey));
        var raw = Crypto.Encrypt(groupKey, wrappingKey);
        return new GroupInviteMessage
        {
            SenderId = NodeId.FromPublicKey(creatorKeys.PublicKey).ToByteArray(),
            ConversationId = "g-invite",
            GroupId = "g-invite",
            GroupName = "受邀群",
            EncryptedGroupKey = overrideCiphertext ?? raw[12..^16],
            MemberIds = [NodeId.FromPublicKey(creatorKeys.PublicKey).ToByteArray()],
            SenderPublicKey = creatorKeys.PublicKey,
            EncryptedGroupKeyNonceAndTag = [.. raw[..12], .. raw[^16..]]
        };
    }

    [Fact]
    public async Task 群邀请_字段合法时写入群密钥并上报邀请事件()
    {
        var creator = Crypto.GenerateKeyPair();
        var recipientKs = new InMemoryKeyStore(Crypto, Crypto.GenerateKeyPair());
        var recipient = recipientKs.GetOrCreateIdentity();
        var handler = new GroupInviteHandler(Crypto, recipientKs, NullLogger<GroupInviteHandler>.Instance);
        var creatorId = NodeId.FromPublicKey(creator.PublicKey);

        await handler.HandleAsync(
            ValidInvite(creator, recipient, GroupKey), new FakeTcpConnection(),
            AnyEnvelope(creatorId, MessageType.GroupInvite));

        recipientKs.GetGroupKey("g-invite").ShouldBe(GroupKey);
        var evt = await AsyncStream.FirstAsync(handler.OnInviteReceived);
        evt.GroupName.ShouldBe("受邀群");
        evt.GroupId.ShouldBe("g-invite");
    }

    [Fact]
    public async Task 群邀请_密文主体长度不是32字节时丢弃邀请()
    {
        var creator = Crypto.GenerateKeyPair();
        var ks = new InMemoryKeyStore(Crypto, Crypto.GenerateKeyPair());
        var handler = new GroupInviteHandler(Crypto, ks, NullLogger<GroupInviteHandler>.Instance);
        var creatorId = NodeId.FromPublicKey(creator.PublicKey);
        var recipient = ks.GetOrCreateIdentity();

        await handler.HandleAsync(
            ValidInvite(creator, recipient, GroupKey, overrideCiphertext: new byte[16]),
            new FakeTcpConnection(), AnyEnvelope(creatorId, MessageType.GroupInvite));

        ks.GetGroupKey("g-invite").ShouldBeNull();
        await Should.ThrowAsync<TimeoutException>(
            () => AsyncStream.FirstAsync(handler.OnInviteReceived, timeoutMs: 300));
    }

    [Fact]
    public async Task 群邀请_缺少nonce_tag元数据时丢弃邀请()
    {
        var creator = Crypto.GenerateKeyPair();
        var ks = new InMemoryKeyStore(Crypto, Crypto.GenerateKeyPair());
        var handler = new GroupInviteHandler(Crypto, ks, NullLogger<GroupInviteHandler>.Instance);
        var creatorId = NodeId.FromPublicKey(creator.PublicKey);
        var invite = ValidInvite(creator, ks.GetOrCreateIdentity(), GroupKey)
            with { EncryptedGroupKeyNonceAndTag = null };

        await handler.HandleAsync(invite, new FakeTcpConnection(), AnyEnvelope(creatorId, MessageType.GroupInvite));

        ks.GetGroupKey("g-invite").ShouldBeNull();
        await Should.ThrowAsync<TimeoutException>(
            () => AsyncStream.FirstAsync(handler.OnInviteReceived, timeoutMs: 300));
    }

    [Fact]
    public async Task 群邀请_发送方公钥长度不是P256的91字节时丢弃邀请()
    {
        var creator = Crypto.GenerateKeyPair();
        var ks = new InMemoryKeyStore(Crypto, Crypto.GenerateKeyPair());
        var handler = new GroupInviteHandler(Crypto, ks, NullLogger<GroupInviteHandler>.Instance);
        var creatorId = NodeId.FromPublicKey(creator.PublicKey);
        var invite = ValidInvite(creator, ks.GetOrCreateIdentity(), GroupKey)
            with { SenderPublicKey = new byte[32] };

        await handler.HandleAsync(invite, new FakeTcpConnection(), AnyEnvelope(creatorId, MessageType.GroupInvite));

        ks.GetGroupKey("g-invite").ShouldBeNull();
        await Should.ThrowAsync<TimeoutException>(
            () => AsyncStream.FirstAsync(handler.OnInviteReceived, timeoutMs: 300));
    }

    [Fact]
    public async Task 群邀请_密文由他人公钥包装时ECDH解不开_丢弃且不落密钥()
    {
        var creator = Crypto.GenerateKeyPair();
        var ks = new InMemoryKeyStore(Crypto, Crypto.GenerateKeyPair());
        var handler = new GroupInviteHandler(Crypto, ks, NullLogger<GroupInviteHandler>.Instance);
        // 公钥元数据被换成了别人的公钥：ECDH 派生的包装密钥对不上，AES-GCM 认证必然失败
        var invite = ValidInvite(creator, ks.GetOrCreateIdentity(), GroupKey)
            with { SenderPublicKey = Crypto.GenerateKeyPair().PublicKey };

        await handler.HandleAsync(invite, new FakeTcpConnection(),
            AnyEnvelope(NodeId.CreateRandom(), MessageType.GroupInvite));

        ks.GetGroupKey("g-invite").ShouldBeNull();
        await Should.ThrowAsync<TimeoutException>(
            () => AsyncStream.FirstAsync(handler.OnInviteReceived, timeoutMs: 300));
    }

    #endregion

    #region 密钥交换处理器

    [Fact]
    public async Task 密钥交换请求_必须沿同一连接回写一条已签名的响应()
    {
        var responderKs = NewKeyStore();
        var responder = responderKs.GetOrCreateIdentity();
        var handler = new KeyExchangeHandler(
            Crypto, responderKs, NullLogger<KeyExchangeHandler>.Instance);

        var initiatorEphemeral = Crypto.GenerateKeyPair();
        var initiatorId = NodeId.FromPublicKey(initiatorEphemeral.PublicKey);
        var link = new FakeTcpConnection();

        await handler.HandleAsync(new KeyExchangeMessage
        {
            SenderId = initiatorId.ToByteArray(),
            ConversationId = "kx",
            EphemeralPublicKey = initiatorEphemeral.PublicKey,
            IsResponse = false
        }, link, AnyEnvelope(initiatorId));

        link.SentFrames.Count.ShouldBe(1, "响应必须走收到请求的同一条连接，而不是只写事件流");
        var response = MessageRouter.DeserializeEnvelope(link.SentFrames[0]);
        response.MessageType.ShouldBe(MessageType.KeyExchange);
        response.SenderId.ShouldBe(responder.NodeId.ToByteArray(), "响应方身份必须是本节点真实 NodeId");
        MessageRouter.VerifyEnvelope(response, Crypto, out var reason).ShouldBeTrue(reason);

        var payload = (KeyExchangeMessage)Wire.Deserialize<Message>(response.Payload);
        payload.IsResponse.ShouldBeTrue();
        payload.ConversationId.ShouldBe("kx");
    }

    [Fact]
    public async Task 密钥交换请求_双方必须派生出同一会话密钥()
    {
        var responderKs = NewKeyStore();
        var handler = new KeyExchangeHandler(
            Crypto, responderKs, NullLogger<KeyExchangeHandler>.Instance);

        var initiatorEphemeral = Crypto.GenerateKeyPair();
        var initiatorId = NodeId.FromPublicKey(initiatorEphemeral.PublicKey);
        var link = new FakeTcpConnection();

        await handler.HandleAsync(new KeyExchangeMessage
        {
            SenderId = initiatorId.ToByteArray(),
            ConversationId = "kx",
            EphemeralPublicKey = initiatorEphemeral.PublicKey,
            IsResponse = false
        }, link, AnyEnvelope(initiatorId));

        var responderSessionKey = responderKs.GetSessionKey(initiatorId);
        responderSessionKey.ShouldNotBeNull();
        responderSessionKey!.Length.ShouldBe(32);

        // 发起方视角：从响应里取对端临时公钥，算出的会话密钥必须一致
        var response = MessageRouter.DeserializeEnvelope(link.SentFrames[0]);
        var payload = (KeyExchangeMessage)Wire.Deserialize<Message>(response.Payload);
        var shared = Crypto.DeriveSharedSecret(initiatorEphemeral.PrivateKey, payload.EphemeralPublicKey);
        Crypto.DeriveSessionKey(shared).ShouldBe(responderSessionKey);
    }

    [Fact]
    public async Task 密钥交换响应_不回写连接_也不自行写入会话密钥()
    {
        var ks = NewKeyStore();
        var handler = new KeyExchangeHandler(Crypto, ks, NullLogger<KeyExchangeHandler>.Instance);
        var peer = Crypto.GenerateKeyPair();
        var peerId = NodeId.FromPublicKey(peer.PublicKey);
        var link = new FakeTcpConnection();

        await handler.HandleAsync(new KeyExchangeMessage
        {
            SenderId = peerId.ToByteArray(),
            ConversationId = "kx",
            EphemeralPublicKey = peer.PublicKey,
            IsResponse = true
        }, link, AnyEnvelope(peerId));

        link.SentFrames.ShouldBeEmpty();
        ks.GetSessionKey(peerId).ShouldBeNull(
            "发起方持有自己的临时私钥，由发起方负责派生并写入会话密钥");
    }

    #endregion
}

