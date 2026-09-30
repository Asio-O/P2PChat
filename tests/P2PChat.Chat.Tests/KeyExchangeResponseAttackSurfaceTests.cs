using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
/// 攻击面回归守卫 —— <c>ChatService.ReadKeyExchangeResponseAsync</c>（task-34）。
/// <para>
/// <b>为什么这条路径要单独守</b>：它是全项目<b>唯一不走 <c>MessageRouter.RouteIncomingAsync</c>
/// 的入站读路径</b>（出站连接没有入站消费循环，握手响应必须在本连接上现读）。
/// 换句话说，路由器上的验签与重放防护两道关卡在这里<b>都被绕过</b> ——
/// 而它恰好服务于 <c>/connect</c>，即<b>唯一不需预知对端身份</b>的接入方式，攻击面最大。
/// </para>
///
/// <para><b>本文件的三道关卡与六条用例的对应关系</b>：
/// <list type="bullet">
///   <item>① <c>EnvelopeVerifier.Verify</c>（签名 + 公钥↔身份绑定）⇒ 用例 2；</item>
///   <item>② <c>IReplayGuard.TryAccept</c>（时间新鲜度 + MessageId 去重）⇒ 用例 1、3、4；</item>
///   <item>③ 身份必须等于我方拨号目标 ⇒ 用例 6；</item>
/// </list>
/// 另外用例 5 覆盖一条<b>横切</b>不变量：身份只能取自<b>信封</b>，绝不能取自载荷
/// （与 <c>KeyExchangeHandler</c> 的同族保护对应 —— 两处都犯过同一个错）。
/// </para>
///
/// <para><b>为什么每条都先「证明基线有效」</b>：下面构造的包<b>密码学上完全合法</b> ——
/// 攻击者用真实私钥签名、<c>MessageRouter.VerifyEnvelope</c> 全过。
/// 若哪天实现变了导致这些包真的验签不过，用例会立刻红，
/// 而不是悄悄退化成「非法包被拒」的重复测试（本项目已为此栽过两次）。
/// </para>
///
/// <para><b>reason 只锁稳定 token，不锁整句</b>：措辞是这个项目里最容易漂移的地方，
/// 锁整句会制造「改注释比改代码容易」的反向激励。</para>
/// </summary>
public class KeyExchangeResponseAttackSurfaceTests
{
    private static readonly IEncryptionService Crypto =
        new AesGcmEncryptionService(NullLogger<AesGcmEncryptionService>.Instance);

    private static readonly ISerializer Wire = new MessagePackSerializer();

    /// <summary>整毫秒的固定时钟 —— 避免 Unix 毫秒截断导致时间窗边界假红。</summary>
    private static readonly DateTimeOffset FixedNow = DateTimeOffset.FromUnixTimeMilliseconds(1_780_000_000_000L);

    // ================================================================= 夹具

    private sealed record Rig(
        ChatService Service,
        RecordingMessageRouter Router,
        InMemoryKeyStore KeyStore,
        NodeId LocalNodeId,
        NodeInfo Peer,
        KeyPair PeerKeys,
        Func<ITcpConnection> NewConnection);

    /// <summary>
    /// 搭一套「拨号 → 在出站连接上喂一帧响应」的最小链路。
    /// <para>
    /// 入口选 <c>SendPrivateMessageAsync</c>：<c>ReadKeyExchangeResponseAsync</c> 是 private，
    /// 唯一能命中它的公开入口就是「本节点尚未持有该对端会话密钥 ⇒ 发起 ECDH 握手」。
    /// 这也让用例走的是<b>真实调用链</b>，而不是反射直调私有方法。
    /// </para>
    /// </summary>
    private static Rig BuildRig(Func<DateTimeOffset>? now = null)
    {
        var keyStore = new InMemoryKeyStore(Crypto);
        var local = keyStore.GetOrCreateIdentity();

        var peerKeys = Crypto.GenerateKeyPair();
        var peer = new NodeInfo
        {
            NodeId = peerKeys.NodeId,
            EndPoint = new IPEndPoint(IPAddress.Loopback, 47011),
            PublicKey = peerKeys.PublicKey
        };

        var dht = new StubDhtService(new NodeInfo
        {
            NodeId = local.NodeId,
            EndPoint = new IPEndPoint(IPAddress.Loopback, 47010),
            PublicKey = local.PublicKey
        }).Register(peer);

        var router = new RecordingMessageRouter { Connection = new FakeTcpConnection(peer.EndPoint) };
        var guard = new MessageReplayGuard(NullLogger<MessageReplayGuard>.Instance, now: now);
        var service = new ChatService(dht, router, Crypto, keyStore, guard, NullLogger<ChatService>.Instance);

        // 每次「拨号」返回一条**新的**连接，模拟重放走的是另一条 TCP 连接
        ITcpConnection MakeConnection() => new FakeTcpConnection(peer.EndPoint);
        return new Rig(service, router, keyStore, local.NodeId, peer, peerKeys, MakeConnection);
    }

    /// <summary>造一条「响应者」身份的签名信封。</summary>
    private static MessageEnvelope SignedResponse(
        KeyPair signerKeys,
        Guid messageId,
        long timestampMs,
        KeyPair responderEphemeral,
        NodeId payloadClaimedSenderId,
        string conversationId = "kx-abcdef01")
    {
        var payload = new KeyExchangeMessage
        {
            SenderId = payloadClaimedSenderId.ToByteArray(),
            ConversationId = conversationId,
            EphemeralPublicKey = responderEphemeral.PublicKey,
            IsResponse = true,
            MessageId = messageId,
            Timestamp = timestampMs
        };
        var unsigned = new MessageEnvelope
        {
            MessageType = MessageType.KeyExchange,
            SequenceNumber = 1,
            SenderId = signerKeys.NodeId.ToByteArray(),
            MessageId = messageId,
            Timestamp = timestampMs,
            SenderPublicKey = signerKeys.PublicKey,
            Payload = Wire.Serialize<Message>(payload)
        };
        return MessageRouter.SignEnvelope(unsigned, signerKeys, Crypto);
    }

    /// <summary>把一帧排进「下一次拨号」将要用的那条连接。</summary>
    private static void FeedNextConnection(Rig rig, MessageEnvelope envelope)
        => ((FakeTcpConnection)rig.Router.Connection).Enqueue(MessageRouter.SerializeEnvelope(envelope));

    /// <summary>换一条新连接承载下一帧（用于「经不同连接重放」）。</summary>
    private static void UseNewConnection(Rig rig)
        => rig.Router.Connection = rig.NewConnection();

    /// <summary>
    /// 基线断言：这条信封在密码学上<b>完全有效</b>，能过路由器的全部四道验签关卡。
    /// 没有这一段，用例就可能只是碰巧被「非法包」那条理由拦下。
    /// </summary>
    private static void AssertCryptographicallyValid(MessageEnvelope envelope)
    {
        envelope.Signature.ShouldNotBeNull();
        envelope.Signature!.Length.ShouldBeGreaterThan(0);
        envelope.SenderPublicKey.ShouldNotBeNull();

        Crypto.Verify(
                EnvelopeCodec.ComputeSignedBytes(envelope), envelope.Signature, envelope.SenderPublicKey)
            .ShouldBeTrue("该包必须由真实私钥签出且未被篡改 —— 否则本用例会退化成「非法包被拒」");

        MessageRouter.VerifyEnvelope(envelope, Crypto, out var reason)
            .ShouldBeTrue($"信封本身必须完全合法，否则测的不是 task-34 的那条路径：{reason}");
    }

    // =============================================================== 1. 重放

    [Fact]
    public async Task 同一响应经两条不同连接重放_第二条被重放防护拒绝_而不是被验签拒绝()
    {
        var rig = BuildRig(() => FixedNow);
        var messageId = Guid.NewGuid();
        var response = SignedResponse(
            rig.PeerKeys, messageId, FixedNow.ToUnixTimeMilliseconds(),
            Crypto.GenerateKeyPair(), rig.PeerKeys.NodeId);

        AssertCryptographicallyValid(response);

        // 第一次：正常握手，应成功
        FeedNextConnection(rig, response);
        await rig.Service.SendPrivateMessageAsync(rig.Peer.NodeId, "第一次");

        // 攻击者把**同一份字节**（同一 MessageId）从另一条 TCP 连接重放过来。
        // 先清掉会话密钥，逼本节点重新走一次握手 —— 否则第二次会因「已有会话密钥」而
        // 根本不进这条路径，用例就成了空断言。
        rig.KeyStore.RemoveSessionKey(rig.Peer.NodeId);
        UseNewConnection(rig);
        FeedNextConnection(rig, response);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => rig.Service.SendPrivateMessageAsync(rig.Peer.NodeId, "重放"));

        ex.Message.ShouldContain("重放");   // 第二份必须死于「重放防护」
        // 第二份字节与第一份完全相同、同样合法 —— 被拒理由必须是重放，不能是验签
        ex.Message.ShouldNotContain("验签失败");

        rig.KeyStore.GetSessionKey(rig.Peer.NodeId).ShouldBeNull("被拒的重放不得留下任何会话密钥");
    }

    // =============================================================== 2. 载荷篡改 + 合法签名

    [Fact]
    public async Task 载荷被篡改而保留原签名_响应被验签拒绝()
    {
        var rig = BuildRig(() => FixedNow);
        var honest = SignedResponse(
            rig.PeerKeys, Guid.NewGuid(), FixedNow.ToUnixTimeMilliseconds(),
            Crypto.GenerateKeyPair(), rig.PeerKeys.NodeId);

        // 基线：原包完全合法
        AssertCryptographicallyValid(honest);

        // 攻击者：改载荷（换成别人的 NodeId / 换关联标识），但**保留原签名**
        var tamperedPayload = new KeyExchangeMessage
        {
            SenderId = NodeId.CreateRandom().ToByteArray(),
            ConversationId = "kx-forged",
            EphemeralPublicKey = Crypto.GenerateKeyPair().PublicKey,
            IsResponse = true,
            MessageId = honest.MessageId,
            Timestamp = honest.Timestamp
        };
        var tampered = honest with { Payload = Wire.Serialize<Message>(tamperedPayload) };

        // 载荷变了 → 待签字节变了 → 原签名不再匹配
        Crypto.Verify(
                EnvelopeCodec.ComputeSignedBytes(tampered), tampered.Signature!, tampered.SenderPublicKey!)
            .ShouldBeFalse("基线对照：载荷一改，原签名就不再自洽");
        MessageRouter.VerifyEnvelope(tampered, Crypto, out _).ShouldBeFalse();

        FeedNextConnection(rig, tampered);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => rig.Service.SendPrivateMessageAsync(rig.Peer.NodeId, "篡改载荷"));

        ex.Message.ShouldContain("验签");   // 载荷一改，原签名不再自洽
        rig.KeyStore.GetSessionKey(rig.Peer.NodeId).ShouldBeNull();
    }

    // =============================================================== 3. 过期时间戳

    [Fact]
    public async Task 签名合法但时间戳超过maxAge_响应被时间新鲜度拒绝()
    {
        var rig = BuildRig(() => FixedNow);
        var stale = SignedResponse(
            rig.PeerKeys, Guid.NewGuid(),
            FixedNow.ToUnixTimeMilliseconds() - (long)TimeSpan.FromHours(1).TotalMilliseconds - 1,
            Crypto.GenerateKeyPair(), rig.PeerKeys.NodeId);

        AssertCryptographicallyValid(stale);   // 签名有效 —— 被拒只因「过旧」

        FeedNextConnection(rig, stale);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => rig.Service.SendPrivateMessageAsync(rig.Peer.NodeId, "过旧"));

        ex.Message.ShouldContain("过旧");   // 签名合法，被拒的唯一理由只能是时间新鲜度
        rig.KeyStore.GetSessionKey(rig.Peer.NodeId).ShouldBeNull();
    }

    // =============================================================== 4. 未来时间戳

    [Fact]
    public async Task 签名合法但时间戳超前超过maxFutureSkew_响应被时间新鲜度拒绝()
    {
        var rig = BuildRig(() => FixedNow);
        var future = SignedResponse(
            rig.PeerKeys, Guid.NewGuid(),
            FixedNow.ToUnixTimeMilliseconds() + (long)TimeSpan.FromMinutes(5).TotalMilliseconds + 1,
            Crypto.GenerateKeyPair(), rig.PeerKeys.NodeId);

        AssertCryptographicallyValid(future);

        FeedNextConnection(rig, future);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => rig.Service.SendPrivateMessageAsync(rig.Peer.NodeId, "超前"));

        ex.Message.ShouldContain("来自未来");   // 签名合法，被拒的唯一理由只能是超前
        rig.KeyStore.GetSessionKey(rig.Peer.NodeId).ShouldBeNull();
    }

    // =============================================================== 5. 会话密钥归属

    [Fact]
    public async Task 响应载荷谎称第三方且临时公钥被伪造_会话密钥只写在已验签的对端名下()
    {
        var rig = BuildRig(() => FixedNow);
        var thirdParty = Crypto.GenerateKeyPair();          // 攻击者想顶替的身份
        var forgedEphemeral = Crypto.GenerateKeyPair();     // 攻击者自选的「临时公钥」

        // 信封用**对端自己的真实私钥**签名（合法），但载荷里谎称第三方，
        // 临时公钥也换成攻击者自选的那把。
        var forged = SignedResponse(
            rig.PeerKeys, Guid.NewGuid(), FixedNow.ToUnixTimeMilliseconds(),
            forgedEphemeral, thirdParty.NodeId);

        AssertCryptographicallyValid(forged);   // 密码学上完全合法

        FeedNextConnection(rig, forged);
        await rig.Service.SendPrivateMessageAsync(rig.Peer.NodeId, "归属");

        // 会话密钥只能落在**已验签的对端身份**名下
        rig.KeyStore.GetSessionKey(rig.Peer.NodeId).ShouldNotBeNull("应写在对端名下");
        rig.KeyStore.GetSessionKey(thirdParty.NodeId).ShouldBeNull(
            "载荷里的 SenderId 是对端可控输入，绝不能被用来给会话密钥定身份");

        // 整个 keyStore 里只应有这一个写入，且写的就是对端
        rig.KeyStore.SessionKeyWrites.ShouldBe([rig.Peer.NodeId.ToHexString()],
            "不得有任何第三个 NodeId 名下出现会话密钥");
    }

    // =============================================================== 6. 身份与拨号目标不一致

    [Fact]
    public async Task 响应签名合法但身份不是我方拨号的目标_响应被拒绝()
    {
        var rig = BuildRig(() => FixedNow);

        // 别的身份在那个端点上应答（它的签名完全有效，但不是我方要拨的那个节点）
        var impostor = Crypto.GenerateKeyPair();
        var impostorResponse = SignedResponse(
            impostor, Guid.NewGuid(), FixedNow.ToUnixTimeMilliseconds(),
            Crypto.GenerateKeyPair(), impostor.NodeId);

        AssertCryptographicallyValid(impostorResponse);   // 密码学上完全合法

        FeedNextConnection(rig, impostorResponse);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => rig.Service.SendPrivateMessageAsync(rig.Peer.NodeId, "冒名"));

        // 签名合法、时间窗合法、MessageId 未见过 —— 被拒理由只能是「不是我拨号的那个」
        ex.Message.ShouldContain("非预期");
        rig.KeyStore.GetSessionKey(rig.Peer.NodeId).ShouldBeNull(
            "会话密钥绝不能记到一个我方并未拨号的 NodeId 名下");
        rig.KeyStore.GetSessionKey(impostor.NodeId).ShouldBeNull();
    }
}
