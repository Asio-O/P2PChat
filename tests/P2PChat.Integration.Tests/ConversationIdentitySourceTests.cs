using System.Text.RegularExpressions;
using Shouldly;

namespace P2PChat.Integration.Tests;

/// <summary>
/// 回归守卫 —— 本机身份真相源统一（task-29）。
/// <para>
/// 缺陷：<c>P2PChatTui.PrivateConversationKey</c> 用 <c>dhtService.LocalNode.NodeId</c>
/// 计算会话键，而 <c>ChatService.SendPrivateMessageAsync</c> 用
/// <c>keyStore.GetOrCreateIdentity().NodeId</c>。两者是<b>独立真相源</b>：
/// <c>NodeInfo</c> 是 record + init，LocalNode 里的 NodeId 是<b>启动时派生后冻结</b>的值；
/// keyStore 是<b>发送时现读</c>的值。只要身份在进程内变化过，二者即分叉。
/// </para>
/// <para>
/// <b>为什么它比 FileTransferService 的同类缺陷更隐蔽</b>：那边分叉后载荷 SenderId 与信封不自洽，
/// 会被 <c>MessageRouter</c> 的「载荷/信封 SenderId 一致性」检查<b>整条拒掉并留下明确告警</b>；
/// 而这边分叉后消息<b>照常收发</b>，只是落进一个 UI <b>永远选不中</b>的会话桶 ——
/// 没有拒绝日志、没有异常，用户只看到「消息发不出去」。
/// </para>
///
/// <para><b>为什么用结构守卫而不是行为断言</b>：<c>PrivateConversationKey</c> 是 <c>private</c>，
/// TUI 又没有可注入的接缝，无法从外部观测它算出的会话键。直接断言
/// <c>ConversationId.ForPrivate</c> 本身只会测到那个纯函数，测不到「TUI 传了哪个身份进去」——
/// 那正是本缺陷所在。本文件沿用 <c>PlainModeInputTests</c> 的 P11 同形手法（源文本断言，不用反射）。</para>
///
/// <para><b>纪律</b>：断言代码内容必须<b>先去注释</b>再匹配，否则注释里出现目标字样会让
/// 「代码里该有 X」的空断言假通过（本文件一律走 <c>StripComments</c>）。</para>
/// </summary>
[Trait("Category", "KnownDefect")]
public class ConversationIdentitySourceTests
{
    private static readonly Lazy<string> RawSource = new(() => File.ReadAllText(LocateTuiSourcePath()));

    // ------------------------------------------------------------------ 守卫

    /// <summary>
    /// 回归守卫 G1 — TUI 的私聊会话键必须用 <b>keyStore 身份</b>，不得用 <c>dhtService.LocalNode.NodeId</c>。
    /// </summary>
    [Fact]
    public void TUI私聊会话键的本机身份必须取自keyStore而非DHT的LocalNode()
    {
        var method = ExtractMethod(StripComments(RawSource.Value),
            "private string PrivateConversationKey");

        method.ShouldNotBeNull("未能定位 PrivateConversationKey —— 守卫本身失效，必须先修守卫再谈产品");

        // 正面：确实用了 keyStore 的身份。
        Match(method!, @"keyStore\.GetOrCreateIdentity\(\)\s*\.?\s*NodeId")
            .ShouldBeGreaterThanOrEqualTo(0,
                "PrivateConversationKey 必须用 keyStore.GetOrCreateIdentity().NodeId —— " +
                "它与 ChatService 写信封 SenderId、会话分桶用的是同一份身份");

        // 反面：绝不能退回 DHT 的 LocalNode（冻结值，与发送时现读的 keyStore 可能分叉）。
        method.ShouldNotMatch(@"dhtService\.LocalNode",
            "PrivateConversationKey 不得使用 dhtService.LocalNode.NodeId —— 那是启动时冻结的独立真相源，"+
            "一旦与 keyStore 分叉，消息会落进 UI 永远选不中的会话桶，且没有任何拒绝日志");
    }

    /// <summary>
    /// 回归守卫 G2 — 全文件里<b>除本机身份展示外</b>，不得再把
    /// <c>dhtService.LocalNode</c> 当身份真相源使用。
    /// <para>
    /// 区分「身份用途」与「展示/自述用途」：<c>/nodes</c> 状态栏显示本机 ID、
    /// <c>/connect</c> 自报监听端点（读的是 <c>EndPoint</c>）都属后者，允许存在。
    /// 本条只拦「把 LocalNode 当作要给对端看的身份」这一类。
    /// </para>
    /// </summary>
    [Fact]
    public void TUI不得把DHT的LocalNode当身份真相源()
    {
        var stripped = StripComments(RawSource.Value);

        // 本机 ID 展示（状态栏 / 自检块）允许；除此之外的 LocalNode 身份使用一律不允许。
        var identityUses = Regex.Matches(stripped, @"[A-Za-z_][A-Za-z0-9_.]*\s*\.\s*LocalNode\s*\.\s*NodeId")
            .Select(m => m.Value)
            .ToList();

        // 允许的展示点：状态栏与自检块用 Short()/ToHexString() 打印，不参与任何身份判定。
        var displayOnly = identityUses
            .Where(v => v.Contains("dhtService", StringComparison.Ordinal))
            .ToList();

        displayOnly.ShouldAllBe(v => false,
            "TUI 里任何 dhtService.LocalNode.NodeId 的使用都必须移除：它是启动时冻结的独立真相源。" +
            "若只是要在状态栏显示本机 ID，请改用 keyStore.GetOrCreateIdentity().NodeId");
    }

    // ------------------------------------------------------------------ 夹具

    private static int Match(string text, string pattern)
    {
        var m = Regex.Match(text, pattern);
        return m.Success ? m.Index : -1;
    }

    /// <summary>定位 TUI 源码：从测试程序集目录向上找到仓库根，再拼相对路径。</summary>
    private static string LocateTuiSourcePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;

        if (dir is null)
            throw new DirectoryNotFoundException("未能从测试输出目录向上定位到包含 src/ 的仓库根");

        var path = Path.Combine(dir.FullName, "src", "P2PChat.UI", "Views", "P2PChatTui.cs");
        if (!File.Exists(path))
            throw new FileNotFoundException("未找到 P2PChatTui.cs", path);

        return path;
    }

    /// <summary>去掉 // 行注释与 /* */ 块注释，避免注释里的字样造成空断言假通过。</summary>
    private static string StripComments(string source)
    {
        source = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(source, @"//[^\n]*", " ");
    }

    /// <summary>截取某个方法签名的完整源码（从签名处到下一个顶层成员声明为止）。</summary>
    private static string? ExtractMethod(string strippedSource, string signature)
    {
        var start = strippedSource.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0) return null;

        // 下一个"缩进 <= 方法签名缩进"的行即下一成员的开始。
        var lineStart = strippedSource.LastIndexOf('\n', start);
        var indent = start - lineStart - 1;

        var i = start;
        while (i < strippedSource.Length)
        {
            var nextLineStart = strippedSource.IndexOf('\n', i);
            if (nextLineStart < 0) nextLineStart = strippedSource.Length;

            var line = strippedSource[i..nextLineStart];
            var lineIndent = line.Length - line.TrimStart().Length;

            if (lineIndent <= indent && line.Trim().Length > 0 && i > start)
                return strippedSource[start..i];

            i = nextLineStart + 1;
        }

        return strippedSource[start..];
    }
}
