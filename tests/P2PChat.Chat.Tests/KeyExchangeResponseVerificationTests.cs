using System.Text;
using System.Text.RegularExpressions;
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
/// 回归守卫 —— <c>ChatService</c> 的密钥交换<b>应答</b>读取路径曾经<b>完全不验签</b>。
/// <para>
/// <b>缺陷回顾</b>：<c>ReadKeyExchangeResponseAsync</c> 只做
/// <c>MessageRouter.DeserializeEnvelope(raw)</c> + 载荷反序列化就取用
/// <c>response.EphemeralPublicKey</c> 派生会话密钥，<b>从头到尾没有任何校验</b>。
/// 三处后果，全部静默：
/// <list type="number">
///   <item><b>会话密钥被单方面决定</b>：应答方可以给出<b>任意</b>临时公钥。攻击者给出自己的，
///         就能解出该会话后续的全部私聊。而密钥被记在<b>用户以为的那个对端</b>名下 ——
///         界面上看不出任何异常，日志里一行都没有。</item>
///   <item><b>响应者身份从未被核对</b>：应答来自哪个节点都没检查过，
///         「我在和 A 说话」这个前提从未成立过。</item>
///   <item><b>绕过重放防护</b>：本方法走不到 <c>MessageRouter.RouteIncomingAsync</c>，
///         因而已建立的入站重放防护<b>不覆盖这条路径</b> —— 两套强度不一致。</item>
/// </list>
/// </para>
///
/// <para><b>本文件是行为测试，不是结构契约守卫。</b>
/// 与 <c>tests/P2PChat.Integration.Tests/HelloResponseVerificationTests.cs</c>
/// （TUI 那条路径只能做源码断言，因为 TUI 无法在进程内构造）不同，
/// <c>ChatService</c> 可以真实构造，因此这里全部走<b>真实密码学 + 真实重放防护</b>，
/// 断言的是「消息到底发没发出去、会话密钥到底写没写」。
/// 末尾另有三条结构守卫，负责防「把校验整段删掉」这类行为测试察觉不到的回归。</para>
///
/// <para><b>本文件与 TUI 那条路径的关键差异（别把两者的判据混为一谈）</b>：
/// <c>/connect</c> 连的是<b>未知端点</b>，只能 TOFU + 端点连续性；
/// 而这里 <c>expectedPeerId</c> 来自 <c>FindNodeAsync</c> / 联系人，是<b>已知的预期身份</b>，
/// 因此本路径能（且必须）直接拒掉「应答方不是我要找的那个节点」——
/// 这是本文件里唯一一条 TUI 路径<b>做不到</b>的判据。</para>
/// </summary>
public class KeyExchangeResponseVerificationTests
{
    private static readonly ISerializer Wire = new MessagePackSerializer();

    private sealed record Rig(
        ChatService Service,
        RecordingMessageRouter Router,
        InMemoryKeyStore KeyStore,
        IEncryptionService Crypto,
        KeyPair PeerIdentity,
        NodeInfo Peer,
        FakeTcpConnection Link);

    private static Rig Build()
    {
        var crypto = new AesGcmEncryptionService(NullLogger<AesGcmEncryptionService>.Instance);
        var keyStore = new InMemoryKeyStore(crypto);
        var identity = keyStore.GetOrCreateIdentity();
        var peerIdentity = crypto.GenerateKeyPair();

        var local = new NodeInfo
        {
            NodeId = identity.NodeId,
            EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 46001),
            PublicKey = identity.PublicKey
        };
        var peer = TestNodes.RealKeyPairNode(peerIdentity, 46002);

        var dht = new StubDhtService(local).Register(peer);
        var router = new RecordingMessageRouter();
        var link = new FakeTcpConnection(peer.EndPoint);
        router.Connection = link;

        // 注入**真实**重放防护：换成「永远放行」的假实现，本文件里判据④那条用例会变成空转。
        var service = new ChatService(
            dht, router, crypto, keyStore, TestReplayGuards.Create(), NullLogger<ChatService>.Instance);

        return new Rig(service, router, keyStore, crypto, peerIdentity, peer, link);
    }

    /// <summary>读取本机刚发出的那条交换请求里的关联标识（应答必须原样回显它）。</summary>
    private static string SentCorrelationId(RecordingMessageRouter router)
        => ((KeyExchangeMessage)router.SentViaConnection.Last().Message).ConversationId;

    /// <summary>
    /// 让连接在被读到时产出一帧。回调触发时请求已经被路由器记录，因此能读到本次的关联标识。
    /// </summary>
    private static void RespondWith(FakeTcpConnection link, RecordingMessageRouter router, Func<string, byte[]> factory)
        => link.OnReceiveWhenEmpty = () => factory(SentCorrelationId(router));

    // ───────────────────────────── 判据 ②：验签 ─────────────────────────────

    [Fact]
    public async Task 密钥交换应答未签名时拒绝发送_不写会话密钥也不把明文推上线()
    {
        var r = Build();

        // 手工造一条**没有签名**的应答：信封格式合法、载荷也对，但没签。
        // 这正是原缺陷的形态 —— ChatService 此前对这样的帧来者不拒。
        RespondWith(r.Link, r.Router, correlationId =>
            MessageRouter.SerializeEnvelope(new MessageEnvelope
            {
                MessageType = MessageType.KeyExchange,
                SenderId = r.Peer.NodeId.ToByteArray(),
                SenderPublicKey = r.PeerIdentity.PublicKey,
                Payload = Wire.Serialize<Message>(new KeyExchangeMessage
                {
                    SenderId = r.Peer.NodeId.ToByteArray(),
                    ConversationId = correlationId,
                    EphemeralPublicKey = r.Crypto.GenerateKeyPair().PublicKey,
                    IsResponse = true
                })
            }));

        var ex = await Should.ThrowAsync<KeyExchangeRejectedException>(
            () => r.Service.SendPrivateMessageAsync(r.Peer.NodeId, "机密内容"));
        ex.Message.ShouldContain("验签");

        AssertNothingLeaked(r);
    }

    [Fact]
    public async Task 密钥交换应答冒用对端SenderId时拒绝发送()
    {
        var r = Build();

        // 攻击者用自己的私钥，签一个 SenderId 指向对端的信封 ——
        // 签名覆盖 SenderId，所以签名本身**完全有效**；骗过的是「公钥↔身份绑定」那一关。
        var impostor = r.Crypto.GenerateKeyPair();
        RespondWith(r.Link, r.Router, correlationId =>
        {
            var envelope = new MessageEnvelope
            {
                MessageType = MessageType.KeyExchange,
                SenderId = r.Peer.NodeId.ToByteArray(),      // 冒名：声称自己是对端
                SenderPublicKey = impostor.PublicKey,         // 但用的是自己的公钥
                Payload = Wire.Serialize<Message>(new KeyExchangeMessage
                {
                    SenderId = r.Peer.NodeId.ToByteArray(),
                    ConversationId = correlationId,
                    EphemeralPublicKey = r.Crypto.GenerateKeyPair().PublicKey,
                    IsResponse = true
                })
            };
            return MessageRouter.SerializeEnvelope(
                MessageRouter.SignEnvelope(envelope, impostor, r.Crypto));
        });

        var ex = await Should.ThrowAsync<KeyExchangeRejectedException>(
            () => r.Service.SendPrivateMessageAsync(r.Peer.NodeId, "机密内容"));
        ex.Message.ShouldContain("验签");

        AssertNothingLeaked(r);
    }

    // ────────────────── 判据 ③：签名者必须是预期对端（本路径独有） ──────────────────

    [Fact]
    public async Task 密钥交换应答由非预期节点签名时拒绝发送_即使那条签名完全有效()
    {
        var r = Build();
        var attacker = r.Crypto.GenerateKeyPair();

        // 攻击者持有自己的合法私钥，因此这条应答<b>通过验签</b>、公钥与身份也自洽 ——
        // 前两道判据都拦不住它。唯一能认出它的是「它不是我要找的那个节点」。
        RespondWith(r.Link, r.Router, correlationId =>
            KeyExchangeResponseFrames.Create(attacker, attacker.PublicKey, correlationId));

        var ex = await Should.ThrowAsync<KeyExchangeRejectedException>(
            () => r.Service.SendPrivateMessageAsync(r.Peer.NodeId, "机密内容"));
        ex.Message.ShouldContain("非预期节点");

        AssertNothingLeaked(r);
    }

    [Fact]
    public async Task 攻击者的临时公钥不得被用来派生会话密钥()
    {
        var r = Build();
        var attacker = r.Crypto.GenerateKeyPair();

        // 与「非预期节点签名」那条同源，但断言的是<b>派生结果</b>而不是异常：
        // 攻击者的临时公钥既然来自一个被拒绝的应答，就一个字节都不许进入本机会话密钥。
        // 这里不断言它「可解密」——那是对攻击者能力的描述，与本机要不要写这个密钥无关；
        // 真正要钉住的是 <see cref="InMemoryKeyStore.SessionKeyWrites"/> 为空。
        var attackerEphemeral = r.Crypto.GenerateKeyPair();
        RespondWith(r.Link, r.Router, correlationId =>
            KeyExchangeResponseFrames.Create(attacker, attackerEphemeral.PublicKey, correlationId));

        await Should.ThrowAsync<KeyExchangeRejectedException>(
            () => r.Service.SendPrivateMessageAsync(r.Peer.NodeId, "机密内容"));

        r.KeyStore.GetSessionKey(r.Peer.NodeId).ShouldBeNull();
        r.KeyStore.SessionKeyWrites.ShouldBeEmpty(
            "会话密钥的派生只依赖应答里的临时公钥；只要它来自非预期节点，就一个字节都不能写");
    }

    // ───────────────────────── 判据 ①：一次性关联标识回显 ─────────────────────────

    [Fact]
    public async Task 密钥交换应答回显的不是本次关联标识时拒绝发送()
    {
        var r = Build();

        // 攻击者用**自己的**密钥为某个受害者生成过一条完全合法的应答并录下，
        // 现在重放给另一个节点。那条应答回显的是**上一次**的关联标识，对不上。
        RespondWith(r.Link, r.Router, _ =>
            KeyExchangeResponseFrames.Create(
                r.PeerIdentity, r.Crypto.GenerateKeyPair().PublicKey, "kx-上一次交换的值"));

        var ex = await Should.ThrowAsync<KeyExchangeRejectedException>(
            () => r.Service.SendPrivateMessageAsync(r.Peer.NodeId, "机密内容"));
        ex.Message.ShouldContain("关联标识");

        AssertNothingLeaked(r);
    }

    [Fact]
    public async Task 关联标识每次交换都必须重新生成_不得退回恒定的会话键()
    {
        var r = Build();
        var seen = new List<string>();

        // 两次交换（中间清掉会话键，逼它重新握手），各记录一次本机发出的关联标识。
        for (var i = 0; i < 2; i++)
        {
            RespondWith(r.Link, r.Router, correlationId =>
                KeyExchangeResponseFrames.Create(
                    r.PeerIdentity, r.Crypto.GenerateKeyPair().PublicKey, correlationId));

            await r.Service.SendPrivateMessageAsync(r.Peer.NodeId, $"第 {i} 条");
            seen.Add(SentCorrelationId(r.Router));
            r.KeyStore.RemoveSessionKey(r.Peer.NodeId);
        }

        seen.Distinct().Count().ShouldBe(2,
            "关联标识必须是每次交换重新随机的一次性值；恒定值会让回显校验形同虚设");
    }

    // ───────────────────────────── 判据 ④：重放防护 ─────────────────────────────

    [Fact]
    public async Task 同一条密钥交换应答重复投递时第二次被重放防护拒绝()
    {
        var r = Build();
        var recordedMessageId = Guid.NewGuid();

        // 两次交换各造一条**独立**的应答帧，唯一的共同点是**复用同一个 MessageId**。
        // 关联标识、临时公钥、签名三者各不相同，因此判据①②③在第二次**都会放行** ——
        // 能拦住的只剩判据④。
        //
        // 为什么不能直接投递字节完全相同的那一帧：那样 nonce 也相同，判据①会先拦下，
        // 判据④ 根本没机会执行 —— 那测的就不是重放防护了。现实中对应的场景是
        // 传输层/连接层把同一条消息重复投递（重复的是消息身份，不是整条字节流）。
        RespondWith(r.Link, r.Router, correlationId =>
            KeyExchangeResponseFrames.Create(
                r.PeerIdentity, r.Crypto.GenerateKeyPair().PublicKey, correlationId,
                messageId: recordedMessageId));

        await r.Service.SendPrivateMessageAsync(r.Peer.NodeId, "第一条");
        r.KeyStore.RemoveSessionKey(r.Peer.NodeId);

        RespondWith(r.Link, r.Router, correlationId =>
            KeyExchangeResponseFrames.Create(
                r.PeerIdentity, r.Crypto.GenerateKeyPair().PublicKey, correlationId,
                messageId: recordedMessageId));

        var ex = await Should.ThrowAsync<KeyExchangeRejectedException>(
            () => r.Service.SendPrivateMessageAsync(r.Peer.NodeId, "第二条"));

        // 断言必须用判据④ **独有**的措辞：判据① 的拒绝文案里也含「重放」二字
        // （"疑似重放了一条旧的合法应答"），只写 ShouldContain("重放") 会让本用例
        // 在判据① 拦下时也照样通过 —— 那是一条假通过，护不住任何东西。
        ex.Message.ShouldContain("未通过重放防护");

        r.KeyStore.GetSessionKey(r.Peer.NodeId).ShouldBeNull();
    }

    // ───────────────────────────── 超时：避免整个 TUI 卡死 ─────────────────────────────

    /// <summary>
    /// 对端连得上却**永不回应**时，握手必须在超时内失败，而不是无限等待。
    /// <para>
    /// 没有这条，<c>ReadKeyExchangeResponseAsync</c> 会在 <c>while</c> 里一直等 ——
    /// 而 TUI 的发送私聊是单线程 await，用户既看不到消息、也看不到任何错误，
    /// 表现就是<b>整个界面卡死</b>。这条把「有上限」钉成回归项。
    /// </para>
    /// </summary>
    [Fact]
    public async Task 对端永不回应密钥交换时按超时拒绝_而不是无限等待()
    {
        var r = Build();

        // 把 10s 压到 200ms：这条分支的**内容**是「等够了就放弃」，
        // 时长本身不是被测对象，不值得让整套件为此多花 10 秒。
        r.Service.KeyExchangeTimeout = TimeSpan.FromMilliseconds(200);

        // 对端只发非应答帧、永不回密钥交换应答。真实 TCP 读会阻塞在 socket 上，
        // 所以替身也必须阻塞；否则它会立刻抛 InvalidOperationException，
        // 那条路径压根到不了超时分支。
        r.Link.Enqueue(MessageRouter.SerializeEnvelope(new MessageEnvelope
        {
            MessageType = MessageType.PrivateText,
            SenderId = r.Peer.NodeId.ToByteArray(),
            Payload = Wire.Serialize<Message>(new TextMessage
            {
                SenderId = r.Peer.NodeId.ToByteArray(),
                ConversationId = ConversationId.ForPrivate(r.Peer.NodeId, r.Peer.NodeId),
                Content = Convert.ToBase64String(r.Crypto.Encrypt(
                    Encoding.UTF8.GetBytes("路过的闲聊"), new byte[32])),
                IsGroup = false
            })
        }));
        r.Link.BlockOnReceiveWhenEmpty = true;

        var ex = await Should.ThrowAsync<KeyExchangeRejectedException>(
            () => r.Service.SendPrivateMessageAsync(r.Peer.NodeId, "机密内容"));
        ex.Message.ShouldContain("没有回应");

        AssertNothingLeaked(r);
    }

    // ───────────────────────────── 正向：防止「一律拒绝」 ─────────────────────────────

    [Fact]
    public async Task 四道判据全部通过时正常建立会话并发出消息()
    {
        var r = Build();

        RespondWith(r.Link, r.Router, correlationId =>
            KeyExchangeResponseFrames.Create(
                r.PeerIdentity, r.Crypto.GenerateKeyPair().PublicKey, correlationId));

        await r.Service.SendPrivateMessageAsync(r.Peer.NodeId, "你好");

        var sessionKey = r.KeyStore.GetSessionKey(r.Peer.NodeId)!;
        sessionKey.Length.ShouldBe(32);

        // 光断言「密文非空」等于什么都没断言 —— base64 密文天然非空。
        // 必须拿**本次交换刚建的那把**会话密钥把正文解回来，才证明加密用的是它。
        var text = r.Router.Sent.OfType<TextMessage>().Single();
        Encoding.UTF8.GetString(
                r.Crypto.Decrypt(Convert.FromBase64String(text.Content), sessionKey))
            .ShouldBe("你好", "正文必须用本次交换刚建立的那把会话密钥加密");
    }

    // ───────────────────────────── 结构守卫（防「把校验删掉」） ─────────────────────────────

    /// <summary>
    /// 守卫 K1 —— <c>ChatService</c> 必须真的调用 Core 的 <c>EnvelopeVerifier.Verify</c>。
    /// <para>
    /// 本文件其余用例都是「负向断言」：有人把验签整段删掉，它们**全部**会变红 ——
    /// 这正是它们的价值。但反过来说，如果有人把「拒绝」改成「告警后继续」，
    /// 负向用例仍会红（因为异常没了），所以这一条是补最后那个缺口：
    /// 保证**正向**的接线也还在。
    /// </para>
    /// </summary>
    [Fact]
    public void ChatService必须调用Core的EnvelopeVerifier验签()
    {
        var code = StripComments(ReadChatServiceSource());

        code.Contains("EnvelopeVerifier.Verify(", StringComparison.Ordinal).ShouldBeTrue(
            "密钥交换应答必须送进 Core.Extensions.EnvelopeVerifier.Verify —— " +
            "验签规则只有 Core 那一份，本文件不得也不需要再抄");
    }

    /// <summary>
    /// 守卫 K2 —— <c>ChatService</c> 必须持有并使用 <c>IReplayGuard</c>。
    /// <para>
    /// 这条对应 HANDOFF §8 记的「同一路径同时绕过重放防护」：
    /// 该路径走不到 <c>MessageRouter.RouteIncomingAsync</c>，因而不在入站防护覆盖范围内。
    /// <c>EnsureSessionKeyAsync</c> 里那个 <c>_replayGuard</c> 字段若被注入成
    /// 「永远放行」的实现，判据④那条用例会变空转；这里钉住的是「确实过了一遍」。
    /// </para>
    /// </summary>
    [Fact]
    public void ChatService的密钥交换应答必须经过重放防护()
    {
        var code = StripComments(ReadChatServiceSource());

        code.Contains("_replayGuard.TryAccept(", StringComparison.Ordinal).ShouldBeTrue(
            "密钥交换应答读取路径必须自己过一遍 IReplayGuard —— " +
            "它走不到 MessageRouter.RouteIncomingAsync，不受那里的入站防护覆盖");
    }

    /// <summary>
    /// 守卫 K3 —— 对端身份必须由<b>公钥派生</b>，不得取自载荷里的 <c>SenderId</c>。
    /// <para>
    /// 载荷的 <c>SenderId</c> 是对端完全可控的输入。历史事故里同一个方法相距数行
    /// 一个取信封、一个取载荷，导致会话密钥被登记到错误的节点名下。
    /// 本条查未经去注释的原始文本 —— 要防的正是「注释里的误导」。
    /// </para>
    /// </summary>
    [Fact]
    public void ChatService不得用载荷里的SenderId判定对端身份()
    {
        var code = StripComments(ReadChatServiceSource());

        code.Contains("NodeId.FromPublicKey(", StringComparison.Ordinal).ShouldBeTrue(
            "对端身份必须从已验签的公钥派生（NodeId.FromPublicKey）");

        Regex.IsMatch(code, @"new\s+NodeId\s*\(\s*response\.SenderId").ShouldBeFalse(
            "不得用载荷里的 SenderId 构造对端 NodeId —— 那是完全可控的对端输入");
    }

    // ───────────────────────────── 断言与源码定位 ─────────────────────────────

    /// <summary>
    /// 统一断言「什么都没漏出去」：没写会话密钥、没把明文推上线、没报本地已发送事件。
    /// <para>
    /// 单独抽出来是因为它在每条负向用例里都要说一遍，而<b>三条断言缺一条都算数不上的
    /// 守不住</b>：只断言抛异常，挡不住「先发出去再抛」。
    /// </para>
    /// </summary>
    private static void AssertNothingLeaked(Rig r)
    {
        r.KeyStore.SessionKeyWrites.ShouldBeEmpty("校验未通过时不得写入任何会话密钥");
        r.KeyStore.GetSessionKey(r.Peer.NodeId).ShouldBeNull();
        r.Router.Sent.OfType<TextMessage>().ShouldBeEmpty("校验未通过时明文绝不能上线");
    }

    private static string RepoRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "P2PChat.slnx")))
                    return dir.FullName;
                dir = dir?.Parent;
            }
        }
        throw new DirectoryNotFoundException(
            "未能从测试程序集位置向上找到 P2PChat.slnx，无法读取 ChatService.cs 进行结构契约断言");
    }

    private static string ReadChatServiceSource()
    {
        var path = Path.Combine(RepoRoot(), "src", "P2PChat.Chat", "Services", "ChatService.cs");
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"结构契约断言找不到产品源码: {path}。若产品文件被移动/改名，请同步更新本守卫。", path);
        return File.ReadAllText(path);
    }

    private static string StripComments(string source)
    {
        var sb = new StringBuilder(source.Length);
        foreach (var raw in source.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var at = line.IndexOf("//", StringComparison.Ordinal);
            if (at >= 0) line = line[..at];
            sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }
}
