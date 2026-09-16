using P2PChat.Core.Enums;
using P2PChat.Core.Models;

namespace P2PChat.Core.Abstractions;

/// <summary>
/// 非泛型消息处理器接口 — 供路由器做静态可解析的类型擦除分发。
/// 存在的意义: 让 MessageRouter 无需借助反射(GetMethod/Invoke)调用泛型处理器，
/// 否则 Native-AOT 下 ILC 看不到静态调用点，可能裁剪掉各处理器的 HandleAsync 实现，
/// 导致所有入站消息路由(私聊/群聊)在原生产物中静默失效。
/// </summary>
public interface IMessageHandler
{
    /// <summary>该处理器处理的消息类型</summary>
    MessageType MessageType { get; }

    /// <summary>
    /// 处理入站消息 (消息类型已擦除为基类 Message)
    /// </summary>
    /// <param name="message">解析后的消息</param>
    /// <param name="senderConnection">发送者TCP连接</param>
    /// <param name="envelope">原始消息信封(包含元数据)</param>
    Task HandleAsync(Message message, ITcpConnection senderConnection, MessageEnvelope envelope, CancellationToken ct = default);
}

/// <summary>
/// 消息处理器接口 — 插件扩展点
/// 每种消息类型对应一个处理器实现
/// </summary>
/// <typeparam name="T">消息类型</typeparam>
public interface IMessageHandler<T> : IMessageHandler where T : Message
{
    /// <summary>
    /// 处理入站消息
    /// </summary>
    /// <param name="message">解析后的消息</param>
    /// <param name="senderConnection">发送者TCP连接</param>
    /// <param name="envelope">原始消息信封(包含元数据)</param>
    Task HandleAsync(T message, ITcpConnection senderConnection, MessageEnvelope envelope, CancellationToken ct = default);

    /// <summary>
    /// 默认桥接实现: 把基类 Message 还原为 T 后转发到强类型重载。
    /// 采用默认接口方法，使各处理器实现类无需新增任何样板代码，
    /// 同时把分发路径变成编译器/ILC 可静态分析的接口调用。
    /// </summary>
    Task IMessageHandler.HandleAsync(Message message, ITcpConnection senderConnection, MessageEnvelope envelope, CancellationToken ct)
        => HandleAsync((T)message, senderConnection, envelope, ct);
}
