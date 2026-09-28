using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using P2PChat.Chat.Routing;
using P2PChat.Core.Abstractions;
using P2PChat.Core.Enums;
using P2PChat.Core.Extensions;
using P2PChat.Core.Models;
using P2PChat.Crypto.Encryption;
using Shouldly;

namespace P2PChat.Chat.Tests;

/// <summary>
/// task-17 的判定依据单测 —— <c>Core.Extensions.EnvelopeVerifier</c> 与端点→身份连续性判定。
/// <para>
/// <b>本文件最重要的一条是「伪造者用自己的合法私钥自签、但冒名第三方」那条。</b>
/// 它与本项目其它「非法包被拒」的用例有本质区别，请勿混淆：
/// </para>
/// <list type="bullet">
///   <item>普通非法包：签名对不上 / 被篡改 / 缺签名 —— 那些在
///         <c>MessageSigningTests</c> 与上一轮 <c>ReplayProtectionTests</c> 已覆盖；</item>
///   <item><b>本文件这条</b>：攻击者用<b>自己</b>的私钥签名，ECDSA <b>完全有效</b>，
///         签名与公钥字段<b>一应不缺</b> —— 它是一份密码学上完全合法的信封，
///         被拒的唯一原因是<b>它自称的身份不是它出示的密钥所拥有的</b>。
///         这正是 <c>/connect</c> 盲连接场景下唯一真正可拒的攻击形态。</item>
/// </list>
///
/// <para>因此每条相关用例都会先断言「基线确实是有效的」，避免它退化成对非法包的重复测试。</para>
/// </summary>
public class EnvelopeVerifierTests
{
    private static readonly IEncryptionService Crypto =
        new AesGcmEncryptionService(NullLogger<AesGcmEncryptionService>.Instance);

    /// <summary>造一条 hello 响应载荷（<c>/connect</c> 的应答就是它）。</summary>
    private static KeyExchangeMessage HelloResponse(NodeId claimedSenderId, KeyPair responder)
        => new()
        {
            SenderId = claimedSenderId.ToByteArray(),
            ConversationId = "connect-abcd1234",
            EphemeralPublicKey = responder.PublicKey,
            IsResponse = true
        };

    /// <summary>
    /// 用给定密钥对信封签名（<c>SenderPublicKey</c> 与 <c>SenderId</c> 由调用方决定，可故意不自洽）。
    /// 签名走 <c>MessageRouter.SignEnvelope</c> —— 与线上出站路径同一份实现，
    /// <c>EnvelopeCodec</c> 只管编解码与待签字节计算，不提供签名。
    /// </summary>
    private static MessageEnvelope Sign(
        KeyPair signer, Message message, NodeId senderId, byte[]? senderPublicKey = null)
    {
        var unsigned = new MessageEnvelope
        {
            MessageType = MessageType.KeyExchange,
            SequenceNumber = 1,
            SenderId = senderId.ToByteArray(),
            SenderPublicKey = senderPublicKey ?? signer.PublicKey,
            Payload = new MessagePackSerializer().Serialize<Message>(message)
        };
        return MessageRouter.SignEnvelope(unsigned, signer, Crypto);
    }

    #region 核心：伪造者自签 + 冒名第三方

    [Fact]
    public void 伪造者用自己的合法私钥自签但冒名第三方_必须被拒()
    {
        var attacker = Crypto.GenerateKeyPair();
        var victimFriend = NodeId.CreateRandom();      // 受害者本来想连的那个好友

        // 攻击者用**自己的**私钥签名，但把 SenderId 伪造成好友的 NodeId
        var forged = Sign(attacker, HelloResponse(victimFriend, attacker), victimFriend);

        // ---- 先证明「这不是一个非法包」：它密码学上完全有效 ----
        // 1) 签名与公钥字段一应不缺
        forged.Signature.ShouldNotBeNull();
        forged.Signature!.Length.ShouldBeGreaterThan(0);
        forged.SenderPublicKey.ShouldBe(attacker.PublicKey);

        // 2) ECDSA 验签本身是通过的 —— 签名确实是攻击者用自己的私钥算出来的
        Crypto.Verify(
            EnvelopeCodec.ComputeSignedBytes(forged), forged.Signature, forged.SenderPublicKey)
            .ShouldBeTrue("攻击者用自己的私钥自签，验签必须通过 —— 否则本用例就退化成「非法包被拒」的重复测试");

        // 3) 被拒的唯一原因是身份冒名
        EnvelopeVerifier.Verify(forged, Crypto, out var reason).ShouldBeFalse();
        reason.ShouldNotBeNull();
        reason!.ShouldContain("不匹配");
    }

    [Fact]
    public void 伪造响应被拒时_从公钥派生的身份绝不是第三方_会话密钥不会记在好友名下()
    {
        // 这一条锁的是**后果**，不只是「被拒」：
        // `/connect` 会用「本次应答的身份」作为 keyStore 的键、并把它登记成静态对端。
        // 修复前用 `new NodeId(response.SenderId)`（对端可控字段）取身份，
        // 攻击者因此能把会话密钥挂到好友的 NodeId 名下，导致受害者之后发给好友的
        // 每条私聊都加密给攻击者派生的密钥。正确做法是**从公钥派生身份**。
        var attacker = Crypto.GenerateKeyPair();
        var victimFriend = NodeId.CreateRandom();

        var forged = Sign(attacker, HelloResponse(victimFriend, attacker), victimFriend);
        EnvelopeVerifier.Verify(forged, Crypto, out _).ShouldBeFalse();

        // 无论对端在载荷里怎么自称，从它出示的公钥派生的身份只能是它自己
        var identityFromKey = NodeId.FromPublicKey(forged.SenderPublicKey!);
        identityFromKey.ShouldBe(attacker.NodeId);
        identityFromKey.ShouldNotBe(victimFriend);
    }

    [Fact]
    public void 自洽的陌生主机可以通过验签_这是TOFU的合法起点()
    {
        // 反向对照：把上一条的「冒名」去掉（SenderId 就是自己），
        // 验签必须通过 —— 否则就成了「盲连接一律拒绝」的假断言。
        var stranger = Crypto.GenerateKeyPair();
        var honest = Sign(stranger, HelloResponse(stranger.NodeId, stranger), stranger.NodeId);

        EnvelopeVerifier.Verify(honest, Crypto, out var reason).ShouldBeTrue(
            "首次接触时没有可用的判据，完全自洽的陌生主机必须能通过 —— 这是 TOFU，不是拒绝");
        reason.ShouldBeNull();
    }

    #endregion

    #region 端点→身份连续性（首次接触之外的唯一可用判据）

    private static IPEndPoint Endpoint(int port) => new(IPAddress.Loopback, port);

    [Fact]
    public void 端点身份判定_从未见过的端点属首次接触()
    {
        EnvelopeVerifier.EvaluatePeerIdentity(
            [], Endpoint(4001), NodeId.CreateRandom())
            .ShouldBe(EnvelopeVerifier.PeerIdentityVerdict.FirstContact);
    }

    [Fact]
    public void 端点身份判定_同一端点同一身份为连续性成立()
    {
        var id = NodeId.CreateRandom();
        var pins = new[] { (Endpoint(4002), id) };

        EnvelopeVerifier.EvaluatePeerIdentity(pins, Endpoint(4002), id)
            .ShouldBe(EnvelopeVerifier.PeerIdentityVerdict.MatchesExistingPin);
    }

    [Fact]
    public void 端点身份判定_同一端点换了身份必须判为冲突_这是唯一能拒自签攻击者的手段()
    {
        // 「攻击者用自己的合法私钥应答」在**首次接触**时与合法节点不可区分；
        // 唯一可用的额外信息是「我们此前为这个端点记下的身份」。
        // 同一端点前后身份不一致 = 中间人或对端换身份的强信号。
        var pins = new[] { (Endpoint(4003), NodeId.CreateRandom()) };

        EnvelopeVerifier.EvaluatePeerIdentity(pins, Endpoint(4003), NodeId.CreateRandom())
            .ShouldBe(EnvelopeVerifier.PeerIdentityVerdict.ConflictsWithExistingPin);
    }

    [Fact]
    public void 端点身份判定_别的端点的绑定不影响本端点()
    {
        // 绑定是**按端点**的：换一个端口就等于换了一个主体，不应被旧绑定误伤。
        var pins = new[] { (Endpoint(4004), NodeId.CreateRandom()) };

        EnvelopeVerifier.EvaluatePeerIdentity(pins, Endpoint(4005), NodeId.CreateRandom())
            .ShouldBe(EnvelopeVerifier.PeerIdentityVerdict.FirstContact);
    }

    [Fact]
    public void 端点身份判定_本地记录自相矛盾时按冲突处理_宁可拒绝()
    {
        // 同一端点既有匹配又有不匹配，说明本地绑定数据已损坏。
        // 此时不能返回 MatchesExistingPin（那等于放行），必须按冲突拒绝。
        var id = NodeId.CreateRandom();
        var pins = new[] { (Endpoint(4006), id), (Endpoint(4006), NodeId.CreateRandom()) };

        EnvelopeVerifier.EvaluatePeerIdentity(pins, Endpoint(4006), id)
            .ShouldBe(EnvelopeVerifier.PeerIdentityVerdict.ConflictsWithExistingPin);
    }

    [Fact]
    public void 端点身份判定_参数非法时抛异常而不是静默放行()
    {
        Should.Throw<ArgumentNullException>(() =>
            EnvelopeVerifier.EvaluatePeerIdentity(null!, Endpoint(4007), NodeId.CreateRandom()));

        Should.Throw<ArgumentNullException>(() =>
            EnvelopeVerifier.EvaluatePeerIdentity([], null!, NodeId.CreateRandom()));
    }

    #endregion

    #region 收敛本身：MessageRouter 委托后行为与签名都不得变

    [Fact]
    public void MessageRouter的VerifyEnvelope必须与EnvelopeVerifier判定完全一致()
    {
        // task-17 把验签收敛到 Core，但 `MessageRouter.VerifyEnvelope` 是被
        // 17 处测试直接调用的既有 public static API。收敛**不得**改变它的行为，
        // 否则就是「为了重构而改语义」。本条把两者钉成等价。
        var signer = Crypto.GenerateKeyPair();
        var stranger = NodeId.CreateRandom();

        var cases = new (string Name, MessageEnvelope Envelope)[]
        {
            ("合法自签信封", Sign(signer, HelloResponse(signer.NodeId, signer), signer.NodeId)),
            ("冒名第三方", Sign(signer, HelloResponse(stranger, signer), stranger)),
            ("缺签名", Sign(signer, HelloResponse(signer.NodeId, signer), signer.NodeId) with { Signature = null }),
            ("缺公钥", Sign(signer, HelloResponse(signer.NodeId, signer), signer.NodeId) with { SenderPublicKey = null }),
            ("载荷被篡改", Sign(signer, HelloResponse(signer.NodeId, signer), signer.NodeId)
                with { Payload = [9, 9, 9] })
        };

        foreach (var (name, envelope) in cases)
        {
            var viaRouter = MessageRouter.VerifyEnvelope(envelope, Crypto, out var routerReason);
            var viaCore = EnvelopeVerifier.Verify(envelope, Crypto, out var coreReason);

            viaRouter.ShouldBe(viaCore, $"「{name}」两种入口判定必须一致，否则收敛改变了行为");
            routerReason.ShouldBe(coreReason, $"「{name}」的失败原因必须一致");
        }
    }

    [Fact]
    public void 缺签名或缺公钥时EnvelopeVerifier给出与既有测试相同的中文明因()
    {
        // 既有 MessageSigningTests 断言了具体文案（"缺少签名"/"缺少发送方公钥"）。
        // 收敛到 Core 后这些文案不能变，否则会炸红一批语义正确的测试。
        var signer = Crypto.GenerateKeyPair();
        var signed = Sign(signer, HelloResponse(signer.NodeId, signer), signer.NodeId);

        EnvelopeVerifier.Verify(signed with { Signature = null }, Crypto, out var noSig)
            .ShouldBeFalse();
        noSig.ShouldBe("缺少签名");

        EnvelopeVerifier.Verify(signed with { SenderPublicKey = null }, Crypto, out var noPk)
            .ShouldBeFalse();
        noPk.ShouldBe("缺少发送方公钥");
    }

    #endregion
}
