using MessagePack;

namespace P2PChat.Networking.Dht;

/// <summary>
/// 仅为触发 MessagePack 源生成器生成集合 formatter 的锚定类型。
/// <para>
/// <b>不参与任何线上协议</b>：该类型永远不会被实例化或序列化，也不出现在任何网络报文中；
/// 它存在的唯一目的是让编译期源生成器为 <c>List&lt;NodeInfoDto&gt;</c> 注册 formatter。
/// </para>
/// <para>
/// 背景：MessagePack v3 的 source generator 只为“被 [MessagePackObject] 类型成员引用到的集合构造”
/// 生成 formatter。而 <see cref="KademliaDhtService"/> 在运行时通过 <c>ISerializer</c> 序列化
/// <c>List&lt;NodeInfoDto&gt;</c>（DHT contacts 响应），该构造不被任何 DTO 成员引用，
/// 在纯 source-generated resolver 下会抛 FormatterNotRegisteredException；
/// BuiltinResolver 也不覆盖任意 <c>List&lt;T&gt;</c>。
/// </para>
/// <para>
/// 本类型通过暴露一个 <c>List&lt;NodeInfoDto&gt;</c> 成员，使本程序集生成的 resolver 注册
/// <c>ListFormatter&lt;NodeInfoDto&gt;</c>，运行时由 Core 的
/// <c>RegisteredResolverFallback</c> 显式查询。此方案不引入任何动态代码（无 Reflection.Emit），
/// 且不改变任何现有 DTO 的 [Key]/[Union] 布局。
/// </para>
/// </summary>
[MessagePackObject(AllowPrivate = true)]
internal sealed class DhtMessagePackFormatterAnchor
{
    /// <summary>仅用于编译期触发生成 List&lt;NodeInfoDto&gt; 的 formatter，运行时永不访问。</summary>
    [Key(0)]
    public List<NodeInfoDto>? Nodes { get; set; }
}
