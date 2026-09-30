using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
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
/// 回归守卫 —— task-23：对端身份必须来自<b>信封</b>，且 <c>/connect</c> 变双向。
/// <para>
/// <b>缺陷回顾</b>：<c>KeyExchangeHandler</c> 曾用 <c>new NodeId(message.SenderId)</c>（<b>载荷</b>，
/// 对端完全可控）作为 <c>SetSessionKey</c> 的键，而同一方法里相隔 6 行的
/// <c>RecordPeerPublicKey</c> 却正确地用了 <b>信封</b> —— 一个信载荷、一个信信封。
/// 恶意对端可把载荷 SenderId 填成任意第三方，使会话密钥被写进第三方的槽位。
/// </para>
///
/// <para><b>本文件全部用例都经真实 <see cref="MessageRouter"/> 路由</b>，
/// <b>不直接调 <c>KeyExchangeHandler.HandleAsync</c></b>。原因：载荷/信封一致性检查落在
/// <c>MessageRouter.RouteIncomingAsync</c>（反序列化后、handler 派发前），
/// 绕开路由器直接调 handler 会跳过它，测到的就不是真实链路。
/// 顺带好处是每条用例都把「验签四关 + 重放防护 + 载荷一致性 + handler 行为」一次串全。</para>
///
/// <para><b>三条守卫的分工（务必读完再改）</b>：
/// <list type="bullet">
///   <item><b>A</b> —— 防原始 bug：载荷与信封不一致时，<b>第三方不得获得任何东西</b>。
///         非空（旧的 <c>new NodeId(message.SenderId)</c> 实现下第三方会真的拿到会话密钥 → 会红），
///         且与机制无关（无论是「路由器加检查」还是「改用信封」都绿）。</item>
///   <item><b>B</b> —— 锁 task-23 的新能力：正常对端必须以 <b>信封的 N/K 与 TCP 连接源端点</b>登记静态对端。
///         无论加不加一致性检查都成立，所以它才是真正锁住功能的那条。</item>
///   <item><b>C</b> —— 附加，锁「拒绝理由里点到了 SenderId」这个稳定 token，<b>不锁整句文案</b>。</item>
/// </list>
/// </para>
///
/// <para><b>为什么每条都先立「基线有效」</b>：下面构造的包<b>密码学上完全有效</b> ——
/// 攻击者用自己的私钥签名，ECDSA 验签通过，路由器的全部四道关卡都过。
/// 若哪天实现变了导致这个包真的验签不过，用例会立刻红，
/// 而不是悄悄退化成「非法包被拒」的重复测试（本项目已为此栽过一次）。
/// </para>
/// </summary>
public class KeyExchangeIdentityTests
{
    private static readonly IEncryptionService Crypto =
        new AesGcmEncryptionService(NullLogger<AesGcmEncryptionService>.Instance);

    private static readonly ISerializer Wire = new MessagePackSerializer();

    /// <summary>
    /// 造一个「登录者」：它拥有密钥 K，其真实身份 N = NodeId.FromPublicKey(K)。
    /// 所有信封都由它签名，因此信封在密码学上完全有效。
    /// </summary>
    private sealed record Peer(string Name, KeyPair Keys)
    {
        public NodeId NodeId => NodeId.FromPublicKey(Keys.PublicKey);
    }

    private static Peer NewPeer(string name) => new(name, Crypto.GenerateKeyPair());

    /// <summary>
    /// 造一条密钥交换<b>请求</b>载荷（<c>IsResponse = false</c>）。
    /// </summary>
    /// <param name="listenEndPoint">
    /// 对端<b>自报</b>的监听端点（<c>ip:port</c> 文本）。
    /// <para>
    /// <b>为什么端点只能自报、不能用 <c>connection.RemoteEndPoint</c>：</b>
    /// <c>TcpTransport.ConnectAsync</c> 用普通 <c>Socket.ConnectAsync</c>，本地端口由内核分配
    /// <b>ephemeral 临时端口</b>。应答侧从 <c>connection.RemoteEndPoint</c> 拿到的<b>永远是对方的
    /// 临时源端口、不是监听端口</b>，登记后回连必然 <c>SocketException: 积极拒绝</c>。
    /// 副作用更糟：<c>FindNodeAsync</c> <b>优先</b>查静态对端表，一条「对端IP:临时端口」的废记录
    /// 会<b>永久遮蔽</b>该对端的 DHT 解析路径，把「同一局域网 / 各自家宽」两个本来能正常发现的场景弄坏。
    /// 监听端口这个信息在应答侧根本不存在（三次握手不携带对端监听端口），只能由发起方自己说；
    /// 而它位于 Payload 内、被签名整体覆盖 ⇒「A 声称自己监听 P」<b>可归因</b>（谎报只会 A 自己连不上）。
    /// </para>
    /// </param>
    private static KeyExchangeMessage Request(
        NodeId claimedSenderId, KeyPair ephemeral, string conversationId, string? listenEndPoint = null)
        => new()
        {
            SenderId = claimedSenderId.ToByteArray(),
            ConversationId = conversationId,
            EphemeralPublicKey = ephemeral.PublicKey,
            SenderListenEndPoint = listenEndPoint,
            IsResponse = false
        };

    /// <summary>用登录者的私钥签出信封；<c>envelopeSenderId</c> 可与载荷里的不同（攻击场景）。</summary>
    private static MessageEnvelope Sign(Peer signer, Message message, NodeId envelopeSenderId)
    {
        var unsigned = new MessageEnvelope
        {
            MessageType = MessageType.KeyExchange,
            SequenceNumber = 1,
            SenderId = envelopeSenderId.ToByteArray(),
            SenderPublicKey = signer.Keys.PublicKey,
            Payload = Wire.Serialize<Message>(message)
        };
        return MessageRouter.SignEnvelope(unsigned, signer.Keys, Crypto);
    }

    /// <summary>
    /// 搭一套「登录者 → 路由器 → 密钥交换处理器」的最小可运行链路。
    /// 全部经 <see cref="MessageRouter.RouteIncomingAsync"/> 投递。
    /// </summary>
    private sealed record Rig(
        MessageRouter Router,
        KeyExchangeHandler Handler,
        InMemoryKeyStore KeyStore,
        RecordingStaticPeerDht Dht,
        PeerPublicKeyRegistry PublicKeys,
        RecordingLogger<object> Logs);

    private static Rig BuildRig()
    {
        var keyStore = new InMemoryKeyStore(Crypto);
        var transport = new FakeTcpTransport();
        var dht = new RecordingStaticPeerDht();
        // 注入**专属**的公钥登记表，而不是默认的 PeerPublicKeyRegistry.Shared
        // （后者是进程级静态，会被同程序集其它用例污染）。
        var publicKeys = new PeerPublicKeyRegistry();
        // 两个 logger 共享同一份 Entries，这样 C 能一次性看到
        // 「MessageRouter 记的那条」与「KeyExchangeHandler 记的那些」。
        var entries = new List<(LogLevel, string)>();
        var routerLog = new RecordingLogger<MessageRouter>(entries);
        var handlerLog = new RecordingLogger<KeyExchangeHandler>(entries);

        var router = TestRouters.Create(transport, Wire, Crypto, keyStore, logger: routerLog);
        var handler = new KeyExchangeHandler(Crypto, keyStore, handlerLog, dht, publicKeys);
        router.RegisterHandler(handler);
        return new Rig(router, handler, keyStore, dht, publicKeys, new RecordingLogger<object>(entries));
    }

    /// <summary>用一条「指定的」远端地址构造连接，让「误用本机地址/配置值」的实现一定会露出来。</summary>
    private static FakeTcpConnection ConnectionFrom(int port)
        => new(new IPEndPoint(IPAddress.Parse("198.51.100.23"), port));

    // ================================================================ A：防原始 bug

    [Fact]
    public async Task 载荷SenderId与信封不一致时_第三方不得获得任何东西()
    {
        // 攻击者：拥有 K，真实身份 N。他把**载荷**里的 SenderId 填成第三方。
        var attacker = NewPeer("attacker");
        var victim = NewPeer("victim");
        var thirdParty = NewPeer("thirdParty");

        var rig = BuildRig();
        var envelope = Sign(
            attacker,
            Request(thirdParty.NodeId, attacker.Keys, "connect-1234abcd"),   // ← 载荷谎称第三方
            attacker.NodeId);                                                  // ← 信封是攻击者自己

        // ---- 基线：这个包密码学上完全有效，能过路由器的全部关卡 ----
        envelope.Signature.ShouldNotBeNull();
        envelope.Signature!.Length.ShouldBeGreaterThan(0);
        envelope.SenderPublicKey.ShouldBe(attacker.Keys.PublicKey);
        Crypto.Verify(
                EnvelopeCodec.ComputeSignedBytes(envelope), envelope.Signature, envelope.SenderPublicKey)
            .ShouldBeTrue("攻击者用自己的私钥自签，ECDSA 必须验签通过 —— 否则本用例就退化成「非法包被拒」");
        MessageRouter.VerifyEnvelope(envelope, Crypto, out var verifyReason)
            .ShouldBeTrue($"信封本身必须完全合法：{verifyReason}");

        await rig.Router.RouteIncomingAsync(envelope, ConnectionFrom(41001));

        // ---- 断言：第三方什么都没有拿到（这条在旧实现下会红）----
        rig.KeyStore.GetSessionKey(thirdParty.NodeId).ShouldBeNull(
            "载荷里的 SenderId 是对端可控输入，绝不能被用来给会话密钥定身份");
        rig.Dht.RegisteredStaticPeers.ShouldNotContain(n => n.NodeId.Equals(thirdParty.NodeId),
            "第三方不得被登记为静态对端");
        rig.Dht.RegisteredStaticPeers.ShouldNotContain(n => n.NodeId.Equals(attacker.NodeId),
            "整条报文应被丢弃，不应产生任何静态对端登记");
    }

    // ================================================================ B：锁 task-23 的新能力

    [Fact]
    public async Task 正常对端_会话密钥与静态对端登记都必须来自信封与自报监听端点()
    {
        var peer = NewPeer("peer");
        var rig = BuildRig();
        var connection = ConnectionFrom(42002);          // 源端口 42002（临时端口语义）
        const string selfReported = "203.0.113.7:31000";  // 对端自报的监听端点

        var envelope = Sign(
            peer, Request(peer.NodeId, peer.Keys, "connect-cafebabe", selfReported), peer.NodeId);
        MessageRouter.VerifyEnvelope(envelope, Crypto, out var reason).ShouldBeTrue(reason);

        await rig.Router.RouteIncomingAsync(envelope, connection);

        // (1) 会话密钥落在对端的真实身份 N 名下（来自已验签的信封，不是载荷）
        rig.KeyStore.GetSessionKey(peer.NodeId).ShouldNotBeNull("会话密钥必须按对端身份登记");
        rig.KeyStore.GetSessionKey(peer.NodeId)!.Length.ShouldBe(32);

        // (2) 静态对端以「信封身份 + 信封公钥」登记
        var registered = rig.Dht.RegisteredStaticPeers.ShouldHaveSingleItem();
        registered.NodeId.ShouldBe(peer.NodeId, "登记的身份必须来自已验签的信封");
        registered.PublicKey.ShouldBe(peer.Keys.PublicKey,
            "登记的公钥必须是信封里那个 —— NodeId 与公钥的绑定在验签层已保证，" +
            "只断言 NodeId 是弱断言；若误用本机公钥，NodeId 可能凑巧对上而公钥对不上");

        // (3) 端点必须来自对方**自报**的监听端点 …
        registered.EndPoint.Port.ShouldBe(31000, "登记的端点必须是对方自报的监听端口");
        registered.EndPoint.Address.ShouldBe(IPAddress.Parse("203.0.113.7"), "地址也应取自自报端点");

        // … 且**绝不能**退回用连接源端点。
        // 这一条反向断言才是本次修复的核心形状：只断言「用了 A」而不断言「没用 B」，
        // 缺陷下次换个形式照样回来。
        registered.EndPoint.ShouldNotBe(connection.RemoteEndPoint,
            "绝不能退回用 connection.RemoteEndPoint —— 那是对方的临时出站端口（内核分配的 ephemeral），" +
            "登记后回连必然被拒，且会永久遮蔽该对端的 DHT 解析路径");
    }

    /// <summary>
    /// 降级守卫 —— 对方<b>不</b>自报监听端点（模拟旧版节点）时，
    /// <b>不得写入静态对端表</b>，但身份与公钥仍须登记。
    /// <para>
    /// 锁的是「<b>宁可退化成『对方暂时无法回话』，也不写一条会永久遮蔽 DHT 解析路径的废记录</b>」：
    /// 静态对端表的语义是「我知道它<b>在哪</b>」，而 <c>NodeInfo.EndPoint</c> 是 required，
    /// 当前模型无法表达「身份已知、端点未知」的条目 —— 那只能退到
    /// <c>PeerPublicKeyRegistry</c>（身份 + 公钥），并由用户用 <c>/add</c> 或反向 <c>/connect</c> 补齐。
    /// </para>
    /// </summary>
    [Fact]
    public async Task 对方未自报监听端点时_不写静态对端表_但身份与公钥仍被登记()
    {
        var peer = NewPeer("peer");
        var rig = BuildRig();

        // 不传自报端点 → 载荷里该字段为 null（MessagePack 键缺失，解析后保持 null）
        var envelope = Sign(peer, Request(peer.NodeId, peer.Keys, "connect-noport"), peer.NodeId);
        await rig.Router.RouteIncomingAsync(envelope, ConnectionFrom(42004));

        rig.Dht.RegisteredStaticPeers.ShouldBeEmpty(
            "端点未知时写入静态对端表就是埋废记录 —— " +
            "FindNodeAsync 优先查该表，一条「对端IP:临时端口」会永久遮蔽其 DHT 解析路径");

        rig.PublicKeys.TryGet(peer.NodeId, out var learned).ShouldBeTrue(
            "身份与公钥仍须登记 —— 群邀请包装群密钥依赖它");
        learned.ShouldBe(peer.Keys.PublicKey);

        // 会话密钥照常建立：端点未知只影响「对方能否回话」，不影响本次握手本身
        rig.KeyStore.GetSessionKey(peer.NodeId).ShouldNotBeNull();
    }

    [Fact]
    public async Task 正常对端_重复请求不会重复登记同一静态对端_但会话密钥会被刷新()
    {
        // 防「每次握手都往路由表塞一条」的退化。端点身份相同则应复用同一条登记。
        var peer = NewPeer("peer");
        var rig = BuildRig();

        for (var i = 0; i < 3; i++)
        {
            var envelope = Sign(
                peer, Request(peer.NodeId, peer.Keys, $"connect-{i}", "203.0.113.7:31000"), peer.NodeId);
            await rig.Router.RouteIncomingAsync(envelope, ConnectionFrom(42003));
        }

        rig.Dht.RegisteredStaticPeers.ShouldNotBeEmpty();
        rig.Dht.RegisteredStaticPeers.ShouldAllBe(n => n.NodeId.Equals(peer.NodeId),
            "重复握手只应登记同一个身份，不应出现其它身份");
        rig.KeyStore.GetSessionKey(peer.NodeId).ShouldNotBeNull();
    }

    // ================================================================ C：拒绝理由（附加）

    [Fact]
    public async Task 载荷与信封不一致时_拒绝理由里必须点到SenderId这个稳定token()
    {
        // 只锁**稳定 token**，不锁整句中文：措辞是这个项目里最容易漂移的地方，
        // 锁整句会制造「改注释比改代码容易」的反向激励。
        var attacker = NewPeer("attacker");
        var thirdParty = NewPeer("thirdParty");
        var rig = BuildRig();

        var envelope = Sign(
            attacker, Request(thirdParty.NodeId, attacker.Keys, "connect-9999"), attacker.NodeId);
        MessageRouter.VerifyEnvelope(envelope, Crypto, out _).ShouldBeTrue("基线：包本身完全合法");

        await rig.Router.RouteIncomingAsync(envelope, ConnectionFrom(43001));

        var rejection = rig.Logs.Entries
            .Where(e => e.Level >= LogLevel.Warning)
            .ToList();
        rejection.ShouldNotBeEmpty("不一致的报文必须留下可查的告警");
        rejection.ShouldContain(e => e.Message.Contains("SenderId"),
            "拒绝理由里必须出现 SenderId 这个稳定 token，理由才有定位价值");
    }

    // ================================================================ 追加负向

    [Fact]
    public async Task 密钥交换响应不得登记静态对端也不得写会话密钥()
    {
        // 只有「收到请求」才让 /connect 变双向。响应是**回程**报文：
        // 若响应也去登记，就会把「谁回应了我」误当成「我该记住谁」，
        // 从而把一个仅仅是回声的对端写进路由表。
        var responder = NewPeer("responder");
        var rig = BuildRig();

        var response = new KeyExchangeMessage
        {
            SenderId = responder.NodeId.ToByteArray(),
            ConversationId = "connect-abc",
            EphemeralPublicKey = responder.Keys.PublicKey,
            IsResponse = true
        };
        var envelope = Sign(responder, response, responder.NodeId);
        MessageRouter.VerifyEnvelope(envelope, Crypto, out _).ShouldBeTrue();

        await rig.Router.RouteIncomingAsync(envelope, ConnectionFrom(44001));

        rig.Dht.RegisteredStaticPeers.ShouldBeEmpty("响应报文不得触发静态对端登记");
        rig.KeyStore.GetSessionKey(responder.NodeId).ShouldBeNull(
            "会话密钥由发起方在自己那条连接上派生，响应侧不写槽位");
    }

    [Fact]
    public async Task 公钥与身份不自洽时_即使直接调用处理器也不得登记静态对端()
    {
        // ⚠️ 本条**刻意直接调用 handler**，不走路由器 —— 这是全文件唯一的例外，理由如下：
        // 若走路由器，`EnvelopeVerifier.Verify` 的第 3 关（公钥派生 == SenderId）
        // 会**先一步**拒掉这个包，handler 根本收不到，于是本条断言「不登记」就是**空断言**
        // （代码整块被注释掉也会变绿）。
        // 直接调用 handler 才能真正测到 `RegisterInboundPeer` 里那道
        // 「公钥与身份不自洽就不登记」的**纵深防御** —— 它防的是「handler 被绕过路由器直接调用」
        // 这条路径。宁可显式标注例外，也不写一条看起来在守、实际拦不住任何东西的用例。
        var attacker = NewPeer("attacker");
        var rig = BuildRig();
        var unrelated = NewPeer("unrelated");

        // 信封的 SenderId 是攻击者，但 SenderPublicKey 塞了**别人**的公钥。
        // 这在真实链路上过不了验签；这里只用于击穿 handler 自己的那道自洽校验。
        var forged = new MessageEnvelope
        {
            MessageType = MessageType.KeyExchange,
            SequenceNumber = 1,
            SenderId = attacker.NodeId.ToByteArray(),
            SenderPublicKey = unrelated.Keys.PublicKey,
            Payload = Wire.Serialize<Message>(
                Request(attacker.NodeId, attacker.Keys, "connect-poison"))
        };
        var signed = MessageRouter.SignEnvelope(forged, attacker.Keys, Crypto);

        await rig.Handler.HandleAsync(
            (KeyExchangeMessage)Wire.Deserialize<Message>(signed.Payload),
            ConnectionFrom(45001),
            signed);

        rig.Dht.RegisteredStaticPeers.ShouldBeEmpty(
            "公钥与身份不自洽时不得往静态对端表里埋毒丸 —— " +
            "否则之后每一次群邀请都会用错误的公钥包装群密钥");
    }

    // ---------------------------------------------------------------- 夹具
    // 日志替身 RecordingLogger<T> 已移入 Support/TestDoubles.cs，与 StaticPeerRegistrationTests 共用。
}
