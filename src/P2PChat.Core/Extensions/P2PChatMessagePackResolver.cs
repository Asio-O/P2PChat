using MessagePack;

namespace P2PChat.Core.Extensions;

/// <summary>
/// MessagePack 源生成解析器 — 仅包含编译期生成的 formatter
/// <para>
/// 该类型由 MessagePack 内置 source generator 在编译期补齐（[GeneratedMessagePackResolver]），
/// 因此不依赖 Reflection.Emit / 运行时动态代码生成，可在 Native-AOT 下安全使用。
/// </para>
/// <para>
/// 覆盖范围：本程序集（P2PChat.Core）中所有标注 [MessagePackObject]/[Union] 的类型，
/// 即 Message 基类与其 8 个 Union 子类。
/// </para>
/// </summary>
[GeneratedMessagePackResolver]
internal partial class P2PChatMessagePackResolver
{
}
