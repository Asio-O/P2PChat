using System.Text;
using System.Text.RegularExpressions;
using Shouldly;

namespace P2PChat.Integration.Tests;

/// <summary>
/// 回归守卫 —— task-17：<c>/connect &lt;ip:port&gt;</c> 的 hello 响应<b>未经任何验签</b>。
/// <para>
/// <b>缺陷回顾</b>：<c>P2PChatTui.ReadHelloResponseAsync</c> 只做
/// <c>EnvelopeCodec.Deserialize(raw)</c> 就直接取载荷使用，从头到尾没调用任何验签。
/// 因为 <c>/connect</c> 是「连上未知端点、等对方先应答」的模式，对端可以自由填写
/// <c>SenderId</c>，于是受害者会
/// <b>把会话密钥登记在第三方 NodeId 名下、并把第三方 NodeId 登记到攻击者的端点</b> ——
/// 之后发给那位好友的每条私聊都加密给攻击者派生的密钥。
/// </para>
///
/// <para><b>本文件性质：源码「结构契约」守卫，不是端到端行为测试。</b>
/// 它<b>不启动 TUI、不发任何 TCP、不写 stdin</b>，只读 <c>src/P2PChat.UI/Views/P2PChatTui.cs</c> 的源码文本。
/// 原因：<c>P2PChatTui</c> 是控制台交互类，其 <c>ConnectCommandAsync</c> 依赖真实控制台状态，
/// 无法在进程内构造（与 <c>PlainModeInputTests</c> 遇到的是同一类限制）。
/// 因此「判定逻辑本身」由 <c>tests/P2PChat.Chat.Tests/EnvelopeVerifierTests.cs</c> 以纯函数方式覆盖，
/// 本文件只负责守住<b>「TUI 确实调用了它」</b>这一条连线。</para>
///
/// <para><b>为什么需要这一层</b>：<c>/connect</c> 这个缺陷的<b>成因</b>就是
/// 「UI 层抄了一份、而 UI 又引用不到 Chat 层的那一份」——
/// 与上一轮 <c>EnvelopeCodec</c> 事故同构。行为测试只能证明「当前这段代码验签了」，
/// 而<b>下一个人</b>再在 UI 里写一份、或干脆把验签调用删掉，行为测试统统察觉不到
/// （因为判定逻辑被抽到 Core 之后，Core 的单测照样全绿）。
/// 结构守卫就是把「UI 不得自建验签、且必须走 Core 那一份」这条规矩本身钉住。</para>
///
/// <para><b>为什么不用反射</b>：本项目 tests/ 下反射用量为 0，Agent.md §4.3 将反射列为禁忌
/// （Native-AOT 下反射目标缺静态调用点会被 ILC 裁剪）。故沿用
/// <c>PlainModeInputTests</c> P11 与 <c>ChatEventDeliveryTests</c> D1/D2/D3 的同形写法。</para>
/// </summary>
public class HelloResponseVerificationTests
{
    /// <summary>
    /// 守卫 H1 —— TUI 必须调用 Core 的 <c>EnvelopeVerifier.Verify</c>。
    /// <para>
    /// 这是本文件<b>唯一一条「必须存在」</b>的正向断言，缺了它整个文件就只剩禁止性规则：
    /// 有人把验签调用整段删掉，负向规则一条都不会触发，缺陷原样复活而测试全绿。
    /// </para>
    /// </summary>
    [Fact]
    public void TUI的hello响应必须经过Core的EnvelopeVerifier验签()
    {
        var code = StripComments(ReadTuiSource());

        Regex.IsMatch(code, @"EnvelopeVerifier\.Verify\s*\(").ShouldBeTrue(
            "TUI 读到的 hello 响应必须送进 Core.Extensions.EnvelopeVerifier.Verify —— " +
            "不验签就等于对「任何抢在真节点前应答的主机」无条件信任（task-17）");
    }

    /// <summary>
    /// 守卫 H2 —— TUI 不得自行实现任何信封验签。
    /// <para>
    /// 验签规则只有 <c>Core.Extensions.EnvelopeVerifier</c> 一份。
    /// UI 层再抄一份 = 上一轮 <c>EnvelopeCodec</c> 事故的同构重演。
    /// </para>
    /// </summary>
    [Fact]
    public void TUI不得自行实现信封验签()
    {
        var code = StripComments(ReadTuiSource());

        foreach (var forbidden in new[]
                 {
                     "ECDsa",                 // 不得直接用 ECDSA API 自己验
                     "VerifyData",            // 同上
                     "VerifyHash",            // 同上
                     "DeserializeEnvelope",   // 不得自带信封反序列化副本
                     "ComputeSignedBytes",    // 不得自行计算待签字节
                     "Signature"              // 不得自行处理签名字段
                 })
        {
            code.Contains(forbidden, StringComparison.Ordinal).ShouldBeFalse(
                $"TUI 不得出现 `{forbidden}` —— 信封验签只有 EnvelopeVerifier 一份实现");
        }
    }

    /// <summary>
    /// 守卫 H3 —— TUI 不得自行实现信封<b>解析</b>。
    /// <para>
    /// 「信封怎么解析」在阶段 3.2 已经收敛到 <c>EnvelopeCodec</c>（<c>PlainModeInputTests</c> P11 守着），
    /// 本条是同一原则的补强：连字段级手工切分也不许出现。
    /// </para>
    /// </summary>
    [Fact]
    public void TUI不得自行实现信封解析()
    {
        var code = StripComments(ReadTuiSource());

        code.Contains("BinaryPrimitives", StringComparison.Ordinal).ShouldBeFalse(
            "TUI 不得手工逐字段解析信封字节 —— 格式只有 EnvelopeCodec 一份实现");

        // 正向：必须走 EnvelopeCodec
        Regex.IsMatch(code, @"EnvelopeCodec\.Deserialize\s*\(").ShouldBeTrue(
            "信封反序列化必须调用 Core.Extensions.EnvelopeCodec.Deserialize");
    }

    /// <summary>
    /// 守卫 H4 —— 对端身份必须<b>从公钥派生</b>，不得取载荷里的 <c>SenderId</c>。
    /// <para>
    /// 这是本轮修复里最容易被回退的一处。<c>TextMessage/KeyExchangeMessage.SenderId</c>
    /// 是<b>对端可控的载荷字段</b>；即使验签已保证它与公钥自洽，
    /// 拿它定身份仍然是把「信任一个外部输入」写进了关键路径 ——
    /// 攻击者只要在验签之前抢先填值，就会把会话密钥挂到别人名下。
    /// </para>
    /// </summary>
    [Fact]
    public void TUI必须从公钥派生对端身份_不得使用载荷里的SenderId()
    {
        var code = StripComments(ReadTuiSource());

        Regex.IsMatch(code, @"NodeId\.FromPublicKey\s*\(").ShouldBeTrue(
            "对端身份必须由 NodeId.FromPublicKey(envelope.SenderPublicKey) 派生");

        Regex.IsMatch(code, @"new\s+NodeId\s*\(\s*response\.SenderId\s*\)").ShouldBeFalse(
            "不得用载荷里的 response.SenderId 定身份 —— 那是对端可控字段，" +
            "会导致会话密钥被登记在第三方 NodeId 名下（task-17 的实际危害）");
    }

    /// <summary>
    /// 守卫 H5 —— 那段<b>自相矛盾</b>的注释必须消失。
    /// <para>
    /// <c>ReadHelloResponseAsync</c> 原先的 XML 注释断言
    /// 「<c>MessageRouter.VerifyEnvelopeCore</c> 已强制公钥↔身份绑定，因此绑定可信」，
    /// 而<b>这条代码路径根本没调用过它</b>。这种注释比没注释更危险：
    /// 它会让下一个读代码的人以为「这里已经验过了」。
    /// 修复后正确引用是 <c>EnvelopeVerifier</c>，因此 TUI 里不应再出现
    /// <c>VerifyEnvelopeCore</c>（那是 Chat 层的 private 薄封装，UI 引用不到也不该引用）。
    /// </para>
    /// <para>
    /// 本条查的是<b>未经去注释的原始文本</b>—— 因为要防的正是「注释里的误导」，
    /// 去掉注释就看不见了。
    /// </para>
    /// </summary>
    [Fact]
    public void TUI不得再声称VerifyEnvelopeCore替它守过身份绑定()
    {
        var raw = ReadTuiSource();

        raw.Contains("VerifyEnvelopeCore", StringComparison.Ordinal).ShouldBeFalse(
            "TUI 里不得再提及 VerifyEnvelopeCore —— 那段注释断言了「公钥↔身份绑定可信」，" +
            "而这条路径从未调用过验签。修复后正确引用是 Core 的 EnvelopeVerifier");
    }

    /// <summary>
    /// 守卫 H6 —— 第 2 层：hello 应答必须<b>回显本次 hello 的一次性 <c>ConversationId</c></b>。
    /// <para>
    /// 这一层堵的是<b>跨受害者重放</b>，也正是本轮「不能用自己重签的包」那句话的落点：
    /// 攻击者用<b>自己的</b>密钥为某个受害者生成一条<b>完全合法</b>的 hello 应答并录下，
    /// 再重放给另一个节点。该节点<b>从未见过这个 MessageId</b>，重放防护（本地状态）会放行；
    /// 而攻击者知道自己的临时私钥，可以解出受害节点派生的会话密钥。
    /// 能拦住它的只有「应答里的关联标识不是这次 hello 的」——
    /// 因为 <c>connect-{tempId..8}</c> 每次调用重新随机生成。
    /// </para>
    /// <para>
    /// <b>性质：结构契约。</b>该检查位于 TUI 私有方法内（不可在进程内执行，见类注释），
    /// 本条确保它<b>存在且真的拿 expected 值做比较</b>；比对语义本身由 H1/H4 覆盖。
    /// </para>
    /// </summary>
    [Fact]
    public void TUI的hello路径必须校验应答回显了本次的关联标识()
    {
        var code = StripComments(ReadTuiSource());

        Regex.IsMatch(code, @"string\.Equals\s*\(\s*response\.ConversationId\s*,\s*expectedConversationId").ShouldBeTrue(
            "hello 应答必须与本次 hello 的 ConversationId 做逐字节比较（顺序参数）—— " +
            "缺失这一步，攻击者录下一条合法应答即可跨受害者重放");

        code.Contains("expectedConversationId", StringComparison.Ordinal).ShouldBeTrue(
            "/connect 的 hello 读取路径必须接收一个期望的关联标识参数");
    }

    /// <summary>
    /// 守卫 H7 —— 第 3 层：TUI 必须真的调用 <c>EvaluatePeerIdentity</c> 并按裁决分流。
    /// <para>
    /// 端点身份连续性是<b>首次接触之外唯一真正能拒绝自签攻击者</b>的手段
    /// （首次接触时合法节点与攻击者的信封在密码学上不可区分）。
    /// <c>EvaluatePeerIdentity</c> 本身是 Core 里的纯函数，其判定语义由
    /// <c>tests/P2PChat.Chat.Tests/EnvelopeVerifierTests.cs</c> 覆盖；
    /// 本条只管「TUI 调了它、按三种裁决分流」。
    /// </para>
    /// </summary>
    [Fact]
    public void TUI必须按端点身份裁决分流_冲突时默认拒绝()
    {
        var code = StripComments(ReadTuiSource());

        Regex.IsMatch(code, @"EnvelopeVerifier\.EvaluatePeerIdentity\s*\(").ShouldBeTrue(
            "/connect 必须调用 EnvelopeVerifier.EvaluatePeerIdentity 判定端点→身份连续性");

        code.Contains("PeerIdentityVerdict.ConflictsWithExistingPin", StringComparison.Ordinal).ShouldBeTrue(
            "必须显式处理 ConflictsWithExistingPin 裁决（同端点换身份 = 中间人信号）");

        // 冲突必须默认拒绝：放行只能经 --force 显式豁免，不能无条件继续
        Regex.IsMatch(code, @"if\s*\(\s*!\s*force\s*\)").ShouldBeTrue(
            "身份冲突必须在非 --force 时中断流程 —— 无条件继续等于没有第 3 层");
    }

    /// <summary>
    /// 守卫 H8 —— 首次接触必须<b>如实告知「未经带外验证」</b>，绝不能显示「已验证」。
    /// <para>
    /// 这是<b>防「文档与文案夸大」</b>的一道守卫。TOFU 阶段在信息论上无法确认对方身份，
    /// 一旦 UI 写成「身份已验证」，用户就会据此做出错误的安全判断 ——
    /// 而且这种夸大极难被其它测试发现。
    /// </para>
    /// <para><b>性质：结构契约。</b>断言的是源码里的提示文案。</para>
    /// </summary>
    [Fact]
    public void 首次接触的提示必须写明未经带外验证_不得声称已验证()
    {
        // 必须**去注释后**再断言：源码里那句「绝不能出现『已验证』『身份可信』这类字眼」
        // 本身就是一条注释，它恰好含有被禁的字样。不去注释的话，本守卫会去指控
        // 记录这条纪律的那行注释自己 —— 守卫把自己写下的说明书当成违规现场。
        var code = StripComments(ReadTuiSource());

        code.Contains("未经带外验证", StringComparison.Ordinal).ShouldBeTrue(
            "首次接触的提示必须包含「未经带外验证」字样，让用户知道这是 TOFU");

        // 「已验证」一旦出现在用户可见提示里就是误导。逐个可疑措辞断言。
        foreach (var forbidden in new[] { "身份已验证", "已通过身份验证", "身份可信", "已认证" })
        {
            code.Contains(forbidden, StringComparison.Ordinal).ShouldBeFalse(
                $"TUI 不得出现「{forbidden}」—— 首次接触无法确认对方身份（TOFU），" +
                "声称已验证会让用户据此做出错误的安全判断");
        }
    }

    // ------------------------------------------------------------ 源码定位与处理

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
            "未能从测试程序集位置向上找到 P2PChat.slnx，无法读取 P2PChatTui.cs 进行结构契约断言");
    }

    private static string ReadTuiSource()
    {
        var path = Path.Combine(RepoRoot(), "src", "P2PChat.UI", "Views", "P2PChatTui.cs");
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"结构契约断言找不到产品源码: {path}。若产品文件被移动/改名，请同步更新本守卫。", path);
        return File.ReadAllText(path);
    }

    /// <summary>去掉 <c>//</c> 行注释（含 <c>///</c> XML 文档注释），保留换行。</summary>
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
