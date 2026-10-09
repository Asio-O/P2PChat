using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using P2PChat.Chat.Mesh;
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
/// mesh 泛洪与拓扑维护的单元测试（2026-10-09-mesh-topology-and-flooding）。
/// <para>
/// 锁住的契约：泛洪发到全部活跃出站邻居；转发跳过原始发送者；同一信封二次到达
/// 被重放防护拦截后不二次转发；FloodingEnabled=false 时入站聊天消息不转发；
/// 拓扑维护对「已知但无连接」的节点主动建连。
/// </para>
/// </summary>
public class MeshFloodingTests
{
    private static readonly ISerializer Wire = new MessagePackSerializer();

    private sealed record Harness(
        MessageRouter Router,
        FakeTcpTransport Transport,
        AesGcmEncryptionService Crypto,
        InMemoryKeyStore KeyStore,
        FakeTcpConnection LinkB,
        NodeInfo PeerB);

    private static Harness Build(FakeTcpConnection? linkB = null, bool floodingEnabled = true)
    {
        var crypto = new AesGcmEncryptionService(NullLogger<AesGcmEncryptionService>.Instance);
        var keyStore = new InMemoryKeyStore(crypto);
        var transport = new FakeTcpTransport();
        var peerB = TestNodes.LegacyPublicKeyNode(port: 49002);
        var link = linkB ?? new FakeTcpConnection(peerB.EndPoint);
        transport.ConnectionFactory = ep => ep.Port == peerB.EndPoint.Port
            ? link
            : new FakeTcpConnection(ep);

        var router = new MessageRouter(
            transport, Wire, crypto, keyStore,
            new MessageReplayGuard(NullLogger<MessageReplayGuard>.Instance),
            NullLogger<MessageRouter>.Instance,
            floodingEnabled);

        return new Harness(router, transport, crypto, keyStore, link, peerB);
    }

    /// <summary>造一条「来自对端 A、已签名、验签可通过」的私聊信封。</summary>
    private static MessageEnvelope SignedPrivateEnvelope(
        AesGcmEncryptionService crypto, KeyPair senderKeys)
    {
        var senderId = NodeId.FromPublicKey(senderKeys.PublicKey);
        var payload = new TextMessage
        {
            SenderId = senderId.ToByteArray(),
            ConversationId = "conv",
            Content = "x",
            IsGroup = false
        };
        var unsigned = new MessageEnvelope
        {
            MessageType = MessageType.PrivateText,
            SenderId = senderId.ToByteArray(),
            SenderPublicKey = senderKeys.PublicKey,
            Payload = Wire.Serialize<Message>(payload)
        };
        return MessageRouter.SignEnvelope(unsigned, senderKeys, crypto);
    }

    private static void RegisterDummyPrivateHandler(MessageRouter router)
    {
        var handler = new Mock<IMessageHandler<TextMessage>>();
        handler.SetupGet(h => h.MessageType).Returns(MessageType.PrivateText);
        router.RegisterHandler(handler.Object);
    }

    [Fact]
    public async Task 泛洪_发送到连接池中全部活跃连接()
    {
        var h = Build();
        var peerC = TestNodes.LegacyPublicKeyNode(port: 49003);
        var linkC = new FakeTcpConnection(peerC.EndPoint);
        h.Transport.ConnectionFactory = ep => ep.Port switch
        {
            49002 => h.LinkB,
            49003 => linkC,
            _ => new FakeTcpConnection(ep)
        };

        await h.Router.GetOrCreateConnectionAsync(h.PeerB);
        await h.Router.GetOrCreateConnectionAsync(peerC);

        var sent = await h.Router.FloodAsync(new TextMessage
        {
            SenderId = h.KeyStore.GetOrCreateIdentity().NodeId.ToByteArray(),
            ConversationId = "g",
            Content = "群消息",
            IsGroup = true
        });

        sent.ShouldBe(2);
        h.LinkB.SentFrames.Count.ShouldBe(1);
        linkC.SentFrames.Count.ShouldBe(1);
    }

    [Fact]
    public async Task 转发_跳过原始发送者的出站连接()
    {
        var h = Build();
        // 池里两条：h.PeerB（键 = PeerB）与 peerA（原始发送者）。
        var peerA = TestNodes.LegacyPublicKeyNode(port: 49001);
        var linkA = new FakeTcpConnection(peerA.EndPoint);
        h.Transport.ConnectionFactory = ep => ep.Port switch
        {
            49001 => linkA,
            49002 => h.LinkB,
            _ => new FakeTcpConnection(ep)
        };
        await h.Router.GetOrCreateConnectionAsync(peerA);
        await h.Router.GetOrCreateConnectionAsync(h.PeerB);

        // 信封的原始发送者是 peerA —— 转发必须跳过它，只发给 PeerB。
        var envelope = new MessageEnvelope
        {
            MessageType = MessageType.PrivateText,
            SenderId = peerA.NodeId.ToByteArray(),
            Payload = []
        };

        var sent = await h.Router.ForwardAsync(envelope, exclude: null);

        sent.ShouldBe(1, "只转发给原始发送者之外的邻居");
        linkA.SentFrames.ShouldBeEmpty("不得回发给原始发送者");
        h.LinkB.SentFrames.Count.ShouldBe(1);
    }

    [Fact]
    public async Task 路由_同一信封二次到达_重放防护拦截后不二次转发()
    {
        var h = Build();
        await h.Router.GetOrCreateConnectionAsync(h.PeerB);
        RegisterDummyPrivateHandler(h.Router);

        var senderKeys = h.Crypto.GenerateKeyPair();
        var envelope = SignedPrivateEnvelope(h.Crypto, senderKeys);
        var inbound = new FakeTcpConnection();

        await h.Router.RouteIncomingAsync(envelope, inbound);
        h.LinkB.SentFrames.Count.ShouldBe(1, "首次到达：验签+重放防护通过后应转发一次");

        await h.Router.RouteIncomingAsync(envelope, inbound);
        h.LinkB.SentFrames.Count.ShouldBe(1, "同一 MessageId 二次到达被重放防护拦截，不得再次转发");
    }

    [Fact]
    public async Task 路由_FloodingEnabled为false时_入站聊天消息不转发()
    {
        var h = Build(floodingEnabled: false);
        await h.Router.GetOrCreateConnectionAsync(h.PeerB);
        RegisterDummyPrivateHandler(h.Router);

        var senderKeys = h.Crypto.GenerateKeyPair();
        var envelope = SignedPrivateEnvelope(h.Crypto, senderKeys);
        var inbound = new FakeTcpConnection();

        await h.Router.RouteIncomingAsync(envelope, inbound);

        h.LinkB.SentFrames.ShouldBeEmpty("回退阀关闭时，入站消息只做本地处理、不向邻居转发");
    }

    [Fact]
    public async Task MeshTopologyService_对已知但无连接的节点主动建连()
    {
        var crypto = new AesGcmEncryptionService(NullLogger<AesGcmEncryptionService>.Instance);
        var keyStore = new InMemoryKeyStore(crypto);
        var transport = new FakeTcpTransport();
        var identity = keyStore.GetOrCreateIdentity();
        var local = new NodeInfo
        {
            NodeId = identity.NodeId,
            EndPoint = new IPEndPoint(IPAddress.Loopback, 49100),
            PublicKey = identity.PublicKey
        };
        var dht = new StubDhtService(local).Register(TestNodes.LegacyPublicKeyNode(port: 49101));

        var router = TestRouters.Create(transport, Wire, crypto, keyStore);
        var service = new MeshTopologyService(
            dht, router, seedPeers: [],
            new MeshOptions { MaintainIntervalSeconds = 30 },
            NullLogger<MeshTopologyService>.Instance);

        await service.TickAsync();

        transport.ConnectCount.ShouldBe(1, "DHT 路由表中的已知节点必须被主动建连");
    }

    [Fact]
    public async Task MeshTopologyService_已有活跃连接时不重复建连()
    {
        var crypto = new AesGcmEncryptionService(NullLogger<AesGcmEncryptionService>.Instance);
        var keyStore = new InMemoryKeyStore(crypto);
        var transport = new FakeTcpTransport();
        var identity = keyStore.GetOrCreateIdentity();
        var local = new NodeInfo
        {
            NodeId = identity.NodeId,
            EndPoint = new IPEndPoint(IPAddress.Loopback, 49200),
            PublicKey = identity.PublicKey
        };
        var peer = TestNodes.LegacyPublicKeyNode(port: 49201);
        var dht = new StubDhtService(local).Register(peer);

        var router = TestRouters.Create(transport, Wire, crypto, keyStore);
        await router.GetOrCreateConnectionAsync(peer);   // 先建一条活跃连接
        var service = new MeshTopologyService(
            dht, router, seedPeers: [],
            new MeshOptions { MaintainIntervalSeconds = 30 },
            NullLogger<MeshTopologyService>.Instance);

        await service.TickAsync();

        transport.ConnectCount.ShouldBe(1, "已有活跃连接时维护轮必须复用，不得重复拨号");
    }
}
