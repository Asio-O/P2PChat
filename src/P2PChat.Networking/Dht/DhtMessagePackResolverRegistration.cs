using System.Runtime.CompilerServices;
using P2PChat.Core.Extensions;

namespace P2PChat.Networking.Dht;

/// <summary>
/// 把本程序集编译期生成的 MessagePack resolver 注册到 <see cref="MessagePackResolverRegistry"/>。
/// <para>
/// 目的：Core 的序列化器需要解析 <c>List&lt;NodeInfoDto&gt;</c>（DHT contacts 响应的真实路径）。
/// Core 的序列化器通过显式注册表查询下游 resolver；
/// 该 formatter 由本程序集在编译期生成（见 <see cref="DhtMessagePackFormatterAnchor"/> 的锚定说明）。
/// </para>
/// <para>
/// 本注册在程序集加载时（[ModuleInitializer]）执行，早于任何 DHT 报文的序列化；
/// 全静态实例化，无 Reflection.Emit / MakeGenericType，Native-AOT 安全。
/// </para>
/// </summary>
internal static class DhtMessagePackResolverRegistration
{
    // 这是有意的库级 ModuleInitializer：确保 Networking 的生成 resolver 在首次 DHT 序列化前注册。
#pragma warning disable CA2255 // 此处属于有意的 source-generated resolver 注册方案。
    [ModuleInitializer]
    internal static void Register() =>
        MessagePackResolverRegistry.Register(MessagePack.GeneratedMessagePackResolver.Instance);
#pragma warning restore CA2255
}
