using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Shouldly;

namespace P2PChat.Integration.Tests;

/// <summary>
/// 回归守卫 —— HANDOFF §4.1「plain 模式无 stdin 输入通道」的锁死测试集。
/// <para>
/// 历史缺陷：<c>P2PChatTui.ReadKeyOrNull()</c> 在 <c>_plainMode</c> 下恒
/// <c>return null</c>，而主循环「只有拿到按键才分派命令」——于是
/// <c>P2PCHAT_PLAIN=1</c> 模式下 <b>一切 stdin 输入被静默丢弃</b>。
/// 这不是脚本问题：控制台初始化失败（catch）同样降级到 <c>_plainMode</c>，
/// 所以任何 stdout 被重定向 / CI / 管道的用户都落入无输入模式。
/// </para>
///
/// <para><b>这些用例的性质：源码「结构契约」守卫，不是端到端行为测试。</b>
/// 它们读取 <c>src/P2PChat.UI/Views/P2PChatTui.cs</c>，去注释 + 空白归一后按<b>方法体</b>
/// 断言语义要素，因此<b>并没有真的向 stdin 写任何东西</b>。真正的行为验证在 e2e
/// 脚本 A30/A31（明文往返）。请勿把本文件误读为「已跑过真实 stdin」。</para>
///
/// <para>
/// 为什么不能改成进程内行为测试：<c>SubmitLine</c> / <c>ReadPlainInputAsync</c> /
/// <c>EnsurePlainInputStarted</c> / <c>_plainInput</c> 全是 <c>private</c>，且
/// <c>tests/P2PChat.Integration.Tests</c> <b>没有</b> 引用 <c>P2PChat.UI</c>。
/// 要在进程内调用它们，必须先给测试工程加 ProjectReference，再把私有成员改成
/// internal + <c>InternalsVisibleTo</c>，或者用反射 —— 三者分别属于 HANDOFF §5 步骤 2
/// 明确排除的「为可测性改产品代码」与「不得引入反射（动态代码）」。本文件因此在零产品
/// 改动的前提下，把修复的结构钉死。
/// </para>
///
/// <para><b>断言强度约定（重要）：</b>一条结构契约用例只有在「它守护的语义被破坏时」
/// 才应该变红。凡是换个写法 / 调排版 / 等价重构就会红的断言，都是负资产 —— 它只会诱导
/// 后来的人「为了让测试变绿」而回退正确的修复。故本文件一律<b>不</b>锁排版级连续子串：
/// 断言一律拆成语义要素（存在性 + 相对顺序），用正则容忍空白、括号换行、参数名与
/// <c>.ConfigureAwait</c> 之类的等价改写。</para>
/// </summary>
[Trait("Category", "KnownDefect")]
public class PlainModeInputTests
{
    private static readonly Lazy<string> RawSource = new(() => File.ReadAllText(LocateTuiSourcePath()));

    // ------------------------------------------------------- §4.1 修复：输入通道本身

    /// <summary>
    /// 回归守卫 P1 — 启动阶段必须经幂等门控拉起后台 stdin 读取任务。
    /// <para>
    /// 旧实现里 <c>RunAsync</c> 只按「前台轮询 + 按键」工作，plain 模式启动后根本没有
    /// 第二个数据源。守护的语义是「启动阶段就会起读取任务」，<b>不是</b>具体怎么写 ——
    /// 所以只断言启动路径经过 <c>EnsurePlainInputStarted</c> 这道门，而<b>不</b>锁
    /// 「必须是 <c>EnsurePlainInputStarted(ct);</c> 这一行」。
    /// </para>
    /// </summary>
    [Fact]
    public void plain模式_启动阶段必须经幂等门控拉起后台stdin读取()
    {
        var run = Normalize(ExtractMethod(StripComments(RawSource.Value), "public async Task RunAsync"));

        Match(run, @"EnsurePlainInputStarted\s*\(").ShouldBeGreaterThanOrEqualTo(0,
            "RunAsync 必须在进入主循环前启动 plain stdin 读取 —— 漏掉它 plain 模式就永远没有第二个数据源");

        run.ShouldNotMatch(@"ReadPlainInputAsync\s*\(",
            "RunAsync 不得直接启动 ReadPlainInputAsync，必须统一走 EnsurePlainInputStarted 的幂等门控");
    }

    /// <summary>
    /// 回归守卫 P2 — 主循环必须把通道里读到的整行 <b>提交</b>出去。
    /// <para>
    /// 这是本次修复的<b>核心一行</b>：没有它，<c>ReadPlainInputAsync</c> 把行投进通道后
    /// 无人取用，输入照样被丢弃。按 Lead 裁决，这里只断言语义要素（读了通道 / 提交了行 /
    /// 提交发生在读取之后），<b>不</b>锁任何跨行的连续排版。
    /// </para>
    /// </summary>
    [Fact]
    public void plain模式_主循环必须把通道读到的整行提交出去()
    {
        var run = MainLoop(StripComments(RawSource.Value));

        var readAt = Match(run, @"_plainInput\.Reader\.TryRead");
        var submitAt = Match(run, @"SubmitLine\s*\(");

        readAt.ShouldBeGreaterThanOrEqualTo(0,
            "主循环必须从 _plainInput 读取整行 —— 没有 TryRead = 旧缺陷原样重现（stdin 被静默丢弃）");
        submitAt.ShouldBeGreaterThan(readAt, "读到行之后必须把它提交给分派入口 SubmitLine");
    }

    /// <summary>
    /// 回归守卫 P3 — 阻塞式 <c>Console.ReadLine()</c> 只能出现在后台任务里。
    /// <para>
    /// <c>Console.ReadLine()</c> 是阻塞调用：若被搬进主循环，50ms 轮询会退化成永久阻塞，
    /// 整个 TUI（含消息接收）停摆。守护的语义是「全文件唯一一处 <c>ReadLine</c>，且在
    /// <c>ReadPlainInputAsync</c> 里被派发到后台执行」——因此刻意<b>不</b>锁
    /// <c>await Task.Run(...).ConfigureAwait(false)</c> 这种可被等价改写的链式写法。
    /// </para>
    /// </summary>
    [Fact]
    public void plain模式_阻塞stdin读取必须只出现在后台任务里()
    {
        var stripped = StripComments(RawSource.Value);

        var readAll = Regex.Matches(stripped, "Console\\.ReadLine\\(\\)").Count;
        readAll.ShouldBe(1, "去除注释后，Console.ReadLine() 在整个 TUI 里必须且只能出现一次");

        var body = Normalize(ExtractMethod(stripped, "private async Task ReadPlainInputAsync"));
        body.ShouldMatch(@"Console\.ReadLine\s*\(",
            "ReadPlainInputAsync 必须真的读 stdin");
        body.ShouldMatch(@"Task\.Run\s*\(",
            "阻塞的 ReadLine 必须派发到后台执行，不能占用主循环");
    }

    /// <summary>
    /// 回归守卫 P4 — stdin 读到 EOF（<c>ReadLine</c> 返回 <c>null</c>）时必须结束通道。
    /// <para>
    /// 脚本注入完毕后关闭管道是常态。若 EOF 不 <c>TryComplete</c>，主循环的 <c>TryRead</c>
    /// 会永远返回 <c>false</c>，程序以 50ms 周期空转烧 CPU 且永不退出。断言用
    /// 「<c>finally</c> 块里调用 <c>Writer.TryComplete()</c>」而非「恰好吃这一行字」，
    /// 以容忍把单行 <c>{ }</c> 展开成多行等等价改写。
    /// </para>
    /// </summary>
    [Fact]
    public void plain模式_stdin读到EOF时必须结束通道避免主循环空转()
    {
        var body = Normalize(ExtractMethod(StripComments(RawSource.Value), "private async Task ReadPlainInputAsync"));

        body.ShouldMatch(@"line\s*(?:is\s+null|==\s*null)",
            "Console.ReadLine() 返回 null 表示 stdin 已 EOF，必须识别并跳出读取循环");
        body.ShouldMatch(@"finally\s*\{[^}]*_plainInput\.Writer\.TryComplete\s*\(\s*\)",
            "EOF 后必须 TryComplete 通道，否则主循环 TryRead 恒 false 并以 50ms 周期空转烧 CPU");
    }

    /// <summary>
    /// 回归守卫 P5 — 交互按键路径必须保持原样。
    /// <para>
    /// 修复只应<b>新增</b> plain 数据源，不得动 <c>!_plainMode</c> 的按键分支。
    /// 断言只到「语义要素」这一层：plain 模式下按键探测返回 null、探测是非阻塞的、
    /// <c>Enter</c> 走 <c>SubmitInput()</c>、主循环仍调 <c>HandleKey</c>——
    /// 而<b>不</b>去匹配 <c>if (...) return null;</c> 这类可被等价改写的排版。
    /// </para>
    /// </summary>
    [Fact]
    public void 交互按键路径必须保持原样_ReadKeyOrNull在plain模式仍返回null()
    {
        var stripped = StripComments(RawSource.Value);

        var readKey = Normalize(ExtractMethod(stripped, "private ConsoleKeyInfo? ReadKeyOrNull"));
        readKey.ShouldMatch(@"if\s*\(\s*_plainMode\s*\)\s*\{?\s*return\s+null",
            "plain 模式仍不得走 Console.ReadKey —— 修复只应新增行式数据源，不得改动按键判定");
        readKey.ShouldMatch(@"Console\.KeyAvailable",
            "交互模式的按键探测必须保持非阻塞（KeyAvailable 轮询），不得改成阻塞 ReadKey");
        readKey.ShouldMatch(@"Console\.ReadKey",
            "交互模式仍需通过 Console.ReadKey 读取按键");

        var handleKey = Normalize(ExtractMethod(stripped, "private void HandleKey"));
        handleKey.ShouldMatch(@"ConsoleKey\.Enter",
            "Enter 键的处理必须保留");
        handleKey.ShouldMatch(@"SubmitInput\s*\(\s*\)",
            "Enter 键仍必须走 SubmitInput()，交互路径未被本次修复改动");

        var run = MainLoop(stripped);
        run.ShouldMatch(@"ReadKeyOrNull\s*\(", "主循环仍需读取按键");
        run.ShouldMatch(@"HandleKey\s*\(", "主循环仍需分派按键");
    }

    /// <summary>
    /// 回归守卫 P6 — <c>EnsurePlainInputStarted</c> 必须存在、只在 plain 模式启动、且<b>幂等</b>。
    /// <para>
    /// <c>_plainMode</c> 有三条进入路径，其中 <c>ReadKeyOrNull</c> 的 <c>catch</c> 与
    /// <c>Render()</c> 的 <c>InvalidOperationException</c> 降级<b>可能运行中途</b>才把它翻成
    /// <c>true</c>。若只在 <c>RunAsync</c> 开头判断一次，stdin 读取任务永不启动 ——
    /// 症状与修复前完全相同。但主循环每 50ms 就会经过这条分支，所以门控必须靠
    /// <c>Interlocked</c> 原子「检查并置位」保证幂等，否则会反复启动一个阻塞在
    /// <c>Console.ReadLine()</c> 的任务。此处刻意不锁具体 API
    /// （<c>Exchange</c> 或 <c>CompareExchange</c> 均可）与具体写法。
    /// </para>
    /// </summary>
    [Fact]
    public void plain模式_EnsurePlainInputStarted必须只在plain模式启动且幂等()
    {
        var stripped = StripComments(RawSource.Value);

        Normalize(stripped).ShouldMatch(@"private\s+(?:volatile\s+)?int\s+_plainInputStarted\b",
            "必须有一个 _plainInputStarted 标志位来支撑幂等启动");

        var body = Normalize(ExtractMethod(stripped, "private void EnsurePlainInputStarted"));

        body.ShouldMatch(@"!\s*_plainMode\s*\)",
            "必须先判断 _plainMode，交互模式下不得启动行式读取");

        var gateAt = Match(body, @"Interlocked\.\w+\(\s*ref\s+_plainInputStarted");
        var startAt = Match(body, @"ReadPlainInputAsync\s*\(");

        gateAt.ShouldBeGreaterThanOrEqualTo(0,
            "幂等门控必须用 Interlocked 对 _plainInputStarted 做原子「检查并置位」");
        startAt.ShouldBeGreaterThan(gateAt,
            "必须在置位之后才启动任务，否则主循环每 50ms 会重复起一个阻塞 ReadLine 的任务");

        body.ShouldMatch(@"Interlocked\.\w+\(\s*ref\s+_plainInputStarted\b[^;]*\breturn\b",
            "Interlocked 门控之后必须立刻 return —— 缺这个早退就不是幂等，会重复启动读取任务");
    }

    /// <summary>
    /// 回归守卫 P7 — 主循环内必须<b>先 Ensure 再 TryRead</b>。
    /// <para>
    /// 顺序即正确性：若先 <c>TryRead</c> 再 <c>Ensure</c>，那么「中途降级到 plain 模式」的那一轮
    /// 里读取任务尚未启动，通道既无内容也未完成，行为退化不可预期（且启动瞬间已到达的
    /// 那一行会迟迟不被处理）。本条只断言<b>相对顺序</b>，且限定在 <c>while</c> 主循环体内，
    /// 因此与排版、缩进、是否拆成多行都无关。
    /// </para>
    /// </summary>
    [Fact]
    public void plain模式_主循环内必须先Ensure再TryRead()
    {
        var loop = MainLoop(StripComments(RawSource.Value));

        var ensureAt = Match(loop, @"EnsurePlainInputStarted\s*\(");
        var readAt = Match(loop, @"_plainInput\.Reader\.TryRead");

        ensureAt.ShouldBeGreaterThanOrEqualTo(0,
            "主循环的 plain 分支必须调用 EnsurePlainInputStarted —— 否则运行中途降级到 plain 模式时 stdin 任务永不启动");
        readAt.ShouldBeGreaterThanOrEqualTo(0, "主循环必须从 _plainInput 读取");
        ensureAt.ShouldBeLessThan(readAt, "主循环内必须先 Ensure 再 TryRead");
    }

    // ------------------------------------------------- SubmitInput → SubmitLine 抽取

    /// <summary>
    /// 回归守卫 P8 — <c>SubmitInput</c> 必须把分派委托给 <c>SubmitLine</c>。
    /// <para>
    /// 这条锁的是「抽取」本身：若有人把 <c>SubmitLine</c> 改回内联进 <c>SubmitInput</c>，
    /// <c>SubmitInput</c> 里会重新出现 <c>ProcessCommandAsync</c> / <c>SendMessageAsync</c>，
    /// 本条立刻变红。共享分派路径是「交互模式与 plain 模式语义一致」的唯一保证。
    /// </para>
    /// </summary>
    [Fact]
    public void SubmitInput_必须委托给SubmitLine_两条输入路径共用同一分派()
    {
        var body = Normalize(ExtractMethod(StripComments(RawSource.Value), "private void SubmitInput"));

        body.ShouldMatch(@"SubmitLine\s*\(",
            "交互模式与 plain 模式必须共用同一分派入口");

        body.ShouldNotMatch(@"ProcessCommandAsync",
            "命令分派已抽取到 SubmitLine，SubmitInput 不得内联一份（否则两条输入路径语义会漂移）");
        body.ShouldNotMatch(@"SendMessageAsync",
            "消息发送分派已抽取到 SubmitLine，SubmitInput 不得内联一份");
        body.ShouldNotMatch(@"AddSystemMessage",
            "无会话提示已抽取到 SubmitLine，SubmitInput 不得内联一份");
    }

    /// <summary>
    /// 回归守卫 P9 — <c>SubmitLine</c> 必须先裁剪并忽略空行。
    /// <para>
    /// plain 模式的 stdin 天然会灌进尾随换行与用户误按的裸回车；忘记裁剪 / 忘记短路空行，
    /// 会让空行触发「请先选择联系人」这类噪声提示，或让 <c>text[0]</c> 越界抛
    /// <c>IndexOutOfRangeException</c>。断言的是「先裁剪 → 再判空」的<b>相对顺序</b>，
    /// 而不锁 <c>text = text.Trim();</c> 这类可被 <c>var trimmed = ...</c> 取代的写法。
    /// </para>
    /// </summary>
    [Fact]
    public void SubmitLine_必须先裁剪再忽略空行()
    {
        var body = Normalize(ExtractMethod(StripComments(RawSource.Value), "private void SubmitLine"));

        var trimAt = Match(body, @"\.Trim\s*\(");
        var emptyAt = Match(body, @"(?:Length\s*==\s*0|IsNullOrEmpty)");

        trimAt.ShouldBeGreaterThanOrEqualTo(0, "SubmitLine 必须裁剪输入");
        emptyAt.ShouldBeGreaterThanOrEqualTo(0, "SubmitLine 必须忽略空行（stdin 裸回车）");
        trimAt.ShouldBeLessThan(emptyAt, "必须先裁剪再判空，否则带空白的空行会漏过短路");
    }

    /// <summary>
    /// 回归守卫 P10 — <c>SubmitLine</c> 必须按「斜杠命令 → 有会话才发送 → 否则提示」三段分派。
    /// <para>
    /// 顺序即语义：若把「无会话」判断提到「斜杠前缀」判断之前，<c>/msg nodeB hi</c>
    /// 这类纯命令行在未选会话时会被错误拦下；若把发送判断提前，命令就会被当成聊天文本发给对端。
    /// 本条只断言四个语义锚点<b>各自的相对顺序</b>，不锁任何一条的具体语句排版。
    /// </para>
    /// </summary>
    [Fact]
    public void SubmitLine_必须按斜杠命令_有会话才发送_否则提示三段分派()
    {
        var body = Normalize(ExtractMethod(StripComments(RawSource.Value), "private void SubmitLine"));

        body.ShouldMatch(@"ProcessCommandAsync",
            "斜杠开头的行必须走命令处理");

        var commandAt = Match(body, @"text\s*\[\s*0\s*\]\s*==\s*'/'");
        var guardAt = Match(body, @"_currentConversationId\.Length\s*>\s*0");
        var sendAt = Match(body, @"SendMessageAsync\s*\(");
        var hintAt = body.IndexOf("请先选择联系人或群组", StringComparison.Ordinal);

        commandAt.ShouldBeGreaterThanOrEqualTo(0, "必须按首字符是否 '/' 区分命令与聊天文本");
        guardAt.ShouldBeGreaterThan(commandAt, "命令分支必须优先于「有会话才发送」判断");
        sendAt.ShouldBeGreaterThan(guardAt, "普通文本必须在确认存在当前会话之后才发送");
        hintAt.ShouldBeGreaterThan(sendAt, "既不是命令又没有会话时才提示用户");
    }

    // --------------------------------------- 信封编解码唯一真相源（TUI 不得再抄一份解析器）

    /// <summary>
    /// 回归守卫 P11 — <c>P2PChatTui.cs</c> 不得自带信封解析副本，必须引用
    /// <c>Core.Extensions.EnvelopeCodec</c>。
    /// <para>
    /// 历史缺陷：本文件曾自带一份「50 字节固定头直接切 <c>Payload</c>」的
    /// <c>MessageEnvelope</c> 解析器，是阶段 3.2 引入签名（公钥长度前缀 + 签名前缀）**之前**
    /// 的旧格式。阶段 3.2 上线时没有同步更新，导致 <c>/connect &lt;ip:port&gt;</c>
    /// 盲连接的 hello 响应必然解析失败，而该路径当时零测试覆盖。编解码已收敛到
    /// <c>EnvelopeCodec</c> 作为唯一真相源；本条阻止任何人再往 TUI 里抄一份。
    /// </para>
    /// <para>
    /// 编解码本身的行为由 <c>tests/P2PChat.Core.Tests/EnvelopeCodecTests.cs</c> 覆盖，
    /// 本条只守「TUI 侧不再出现第二份实现」这一结构事实。
    /// </para>
    /// </summary>
    [Fact]
    public void TUI不得自带信封解析副本_必须走EnvelopeCodec唯一真相源()
    {
        var stripped = StripComments(RawSource.Value);
        var flat = Normalize(stripped);

        flat.ShouldNotMatch(@"DeserializeEnvelope",
            "TUI 不得再定义/调用本地 DeserializeEnvelope —— 信封格式只有 EnvelopeCodec 一份实现");

        Regex.IsMatch(stripped, @"1\s*\+\s*1\s*\+\s*4\s*\+\s*NodeId\.Size").ShouldBeFalse(
            "TUI 不得再手写「1+1+4+NodeId.Size…」这种固定头长度算式 —— 它在阶段 3.2 加签名后已失效");

        Regex.IsMatch(stripped, @"BinaryPrimitives\.Read\w*BigEndian\(\s*\w+\.Slice").ShouldBeFalse(
            "TUI 不得再手工逐字段解析信封字节");

        flat.ShouldMatch(@"EnvelopeCodec\.Deserialize\s*\(",
            "hello 响应解析必须调用 Core.Extensions.EnvelopeCodec.Deserialize");
    }

    // ----------------------------------------------------- 通道语义（运行时，不碰产品代码）

    /// <summary>
    /// 回归守卫 P12 — <c>ReadPlainInputAsync</c> 依赖的通道契约：写入方完成后
    /// <c>TryRead</c> 必须恒返回 <c>false</c>，既不阻塞也不抛异常。
    /// <para>
    /// 本条在运行时真实跑一遍该契约（<c>Channel&lt;T&gt;</c>，无反射、无产品改动），
    /// 与 P4 互为表里：P4 锁「产品代码确实在 EOF 时 <c>TryComplete</c>」，
    /// 本条锁「这么做之后主循环 <c>TryRead</c> 的行为是安全的」。
    /// </para>
    /// </summary>
    [Fact]
    public async Task plain输入通道_写入方完成后_TryRead必须恒返回false且不抛异常()
    {
        var channel = Channel.CreateUnbounded<string>();
        await channel.Writer.WriteAsync("/help");
        channel.Writer.TryComplete();   // ← ReadPlainInputAsync 的 finally 里就是这一句

        channel.Reader.TryRead(out var line).ShouldBeTrue();
        line.ShouldBe("/help");

        // EOF 之后：恒 false（不阻塞、不抛），主循环据此回落到 50ms 轮询而不是空转
        for (var i = 0; i < 100; i++)
        {
            channel.Reader.TryRead(out var afterEof).ShouldBeFalse(
                $"EOF 后第 {i + 1} 次 TryRead 必须返回 false —— 若返回 true 说明通道未完成");
        }
    }

    // ------------------------------------------------------------------ 源码解析辅助

    /// <summary>
    /// 截取 <c>RunAsync</c> 的 <c>while</c> 主循环体（只到第一个平衡的 <c>}</c>）。
    /// <para>
    /// 把「主循环内」与「启动阶段」区分开，是 P2/P5/P7 能只断言<b>相对顺序</b>、
    /// 而不依赖具体排版的关键。
    /// </para>
    /// </summary>
    private static string MainLoop(string strippedSource)
    {
        var run = Normalize(ExtractMethod(strippedSource, "public async Task RunAsync"));
        var whileAt = Match(run, @"while\s*\(\s*_running\b");
        if (whileAt < 0) throw new InvalidOperationException("RunAsync 中找不到主循环 while (_running && ...)");

        var open = run.IndexOf('{', whileAt);
        var depth = 0;
        for (var i = open; i < run.Length; i++)
        {
            if (run[i] == '{') depth++;
            else if (run[i] == '}' && --depth == 0) return run[open..(i + 1)];
        }
        throw new InvalidOperationException("RunAsync 的主循环花括号不配平");
    }

    /// <summary>首次匹配的位置；未命中返回 -1（供「必须存在 / 必须不存在」两类断言复用）。</summary>
    private static int Match(string text, string pattern)
    {
        var m = Regex.Match(text, pattern);
        return m.Success ? m.Index : -1;
    }

    /// <summary>从测试程序集位置向上找 <c>P2PChat.slnx</c>，定位产品源码文件。</summary>
    private static string LocateTuiSourcePath()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "P2PChat.slnx")))
                {
                    var path = Path.Combine(dir.FullName, "src", "P2PChat.UI", "Views", "P2PChatTui.cs");
                    if (File.Exists(path)) return path;
                }
                dir = dir.Parent;
            }
        }
        throw new DirectoryNotFoundException(
            "未能从测试程序集位置向上找到 P2PChat.slnx，无法读取 P2PChatTui.cs 进行结构契约断言");
    }

    /// <summary>去掉 <c>//</c> 行注释（含 XML 文档注释），保留换行以便按花括号提取方法。</summary>
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

    /// <summary>按花括号配平截取某个方法的完整方法体（含外层花括号）。</summary>
    private static string ExtractMethod(string strippedSource, string signature)
    {
        var at = strippedSource.IndexOf(signature, StringComparison.Ordinal);
        if (at < 0) throw new InvalidOperationException($"P2PChatTui.cs 中找不到方法签名: {signature}");

        var open = strippedSource.IndexOf('{', at);
        if (open < 0) throw new InvalidOperationException($"方法 {signature} 没有方法体");

        var depth = 0;
        for (var i = open; i < strippedSource.Length; i++)
        {
            if (strippedSource[i] == '{') depth++;
            else if (strippedSource[i] == '}' && --depth == 0) return strippedSource[open..(i + 1)];
        }
        throw new InvalidOperationException($"方法 {signature} 的花括号不配平");
    }

    /// <summary>把连续空白压成单个空格，使断言不受缩进/换行/代码排版影响。</summary>
    private static string Normalize(string text) => Regex.Replace(text, @"\s+", " ");
}
