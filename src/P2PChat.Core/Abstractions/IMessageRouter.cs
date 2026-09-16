using P2PChat.Core.Enums;
using P2PChat.Core.Models;

namespace P2PChat.Core.Abstractions;

/// <summary>
/// 消息路由器 — 插件式消息分发中枢
/// </summary>
public interface IMessageRouter
{
    /// <summary>
    /// 注册消息处理器 (插件注册入口)
    /// </summary>
    void RegisterHandler<T>(IMessageHandler<T> handler) where T : Message;

    /// <summary>
    /// 移除消息处理器
    /// </summary>
    void UnregisterHandler(MessageType messageType);

    /// <summary>
    /// 将入站消息路由到正确的处理器
    /// </summary>
    Task RouteIncomingAsync(MessageEnvelope envelope, ITcpConnection sender, CancellationToken ct = default);

    /// <summary>
    /// 发送消息到指定节点 (自动建立TCP连接和序列化)
    /// </summary>
    Task SendAsync(NodeInfo recipient, Message message, CancellationToken ct = default);

    /// <summary>
    /// 通过已有连接发送消息
    /// </summary>
    Task SendViaConnectionAsync(ITcpConnection connection, Message message, CancellationToken ct = default);

    /// <summary>
    /// 获取或建立到指定节点的TCP连接
    /// </summary>
    Task<ITcpConnection> GetOrCreateConnectionAsync(NodeInfo node, CancellationToken ct = default);

    /// <summary>
    /// 关闭到指定节点ID的所有连接
    /// </summary>
    Task CloseConnectionAsync(byte[] nodeId);
}
