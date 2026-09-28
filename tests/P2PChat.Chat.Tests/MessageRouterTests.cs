using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
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
/// 消息路由器 <c>MessageRouter</c> 的单元测试。
/// <para>
/// 锁住四组契约：信封线路格式往返（含公钥/签名缺失与空载荷）、出站信封的发送方身份强制覆盖、
/// 验签防冒名/防篡改、连接池复用与断线重连。
/// 用真实 <c>MessageRouter</c> + 真实 <c>AesGcmEncryptionService</c>，只在传输层用替身。
/// </para>
/// </summary>
public class MessageRouterTests
{
    private static readonly ISerializer Wire = new MessagePackSerializer();

    private sealed record Harness(
        MessageRouter Router,
        FakeTcpTransport Transport,
        InMemoryKeyStore KeyStore,
        IEncryptionService Crypto,
        KeyPair Identity,
        NodeId LocalNodeId,
        NodeInfo Peer);

    private static Harness Build()
    {
        var crypto = new AesGcmEncryptionService(NullLogger<AesGcmEncryptionService>.Instance);
        var keyStore = new InMemoryKeyStore(crypto);
        var identity = keyStore.GetOrCreateIdentity();
        var transport = new FakeTcpTransport();
        // 传输层每次拨号都返回一条全新连接，贴近真实 TCP 语义
        transport.ConnectionFactory = ep => new FakeTcpConnection(ep);
        var peer = TestNodes.LegacyPublicKeyNode(port: 47001);
        var router = TestRouters.Create(transport, Wire, crypto, keyStore);
        return new Harness(router, transport, keyStore, crypto, identity, identity.NodeId, peer);
    }

    /// <summary>造一个"对端"身份：公钥与其真实派生的 NodeId 必须成对使用。</summary>
    private static (KeyPair Keys, NodeId NodeId) NewPeerIdentity(Harness h)
    {
        var keys = h.Crypto.GenerateKeyPair();
        return (keys, NodeId.FromPublicKey(keys.PublicKey));
    }

    /// <summary>用对端私钥为一条报文签名，得到一条走网络时合法的信封。</summary>
    private static MessageEnvelope Sign(
        Harness h, (KeyPair Keys, NodeId NodeId) peer, MessageType type, Message payload)
    {
        var unsigned = new MessageEnvelope
        {
            MessageType = type,
            SequenceNumber = 42,
            SenderId = peer.NodeId.ToByteArray(),
            SenderPublicKey = peer.Keys.PublicKey,
            Payload = Wire.Serialize<Message>(payload)
        };
        return MessageRouter.SignEnvelope(unsigned, peer.Keys, h.Crypto);
    }

    [Fact]
    public void 信封序列化往返_所有字段逐字保持()
    {
        var original = new MessageEnvelope
        {
            Version = 1,
            MessageType = MessageType.GroupInvite,
            SequenceNumber = 0xDEADBEEF,
            SenderId = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray(),
            MessageId = Guid.NewGuid(),
            Timestamp = 1_726_000_000_000L,
            SenderPublicKey = Enumerable.Range(0, 91).Select(i => (byte)(255 - i)).ToArray(),
            Signature = Enumerable.Range(0, 64).Select(i => (byte)(i * 3)).ToArray(),
            Payload = "群密钥密文载荷"u8.ToArray()
        };

        var restored = MessageRouter.DeserializeEnvelope(MessageRouter.SerializeEnvelope(original));

        restored.Version.ShouldBe(original.Version);
        restored.MessageType.ShouldBe(original.MessageType);
        restored.SequenceNumber.ShouldBe(original.SequenceNumber);
        restored.SenderId.ShouldBe(original.SenderId);
        restored.MessageId.ShouldBe(original.MessageId);
        restored.Timestamp.ShouldBe(original.Timestamp);
        restored.SenderPublicKey.ShouldBe(original.SenderPublicKey);
        restored.Signature.ShouldBe(original.Signature);
        restored.Payload.ShouldBe(original.Payload);
    }

    [Fact]
    public void 信封往返_公钥与签名缺失时反序列化为null而不是空数组()
    {
        var original = new MessageEnvelope
        {
            MessageType = MessageType.PrivateText,
            SequenceNumber = 7,
            SenderId = new byte[20],
            Payload = [1, 2, 3]
        };

        var restored = MessageRouter.DeserializeEnvelope(MessageRouter.SerializeEnvelope(original));

        restored.SenderPublicKey.ShouldBeNull();
        restored.Signature.ShouldBeNull();
        restored.Payload.ShouldBe(original.Payload);
    }

    [Fact]
    public void 信封往返_空载荷不串位_长度前缀字段仍能正确切分()
    {
        var original = new MessageEnvelope
        {
            MessageType = MessageType.DeliveryAck,
            SequenceNumber = 1,
            SenderId = new byte[20],
            SenderPublicKey = [9, 9],
            Signature = [8, 8],
            Payload = []
        };

        var restored = MessageRouter.DeserializeEnvelope(MessageRouter.SerializeEnvelope(original));

        restored.SenderPublicKey.ShouldBe(new byte[] { 9, 9 });
        restored.Signature.ShouldBe(new byte[] { 8, 8 });
        restored.Payload.ShouldBeEmpty();
    }

    [Fact]
    public async Task 出站消息_SenderId与SenderPublicKey被强制覆盖为keyStore的真实身份()
    {
        var h = Build();
        var link = new FakeTcpConnection(h.Peer.EndPoint);
        h.Transport.ConnectionFactory = _ => link;

        // 消息体里的 SenderId 是伪造的 20 字节零值
        await h.Router.SendAsync(h.Peer, new TextMessage
        {
            SenderId = new byte[20], ConversationId = "c1", Content = "你好", IsGroup = false
        });

        var envelope = MessageRouter.DeserializeEnvelope(link.SentFrames.Single());
        envelope.SenderId.ShouldBe(h.LocalNodeId.ToByteArray(),
            "信封 SenderId 必须由 keyStore 身份决定，不能由 Message.SenderId 决定");
        envelope.SenderPublicKey.ShouldBe(h.Identity.PublicKey);
        MessageRouter.VerifyEnvelope(envelope, h.Crypto, out var reason).ShouldBeTrue(reason);
    }

    [Fact]
    public async Task 出站消息_序列号逐条递增_且必须连到对端解析出的那个端点()
    {
        var h = Build();
        var link = new FakeTcpConnection(h.Peer.EndPoint);
        h.Transport.ConnectionFactory = _ => link;

        await h.Router.SendAsync(h.Peer, new TextMessage
        {
            SenderId = new byte[20], ConversationId = "c1", Content = "第一条", IsGroup = false
        });
        await h.Router.SendAsync(h.Peer, new TextMessage
        {
            SenderId = new byte[20], ConversationId = "c1", Content = "第二条", IsGroup = false
        });

        var envelopes = link.SentFrames.Select(f => MessageRouter.DeserializeEnvelope(f)).ToList();
        envelopes.Count.ShouldBe(2);
        envelopes[1].SequenceNumber.ShouldBeGreaterThan(envelopes[0].SequenceNumber);
        var payload = (TextMessage)Wire.Deserialize<Message>(envelopes[1].Payload);
        payload.Content.ShouldBe("第二条");
        payload.ConversationId.ShouldBe("c1");
    }

    [Fact]
    public async Task 出站连接_必须向对端NodeInfo的显式端点发起_不自行解析地址()
    {
        var crypto = new AesGcmEncryptionService(NullLogger<AesGcmEncryptionService>.Instance);
        var keyStore = new InMemoryKeyStore(crypto);
        var dialed = new List<IPEndPoint>();
        var transport = new Mock<ITcpTransport>();
        transport.Setup(t => t.ConnectAsync(It.IsAny<IPEndPoint>(), It.IsAny<CancellationToken>()))
                 .Callback<IPEndPoint, CancellationToken>((ep, _) => dialed.Add(ep))
                 .ReturnsAsync((ITcpConnection)new FakeTcpConnection());

        // 显式端点：手工 /add 登记的静态对端地址
        var peer = new NodeInfo
        {
            NodeId = NodeId.CreateRandom(),
            EndPoint = new IPEndPoint(IPAddress.Parse("203.0.113.7"), 51234),
            PublicKey = new byte[32]
        };
        var router = TestRouters.Create(transport.Object, Wire, crypto, keyStore);

        await router.SendAsync(peer, new TextMessage
        {
            SenderId = new byte[20], ConversationId = "c", Content = "x", IsGroup = false
        });

        transport.Verify(t => t.ConnectAsync(peer.EndPoint, It.IsAny<CancellationToken>()), Times.Once);
        dialed.ShouldBe(new[] { peer.EndPoint });
    }

    [Fact]
    public void 验签_篡改SenderId后必须被拒绝_原因是不匹配()
    {
        var h = Build();
        var peer = NewPeerIdentity(h);
        var envelope = Sign(h, peer, MessageType.PrivateText, new TextMessage
        {
            SenderId = peer.NodeId.ToByteArray(), ConversationId = "c", Content = "x", IsGroup = false
        });
        MessageRouter.VerifyEnvelope(envelope, h.Crypto, out _).ShouldBeTrue("基线：原信封本应验签通过");

        var tampered = envelope with { SenderId = NodeId.CreateRandom().ToByteArray() };

        MessageRouter.VerifyEnvelope(tampered, h.Crypto, out var reason).ShouldBeFalse();
        reason.ShouldBe("SenderId 与 SenderPublicKey 不匹配");
    }

    [Fact]
    public void 验签_篡改载荷后必须被拒绝_ECDSA认证失败()
    {
        var h = Build();
        var peer = NewPeerIdentity(h);
        var envelope = Sign(h, peer, MessageType.GroupText, new TextMessage
        {
            SenderId = peer.NodeId.ToByteArray(), ConversationId = "g", Content = "原文", IsGroup = true
        });
        MessageRouter.VerifyEnvelope(envelope, h.Crypto, out _).ShouldBeTrue();

        var tampered = envelope with { Payload = Encoding.UTF8.GetBytes("被改写的密文") };

        MessageRouter.VerifyEnvelope(tampered, h.Crypto, out var reason).ShouldBeFalse();
        reason.ShouldBe("ECDSA 验签失败");
    }

    [Fact]
    public void 验签_篡改序列号或时间戳后必须被拒绝_签名覆盖全部头部字段()
    {
        var h = Build();
        var peer = NewPeerIdentity(h);
        var envelope = Sign(h, peer, MessageType.PrivateText, new TextMessage
        {
            SenderId = peer.NodeId.ToByteArray(), ConversationId = "c", Content = "x", IsGroup = false
        });

        MessageRouter.VerifyEnvelope(
            envelope with { SequenceNumber = envelope.SequenceNumber + 1 }, h.Crypto, out _).ShouldBeFalse();
        MessageRouter.VerifyEnvelope(
            envelope with { Timestamp = envelope.Timestamp + 1 }, h.Crypto, out _).ShouldBeFalse();
    }

    [Fact]
    public void 验签_缺签名或缺公钥时必须被拒绝()
    {
        var h = Build();
        var peer = NewPeerIdentity(h);
        var signed = Sign(h, peer, MessageType.PrivateText, new TextMessage
        {
            SenderId = peer.NodeId.ToByteArray(), ConversationId = "c", Content = "x", IsGroup = false
        });

        MessageRouter.VerifyEnvelope(signed with { Signature = null }, h.Crypto, out var noSig)
            .ShouldBeFalse();
        noSig.ShouldBe("缺少签名");

        MessageRouter.VerifyEnvelope(signed with { SenderPublicKey = null }, h.Crypto, out var noPk)
            .ShouldBeFalse();
        noPk.ShouldBe("缺少发送方公钥");
    }

    [Fact]
    public async Task 入站消息_验签失败时不得派发给处理器()
    {
        var h = Build();
        var peer = NewPeerIdentity(h);
        var handler = new RecordingHandler();
        h.Router.RegisterHandler(handler);

        var envelope = Sign(h, peer, MessageType.PrivateText, new TextMessage
        {
            SenderId = peer.NodeId.ToByteArray(), ConversationId = "c", Content = "原文", IsGroup = false
        });
        var tampered = envelope with { Payload = Encoding.UTF8.GetBytes("被改写") };

        await h.Router.RouteIncomingAsync(tampered, new FakeTcpConnection());

        handler.Calls.ShouldBe(0, "验签失败的报文不得进入任何处理器");
    }

    [Fact]
    public async Task 入站消息_未注册处理器时不得抛异常()
    {
        var h = Build();
        var peer = NewPeerIdentity(h);
        var envelope = Sign(h, peer, MessageType.FileChunk, new TextMessage
        {
            SenderId = peer.NodeId.ToByteArray(), ConversationId = "c", Content = "x", IsGroup = false
        });

        await Should.NotThrowAsync(
            () => h.Router.RouteIncomingAsync(envelope, new FakeTcpConnection()));
    }

    [Fact]
    public async Task 入站消息_处理器抛异常时被吞掉_路由器仍可继续使用()
    {
        var h = Build();
        var peer = NewPeerIdentity(h);
        var handler = new ThrowingHandler();
        h.Router.RegisterHandler(handler);

        var envelope = Sign(h, peer, MessageType.PrivateText, new TextMessage
        {
            SenderId = peer.NodeId.ToByteArray(), ConversationId = "c", Content = "x", IsGroup = false
        });

        await Should.NotThrowAsync(
            () => h.Router.RouteIncomingAsync(envelope, new FakeTcpConnection()));
        handler.Calls.ShouldBe(1, "处理器确实被调用过，只是它自己抛了异常");
    }

    [Fact]
    public async Task 处理器注销后同类型报文不再被投递_且不抛异常()
    {
        var h = Build();
        var peer = NewPeerIdentity(h);
        var handler = new RecordingHandler();
        h.Router.RegisterHandler(handler);
        h.Router.UnregisterHandler(MessageType.PrivateText);

        var envelope = Sign(h, peer, MessageType.PrivateText, new TextMessage
        {
            SenderId = peer.NodeId.ToByteArray(), ConversationId = "c", Content = "没人接", IsGroup = false
        });

        await Should.NotThrowAsync(
            () => h.Router.RouteIncomingAsync(envelope, new FakeTcpConnection()));
        handler.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task 同类型处理器重复注册时后者覆盖前者()
    {
        var h = Build();
        var peer = NewPeerIdentity(h);
        var first = new RecordingHandler();
        var second = new RecordingHandler();
        h.Router.RegisterHandler(first);
        h.Router.RegisterHandler(second);

        await h.Router.RouteIncomingAsync(Sign(h, peer, MessageType.PrivateText, new TextMessage
        {
            SenderId = peer.NodeId.ToByteArray(), ConversationId = "c", Content = "x", IsGroup = false
        }), new FakeTcpConnection());

        first.Calls.ShouldBe(0);
        second.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task 载荷SenderId与信封SenderId不一致时必须丢弃_不得进handler()
    {
        // `Message.SenderId` 存在但**不是权威**；权威是 envelope.SenderId。
        // 只要允许两者不一致，下一个读 message.SenderId 的人就会踩 KeyExchangeHandler
        // 踩过的那个坑（会话密钥写进伪造的槽位，静默失败）。
        var h = Build();
        var peer = NewPeerIdentity(h);
        var handler = new RecordingHandler();
        h.Router.RegisterHandler(handler);

        var victim = NewPeerIdentity(h);
        var envelope = Sign(h, peer, MessageType.PrivateText, new TextMessage
        {
            SenderId = victim.NodeId.ToByteArray(),   // 载荷自称是别人
            ConversationId = "c",
            Content = "x",
            IsGroup = false
        });

        // 信封本身是合法签名（对端用自己的私钥签的）—— 所以这条不能退化成
        // 「非法包被拒」的重复测试：唯一的拒绝理由就是载荷/信封不一致。
        MessageRouter.VerifyEnvelope(envelope, h.Crypto, out var reason).ShouldBeTrue(reason);

        await h.Router.RouteIncomingAsync(envelope, new FakeTcpConnection());

        handler.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task 载荷SenderId与信封SenderId一致时正常派发()
    {
        // 与上一条构成对照：检查本身不能误杀合法消息。
        var h = Build();
        var peer = NewPeerIdentity(h);
        var handler = new RecordingHandler();
        h.Router.RegisterHandler(handler);

        await h.Router.RouteIncomingAsync(Sign(h, peer, MessageType.PrivateText, new TextMessage
        {
            SenderId = peer.NodeId.ToByteArray(),
            ConversationId = "c",
            Content = "x",
            IsGroup = false
        }), new FakeTcpConnection());

        handler.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task 连接池_同一节点的多次获取复用同一条连接_不重复建连()
    {
        var h = Build();

        var c1 = await h.Router.GetOrCreateConnectionAsync(h.Peer);
        var c2 = await h.Router.GetOrCreateConnectionAsync(h.Peer);
        var c3 = await h.Router.GetOrCreateConnectionAsync(h.Peer);

        c2.ShouldBeSameAs(c1);
        c3.ShouldBeSameAs(c1);
        h.Transport.ConnectCount.ShouldBe(1);
    }

    [Fact]
    public async Task 连接池_连接已断开时必须重建新连接()
    {
        var h = Build();
        var first = (FakeTcpConnection)await h.Router.GetOrCreateConnectionAsync(h.Peer);
        first.IsConnected = false;

        var second = await h.Router.GetOrCreateConnectionAsync(h.Peer);

        second.ShouldNotBeSameAs(first, "已断开的连接不得被继续复用");
        second.IsConnected.ShouldBeTrue();
        h.Transport.ConnectCount.ShouldBe(2);
    }

    [Fact]
    public async Task 关闭连接_释放旧连接并从池中移除_下次重新建连()
    {
        var h = Build();
        var first = (FakeTcpConnection)await h.Router.GetOrCreateConnectionAsync(h.Peer);

        await h.Router.CloseConnectionAsync(h.Peer.NodeId.ToByteArray());

        first.Disposed.ShouldBeTrue();

        var next = await h.Router.GetOrCreateConnectionAsync(h.Peer);
        next.ShouldNotBeSameAs(first);
        h.Transport.ConnectCount.ShouldBe(2);
    }

    [Fact]
    public async Task 关闭未知节点的连接_静默忽略()
    {
        var h = Build();
        await Should.NotThrowAsync(
            () => h.Router.CloseConnectionAsync(NodeId.CreateRandom().ToByteArray()));
    }

    [Fact]
    public async Task 入站私聊消息_经路由分发到私聊处理器_产出解密后的事件()
    {
        var h = Build();
        var peer = NewPeerIdentity(h);
        var sessionKey = h.Crypto.GenerateRandomKey();
        h.KeyStore.SetSessionKey(peer.NodeId, sessionKey);

        var events = new CapturingChatEventPublisher();
        var handler = new PrivateMessageHandler(
            h.Crypto, h.KeyStore, events, NullLogger<PrivateMessageHandler>.Instance);
        h.Router.RegisterHandler(handler);

        const string plain = "经路由投递的私聊";
        var text = new TextMessage
        {
            SenderId = peer.NodeId.ToByteArray(),
            ConversationId = ConversationId.ForPrivate(h.LocalNodeId, peer.NodeId),
            Content = Convert.ToBase64String(
                h.Crypto.Encrypt(Encoding.UTF8.GetBytes(plain), sessionKey)),
            IsGroup = false
        };

        await h.Router.RouteIncomingAsync(Sign(h, peer, MessageType.PrivateText, text), new FakeTcpConnection());

        var evt = await AsyncStream.FirstAsync(events.Events);
        evt.Content.ShouldBe(plain);
        evt.SenderId.ShouldBe(peer.NodeId);
        evt.IsGroup.ShouldBeFalse();
        evt.IsOutgoing.ShouldBeFalse();
        evt.ConversationId.ShouldBe(text.ConversationId);
    }

    [Fact]
    public async Task 群消息与私聊按信封类型分派_私聊不会串进群消息处理器()
    {
        var h = Build();
        var peer = NewPeerIdentity(h);
        var groupKey = h.Crypto.GenerateRandomKey();
        const string groupId = "g-1234";
        h.KeyStore.SetGroupKey(groupId, groupKey);

        var events = new CapturingChatEventPublisher();
        var handler = new GroupMessageHandler(
            h.Crypto, h.KeyStore, events, NullLogger<GroupMessageHandler>.Instance);
        h.Router.RegisterHandler(handler);

        // 私聊报文：信封类型是 PrivateText，而这里只注册了 GroupText 处理器
        var privateText = new TextMessage
        {
            SenderId = peer.NodeId.ToByteArray(),
            ConversationId = ConversationId.ForPrivate(h.LocalNodeId, peer.NodeId),
            Content = Convert.ToBase64String(h.Crypto.Encrypt("私聊"u8.ToArray(), groupKey)),
            IsGroup = false
        };
        await h.Router.RouteIncomingAsync(
            Sign(h, peer, MessageType.PrivateText, privateText), new FakeTcpConnection());

        var groupText = new TextMessage
        {
            SenderId = peer.NodeId.ToByteArray(),
            ConversationId = groupId,
            Content = Convert.ToBase64String(h.Crypto.Encrypt("群消息"u8.ToArray(), groupKey)),
            IsGroup = true
        };
        await h.Router.RouteIncomingAsync(
            Sign(h, peer, MessageType.GroupText, groupText), new FakeTcpConnection());

        var evt = await AsyncStream.FirstAsync(events.Events);
        evt.Content.ShouldBe("群消息", "只有 GroupText 才应进入群消息处理器");
        evt.IsGroup.ShouldBeTrue();
        evt.ConversationId.ShouldBe(groupId);
        evt.SenderId.ShouldBe(peer.NodeId);
    }

    private class RecordingHandler : IMessageHandler<TextMessage>
    {
        public MessageType MessageType => MessageType.PrivateText;

        public int Calls { get; protected set; }

        public virtual Task HandleAsync(
            TextMessage message, ITcpConnection senderConnection,
            MessageEnvelope envelope, CancellationToken ct = default)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingHandler : RecordingHandler
    {
        public override Task HandleAsync(
            TextMessage message, ITcpConnection senderConnection,
            MessageEnvelope envelope, CancellationToken ct = default)
        {
            Calls++;
            throw new InvalidOperationException("处理器内部炸了");
        }
    }
}
