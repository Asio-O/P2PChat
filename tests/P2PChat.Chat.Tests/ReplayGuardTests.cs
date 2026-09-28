using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using P2PChat.Chat.Routing;
using P2PChat.Core.Abstractions;
using P2PChat.Core.Enums;
using P2PChat.Core.Models;
using Shouldly;

namespace P2PChat.Chat.Tests;

/// <summary>
/// 入站重放防护 <c>MessageReplayGuard</c> 的单元测试。
/// <para>
/// <b>判据（沿用上一轮 plainmode-tester 立的那条）</b>：只有「它声称守护的语义被破坏时才会变红」
/// 的用例才值得留。下面每条都至少做了一件事：证明它<b>拦得住</b>（拒绝一条不该被接受的），
/// 而不是只证明它<b>不误杀</b>（接受一条本该接受的）。
/// </para>
/// <para>
/// <b>为什么时间窗测试必须注入固定时钟</b>：<c>Unix 毫秒是截断的</c>。如果一边用捕获的
/// <c>now</c>、一边让 guard 读 <c>DateTimeOffset.UtcNow</c>，两者会差零点几毫秒，
/// 「恰好落在边界上」的用例会**假红**。因此本文件统一用<b>整毫秒</b>的固定时钟，
/// 且 <c>maxAge</c>/<c>maxFutureSkew</c> 也取整毫秒的 <see cref="TimeSpan"/>，
/// 这样 <c>(now - maxAge).ToUnixTimeMilliseconds()</c> 能无损往返。
/// </para>
/// </summary>
public class ReplayGuardTests
{
    /// <summary>整毫秒的固定时钟基准（避开 Unix 毫秒截断陷阱）。</summary>
    private static readonly DateTimeOffset FixedNow = DateTimeOffset.FromUnixTimeMilliseconds(1_780_000_000_000L);

    /// <summary>整毫秒的窗口参数，保证边界可精确构造。</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromMilliseconds(3_600_000);      // 1 小时
    private static readonly TimeSpan MaxSkew = TimeSpan.FromMilliseconds(300_000);       // 5 分钟

    private static MessageReplayGuard NewGuard(
        TimeSpan? maxAge = null,
        TimeSpan? maxFutureSkew = null,
        int historySize = MessageReplayGuard.DefaultHistorySize,
        ILogger<MessageReplayGuard>? logger = null)
        => new MessageReplayGuard(
            logger ?? NullLogger<MessageReplayGuard>.Instance,
            maxAge: maxAge ?? MaxAge,
            maxFutureSkew: maxFutureSkew ?? MaxSkew,
            historySize: historySize,
            now: () => FixedNow);

    /// <summary>造一条处于「已验签」状态（防护只关心这层之后）的信封。</summary>
    private static MessageEnvelope Envelope(
        long timestampMs,
        Guid messageId,
        NodeId sender) => new()
        {
            Version = 1,
            MessageType = MessageType.PrivateText,
            SequenceNumber = 1,
            SenderId = sender.ToByteArray(),
            MessageId = messageId,
            Timestamp = timestampMs,
            SenderPublicKey = new byte[91],
            Payload = [1, 2, 3]
        };

    private static long Ms(DateTimeOffset t) => t.ToUnixTimeMilliseconds();

    #region 1. 窗口内的新鲜消息

    [Fact]
    public void 窗口内的新鲜消息被接受_且接受时reason为null()
    {
        var guard = NewGuard();
        var envelope = Envelope(Ms(FixedNow), Guid.NewGuid(), NodeId.CreateRandom());

        guard.TryAccept(envelope, out var reason).ShouldBeTrue();
        reason.ShouldBeNull("接受时不得留下拒绝原因");
    }

    [Fact]
    public void 窗口内偏旧偏新的消息都应被接受_两端留有余量()
    {
        var guard = NewGuard();
        var sender = NodeId.CreateRandom();

        // 距下界还差 1 分钟
        guard.TryAccept(Envelope(Ms(FixedNow - MaxAge + TimeSpan.FromMinutes(1)), Guid.NewGuid(), sender),
            out var oldReason).ShouldBeTrue();
        oldReason.ShouldBeNull();

        // 距上界还差 1 分钟
        guard.TryAccept(Envelope(Ms(FixedNow + MaxSkew - TimeSpan.FromMinutes(1)), Guid.NewGuid(), sender),
            out var futureReason).ShouldBeTrue();
        futureReason.ShouldBeNull();
    }

    #endregion

    #region 2. 超过 maxAge 的旧消息

    [Fact]
    public void 超过maxAge的旧消息被拒绝_reason指明过旧()
    {
        var guard = NewGuard();
        var tooOld = Envelope(Ms(FixedNow - MaxAge - TimeSpan.FromMilliseconds(1)), Guid.NewGuid(), NodeId.CreateRandom());

        guard.TryAccept(tooOld, out var reason).ShouldBeFalse("陈旧重放正是时间窗要拦的第一类攻击");
        reason.ShouldNotBeNull();
        reason!.ShouldContain("过旧");
    }

    [Fact]
    public void 远超窗口的陈旧信封被拒绝_不是只有擦边才拦()
    {
        var guard = NewGuard();

        // 攻击者把时间戳改回一周前（仍需持有合法签名才过得了验签，这里只测防护层）
        var weekOld = Envelope(Ms(FixedNow - TimeSpan.FromDays(7)), Guid.NewGuid(), NodeId.CreateRandom());

        guard.TryAccept(weekOld, out var reason).ShouldBeFalse();
        reason!.ShouldContain("过旧");
    }

    #endregion

    #region 3. 超过 maxFutureSkew 的未来消息

    [Fact]
    public void 超过maxFutureSkew的未来消息被拒绝_reason指明来自未来()
    {
        var guard = NewGuard();
        var tooNew = Envelope(Ms(FixedNow + MaxSkew + TimeSpan.FromMilliseconds(1)), Guid.NewGuid(), NodeId.CreateRandom());

        guard.TryAccept(tooNew, out var reason).ShouldBeFalse("超前时间戳是另一种重放/伪造尝试");
        reason.ShouldNotBeNull();
        reason!.ShouldContain("来自未来");
    }

    [Fact]
    public void 远超窗口的未来信封被拒绝()
    {
        var guard = NewGuard();
        var farFuture = Envelope(Ms(FixedNow + TimeSpan.FromDays(1)), Guid.NewGuid(), NodeId.CreateRandom());

        guard.TryAccept(farFuture, out var reason).ShouldBeFalse();
        reason!.ShouldContain("来自未来");
    }

    #endregion

    #region 4. 同一 MessageId 重复投递（重放的核心定义）

    [Fact]
    public void 同一MessageId重复投递_第二次被拒绝_这正是重放的定义()
    {
        var guard = NewGuard();
        var messageId = Guid.NewGuid();
        var sender = NodeId.CreateRandom();
        var envelope = Envelope(Ms(FixedNow), messageId, sender);

        // 第一次：正常投递
        guard.TryAccept(envelope, out var firstReason).ShouldBeTrue();
        firstReason.ShouldBeNull();

        // 第二次：**字节完全相同**的重放 —— 时间戳仍新鲜，时间窗拦不住，只能靠 MessageId 去重
        guard.TryAccept(envelope, out var replayReason).ShouldBeFalse(
            "新鲜的旧包仍在时间窗内，拦住它的是 MessageId 去重层");
        replayReason.ShouldNotBeNull();
        replayReason!.ShouldContain("重复");
        replayReason.ShouldContain(messageId.ToString());
    }

    [Fact]
    public void 同一MessageId在一小时内狂放多次_只第一次被接受()
    {
        var guard = NewGuard();
        var messageId = Guid.NewGuid();
        var envelope = Envelope(Ms(FixedNow), messageId, NodeId.CreateRandom());

        var accepted = 0;
        for (var i = 0; i < 50; i++)
        {
            if (guard.TryAccept(envelope, out _)) accepted++;
        }

        accepted.ShouldBe(1, "同一条消息无论重放多少次，有且只有第一次能通过");
    }

    #endregion

    #region 5. 不同对端的相同 MessageId 不互相误杀

    [Fact]
    public void 不同对端的相同MessageId不互相误杀_去重按对端分桶()
    {
        var guard = NewGuard();
        // 群消息扇出时，同一个 Message 对象（同一个 MessageId）会发给多个不同对端。
        // 若去重做成全局集合，第二条群消息会被全网丢弃。
        var sharedMessageId = Guid.NewGuid();
        var alice = NodeId.CreateRandom();
        var bob = NodeId.CreateRandom();
        var carol = NodeId.CreateRandom();

        guard.TryAccept(Envelope(Ms(FixedNow), sharedMessageId, alice), out var r1).ShouldBeTrue();
        r1.ShouldBeNull();
        guard.TryAccept(Envelope(Ms(FixedNow), sharedMessageId, bob), out var r2).ShouldBeTrue(
            "同一个 MessageId 从另一个对端到达，不是重放");
        r2.ShouldBeNull();
        guard.TryAccept(Envelope(Ms(FixedNow), sharedMessageId, carol), out var r3).ShouldBeTrue();
        r3.ShouldBeNull();
    }

    [Fact]
    public void 相同MessageId同一对端重复到达仍被拒_分桶不是放宽的借口()
    {
        var guard = NewGuard();
        var messageId = Guid.NewGuid();
        var alice = NodeId.CreateRandom();
        var envelope = Envelope(Ms(FixedNow), messageId, alice);

        guard.TryAccept(envelope, out _).ShouldBeTrue();
        guard.TryAccept(envelope, out var reason).ShouldBeFalse("分桶只隔离不同对端，同一桶内仍必须去重");
        reason!.ShouldContain("重复");
    }

    #endregion

    #region 6. 边界是闭区间（最容易写成半开区间而永远测不出来）

    [Fact]
    public void 恰好等于now减maxAge的下界被接受_闭区间含下界端点()
    {
        var guard = NewGuard();
        var exactlyLowerBound = Envelope(Ms(FixedNow - MaxAge), Guid.NewGuid(), NodeId.CreateRandom());

        guard.TryAccept(exactlyLowerBound, out var reason).ShouldBeTrue(
            "窗口语义是「允许的年龄范围」，恰好等于上限的年龄是合法情况，不该被误杀");
        reason.ShouldBeNull();
    }

    [Fact]
    public void 恰好等于now加maxFutureSkew的上界被接受_闭区间含上界端点()
    {
        var guard = NewGuard();
        var exactlyUpperBound = Envelope(Ms(FixedNow + MaxSkew), Guid.NewGuid(), NodeId.CreateRandom());

        guard.TryAccept(exactlyUpperBound, out var reason).ShouldBeTrue(
            "恰好等于允许超前上限的消息应被接受");
        reason.ShouldBeNull();
    }

    [Fact]
    public void 下界再早一毫秒即被拒_证明下界确实是闭的而非开区间()
    {
        var guard = NewGuard();
        var oneMsBefore = Envelope(Ms(FixedNow - MaxAge) - 1, Guid.NewGuid(), NodeId.CreateRandom());

        guard.TryAccept(oneMsBefore, out var reason).ShouldBeFalse("越界 1ms 就必须拒绝");
        reason!.ShouldContain("过旧");
    }

    [Fact]
    public void 上界再晚一毫秒即被拒_证明上界确实是闭的而非开区间()
    {
        var guard = NewGuard();
        var oneMsAfter = Envelope(Ms(FixedNow + MaxSkew) + 1, Guid.NewGuid(), NodeId.CreateRandom());

        guard.TryAccept(oneMsAfter, out var reason).ShouldBeFalse("越界 1ms 就必须拒绝");
        reason!.ShouldContain("来自未来");
    }

    [Fact]
    public void 两条边界在同一守卫上同时成立_窗口不会因先跑过一条而漂移()
    {
        var guard = NewGuard();
        var sender = NodeId.CreateRandom();

        // 先接受一条下界消息，再接受一条上界消息；两者都必须通过
        guard.TryAccept(Envelope(Ms(FixedNow - MaxAge), Guid.NewGuid(), sender), out var lower).ShouldBeTrue();
        lower.ShouldBeNull();
        guard.TryAccept(Envelope(Ms(FixedNow + MaxSkew), Guid.NewGuid(), sender), out var upper).ShouldBeTrue();
        upper.ShouldBeNull();
    }

    #endregion

    #region 7. maxAge <= 0 → 时间窗关闭（逃生阀）

    [Fact]
    public void maxAge设为0_时间窗关闭_过旧消息被接受()
    {
        var guard = NewGuard(maxAge: TimeSpan.Zero);
        var ancient = Envelope(Ms(FixedNow - TimeSpan.FromDays(30)), Guid.NewGuid(), NodeId.CreateRandom());

        guard.TryAccept(ancient, out var reason).ShouldBeTrue(
            "两端时钟严重偏移时，逃生阀必须让陈旧消息仍能通过，否则全网互拒");
        reason.ShouldBeNull();
    }

    [Fact]
    public void maxAge为负数同样关闭时间窗_且MessageId去重是唯一剩下的防线()
    {
        var guard = NewGuard(maxAge: TimeSpan.FromSeconds(-1));
        guard.IsTimeWindowEnabled.ShouldBeFalse();

        // 逃生阀关闭的是**整个**时间窗（两侧都关），不是一个单向的「放宽过旧」
        guard.TryAccept(Envelope(Ms(FixedNow - TimeSpan.FromDays(30)), Guid.NewGuid(), NodeId.CreateRandom()),
            out var oldReason).ShouldBeTrue();
        oldReason.ShouldBeNull();

        guard.TryAccept(Envelope(Ms(FixedNow + TimeSpan.FromDays(30)), Guid.NewGuid(), NodeId.CreateRandom()),
            out var futureReason).ShouldBeTrue("时间窗已整体关闭，超前时间戳也不再被时间层拦");
        futureReason.ShouldBeNull();

        // 只剩 MessageId 去重这一层 —— 它必须仍然生效，否则逃生阀等于拆掉整个防护
        var envelope = Envelope(Ms(FixedNow), Guid.NewGuid(), NodeId.CreateRandom());
        guard.TryAccept(envelope, out _).ShouldBeTrue();
        guard.TryAccept(envelope, out var replayReason).ShouldBeFalse();
        replayReason.ShouldNotBeNull();
        replayReason!.ShouldContain("重复");
    }

    [Fact]
    public void 时间窗关闭时MessageId去重仍然生效_逃生阀不是全面失守()
    {
        var guard = NewGuard(maxAge: TimeSpan.Zero);
        var envelope = Envelope(Ms(FixedNow - TimeSpan.FromDays(1)), Guid.NewGuid(), NodeId.CreateRandom());

        guard.TryAccept(envelope, out _).ShouldBeTrue();
        guard.TryAccept(envelope, out var reason).ShouldBeFalse(
            "关闭时间窗只应放行陈旧消息，不应放行重放");
        reason!.ShouldContain("重复");
    }

    [Fact]
    public void 时间窗启用时IsTimeWindowEnabled为真_状态可被自检读取()
    {
        NewGuard().IsTimeWindowEnabled.ShouldBeTrue();
        NewGuard(maxAge: TimeSpan.Zero).IsTimeWindowEnabled.ShouldBeFalse();
    }

    #endregion

    #region 8. 去重缓存有界

    [Fact]
    public void 灌入超过historySize条后最早者被淘汰_最新者仍被记住_上界恰好是historySize()
    {
        const int historySize = 8;
        var guard = NewGuard(historySize: historySize);
        var sender = NodeId.CreateRandom();

        // 灌 historySize + 1 条不同 MessageId
        var ids = new List<Guid>();
        for (var i = 0; i < historySize + 1; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            guard.TryAccept(Envelope(Ms(FixedNow), id, sender), out _).ShouldBeTrue();
        }

        // 注意：每次「被接受」都会写进环形缓冲并挤掉最旧的一格。
        // 因此下面几次重复投递的**顺序**很重要 —— 先做「仍被记住」的断言，
        // 最后才做「已被淘汰、重新接受」的断言，否则前面的接受会把断言对象挤出去。

        // 最新的那条仍在记忆里 → 仍被拒
        guard.TryAccept(Envelope(Ms(FixedNow), ids[^1], sender), out var newestReason).ShouldBeFalse();
        newestReason!.ShouldContain("重复");

        // 倒数第二条（即环形缓冲里真正的最早一条）仍被记住 → 证明只淘汰了一条，上界恰好是 historySize
        guard.TryAccept(Envelope(Ms(FixedNow), ids[1], sender), out var secondOldest).ShouldBeFalse();
        secondOldest!.ShouldContain("重复");

        // 最早的那条已被环形缓冲覆盖 → 再次投递会被接受（这正是「有界」的代价与证据）
        guard.TryAccept(Envelope(Ms(FixedNow), ids[0], sender), out var oldestReason).ShouldBeTrue(
            "缓存有界意味着最早的 MessageId 会被淘汰；用它证明上界恰好是 historySize");
        oldestReason.ShouldBeNull();
    }

    [Fact]
    public void 灌入远超historySize条后缓存不会无限增长_近期条目仍被正确去重()
    {
        const int historySize = 16;
        var guard = NewGuard(historySize: historySize);
        var sender = NodeId.CreateRandom();

        var first = Guid.NewGuid();
        guard.TryAccept(Envelope(Ms(FixedNow), first, sender), out _).ShouldBeTrue();

        // 灌 5000 条，远超历史上限
        var last = Guid.Empty;
        for (var i = 0; i < 5000; i++)
        {
            last = Guid.NewGuid();
            guard.TryAccept(Envelope(Ms(FixedNow), last, sender), out _).ShouldBeTrue();
        }

        // 最早的那条早已被淘汰
        guard.TryAccept(Envelope(Ms(FixedNow), first, sender), out _).ShouldBeTrue("最早条目应已被环形淘汰");

        // 但最新的一条仍被记得 —— 若结构无界/损坏，这里会开始漏判
        guard.TryAccept(Envelope(Ms(FixedNow), last, sender), out var reason).ShouldBeFalse(
            "近期 MessageId 必须仍被去重，否则洪泛就能绕过");
        reason!.ShouldContain("重复");
    }

    [Fact]
    public void 超过对端数上限时最久未使用的对端桶被淘汰_攻击面不会随对端数无限增长()
    {
        var guard = NewGuard();

        // 第 1 个对端：先建立桶
        var firstPeer = NodeId.CreateRandom();
        var firstMessage = Envelope(Ms(FixedNow), Guid.NewGuid(), firstPeer);
        guard.TryAccept(firstMessage, out _).ShouldBeTrue();

        // 再灌远超 MaxTrackedPeers(512) 个不同对端
        for (var i = 0; i < 2000; i++)
            guard.TryAccept(Envelope(Ms(FixedNow), Guid.NewGuid(), NodeId.CreateRandom()), out _).ShouldBeTrue();

        // 第 1 个对端的桶早已被 LRU 淘汰 → 它那条消息不再受去重保护（已知的取舍，有告警记录）
        guard.TryAccept(firstMessage, out var reason).ShouldBeTrue(
            "对端桶数必须有界；最早的桶应已被淘汰");
        reason.ShouldBeNull();
    }

    #endregion

    #region 9. 并发原子性

    [Fact]
    public void 并发投递同一信封_有且只有一条被接受()
    {
        // 契约要求：「对同一个信封的判定必须是原子的」。
        // 若实现把「查重」和「记下」拆成两步（先 Contains 再 Add），串行测试全绿，
        // 但并发重放可以同时穿过 —— 这正是必须测它的理由。
        //
        // 用**裸 Thread** 而不是 Task.Run：Barrier 会阻塞参与者，若全部丢进线程池，
        // 池的「饥饿注入」要按秒级扩张，测试会慢到不可接受且时机不确定。
        var guard = NewGuard();
        var envelope = Envelope(Ms(FixedNow), Guid.NewGuid(), NodeId.CreateRandom());

        const int concurrency = 64;
        using var gate = new Barrier(concurrency);
        var accepted = 0;

        var threads = new Thread[concurrency];
        for (var i = 0; i < concurrency; i++)
        {
            threads[i] = new Thread(() =>
            {
                gate.SignalAndWait();
                if (guard.TryAccept(envelope, out _)) Interlocked.Increment(ref accepted);
            })
            { IsBackground = true };
            threads[i].Start();
        }
        foreach (var t in threads) t.Join();

        accepted.ShouldBe(1, "并发重放下必须恰好一条通过，否则防护可被绕过");
    }

    [Fact]
    public void 并发投递大量不同信封_每条恰好被接受一次()
    {
        var guard = NewGuard(historySize: 64);
        const int peers = 8;
        const int perPeer = 24;
        const int copies = 4;

        // 每条不同信封被并发投递 copies 次
        var work = new List<MessageEnvelope>();
        for (var p = 0; p < peers; p++)
        {
            var sender = NodeId.CreateRandom();
            for (var m = 0; m < perPeer; m++)
            {
                var envelope = Envelope(Ms(FixedNow), Guid.NewGuid(), sender);
                for (var c = 0; c < copies; c++) work.Add(envelope);
            }
        }

        var distinctCount = peers * perPeer;
        var acceptedPerId = new Dictionary<Guid, int>();
        var cursor = -1;

        const int workers = 16;
        var threads = new Thread[workers];
        for (var w = 0; w < workers; w++)
        {
            threads[w] = new Thread(() =>
            {
                while (true)
                {
                    var index = Interlocked.Increment(ref cursor);
                    if (index >= work.Count) return;
                    if (!guard.TryAccept(work[index], out _)) continue;
                    lock (acceptedPerId)
                    {
                        acceptedPerId[work[index].MessageId] =
                            acceptedPerId.GetValueOrDefault(work[index].MessageId) + 1;
                    }
                }
            })
            { IsBackground = true };
            threads[w].Start();
        }
        foreach (var t in threads) t.Join();

        acceptedPerId.Count.ShouldBe(distinctCount, "每条不同信封都应至少被接受一次");
        acceptedPerId.Values.ShouldAllBe(v => v == 1, "每条信封恰好被接受一次，不得因竞态漏判或多判");
    }

    #endregion

    #region 构造参数与异常信封

    [Fact]
    public void MessageId为空的信封被拒绝_不能放行一条去重层对它失效的消息()
    {
        var guard = NewGuard();
        var noKey = Envelope(Ms(FixedNow), Guid.Empty, NodeId.CreateRandom());

        guard.TryAccept(noKey, out var reason).ShouldBeFalse(
            "没有重放键的信封等于去重层对它完全失效，必须拒绝而不是放行");
        reason!.ShouldContain("MessageId 为空");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void historySize非正时构造即失败_不能配出一个失去去重能力的守卫(int historySize)
    {
        var ex = Should.Throw<ArgumentOutOfRangeException>(
            () => NewGuard(historySize: historySize));
        ex.ParamName.ShouldBe("historySize");
    }

    [Fact]
    public void maxFutureSkew为负时构造即失败_配置写错必须在启动时炸掉()
    {
        var ex = Should.Throw<ArgumentOutOfRangeException>(
            () => new MessageReplayGuard(
                NullLogger<MessageReplayGuard>.Instance,
                maxAge: MaxAge, maxFutureSkew: TimeSpan.FromSeconds(-1), now: () => FixedNow));
        ex.ParamName.ShouldBe("maxFutureSkew");
    }

    [Fact]
    public void 拒绝时记LogWarning并带明因_运维必须能从日志看出发生了什么()
    {
        var logs = new CollectingLogger<MessageReplayGuard>();
        var guard = NewGuard(logger: logs);

        guard.TryAccept(Envelope(Ms(FixedNow), Guid.NewGuid(), NodeId.CreateRandom()), out _).ShouldBeTrue();
        logs.Entries.ShouldBeEmpty("接受时不应产生告警");

        var replay = Envelope(Ms(FixedNow), Guid.NewGuid(), NodeId.CreateRandom());
        guard.TryAccept(replay, out _).ShouldBeTrue();
        guard.TryAccept(replay, out _).ShouldBeFalse();

        logs.Entries.ShouldContain(e =>
            e.Level >= LogLevel.Warning && e.Message.Contains("重复"));
    }

    [Fact]
    public void 拒绝过旧消息时LogWarning明因含过旧_而不是一条无信息的丢弃()
    {
        var logs = new CollectingLogger<MessageReplayGuard>();
        var guard = NewGuard(logger: logs);

        guard.TryAccept(
            Envelope(Ms(FixedNow - MaxAge - TimeSpan.FromMinutes(5)), Guid.NewGuid(), NodeId.CreateRandom()),
            out _).ShouldBeFalse();

        logs.Entries.ShouldContain(e =>
            e.Level >= LogLevel.Warning && e.Message.Contains("过旧"));
    }

    #endregion

    /// <summary>捕获日志的 ILogger，用于断言「拒绝这件事在日志里可查」。</summary>
    private sealed class CollectingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
