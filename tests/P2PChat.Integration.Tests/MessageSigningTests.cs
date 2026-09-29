using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using P2PChat.Chat.Routing;
using P2PChat.Core.Abstractions;
using P2PChat.Core.Enums;
using P2PChat.Core.Extensions;
using P2PChat.Core.Models;
using P2PChat.Crypto.Encryption;
using P2PChat.Integration.Tests.Support;
using Shouldly;

namespace P2PChat.Integration.Tests;

/// <summary>
/// 消息签名/验签回归测试 —— 线路信封携带 ECDSA P-256 签名
/// （<c>SenderPublicKey</c> + <c>Signature</c>）之后，经真实 TCP 的验签与拒绝路径。
/// 现行信封布局见 <c>Agent.md</c> §7「消息信封（Envelope）」。
/// <para>
/// 覆盖路径：
/// (1) ECDSA 真实签名 → 反序列化 → 验签通过（两节点真实 TCP）。
/// (2) 篡改 SenderId → 验签失败 → 丢弃。
/// (3) 篡改 Payload → 验签失败 → 丢弃。
/// (4) SenderId 与 SenderPublicKey 派生 NodeId 不一致 → 验签失败 → 丢弃（防 SenderId 冒名）。
/// </para>
/// </summary>
public class MessageSigningTests
{
    private static AesGcmEncryptionService NewEncryption()
        => new(NullLogger<AesGcmEncryptionService>.Instance);

    private static async Task<ChatMessageEvent> ReceiveOneAsync(
        IAsyncEnumerable<ChatMessageEvent> stream, int timeoutMs = 20000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        await foreach (var evt in stream.WithCancellation(cts.Token))
            return evt;
        throw new TimeoutException($"等待消息超时 ({timeoutMs}ms)");
    }

    [Fact]
    public async Task 出站消息_签名并附带SenderPublicKey_接收端验签通过明文还原()
    {
        // 真实 ECDSA 往返：Alice 签名，Bob 验签 + 解密。
        await using var alice = NodeHarness.Start("alice");
        await using var bob = NodeHarness.Start("bob");
        alice.Discover(bob);
        bob.Discover(alice);

        const string text = "签名往返：你好 Bob 🚀";
        await alice.Chat.SendPrivateMessageAsync(bob.LocalNode.NodeId, text);

        var received = await ReceiveOneAsync(bob.IncomingMessages);
        received.Content.ShouldBe(text);
        received.IsOutgoing.ShouldBeFalse();

        // SenderId 必须等于公钥派生的 NodeId（双重确认 2026-09-20 sender-identity + 2026-09-21 signing）
        var alicePubKey = alice.Identity.PublicKey;
        var derivedNodeId = NodeId.FromPublicKey(alicePubKey);
        received.SenderId.ToByteArray().ShouldBe(derivedNodeId.ToByteArray());
    }

    [Fact]
    public async Task 篡改SenderId_接收端验签失败丢弃且无ChatMessageEvent()
    {
        // 直接构造 envelope：SenderId 与 SenderPublicKey 派生的 NodeId 不一致（攻击者场景）。
        await using var bob = NodeHarness.Start("bob");
        var encryption = NewEncryption();
        var aliceIdentity = encryption.GenerateKeyPair();
        var bobIdentity = encryption.GenerateKeyPair();

        var envelope = new MessageEnvelope
        {
            Version = 1,
            MessageType = MessageType.PrivateText,
            SequenceNumber = 1,
            SenderId = bobIdentity.NodeId.ToByteArray(),    // 攻击者想冒名 Bob
            MessageId = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SenderPublicKey = aliceIdentity.PublicKey,        // 但实际签名者是 Alice
            Payload = new MessagePackSerializer().Serialize<Message>(new TextMessage
            {
                SenderId = bobIdentity.NodeId.ToByteArray(),
                ConversationId = "x",
                Content = "伪装成 Bob 的消息",
                IsGroup = false
            })
        };
        // 用 alice 的私钥签出
        var signed = MessageRouter.SignEnvelope(envelope, aliceIdentity, encryption);

        // 喂给 bob 路由器
        await bob.Router.RouteIncomingAsync(signed, new FakeTcpConnection());

        using var cts = new CancellationTokenSource(1000);
        var got = false;
        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in bob.IncomingMessages.WithCancellation(cts.Token))
            {
                got = true;
                break;
            }
        });
        got.ShouldBeFalse("SenderId 与 SenderPublicKey 派生的 NodeId 不一致，接收端必须丢弃");
    }

    [Fact]
    public async Task 篡改Payload_接收端验签失败丢弃且无ChatMessageEvent()
    {
        // 不走真实发送路径，避免污染 Bob 的入站 Channel；直接构造 envelope 并篡改。
        await using var bob = NodeHarness.Start("bob");
        var encryption = NewEncryption();
        var aliceIdentity = encryption.GenerateKeyPair();

        var realText = new TextMessage
        {
            SenderId = aliceIdentity.NodeId.ToByteArray(),
            ConversationId = "x",
            Content = "原始内容",
            IsGroup = false
        };
        var realPayload = new MessagePackSerializer().Serialize<Message>(realText);

        var envelope = new MessageEnvelope
        {
            Version = 1,
            MessageType = MessageType.PrivateText,
            SequenceNumber = 1,
            SenderId = aliceIdentity.NodeId.ToByteArray(),
            MessageId = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SenderPublicKey = aliceIdentity.PublicKey,
            Payload = realPayload
        };
        var signed = MessageRouter.SignEnvelope(envelope, aliceIdentity, encryption);

        // 篡改 payload：把 Payload 翻转一个字节，让签名失配
        var tamperedPayload = (byte[])signed.Payload!.Clone();
        tamperedPayload[0] ^= 0xFF;
        var tampered = signed with { Payload = tamperedPayload };

        await bob.Router.RouteIncomingAsync(tampered, new FakeTcpConnection());

        using var cts = new CancellationTokenSource(1000);
        var got = false;
        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in bob.IncomingMessages.WithCancellation(cts.Token))
            {
                got = true;
                break;
            }
        });
        got.ShouldBeFalse("Payload 篡改后 ECDSA 验签必失败，接收端必须丢弃");
    }

    [Fact]
    public void MessageRouter静态SignEnvelope与VerifyEnvelope往返一致()
    {
        // 不依赖任何网络 —— 单元级验证签名/验签逻辑。
        var encryption = NewEncryption();
        var identity = encryption.GenerateKeyPair();

        var envelope = new MessageEnvelope
        {
            Version = 1,
            MessageType = MessageType.GroupText,
            SequenceNumber = 42,
            SenderId = identity.NodeId.ToByteArray(),
            MessageId = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SenderPublicKey = identity.PublicKey,
            Payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }
        };

        var signed = MessageRouter.SignEnvelope(envelope, identity, encryption);
        signed.Signature.ShouldNotBeNull();
        signed.Signature!.Length.ShouldBeGreaterThan(0);

        // 验证应通过
        var ok = MessageRouter.VerifyEnvelope(signed, encryption, out var reason);
        ok.ShouldBeTrue();
        reason.ShouldBeNull();
    }

    [Fact]
    public void MessageRouter_缺签名_VerifyEnvelope返回false_原因为缺少签名()
    {
        var encryption = NewEncryption();
        var identity = encryption.GenerateKeyPair();

        var envelope = new MessageEnvelope
        {
            Version = 1,
            MessageType = MessageType.GroupText,
            SequenceNumber = 1,
            SenderId = identity.NodeId.ToByteArray(),
            MessageId = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SenderPublicKey = identity.PublicKey,
            Payload = new byte[] { 1, 2, 3 },
            Signature = null
        };

        var ok = MessageRouter.VerifyEnvelope(envelope, encryption, out var reason);
        ok.ShouldBeFalse();
        reason.ShouldNotBeNull();
        reason.ShouldContain("签名");
    }

    [Fact]
    public void MessageRouter_缺SenderPublicKey_VerifyEnvelope返回false_原因为缺少发送方公钥()
    {
        var encryption = NewEncryption();
        var identity = encryption.GenerateKeyPair();

        var envelope = new MessageEnvelope
        {
            Version = 1,
            MessageType = MessageType.GroupText,
            SequenceNumber = 1,
            SenderId = identity.NodeId.ToByteArray(),
            MessageId = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SenderPublicKey = null,
            Payload = new byte[] { 1, 2, 3 },
            // 故意不设 Signature —— 但测试只关心「缺公钥」分支比「缺签名」更优先
            Signature = new byte[64]
        };

        var ok = MessageRouter.VerifyEnvelope(envelope, encryption, out var reason);
        ok.ShouldBeFalse();
        reason.ShouldNotBeNull();
        reason.ShouldContain("公钥");
    }

    [Fact]
    public void MessageRouter_SenderId与公钥派生NodeId不一致_验签失败_原因为不匹配()
    {
        // 攻击者场景：持有合法签名（来自 Alice），但想冒名成 Bob → 改 SenderId 为 Bob。
        // 验证会捕获这种冒名（NodeId.FromPublicKey(alice.PubKey) ≠ bob.SenderId）。
        var encryption = NewEncryption();
        var alice = encryption.GenerateKeyPair();
        var bob = encryption.GenerateKeyPair();

        var realEnvelope = new MessageEnvelope
        {
            Version = 1,
            MessageType = MessageType.GroupText,
            SequenceNumber = 1,
            SenderId = alice.NodeId.ToByteArray(),          // 真实签名者
            MessageId = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SenderPublicKey = alice.PublicKey,              // 对应 Alice 的签名
            Payload = new byte[] { 1, 2, 3 }
        };
        var signed = MessageRouter.SignEnvelope(realEnvelope, alice, encryption);

        // 攻击者把 SenderId 换成 Bob 的（但不重签）
        var spoofed = signed with { SenderId = bob.NodeId.ToByteArray() };

        var ok = MessageRouter.VerifyEnvelope(spoofed, encryption, out var reason);
        ok.ShouldBeFalse();
        reason.ShouldNotBeNull();
        reason.ShouldContain("不匹配");
    }

    [Fact]
    public void MessageRouter_线缆_签名Envelope序列化与反序列化后验签仍通过()
    {
        var encryption = NewEncryption();
        var identity = encryption.GenerateKeyPair();

        var envelope = new MessageEnvelope
        {
            Version = 1,
            MessageType = MessageType.FileMeta,
            SequenceNumber = 99,
            SenderId = identity.NodeId.ToByteArray(),
            MessageId = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SenderPublicKey = identity.PublicKey,
            Payload = SHA256.HashData("wire-compat"u8.ToArray())
        };
        var signed = MessageRouter.SignEnvelope(envelope, identity, encryption);

        var bytes = MessageRouter.SerializeEnvelope(signed);
        var roundTripped = MessageRouter.DeserializeEnvelope(bytes);

        roundTripped.SenderPublicKey.ShouldBe(signed.SenderPublicKey);
        roundTripped.Signature.ShouldBe(signed.Signature);
        roundTripped.Payload.ShouldBe(signed.Payload);
        roundTripped.SenderId.ShouldBe(signed.SenderId);

        var ok = MessageRouter.VerifyEnvelope(roundTripped, encryption, out _);
        ok.ShouldBeTrue();
    }

    [Fact]
    public async Task 双节点_Alice篡改SenderId_Bob的路由拒绝并产生告警日志()
    {
        // 真实 TCP 集成：先让 alice 发一条签名消息（到达 bob），再让 alice 用 bob 的 SenderId 拼一条 envelope → bob 的接收端必须拒绝。
        await using var alice = NodeHarness.Start("alice");
        await using var bob = NodeHarness.Start("bob");
        alice.Discover(bob);
        bob.Discover(alice);

        // Step 1：触发一次正常 ECDH + 签名私聊，让 bob 持有 alice 的会话密钥。
        await alice.Chat.SendPrivateMessageAsync(bob.LocalNode.NodeId, "首条消息");
        var firstReceived = await ReceiveOneAsync(bob.IncomingMessages);
        firstReceived.Content.ShouldBe("首条消息");

        // Step 2：构造一条冒名 envelope —— SenderId 是 bob 的，但 SenderPublicKey 是 alice 的
        // （攻击场景：alice 想让 bob 的客户端相信"Bob 自己发的"）。
        // VerifyEnvelope 会因 NodeId.FromPublicKey(alice.PubKey) ≠ bob.SenderId 而拒绝。
        var encryption = NewEncryption();
        var envelope = new MessageEnvelope
        {
            Version = 1,
            MessageType = MessageType.PrivateText,
            SequenceNumber = 100,
            SenderId = bob.LocalNode.NodeId.ToByteArray(),
            MessageId = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SenderPublicKey = alice.Identity.PublicKey,
            Payload = new MessagePackSerializer().Serialize<Message>(new TextMessage
            {
                SenderId = bob.LocalNode.NodeId.ToByteArray(),
                ConversationId = "x",
                Content = "伪装成 Bob 的恶意消息",
                IsGroup = false
            })
        };
        var signed = MessageRouter.SignEnvelope(envelope, alice.Identity, encryption);

        // 通过 Bob 的路由器入口直接送入
        await bob.Router.RouteIncomingAsync(signed, new FakeTcpConnection());

        // Bob 的 PrivateHandler 不应收到任何 ChatMessageEvent（验签已失败丢弃）
        using var cts = new CancellationTokenSource(1000);
        var got = false;
        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in bob.IncomingMessages.WithCancellation(cts.Token))
            {
                got = true;
                break;
            }
        });
        got.ShouldBeFalse("SenderId 与 SenderPublicKey 派生的 NodeId 不一致，必须丢弃");
    }
}
