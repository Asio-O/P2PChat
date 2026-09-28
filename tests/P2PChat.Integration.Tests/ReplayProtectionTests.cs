using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using P2PChat.Chat.Routing;
using P2PChat.Core.Abstractions;
using P2PChat.Core.Enums;
using P2PChat.Core.Models;
using P2PChat.Integration.Tests.Support;
using P2PChat.Networking.Transport;
using Shouldly;

namespace P2PChat.Integration.Tests;

/// <summary>
/// 入站重放防护的端到端回归（真实 TCP + 真实 ECDSA 签名）。
/// <para>
/// 单元测试已经证明 <c>MessageReplayGuard</c> 的判定逻辑；本文件证明的是
/// <b>它真的接在链路上、且重放的是攻击者从线路上录下来的那一份原样字节</b>。
/// </para>
/// <para>
/// 关键设计：首次投递是真实的端到端流程（Alice 的 <c>MessageRouter</c> 签名 + 成帧 + 真实 socket），
/// 抓帧靠的是 <see cref="NodeHarness.Start"/> 的<b>连接装饰器</b>钩子 —— 在 router 把字节写出去的那一刻
/// 截下来。测试**不会**自己重新签一遍，因为那样测的是「重新签名」，不是「重放」。
/// </para>
/// </summary>
public class ReplayProtectionTests
{
    private static async Task<ChatMessageEvent> ReceiveOneAsync(
        IAsyncEnumerable<ChatMessageEvent> stream, int timeoutMs = 20000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        await foreach (var evt in stream.WithCancellation(cts.Token))
            return evt;
        throw new TimeoutException($"等待消息超时 ({timeoutMs}ms)");
    }

    /// <summary>
    /// 断言「在被观察窗口内 handler 没有收到任何新事件」。
    /// 沿用本项目既有写法（见 <c>MessageSigningTests</c> / <c>LongTermPublicKeyTests</c>）。
    /// </summary>
    private static async Task AssertNoEventAsync(
        IAsyncEnumerable<ChatMessageEvent> stream, int windowMs, string because)
    {
        using var cts = new CancellationTokenSource(windowMs);
        var got = false;
        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in stream.WithCancellation(cts.Token))
            {
                got = true;
                break;
            }
        });
        got.ShouldBeFalse(because);
    }

    /// <summary>
    /// 核心场景：一条**已经成功送达**的签名信封，被攻击者原样录下、从<b>第二条 TCP 连接</b>重放。
    /// 接收端的 handler 不得被第二次触发，且拒绝这件事必须在告警日志里可查。
    /// </summary>
    [Fact]
    public async Task 已送达的信封被重放到第二条连接_接收端不再触发handler_且留下告警()
    {
        var wireTap = new WireTap();
        var bobLogs = new CollectingLoggerFactory();

        await using var alice = NodeHarness.Start("alice", connectionDecorator: wireTap.Decorate);
        await using var bob = NodeHarness.Start("bob", loggerFactory: bobLogs);
        alice.Discover(bob);
        bob.Discover(alice);

        // Step 1：真实端到端投递。Alice 的 router 负责签名与成帧，这里截获它写出的原始字节。
        const string text = "只应该被显示一次的消息";
        await alice.Chat.SendPrivateMessageAsync(bob.LocalNode.NodeId, text);

        var first = await ReceiveOneAsync(bob.IncomingMessages);
        first.Content.ShouldBe(text);

        var captured = wireTap.CapturedPrivateText();
        captured.Length.ShouldBeGreaterThan(0, "必须真的抓到上线的字节，否则下面测的不是重放");

        // 抓到的这帧必须本身是一条合法信封（否则「被拒」可能只是因为它压根不合法）
        var parsed = MessageRouter.DeserializeEnvelope(captured);
        MessageRouter.VerifyEnvelope(parsed, bob.Encryption, out var verifyReason)
            .ShouldBeTrue($"重放的前提是原包完全合法，验签失败就不是重放而是非法包：{verifyReason}");

        // Step 2：攻击者另开一条 TCP 连接（不复用 Bob 路由器自己的连接池），
        // 把录下来的字节**原样**再发一次。
        using var attacker = new TcpTransport(NullLogger<TcpTransport>.Instance, NullLoggerFactory.Instance);
        var replayConnection = await attacker.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, bob.Port));
        await replayConnection.SendAsync(captured);

        // Step 3：接收端不得被第二次触发
        await AssertNoEventAsync(
            bob.IncomingMessages, 1500,
            "同一条已送达的消息被重放时，接收端绝不能把它当成新消息再显示一次");

        // Step 4：重放被拦这件事必须在告警里可查，且明因指向「重复」
        var warnings = bobLogs.Entries.Where(e => e.Level >= LogLevel.Warning).ToList();
        warnings.ShouldNotBeEmpty("重放被拒绝必须留下可查的告警，否则线上无从排查");
        warnings.ShouldContain(e => e.Message.Contains("重复"));
    }

    /// <summary>
    /// 同一攻击者、同一连接上连续重放多条：全部被拒，一条都不能漏。
    /// （只挡第一条的「防重放」等于没挡。）
    /// </summary>
    [Fact]
    public async Task 同一连接连续重放多次_每一条都被拦下()
    {
        var wireTap = new WireTap();
        var bobLogs = new CollectingLoggerFactory();

        await using var alice = NodeHarness.Start("alice", connectionDecorator: wireTap.Decorate);
        await using var bob = NodeHarness.Start("bob", loggerFactory: bobLogs);
        alice.Discover(bob);
        bob.Discover(alice);

        await alice.Chat.SendPrivateMessageAsync(bob.LocalNode.NodeId, "只放行一次");
        (await ReceiveOneAsync(bob.IncomingMessages)).Content.ShouldBe("只放行一次");

        var captured = wireTap.CapturedPrivateText();

        using var attacker = new TcpTransport(NullLogger<TcpTransport>.Instance, NullLoggerFactory.Instance);
        var connection = await attacker.ConnectAsync(new IPEndPoint(IPAddress.Loopback, bob.Port));
        for (var i = 0; i < 5; i++)
            await connection.SendAsync(captured);

        await AssertNoEventAsync(
            bob.IncomingMessages, 1500,
            "连续 5 次重放，一条都不应被当成新消息");

        bobLogs.Entries.Count(e => e.Level >= LogLevel.Warning && e.Message.Contains("重复"))
            .ShouldBe(5, "5 次重放应有 5 条「重复」告警");
    }

    /// <summary>
    /// 反向对照：防护<b>不能</b>误杀正常流量。
    /// <para>
    /// 如果把去重做成「按对端记住最近 N 条」以外的东西（例如按 payload 内容去重、
    /// 或时间窗写成开区间），这条会红。它与「已送达的信封被重放」那条构成一对：
    /// 一条证明<b>拦得住</b>，一条证明<b>不误杀</b>。
    /// </para>
    /// </summary>
    [Fact]
    public async Task 同一对端连续发送多条不同消息_必须条条送达不被误杀()
    {
        var bobLogs = new CollectingLoggerFactory();

        await using var alice = NodeHarness.Start("alice");
        await using var bob = NodeHarness.Start("bob", loggerFactory: bobLogs);
        alice.Discover(bob);
        bob.Discover(alice);

        const int count = 12;
        var expected = Enumerable.Range(0, count).Select(i => $"第 {i} 条").ToArray();

        foreach (var content in expected)
            await alice.Chat.SendPrivateMessageAsync(bob.LocalNode.NodeId, content);

        var received = new List<string>();
        using var cts = new CancellationTokenSource(20000);
        try
        {
            await foreach (var evt in bob.IncomingMessages.WithCancellation(cts.Token))
            {
                received.Add(evt.Content);
                if (received.Count == count) break;
            }
        }
        catch (OperationCanceledException) { }

        received.Count.ShouldBe(count, "正常消息一条都不该被重放防护误杀：实际只收到 " + received.Count + " 条");
        received.ShouldBe(expected);
        bobLogs.Entries.ShouldNotContain(e =>
            e.Level >= LogLevel.Warning && e.Message.Contains("重复"),
            "正常流量不应触发「重复」告警");
    }

    /// <summary>
    /// 群消息扇出是 per-peer 分桶的<b>真实理由</b>：
    /// <c>GroupChatService.SendGroupMessageAsync</c> 把**同一个 Message 对象（同一个 MessageId）**
    /// 发给多个不同成员。因此任何成员收到的群消息都不能因为「别的成员也收到过」而被去重掉。
    /// </summary>
    [Fact]
    public async Task 同一条群消息扇出给多个成员_每个成员都能收到_不因同MessageId互相误杀()
    {
        var bobLogs = new CollectingLoggerFactory();
        var carolLogs = new CollectingLoggerFactory();

        await using var alice = NodeHarness.Start("alice");
        await using var bob = NodeHarness.Start("bob", loggerFactory: bobLogs);
        await using var carol = NodeHarness.Start("carol", loggerFactory: carolLogs);

        alice.Discover(bob);
        bob.Discover(alice);
        alice.Discover(carol);
        carol.Discover(alice);

        // Bob 与 Carol 互相"发现"，这样她们收到群消息后可以回话。
        bob.Discover(carol);
        carol.Discover(bob);

        // 先建群并把两人拉进来（走真实 TCP 邀请 + 真实 ECDH 群密钥包装）
        var group = await alice.Group.CreateGroupAsync("扇出群", [bob.LocalNode.NodeId, carol.LocalNode.NodeId]);
        (await Wait.UntilAsync(() => bob.KeyStore.GetGroupKey(group.GroupId) is not null))
            .ShouldBeTrue("Bob 应已收到群邀请并解出群密钥");
        (await Wait.UntilAsync(() => carol.KeyStore.GetGroupKey(group.GroupId) is not null))
            .ShouldBeTrue("Carol 应已收到群邀请并解出群密钥");

        // Alice 发一条群消息：同一个 MessageId 会同时发给 Bob 和 Carol
        const string text = "一条群消息，两个收件人";
        await alice.Group.SendGroupMessageAsync(group.GroupId, text);

        var bobGot = await ReceiveOneAsync(bob.IncomingMessages);
        var carolGot = await ReceiveOneAsync(carol.IncomingMessages);

        bobGot.Content.ShouldBe(text);
        carolGot.Content.ShouldBe(text);
        bobGot.ConversationId.ShouldBe(group.GroupId);
        carolGot.ConversationId.ShouldBe(group.GroupId);

        bobLogs.Entries.ShouldNotContain(e => e.Level >= LogLevel.Warning && e.Message.Contains("重复"));
        carolLogs.Entries.ShouldNotContain(e => e.Level >= LogLevel.Warning && e.Message.Contains("重复"));
    }

    #region 测试脚手架

    /// <summary>
    /// 出站抓帧器 — 套在真实 <see cref="ITcpConnection"/> 外层，记录 router 写出的每一帧
    /// <b>未成帧的载荷</b>（成帧由 <c>TcpConnection</c> 负责，重放时再由它重新加一次，
    /// 与真实线上格式一致）。
    /// </summary>
    private sealed class WireTap
    {
        private readonly List<byte[]> _frames = [];

        public IReadOnlyList<byte[]> Frames
        {
            get { lock (_frames) return _frames.ToList(); }
        }

        public ITcpConnection Decorate(ITcpConnection inner) => new TappingConnection(inner, _frames);

        /// <summary>
        /// 取出那条<b>私聊正文</b>信封的原始字节。
        /// <para>
        /// 不能直接取「唯一一帧」：首次给陌生对端发消息会先走一次 ECDH 密钥交换，
        /// 所以线路上先出现一帧 <c>KeyExchange</c>、再出现一帧 <c>PrivateText</c>。
        /// 按消息类型挑出正文帧既精确，也顺带证明密钥交换确实发生了。
        /// </para>
        /// </summary>
        public byte[] CapturedPrivateText()
        {
            var privateTexts = Frames
                .Where(f => MessageRouter.DeserializeEnvelope(f).MessageType == MessageType.PrivateText)
                .ToList();

            privateTexts.Count.ShouldBe(1,
                "预期恰好抓到一帧私聊正文信封，实际抓到 " + privateTexts.Count + " 帧");
            privateTexts[0].Length.ShouldBeGreaterThan(0, "必须真的抓到上线的字节，否则测的不是重放");
            return privateTexts[0];
        }

        private sealed class TappingConnection : ITcpConnection
        {
            private readonly ITcpConnection _inner;
            private readonly List<byte[]> _sink;

            public TappingConnection(ITcpConnection inner, List<byte[]> sink)
            {
                _inner = inner;
                _sink = sink;
            }

            public Guid ConnectionId => _inner.ConnectionId;
            public IPEndPoint RemoteEndPoint => _inner.RemoteEndPoint;
            public bool IsConnected => _inner.IsConnected;

            public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
            {
                lock (_sink) _sink.Add(data.ToArray());
                return _inner.SendAsync(data, ct);
            }

            public Task<ReadOnlyMemory<byte>> ReceiveMessageAsync(CancellationToken ct = default)
                => _inner.ReceiveMessageAsync(ct);

            public ValueTask DisposeAsync() => _inner.DisposeAsync();
        }
    }

    /// <summary>
    /// 收集全部日志的 ILoggerFactory —— 注入给 NodeHarness 后，
    /// <c>MessageRouter</c> 与 <c>MessageReplayGuard</c> 都会写进这里。
    /// <para>
    /// 挂在 guard 上而不是 router 上：router 对重放拒绝只记 <c>LogTrace</c>
    /// （实现刻意避免同一事件打两行），真正的 <c>LogWarning</c> 由 guard 记。
    /// </para>
    /// </summary>
    private sealed class CollectingLoggerFactory : ILoggerFactory
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get { lock (_entries) return _entries.ToList(); }
        }

        public ILogger CreateLogger(string categoryName) => new CollectingLogger(_entries);

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }

        private sealed class CollectingLogger : ILogger
        {
            private readonly List<(LogLevel, string)> _sink;

            public CollectingLogger(List<(LogLevel, string)> sink) => _sink = sink;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (_sink) _sink.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    #endregion
}
