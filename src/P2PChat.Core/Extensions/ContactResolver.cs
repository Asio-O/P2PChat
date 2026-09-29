using P2PChat.Core.Models;

namespace P2PChat.Core.Extensions;

/// <summary>
/// 联系人解析（别名精确匹配 / 节点 ID 前缀匹配）的<b>唯一</b>实现。
/// <para>
/// <b>为什么不留在 UI 层</b>：这段逻辑曾经是 <c>P2PChatTui</c> 里的一个私有
/// <c>FirstOrDefault</c>，而 <c>P2PChatTui</c> 无法在进程内构造（依赖真实控制台状态），
/// <c>tests/</c> 下也<b>没有任何项目引用 <c>P2PChat.UI</c></b> —— 于是它既没有行为测试，
/// 也无法在不新增程序集依赖的前提下补上。这与本项目把 <c>EnvelopeCodec</c> /
/// <c>EnvelopeVerifier</c> 下沉到 Core 的理由<b>完全同构</b>：逻辑是纯函数、
/// 不依赖 UI，把它放在能被引用的层，是唯一能给它装上测试的做法。
/// </para>
/// <para>
/// <b>它修的是什么</b>：短 hex 前缀命中多个联系人时，旧实现静默取列表第一个 ——
/// 消息用<b>错误对端</b>的会话密钥发出，而 UI 紧接着显示「我 -&gt; &lt;别名&gt;: &lt;文本&gt;」
/// （那行在发送<b>之后</b>执行，与成败无关），全链路<b>零告警</b>。
/// </para>
/// <para>
/// <b>唯一的不变量：歧义必须拒发。</b>
/// 「把排序定下来」<b>不是</b>修法 —— 给排序补一个 comparer 只会让选择<b>可复现</b>，
/// 对方改挑一个排在最前的条目即可，攻击链一点没断。
/// </para>
/// <para>
/// <b>本类型不证明什么</b>：解析出某个联系人<b>不</b>代表「那就是你想找的人」。
/// 联系人表本身是本地存储，其完整性是另一个问题（手输的 <c>/add</c>，
/// 或某次 <c>/connect</c> 的 TOFU 接受被自动落盘）。本方法只保证
/// 「在你给的这些联系人里，输入的指向是<b>唯一</b>的」。
/// </para>
/// <para>
/// <b>AOT 安全</b>：纯静态方法，无反射、无动态代码。
/// </para>
/// </summary>
public static class ContactResolver
{
    /// <summary>解析结果。区分「没找到」与「<b>找到多个</b>」是本类型存在的全部理由。</summary>
    /// <param name="Match">唯一命中时的那个联系人；未命中或歧义时为 null。</param>
    /// <param name="Candidates">
    /// 实际命中的全部候选（歧义时长度 &gt; 1）。供 UI 展示，让用户多打几个字符就能消除歧义。
    /// </param>
    public readonly record struct Resolution(Contact? Match, IReadOnlyList<Contact> Candidates)
    {
        /// <summary>是否唯一命中。<b>只有 true 时才可以取用 <see cref="Match"/>。</b></summary>
        public bool Found => Match is not null;

        /// <summary>命中多个。调用方<b>必须</b>拒发，<b>不得</b>取第一个。</summary>
        public bool Ambiguous => Match is null && Candidates.Count > 1;
    }

    /// <summary>
    /// 按<b>别名精确匹配</b>、其次<b>节点 ID 前缀匹配</b>解析联系人。
    /// <para>
    /// 别名优先：别名是用户自己起的，重复的可能性低；短 hex 前缀才是会随联系人增多而
    /// 越来越容易撞车的那种（<c>/connect</c> 成功会自动把对端落盘进联系人，
    /// 别名形如 <c>peer-{hex[..8]}</c>）。
    /// </para>
    /// <para>
    /// 前缀匹配在输入满 40 位时等价于精确匹配 —— 两个不同的 NodeId 都是 40 位等长，
    /// 不可能互为前缀。因此**只有输入短于 40 位时才可能歧义**，而那正是要拒发的情况。
    /// </para>
    /// </summary>
    /// <param name="contacts">候选联系人集合。</param>
    /// <param name="target">用户输入（别名、完整 NodeId 或其前缀）。</param>
    public static Resolution Resolve(IEnumerable<Contact> contacts, string target)
    {
        ArgumentNullException.ThrowIfNull(contacts);
        ArgumentNullException.ThrowIfNull(target);

        var all = contacts as IReadOnlyList<Contact> ?? contacts.ToList();

        var byAlias = all
            .Where(c => c.Alias.Equals(target, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (byAlias.Count > 0)
            return new(byAlias.Count == 1 ? byAlias[0] : null, byAlias);

        // 多个同名别名同样是歧义：/add 没有强制别名唯一，这里不能替用户挑一个。
        var byPrefix = all
            .Where(c => c.NodeId.ToHexString().StartsWith(target, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return new(byPrefix.Count == 1 ? byPrefix[0] : null, byPrefix);
    }
}
