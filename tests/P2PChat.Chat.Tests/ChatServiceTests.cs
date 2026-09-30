using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using P2PChat.Chat.Handlers;
using P2PChat.Chat.Routing;
using P2PChat.Chat.Services;
using P2PChat.Chat.Tests.Support;
using P2PChat.Core.Abstractions;
using P2PChat.Core.Enums;
using P2PChat.Core.Extensions;
using P2PChat.Core.Models;
using P2PChat.Crypto.Encryption;
using Shouldly;

namespace P2PChat.Chat.Tests;

/// <summary>
/// 私聊主管道 <c>ChatService</c> 的单元测试。
/// <para>
/// 锁住的真实分支：目标节点不可发现、会话键有无（决定是否走 ECDH 交换）、
/// 换来的会话键长度不合法、密钥交换响应前混入其它消息、发送方身份必须是本节点真实 NodeId、
/// 线路载荷必须是密文、会话键必须方向无关。
/// </para>
/// </summary>
public class ChatServiceTests
{
    private static readonly ISerializer Wire = new MessagePackSerializer();

    private sealed record Harness(
        ChatService Service,
        RecordingMessageRouter Router,
        StubDhtService Dht,
        InMemoryKeyStore KeyStore,
        IEncryptionService Crypto,
        KeyPair Identity,
        NodeInfo Local,
        NodeInfo Peer);

    /// <param name="peerDiscoverable">对端是否已在 DHT 中登记；false 模拟"目标节点未找到"。</param>
    /// <summary>
    /// 造一个真实的 <see cref="MessageReplayGuard"/>（默认 1h/5min 窗口）。
    /// 刻意不用「永远放行」的替身：那等于测试环境里根本没有重放防护。
    /// </summary>
    private static IReplayGuard NewReplayGuard()
        => new MessageReplayGuard(NullLogger<MessageReplayGuard>.Instance);

    private static Harness Build(bool peerDiscoverable = true)
    {
        var crypto = new AesGcmEncryptionService(NullLogger<AesGcmEncryptionService>.Instance);
        var keyStore = new InMemoryKeyStore(crypto);
        var identity = keyStore.GetOrCreateIdentity();

        var local = new NodeInfo
        {
            NodeId = identity.NodeId,
            EndPoint = new IPEndPoint(IPAddress.Loopback, 45001),
            PublicKey = identity.PublicKey
        };
        var peer = TestNodes.LegacyPublicKeyNode(port: 45002);

        var dht = new StubDhtService(local);
        if (peerDiscoverable) dht.Register(peer);
        var router = new RecordingMessageRouter();
        var service = new ChatService(dht, router, crypto, keyStore, NewReplayGuard(), NullLogger<ChatService>.Instance);

        return new Harness(service, router, dht, keyStore, crypto, identity, local, peer);
    }

    /// <summary>取出唯一一条发往对端的私聊 TextMessage。</summary>
    private static TextMessage SingleText(RecordingMessageRouter router)
    {
        var text = router.Sent.OfType<TextMessage>().ToList();
        text.Count.ShouldBe(1);
        return text[0];
    }

    [Fact]
    public async Task 发送私聊_目标节点DHT不可发现时抛InvalidOperationException_且不发出任何消息()
    {
        var h = Build(peerDiscoverable: false);   // 对端未注册进 DHT

        await Should.ThrowAsync<InvalidOperationException>(
            () => h.Service.SendPrivateMessageAsync(h.Peer.NodeId, "在吗"));

        h.Router.Sent.ShouldBeEmpty();
        h.KeyStore.SessionKeyWrites.ShouldBeEmpty();
    }

    [Fact]
    public void HasSessionKey_仅有当对端会话密钥为32字节时返回真()
    {
        var h = Build();
        h.Service.HasSessionKey(h.Peer.NodeId).ShouldBeFalse("初始没有会话密钥");

        h.KeyStore.SetSessionKey(h.Peer.NodeId, new byte[16]);
        h.Service.HasSessionKey(h.Peer.NodeId).ShouldBeFalse("16 字节不是有效的 AES-256 会话密钥");

        h.KeyStore.SetSessionKey(h.Peer.NodeId, new byte[32]);
        h.Service.HasSessionKey(h.Peer.NodeId).ShouldBeTrue();
    }

    [Fact]
    public async Task 发送私聊_已有会话密钥时跳过密钥交换_只发出一条消息()
    {
        var h = Build();
        h.KeyStore.SetSessionKey(h.Peer.NodeId, h.Crypto.GenerateRandomKey());

        await h.Service.SendPrivateMessageAsync(h.Peer.NodeId, "已建链");

        h.KeyStore.SessionKeyWrites.Count.ShouldBe(1, "只应有测试自己写入的那一次，交换不得再写");
        h.KeyStore.SessionKeyWrites[0].ShouldBe(h.Peer.NodeId.ToHexString());
        h.Router.Sent.OfType<KeyExchangeMessage>().ShouldBeEmpty("有会话密钥时不应再发起密钥交换");
        h.Router.Sent.Count.ShouldBe(1);
    }

    [Fact]
    public async Task 发送私聊_信封SenderId必须是本节点真实身份_不得取公钥前20字节()
    {
        var h = Build();
        h.KeyStore.SetSessionKey(h.Peer.NodeId, h.Crypto.GenerateRandomKey());

        await h.Service.SendPrivateMessageAsync(h.Peer.NodeId, "身份校验");

        var text = SingleText(h.Router);
        text.SenderId.ShouldBe(h.Identity.NodeId.ToByteArray());
        text.SenderId.ShouldNotBe(h.Identity.PublicKey.Take(20).ToArray(),
            "P-256 SPKI 前 20 字节是固定算法头，对每个节点都一样，不能当身份用");
    }

    [Fact]
    public async Task 发送私聊_Content必须是会话密钥加密的AES_GCM密文_不得含明文()
    {
        const string plain = "机密内容-不要上线";
        var h = Build();
        var sessionKey = h.Crypto.GenerateRandomKey();
        h.KeyStore.SetSessionKey(h.Peer.NodeId, sessionKey);

        await h.Service.SendPrivateMessageAsync(h.Peer.NodeId, plain);

        var text = SingleText(h.Router);
        text.Content.ShouldNotContain(plain);
        text.IsGroup.ShouldBeFalse();

        var ciphertext = Convert.FromBase64String(text.Content);
        ciphertext.Length.ShouldBe(12 + Encoding.UTF8.GetByteCount(plain) + 16);
        Encoding.UTF8.GetString(h.Crypto.Decrypt(ciphertext, sessionKey)).ShouldBe(plain);
    }

    [Fact]
    public async Task 发送私聊_必须按DHT解析出的节点连接_不得自行改写端点()
    {
        var h = Build();
        h.KeyStore.SetSessionKey(h.Peer.NodeId, h.Crypto.GenerateRandomKey());

        await h.Service.SendPrivateMessageAsync(h.Peer.NodeId, "端点透传");

        h.Dht.FindNodeCalls.Count.ShouldBe(1, "对端解析必须且只查一次 DHT");
        h.Dht.FindNodeCalls[0].ShouldBe(h.Peer.NodeId.ToHexString());
        h.Router.Recipients.Single().ShouldBe(h.Peer,
            "路由器拿到的必须就是 DHT 解析出的 NodeInfo（含显式/静态端点），Chat 层不得替换");
        h.Router.Recipients.Single().EndPoint.ShouldBe(h.Peer.EndPoint);
    }

    [Fact]
    public async Task 发送私聊_本地事件与线路消息使用同一个方向无关的会话键()
    {
        var h = Build();
        h.KeyStore.SetSessionKey(h.Peer.NodeId, h.Crypto.GenerateRandomKey());

        await h.Service.SendPrivateMessageAsync(h.Peer.NodeId, "方向无关键");

        var onWire = SingleText(h.Router);
        var expected = ConversationId.ForPrivate(h.Local.NodeId, h.Peer.NodeId);

        onWire.ConversationId.ShouldBe(expected);
        // 接收端按「对端 ID」算出的键必须与发送端一致，否则 UI 永远选不中会话桶。
        ConversationId.ForPrivate(h.Peer.NodeId, h.Local.NodeId).ShouldBe(expected);

        var local = await AsyncStream.FirstAsync(h.Service.OnMessageReceived);
        local.ConversationId.ShouldBe(expected);
        local.IsOutgoing.ShouldBeTrue();
        local.IsGroup.ShouldBeFalse();
        local.Content.ShouldBe("方向无关键");
        local.SenderId.ShouldBe(h.Local.NodeId);
    }

    [Fact]
    public async Task 发布收到消息_进入OnMessageReceived事件流且IsOutgoing为假()
    {
        var h = Build();
        var peerConversation = ConversationId.ForPrivate(h.Local.NodeId, h.Peer.NodeId);

        await h.Service.PublishAsync(new ChatMessageEvent
        {
            Content = "收到的消息",
            SenderId = h.Peer.NodeId,
            ConversationId = peerConversation,
            IsGroup = false,
            IsOutgoing = false
        });

        var evt = await AsyncStream.FirstAsync(h.Service.OnMessageReceived);
        evt.Content.ShouldBe("收到的消息");
        evt.SenderId.ShouldBe(h.Peer.NodeId);
        evt.IsOutgoing.ShouldBeFalse();
        evt.ConversationId.ShouldBe(peerConversation);
    }

    [Fact]
    public async Task 发送私聊_无会话密钥时先与真实KeyExchangeHandler完成ECDH_双方派生出同一会话密钥()
    {
        var crypto = new AesGcmEncryptionService(NullLogger<AesGcmEncryptionService>.Instance);
        var localKs = new InMemoryKeyStore(crypto);
        var peerKs = new InMemoryKeyStore(crypto);
        var localIdentity = localKs.GetOrCreateIdentity();
        var peerIdentity = peerKs.GetOrCreateIdentity();

        var local = new NodeInfo
        {
            NodeId = localIdentity.NodeId,
            EndPoint = new IPEndPoint(IPAddress.Loopback, 45011),
            PublicKey = localIdentity.PublicKey
        };
        var peer = TestNodes.RealKeyPairNode(peerIdentity, 45012);

        var dht = new StubDhtService(local).Register(peer);
        var transport = new FakeTcpTransport();
        // 用真实 MessageRouter：它负责签名信封，ChatService 负责发起交换，
        // 真实 KeyExchangeHandler 在同一条连接上回响应 —— 完整走一遍线上握手。
        var router = TestRouters.Create(transport, Wire, crypto, localKs);
        var peerHandler = new KeyExchangeHandler(crypto, peerKs, NullLogger<KeyExchangeHandler>.Instance, new RecordingStaticPeerDht());

        var link = new FakeTcpConnection(peer.EndPoint);
        link.OnSend = async frame =>
        {
            var envelope = MessageRouter.DeserializeEnvelope(frame);
            if (envelope.MessageType != MessageType.KeyExchange) return;   // 后续的私聊正文由发起方自己消费

            // 交换请求/响应在网络上必须是已签名的
            MessageRouter.VerifyEnvelope(envelope, crypto, out var reason).ShouldBeTrue();
            reason.ShouldBeNull();

            var msg = Wire.Deserialize<Message>(envelope.Payload);
            if (msg is not KeyExchangeMessage { IsResponse: false } request) return;

            var before = link.SentFrames.Count;
            await peerHandler.HandleAsync(request, link, envelope);
            // 对端回写的响应帧排进本连接的入站队列，供 ChatService 读取
            foreach (var responseFrame in link.SentFrames.Skip(before))
                link.Enqueue(responseFrame);
        };
        transport.ConnectionFactory = _ => link;

        var service = new ChatService(dht, router, crypto, localKs, NewReplayGuard(), NullLogger<ChatService>.Instance);

        await service.SendPrivateMessageAsync(peer.NodeId, "首次通信");

        // 发起方把会话密钥登记在对端节点 ID 下，响应方登记在本方节点 ID 下，两侧必须相等。
        var localSessionKey = localKs.GetSessionKey(peer.NodeId);
        var peerSessionKey = peerKs.GetSessionKey(local.NodeId);
        localSessionKey.ShouldNotBeNull();
        localSessionKey!.Length.ShouldBe(32);
        peerSessionKey.ShouldBe(localSessionKey, "ECDH 两侧必须派生出同一会话密钥");

        // 交换完成后再把真正的私聊发出去，且明文只在本地事件里出现。
        var text = link.SentFrames
            .Select(f => MessageRouter.DeserializeEnvelope(f))
            .Where(e => e.MessageType == MessageType.PrivateText)
            .Select(e => (TextMessage)Wire.Deserialize<Message>(e.Payload))
            .Single();
        Encoding.UTF8.GetString(crypto.Decrypt(Convert.FromBase64String(text.Content), localSessionKey))
            .ShouldBe("首次通信");
    }

    [Fact]
    public async Task 发送私聊_密钥交换响应前先到达一条普通消息_先路由普通消息再继续等待响应()
    {
        var h = Build();
        var link = (FakeTcpConnection)h.Router.Connection;

        // 密钥交换响应现在必须**验签通过**才能被采用（ChatService 有一条不走路由器的读路径，
        // 历史上完全不验签 —— 那是本项目最后一个代码级安全缺口）。
        // 因此这里的「对端」必须是一把真实 P-256 密钥对：只有这样才能签出
        // NodeId 与公钥自洽、且 SenderId 等于我方拨号目标的响应信封。
        var peerKeys = h.Crypto.GenerateKeyPair();
        var signedPeer = TestNodes.RealKeyPairNode(peerKeys, port: 45050);
        h.Dht.Register(signedPeer);   // 换成可验签的对端后必须重新登记，否则 FindNodeAsync 命中不到

        // 1) 先排入一条私聊文本信封（非密钥交换）
        var aheadText = new TextMessage
        {
            SenderId = signedPeer.NodeId.ToByteArray(),
            ConversationId = ConversationId.ForPrivate(h.Local.NodeId, signedPeer.NodeId),
            Content = Convert.ToBase64String(h.Crypto.Encrypt(Encoding.UTF8.GetBytes("插队"), new byte[32])),
            IsGroup = false
        };
        link.Enqueue(MessageRouter.SerializeEnvelope(new MessageEnvelope
        {
            MessageType = MessageType.PrivateText,
            SenderId = signedPeer.NodeId.ToByteArray(),
            Payload = Wire.Serialize<Message>(aheadText)
        }));

        // 2) 再排入真正的密钥交换响应（**已用对端私钥签名**）
        var responderEphemeral = h.Crypto.GenerateKeyPair();
        var responseEnvelope = new MessageEnvelope
        {
            MessageType = MessageType.KeyExchange,
            SenderId = signedPeer.NodeId.ToByteArray(),
            SenderPublicKey = peerKeys.PublicKey,
            Payload = Wire.Serialize<Message>(new KeyExchangeMessage
            {
                SenderId = signedPeer.NodeId.ToByteArray(),
                ConversationId = signedPeer.NodeId.ToHexString(),
                EphemeralPublicKey = responderEphemeral.PublicKey,
                IsResponse = true
            })
        };
        link.Enqueue(MessageRouter.SerializeEnvelope(
            MessageRouter.SignEnvelope(responseEnvelope, peerKeys, h.Crypto)));

        await h.Service.SendPrivateMessageAsync(signedPeer.NodeId, "握手后正式消息");

        h.Router.RoutedIn.Count.ShouldBe(1, "插队的普通消息必须先交回正常路由，而不是被吞掉");
        h.Router.RoutedIn[0].MessageType.ShouldBe(MessageType.PrivateText);
        SingleText(h.Router).Content.ShouldNotBeNullOrEmpty("拿到响应后应继续完成本次发送");
        h.KeyStore.GetSessionKey(signedPeer.NodeId)!.Length.ShouldBe(32);
    }

    [Fact]
    public async Task 发送私聊_交换得到的会话密钥长度不合法时拒绝发送且不上报本地事件()
    {
        // 只有「派生出非 32 字节会话密钥」这一处需要故意做坏，其余全部走真实密码学，
        // 用来锁住 ChatService 的会话密钥长度校验分支。
        var real = new AesGcmEncryptionService(NullLogger<AesGcmEncryptionService>.Instance);
        IEncryptionService crypto = new TruncatingEncryptionService(real, sessionKeyLength: 16);

        var keyStore = new InMemoryKeyStore(real);
        var identity = keyStore.GetOrCreateIdentity();
        var local = new NodeInfo
        {
            NodeId = identity.NodeId,
            EndPoint = new IPEndPoint(IPAddress.Loopback, 45021),
            PublicKey = identity.PublicKey
        };
        // 「对端」必须是真实 P-256 密钥对：握手响应现在要过 EnvelopeVerifier，
        // 只有 NodeId 与公钥自洽、且用对应私钥签名的信封才会被采用。
        var peerKeys = real.GenerateKeyPair();
        var peer = TestNodes.RealKeyPairNode(peerKeys, port: 45022);
        var dht = new StubDhtService(local).Register(peer);
        var router = new RecordingMessageRouter();

        var link = new FakeTcpConnection(peer.EndPoint);
        router.Connection = link;
        var responseEnvelope = new MessageEnvelope
        {
            MessageType = MessageType.KeyExchange,
            SenderId = peer.NodeId.ToByteArray(),
            SenderPublicKey = peerKeys.PublicKey,
            Payload = Wire.Serialize<Message>(new KeyExchangeMessage
            {
                SenderId = peer.NodeId.ToByteArray(),
                ConversationId = peer.NodeId.ToHexString(),
                EphemeralPublicKey = real.GenerateKeyPair().PublicKey,
                IsResponse = true
            })
        };
        link.Enqueue(MessageRouter.SerializeEnvelope(
            MessageRouter.SignEnvelope(responseEnvelope, peerKeys, real)));

        var service = new ChatService(dht, router, crypto, keyStore, NewReplayGuard(), NullLogger<ChatService>.Instance);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => service.SendPrivateMessageAsync(peer.NodeId, "不该发出去"));
        ex.Message.ShouldContain("会话密钥");

        router.Sent.OfType<TextMessage>().ShouldBeEmpty("会话密钥无效时不得把明文推上线");
    }
}

