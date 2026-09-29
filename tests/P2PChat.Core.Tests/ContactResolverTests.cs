using P2PChat.Core.Extensions;
using P2PChat.Core.Models;
using Shouldly;

namespace P2PChat.Core.Tests;

/// <summary>
/// <see cref="ContactResolver"/> 的行为测试 —— 锁住「<b>歧义即拒发</b>」这条不变量。
/// <para>
/// <b>这个文件为什么以前不可能存在</b>：解析逻辑原先是 <c>P2PChatTui</c> 里的一个私有
/// <c>FirstOrDefault</c>，而 <c>tests/</c> 下<b>没有任何项目引用 <c>P2PChat.UI</c></b>，
/// <c>P2PChatTui</c> 本身也无法在进程内构造。于是这条缺陷天然抓不到 ——
/// 正常场景下「取第一个」本来就是对的，只有<b>构造歧义输入</b>才能暴露它，
/// 而当时没有任何地方能构造。
/// 逻辑下沉到 Core 之后，它第一次变得可测。
/// </para>
/// <para>
/// <b>判据：唯一不变量是「歧义必须拒发」。</b>
/// 「把排序定下来」<b>不是</b>修法 —— 补 comparer 只让选择可复现，
/// 对方改挑一个排在最前的条目即可，攻击链不断。因此本文件<b>不</b>断言任何与排序有关的性质。
/// </para>
/// </summary>
public class ContactResolverTests
{
    private static Contact MakeContact(string hex40, string alias, string? endPoint = null)
    {
        // 刻意不要求 hex40 真的是 40 位：NodeId 的字节长度由构造决定，
        // 而前缀匹配走的是 ToHexString()，用它来造「可预测的碰撞」最直接。
        return new Contact
        {
            NodeId = new NodeId(Convert.FromHexString(hex40.PadRight(40, '0'))),
            Alias = alias,
            EndPoint = endPoint ?? "127.0.0.1:20001"
        };
    }

    [Fact]
    public void 别名精确匹配_唯一命中时返回该联系人()
    {
        var alice = MakeContact("1111111111111111111111111111111111111111", "张三");
        var bob = MakeContact("2222222222222222222222222222222222222222", "李四");

        var r = ContactResolver.Resolve([alice, bob], "张三");

        r.Found.ShouldBeTrue();
        r.Ambiguous.ShouldBeFalse();
        r.Match.ShouldBe(alice);
    }

    [Fact]
    public void 别名匹配_忽略大小写()
    {
        var alice = MakeContact("1111111111111111111111111111111111111111", "Alice");

        ContactResolver.Resolve([alice], "alice").Match.ShouldBe(alice);
    }

    [Fact]
    public void 完整节点ID_唯一命中时返回该联系人()
    {
        var alice = MakeContact("1111111111111111111111111111111111111111", "张三");

        var r = ContactResolver.Resolve([alice], "1111111111111111111111111111111111111111");

        r.Found.ShouldBeTrue();
        r.Match.ShouldBe(alice);
    }

    // ── 核心不变量 ────────────────────────────────────────────────────────

    [Fact]
    public void 短前缀_命中多个联系人时判为歧义_且不返回任何一个()
    {
        // 两个 NodeId 共享 4 位前缀，但彼此不同 ⇒ 短前缀输入必然歧义。
        var a = MakeContact("abcd111111111111111111111111111111111111", "A");
        var b = MakeContact("abcd222222222222222222222222222222222222", "B");

        var r = ContactResolver.Resolve([a, b], "abcd");

        r.Ambiguous.ShouldBeTrue("短前缀命中多个时必须判为歧义");
        r.Found.ShouldBeFalse("歧义时绝不能返回一个 Match —— 那正是旧的 FirstOrDefault 行为");
        r.Match.ShouldBeNull();
        r.Candidates.Count.ShouldBe(2, "候选必须完整返回，供 UI 列出让用户多打几位");
    }

    [Fact]
    public void 短前缀_命中三个联系人时同样判为歧义()
    {
        var contacts = new[]
        {
            MakeContact("beef111111111111111111111111111111111111", "A"),
            MakeContact("beef222222222222222222222222222222222222", "B"),
            MakeContact("beef333333333333333333333333333333333333", "C")
        };

        var r = ContactResolver.Resolve(contacts, "beef");

        r.Ambiguous.ShouldBeTrue();
        r.Candidates.Count.ShouldBe(3);
    }

    [Fact]
    public void 歧义时即使其中一条恰好排在前面的联系人存在_也绝不选它()
    {
        // 这条是给「用排序来修」的假修复设的陷阱：无论列表顺序如何，
        // 歧义就是歧义，取第一个 = 旧缺陷原样保留。
        var first = MakeContact("cafe111111111111111111111111111111111111", "排第一");
        var second = MakeContact("cafe222222222222222222222222222222222222", "排第二");

        var r = ContactResolver.Resolve([first, second], "cafe");

        r.Match.ShouldBeNull("排序与选择无关：歧义必须一律拒发");
        r.Ambiguous.ShouldBeTrue();
    }

    [Fact]
    public void 同名别名_判为歧义_不替用户挑一个()
    {
        // /add 不强制别名唯一，因此重复别名是可达状态，同样必须拒发。
        var a = MakeContact("1111111111111111111111111111111111111111", "peer-abcd1234");
        var b = MakeContact("2222222222222222222222222222222222222222", "peer-abcd1234");

        var r = ContactResolver.Resolve([a, b], "peer-abcd1234");

        r.Ambiguous.ShouldBeTrue("同名别名同样是歧义");
        r.Match.ShouldBeNull();
        r.Candidates.Count.ShouldBe(2);
    }

    // ── 别名优先于前缀 ─────────────────────────────────────────────────────

    [Fact]
    public void 别名精确匹配_优先于同样能命中的前缀()
    {
        // 「abcd」既是某人的别名，又是另一个人的 NodeId 前缀。
        // 选别名那条：别名是用户自己起的，重复可能性低。
        var byPrefix = MakeContact("abcd111111111111111111111111111111111111", "别人");
        var byAlias = MakeContact("9999999999999999999999999999999999999999", "abcd");

        var r = ContactResolver.Resolve([byPrefix, byAlias], "abcd");

        r.Found.ShouldBeTrue();
        r.Match.ShouldBe(byAlias);
    }

    // ── 未命中 ────────────────────────────────────────────────────────────

    [Fact]
    public void 没有任何联系人匹配时_既非命中也非歧义()
    {
        var alice = MakeContact("1111111111111111111111111111111111111111", "张三");

        var r = ContactResolver.Resolve([alice], "ffff");

        r.Found.ShouldBeFalse();
        r.Ambiguous.ShouldBeFalse("0 个候选不是歧义 —— UI 要显示的是「未找到」而不是「有歧义」");
        r.Candidates.ShouldBeEmpty();
    }

    [Fact]
    public void 空联系人表_返回未命中而不是抛异常()
    {
        var r = ContactResolver.Resolve([], "abcd");

        r.Found.ShouldBeFalse();
        r.Ambiguous.ShouldBeFalse();
    }

    // ── 契约 ──────────────────────────────────────────────────────────────

    [Fact]
    public void 传入null联系人集合或null目标_抛ArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() => ContactResolver.Resolve(null!, "abcd"));
        Should.Throw<ArgumentNullException>(() => ContactResolver.Resolve([], null!));
    }

    [Fact]
    public void 解析不修改传入的集合()
    {
        // 纯函数契约：调用方持有的是 UI 的联系人列表，就地排序/去重会污染它。
        var contacts = new List<Contact>
        {
            MakeContact("1111111111111111111111111111111111111111", "张三"),
            MakeContact("2222222222222222222222222222222222222222", "李四")
        };
        var before = contacts.Select(c => c.NodeId.ToHexString()).ToList();

        ContactResolver.Resolve(contacts, "abcd");

        contacts.Select(c => c.NodeId.ToHexString()).ShouldBe(before);
    }
}
