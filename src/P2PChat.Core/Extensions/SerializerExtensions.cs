using MessagePack;
using MessagePack.Resolvers;
using P2PChat.Core.Abstractions;

namespace P2PChat.Core.Extensions;

/// <summary>
/// MessagePack序列化器实现
/// </summary>
public class MessagePackSerializer : ISerializer
{
    /// <summary>
    /// 纯静态（source-generated）解析器组合 — 不包含 StandardResolver /
    /// ContractlessStandardResolver 等依赖 Reflection.Emit 的运行时代码生成路径，
    /// 因此 Native-AOT 下无 IL3050/IL3053 动态代码告警。
/// 解析顺序：
/// 1. P2PChatMessagePackResolver — 本程序集编译期生成的 formatter（Message 基类 + 8 个 Union 子类）
/// 2. SourceGeneratedFormatterResolver — MessagePack 内建，按 typeof(T).Assembly 发现生成 formatter
/// 3. BuiltinResolver — 基元类型、Guid/DateTime、byte[]、数组与内建泛型集合
/// 4. RegisteredResolverFallback — 下游程序集（如 Networking）显式注册的生成 resolver。
///    对 List&lt;NodeInfoDto&gt; 这类泛型构造，显式注册可避免自动发现因 T 落在
///    System.Private.CoreLib 而无法命中的问题。
/// </summary>
    private static readonly IFormatterResolver Resolver = CompositeResolver.Create(
        P2PChatMessagePackResolver.Instance,
        SourceGeneratedFormatterResolver.Instance,
        BuiltinResolver.Instance,
        RegisteredResolverFallback.Instance);

    private readonly MessagePackSerializerOptions _options;

    public MessagePackSerializer()
    {
        // 注意：这里不能用 MessagePackSerializerOptions.Standard.WithResolver(...)，
        // 因为 Standard 的静态构造会引用 StandardResolver（含 DynamicObjectResolver 等
        // [RequiresDynamicCode] 路径），ILC 会因此保留动态代码并继续产生 IL3053。
        // 使用构造函数直接创建，保持与 Standard 一致的安全设置（不可信数据）。
        _options = new MessagePackSerializerOptions(Resolver)
            .WithSecurity(MessagePackSecurity.UntrustedData);
    }

    /// <inheritdoc />
    public byte[] Serialize<T>(T obj)
    {
        return MessagePack.MessagePackSerializer.Serialize(obj, _options);
    }

    /// <inheritdoc />
    public T Deserialize<T>(byte[] data)
    {
        return MessagePack.MessagePackSerializer.Deserialize<T>(data, _options);
    }

    /// <inheritdoc />
    public T Deserialize<T>(ReadOnlySpan<byte> data)
    {
        return MessagePack.MessagePackSerializer.Deserialize<T>(data.ToArray(), _options);
    }

    /// <inheritdoc />
    public T Deserialize<T>(Stream stream)
    {
        return MessagePack.MessagePackSerializer.Deserialize<T>(stream, _options);
    }
}
