using System.Text;
using System.Text.RegularExpressions;
using P2PChat.Core.Models;
using P2PChat.Integration.Tests.Support;
using Shouldly;

namespace P2PChat.Integration.Tests;

/// <summary>
/// 跨组件回归守卫 —— 阻断级缺陷「收到的私聊/群聊消息永远不显示」（REPAIR-PLAN B3）。
/// <para>
/// <b>缺陷回顾</b>：<c>PrivateMessageHandler</c> / <c>GroupMessageHandler</c> 各自持有<b>私有的</b>
/// <c>Channel&lt;ChatMessageEvent&gt;</c> 并写入，而 TUI 读的是 <c>ChatService._messageChannel</c>。
/// 三个通道互不相通，<c>PublishMessageAsync</c> 在 <c>src/**</c> 零调用者 ——
/// 净效果是<b>用户能看到自己发出去的消息，永远看不到任何人发来的消息</b>。
/// </para>
///
/// <para><b>它为什么能长期潜伏</b>：当时全部 <c>OnMessageReceived</c> 测试引用读的都是
/// <b>handler 自己那条通道</b>，没有一处跨组件。所以「handler 确实收到并解密了消息」
/// 与「用户看到这条消息」之间那条连线<b>零覆盖</b>，缺陷再离谱也全绿。
/// 本文件锁的就是<b>那条连线</b>。</para>
///
/// <para><b>⚠️ 给未来加测试的人：<c>IChatService.OnMessageReceived</c> 是合并流。</b>
/// <c>ChatService</c> 发送时会往这条流写一条 <b>本机回显</b>（<c>IsOutgoing == true</c>），
/// 入站消息修复后也进<b>同一条</b>流 —— 因为 TUI 就该在这一条流上同时看到「我发的」与「我收到的」。
/// 因此：一个<b>既发又收</b>的节点若直接读裸流，会先读到自己的回显。
/// <b>请统一用 <see cref="NodeHarness.IncomingMessages"/>（已内置 <c>!IsOutgoing</c> 过滤），
/// 不要各自重写过滤逻辑。</b>这正是本轮 2 处用例不得不特殊处理的根因。</para>
///
/// <para><b>本文件两类用例，性质不同，请勿混淆</b>：
/// (1) <b>行为守卫</b>：真起两个 <c>NodeHarness</c>、走真实 TCP + 真实签名 + 真实 ECDH；
/// (2) <b>结构守卫</b>：<b>只读产品源码文本</b>，不启动任何节点、不发任何报文
/// （与 <c>PlainModeInputTests</c> P11 同形）。</para>
/// </summary>
public class ChatEventDeliveryTests
{
    private static async Task<ChatMessageEvent> ReceiveOneAsync(
        IAsyncEnumerable<ChatMessageEvent> stream, int timeoutMs = 20000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        await foreach (var evt in stream.WithCancellation(cts.Token))
            return evt;
        throw new TimeoutException($"等待聊天事件超时 ({timeoutMs}ms)");
    }

    // ============================================================ 行为守卫（真实 TCP）

    /// <summary>
    /// <b>本文件最重要的一条。</b>经真实 TCP 收到一条私聊后，<b>UI 真正消费的那条流</b>
    /// （<c>IChatService.OnMessageReceived</c>）必须产出该事件。
    /// <para>
    /// 它与「handler 收到并解密了」是<b>两件事</b>。修复前 handler 照常解密成功，
    /// 但事件被写进了一条<b>没人读</b>的私有通道 —— 缺陷就是从这两者之间那道
    /// 断掉的连线上漏过去的。只有这条守卫能覆盖它。
    /// </para>
    /// </summary>
    [Fact]
    public async Task 真实TCP收到私聊_UI消费的那条流必须产出该事件()
    {
        await using var alice = NodeHarness.Start("alice");
        await using var bob = NodeHarness.Start("bob");
        alice.Discover(bob);
        bob.Discover(alice);

        const string text = "跨组件守卫：这条必须真的显示出来";
        await alice.Chat.SendPrivateMessageAsync(bob.LocalNode.NodeId, text);

        // 注意读的是 Chat.OnMessageReceived —— 就是 P2PChatTui 消费的那一条。
        var received = await ReceiveOneAsync(bob.Chat.OnMessageReceived);

        received.Content.ShouldBe(text, "入站私聊必须抵达 UI 消费的那条事件流");
        received.IsOutgoing.ShouldBeFalse();
        received.IsGroup.ShouldBeFalse();
        received.SenderId.ShouldBe(bob.Chat is null ? default : new NodeId(alice.SenderId));
    }

    /// <summary>
    /// 同一条线上，群消息也必须抵达 UI 消费的那条流 —— 缺陷是私聊与群聊<b>同时</b>中招的，
    /// 只守私聊会漏掉群聊那一半。
    /// </summary>
    [Fact]
    public async Task 真实TCP收到群消息_UI消费的那条流必须产出该事件()
    {
        await using var alice = NodeHarness.Start("alice");
        await using var bob = NodeHarness.Start("bob");
        alice.Discover(bob);

        var group = await alice.Group.CreateGroupAsync("跨组件群", [bob.LocalNode.NodeId]);
        bob.KeyStore.SetGroupKey(group.GroupId, group.GroupKey);

        const string text = "跨组件守卫：群消息也必须显示";
        await alice.Group.SendGroupMessageAsync(group.GroupId, text);

        var received = await ReceiveOneAsync(bob.Chat.OnMessageReceived);

        received.Content.ShouldBe(text);
        received.IsGroup.ShouldBeTrue();
        received.ConversationId.ShouldBe(group.GroupId);
    }

    /// <summary>
    /// <b>合并流语义守卫。</b><c>IChatService.OnMessageReceived</c> 同时承载
    /// 「本机发送回显」与「入站消息」—— TUI 靠这一条流同时渲染两者。
    /// <para>
    /// 修复前这条流<b>只有本机回显</b>（入站全走了 handler 私有通道），
    /// 所以这条同时把「回显还在」和「入站也进来了」两件事都锁住。
    /// </para>
    /// </summary>
    [Fact]
    public async Task 同一节点的本机回显与入站消息都在同一条流上()
    {
        await using var alice = NodeHarness.Start("alice");
        await using var bob = NodeHarness.Start("bob");
        alice.Discover(bob);
        bob.Discover(alice);

        // bob 收到 alice 的第一条（入站）
        await alice.Chat.SendPrivateMessageAsync(bob.LocalNode.NodeId, "入站第一条");
        var first = await ReceiveOneAsync(bob.IncomingMessages);
        first.Content.ShouldBe("入站第一条");

        // bob 自己发一条 → bob 的流上应当出现本机回显
        await bob.Chat.SendPrivateMessageAsync(alice.LocalNode.NodeId, "bob 的回显");

        ChatMessageEvent? echo = null;
        using var cts = new CancellationTokenSource(5000);
        await foreach (var evt in bob.Chat.OnMessageReceived.WithCancellation(cts.Token))
        {
            if (evt.IsOutgoing) { echo = evt; break; }
        }

        echo.ShouldNotBeNull("TUI 依赖这条流显示「我发出的消息」，回显不能因为修入站而被弄丢");
        echo!.Content.ShouldBe("bob 的回显");
    }

    /// <summary>
    /// <b>发布端与消费端必须是同一个对象。</b>
    /// <para>
    /// 这是整个修复成立的前提：若 handler 注入的发布器与 UI 读的 <c>IChatService</c>
    /// 是<b>两个实例</b>，事件会进一条没人读的通道，缺陷<b>原样复活</b>，
    /// 而所有「handler 输出」类测试仍然全绿。产品侧靠 DI 的
    /// <c>AddSingleton&lt;IChatEventPublisher&gt;(sp =&gt; sp.GetRequiredService&lt;ChatService&gt;())</c> 保证；
    /// 本条把它锁在测试夹具上，防止有人给 <c>NodeHarness</c> 接线时接错。
    /// </para>
    /// </summary>
    [Fact]
    public async Task 发布器与UI消费的服务必须是同一个实例()
    {
        await using var node = NodeHarness.Start("solo");

        node.Events.ShouldBeSameAs(node.Chat,
            "发布端（IChatEventPublisher）与消费端（IChatService）必须是同一个 ChatService 实例；" +
            "否则事件进了一条没人读的通道，「收到但永远不显示」的缺陷会原样复活");
    }

    /// <summary>
    /// <b>负向对照（negative control）—— 证明这个缺陷确实是可检出的。</b>
    /// <para>
    /// 这里刻意把 handler 的发布器接到一个<b>与 <c>ChatService</c> 无关</b>的对象上，
    /// 精确复现缺陷现场：handler 照常解密成功、照常投递事件，但事件进了一条
    /// <b>UI 永远不读</b>的通道。
    /// </para>
    /// <para>
    /// 断言的重点是那个<b>刺眼的反差</b>：<c>isolated.Published</c> 非空
    /// （"handler 层测试全部通过"），而 <c>node.Chat.OnMessageReceived</c> 上什么都没有
    /// （"用户界面永远空白"）。这正是本轮 40 余处旧测试<b>整体漏检</b>的原因 ——
    /// 它们断言的都是左边那一半。
    /// </para>
    /// <para>
    /// 有了这条，上面 <see cref="真实TCP收到私聊_UI消费的那条流必须产出该事件"/> 就不是
    /// "当然会绿"的摆设：一旦有人把发布器接错，跨组件守卫会立刻变红，而 handler 单测仍全绿。
    /// </para>
    /// </summary>
    [Fact]
    public async Task 负向对照_发布器接错时handler层全绿但UI流上什么都没有()
    {
        await using var node = NodeHarness.Start("victim");

        var crypto = new P2PChat.Crypto.Encryption.AesGcmEncryptionService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<
                P2PChat.Crypto.Encryption.AesGcmEncryptionService>.Instance);
        var keyStore = new P2PChat.Integration.Tests.Support.InMemoryKeyStore(crypto);
        var sender = P2PChat.Core.Models.NodeId.CreateRandom();
        var sessionKey = crypto.GenerateRandomKey();
        keyStore.SetSessionKey(sender, sessionKey);

        // 缺陷现场：发布器与 node.Chat 毫无关系
        var isolated = new P2PChat.Integration.Tests.Support.CapturingChatEventPublisher();
        var handler = new P2PChat.Chat.Handlers.PrivateMessageHandler(
            crypto, keyStore, isolated,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<
                P2PChat.Chat.Handlers.PrivateMessageHandler>.Instance);

        const string plain = "解密成功但用户看不到";
        await handler.HandleAsync(new P2PChat.Core.Models.TextMessage
        {
            SenderId = sender.ToByteArray(),
            ConversationId = "conv",
            Content = Convert.ToBase64String(
                crypto.Encrypt(System.Text.Encoding.UTF8.GetBytes(plain), sessionKey)),
            IsGroup = false
        },
        new P2PChat.Integration.Tests.Support.FakeTcpConnection(),
        new P2PChat.Core.Models.MessageEnvelope
        {
            MessageType = P2PChat.Core.Enums.MessageType.PrivateText,
            SenderId = sender.ToByteArray(),
            Payload = []
        });

        // 左边：handler 层看起来完全正常 —— 这就是旧测试全部漏检的那一半
        isolated.Published.ShouldHaveSingleItem("handler 确实解密成功并投递了事件");
        isolated.Published[0].Content.ShouldBe(plain);

        // 右边：而 UI 消费的那条流上，一件事都没发生
        var sawSomething = false;
        using var cts = new CancellationTokenSource(700);
        try
        {
            await foreach (var _ in node.Chat.OnMessageReceived.WithCancellation(cts.Token))
            {
                sawSomething = true;
                break;
            }
        }
        catch (OperationCanceledException) { }

        sawSomething.ShouldBeFalse(
            "发布器接错时，UI 消费的那条流上不会出现任何事件 —— " +
            "这正是本轮阻断级缺陷的现场，也是旧测试整体漏检的原因");
    }

    // ==================================================== 结构守卫（只读源码，不启动节点）
    /// <summary>
    /// 回归守卫 D1 —— <c>PrivateMessageHandler</c> / <c>GroupMessageHandler</c>
    /// <b>不得再持有自己的 <c>Channel&lt;ChatMessageEvent&gt;</c></b>。
    /// <para>
    /// <b>性质：源码结构契约，不是行为测试。</b>它只读产品源码文本，不启动节点、不发报文。
    /// 之所以需要它：行为测试只能证明「当前这两个 handler 没建第二条通道」，
    /// 而<b>下一个人</b>再写一个 handler 时顺手建一条，所有行为测试照样全绿
    /// —— 缺陷会以同构形式复发。结构守卫把「handler 层不得自建事件通道」这条规矩本身钉住。
    /// </para>
    /// <para>
    /// <b>为什么不用反射</b>：本项目 tests/ 下反射用量为 0，且 Agent.md §4.3 把反射列为禁忌
    /// （Native-AOT 下反射目标缺静态调用点会被 ILC 裁剪）。故沿用
    /// <c>PlainModeInputTests</c> P11「TUI 不得自带信封解析副本」的同形写法。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("PrivateMessageHandler.cs")]
    [InlineData("GroupMessageHandler.cs")]
    public void 消息处理器不得自建聊天事件通道(string fileName)
    {
        var code = StripComments(ReadSource("src", "P2PChat.Chat", "Handlers", fileName));

        code.Contains("Channel<ChatMessageEvent>", StringComparison.Ordinal).ShouldBeFalse(
            $"{fileName} 不得再声明自己的 Channel<ChatMessageEvent> —— " +
            "UI 读的是 ChatService 那条流，第二个通道就是「收到却永远不显示」的复发");

        // 更强的一刀：handler 层连 Channel 这个类型都不该再碰。
        code.Contains("Channel", StringComparison.Ordinal).ShouldBeFalse(
            $"{fileName} 不得再引用任何 Channel —— 消息处理器只负责解密与投递，" +
            $"事件出口唯一（IChatEventPublisher → ChatService）");
    }

    /// <summary>
    /// 回归守卫 D2 —— <b>唯一来源铁律</b>：整个 <c>P2PChat.Chat</c> 项目里，
    /// 真正声明 <c>Channel&lt;ChatMessageEvent&gt;</c> 的文件<b>有且只有</b> <c>ChatService.cs</c>。
    /// <para>
    /// D1 只盯住了两个<b>已知</b>的 handler；本条盯的是<b>整个程序集</b>，
    /// 因此<b>将来新写的任何 handler / service</b> 一旦自建聊天事件通道都会立刻变红。
    /// 这是 D1 做不到的覆盖面。
    /// </para>
    /// <para>
    /// <b>性质：源码结构契约。</b>只读源码文本，不启动节点。
    /// 注意只匹配<b>未转义</b>的 <c>Channel&lt;ChatMessageEvent&gt;</c>（真实声明），
    /// 文档注释里写的转义形式 <c>Channel&amp;lt;ChatMessageEvent&amp;gt;</c> 不算。
    /// </para>
    /// </summary>
    [Fact]
    public void 整个Chat项目里聊天事件通道只能由ChatService声明()
    {
        var offenders = new List<string>();
        var scanned = 0;

        foreach (var path in Directory.EnumerateFiles(ChatSourceRoot(), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetFileName(path);
            if (relative == "ChatService.cs") continue;

            scanned++;
            var code = StripComments(File.ReadAllText(path));
            if (code.Contains("Channel<ChatMessageEvent>", StringComparison.Ordinal))
                offenders.Add(relative);
        }

        // 防「空跑通过」：如果目录被改名/移动导致扫不到任何文件，offenders 天然为空、
        // 断言会毫无意义地变绿。必须先证明确实扫到了 Chat 项目的源码。
        scanned.ShouldBeGreaterThan(5,
            "扫描到的 Chat 项目源码文件过少 —— 结构契约断言可能已空跑（src/P2PChat.Chat 路径是否变了？）");

        offenders.ShouldBeEmpty(
            "「唯一来源铁律」：只有 ChatService 可以持有 ChatMessageEvent 的通道。" +
            "以下文件自建了通道，其写入的事件 UI 永远读不到：" + string.Join(", ", offenders));
    }

    /// <summary>
    /// 回归守卫 D3 —— TUI 消费的必须仍是 <c>IChatService.OnMessageReceived</c>。
    /// <para>
    /// 前两条守的是「生产端别分叉」，本条守的是<b>「消费端没漂移」</b>：
    /// 若有人为了「图方便」把 TUI 改成读某个 handler 的通道，缺陷立刻复发，
    /// 而 handler 单测会依然全绿。
    /// </para>
    /// <para><b>性质：源码结构契约。</b>只读源码文本，不启动节点、不发 stdin。</para>
    /// </summary>
    [Fact]
    public void TUI必须继续消费IChatService的事件流()
    {
        var code = StripComments(ReadSource("src", "P2PChat.UI", "Views", "P2PChatTui.cs"));

        Regex.IsMatch(code, @"chatService\.OnMessageReceived").ShouldBeTrue(
            "TUI 必须读 IChatService.OnMessageReceived —— 那是 UI 唯一的事件入口");

        code.Contains("Handler.OnMessageReceived", StringComparison.Ordinal).ShouldBeFalse(
            "TUI 不得改为直接消费某个消息处理器的流 —— 那是「消息永远不显示」缺陷的同构复发");
    }

    // ------------------------------------------------------------ 结构守卫的实现（源码定位）

    /// <summary>从测试程序集位置向上找 <c>P2PChat.slnx</c>，据此定位产品源码。</summary>
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
            "未能从测试程序集位置向上找到 P2PChat.slnx，无法读取产品源码进行结构契约断言");
    }

    private static string ChatSourceRoot() => Path.Combine(RepoRoot(), "src", "P2PChat.Chat");

    /// <summary>读仓库内某个产品源码文件的文本（路径相对仓库根，如 <c>src/P2PChat.UI/Views/…</c>）。</summary>
    private static string ReadSource(params string[] repoRelative)
    {
        var path = Path.Combine(new[] { RepoRoot() }.Concat(repoRelative).ToArray());

        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"结构契约断言找不到产品源码: {path}。若产品文件被移动/改名，请同步更新本守卫。", path);

        return File.ReadAllText(path);
    }

    /// <summary>
    /// 去掉 <c>//</c> 行注释（含 <c>///</c> XML 文档注释），保留换行。
    /// <para>
    /// 必须去注释：<c>IChatEventPublisher</c> 的契约注释里就写着
    /// 「任何组件都不得再自建 <c>Channel&amp;lt;ChatMessageEvent&amp;gt;</c>」——
    /// 那句话本身含有该字样，不去注释会让守卫读到自己的说明书然后自我误报。
    /// </para>
    /// </summary>
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
