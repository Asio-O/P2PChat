using MessagePack;
using MessagePack.Formatters;

namespace P2PChat.Core.Extensions;

/// <summary>
/// MessagePack resolver 注册表 — 允许下游程序集（如 P2PChat.Networking）把自身编译期
/// 生成的 source-generated resolver 提供给 Core 的序列化器。
/// <para>
/// 为什么需要它：对于 <c>List&lt;NodeInfoDto&gt;</c> 这类泛型构造，Core 的静态解析器组合无法
/// 从自身的生成 resolver 中取得由下游程序集提供的 formatter，可能抛出
/// FormatterNotRegisteredException。而该构造的 formatter 实际由定义元素类型的程序集
/// （P2PChat.Networking）在编译期生成，因此由下游程序集显式注册。
/// </para>
/// <para>
/// 本注册表 + <see cref="RegisteredResolverFallback"/> 让下游 resolver 可被显式发现。
/// 全程只使用静态实例化（无 Reflection.Emit、无 MakeGenericType），Native-AOT 安全。
/// </para>
/// </summary>
public static class MessagePackResolverRegistry
{
    private static readonly object Gate = new();
    private static IFormatterResolver[] _resolvers = Array.Empty<IFormatterResolver>();

    /// <summary>
    /// 注册一个额外的 resolver（幂等：同一实例重复注册会被忽略）。
    /// 下游程序集通常在 <c>[ModuleInitializer]</c> 中调用本方法。
    /// </summary>
    public static void Register(IFormatterResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        lock (Gate)
        {
            if (Array.IndexOf(_resolvers, resolver) >= 0)
            {
                return;
            }

            var next = new IFormatterResolver[_resolvers.Length + 1];
            Array.Copy(_resolvers, next, _resolvers.Length);
            next[^1] = resolver;
            _resolvers = next;
        }
    }

    /// <summary>当前已注册 resolver 的快照（不可变数组，读取无需加锁）。</summary>
    internal static IFormatterResolver[] Snapshot() => _resolvers;
}

/// <summary>
/// 在每次 formatter 解析时查询 <see cref="MessagePackResolverRegistry"/> 的 fallback resolver。
/// <para>
/// 惰性查询（而非在构造 options 时快照），因此下游程序集何时加载/注册都不影响正确性：
/// 只要在真正序列化某个类型之前完成注册即可 —— 而使用下游类型本身就会先加载其程序集。
/// </para>
/// </summary>
internal sealed class RegisteredResolverFallback : IFormatterResolver
{
    public static readonly IFormatterResolver Instance = new RegisteredResolverFallback();

    private RegisteredResolverFallback()
    {
    }

    public IMessagePackFormatter<T>? GetFormatter<T>()
    {
        var resolvers = MessagePackResolverRegistry.Snapshot();
        for (var i = 0; i < resolvers.Length; i++)
        {
            var formatter = resolvers[i].GetFormatter<T>();
            if (formatter is not null)
            {
                return formatter;
            }
        }

        return null;
    }
}
