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
    /// 向 mesh 邻居表中的全部活跃连接泛洪一条本机新发的消息：签名一次，多路复用同一信封。
    /// <para>
    /// 仅聊天载荷（PrivateText / GroupText）应走此路径；控制与文件消息是请求-响应配对或
    /// 点对点大流量，泛洪在语义上是错的（见 2026-10-09-mesh-topology-and-flooding）。
    /// </para>
    /// <para>
    /// 返回实际送达的邻居数（0 = 无活跃邻居）。单个邻居发送失败只记日志，不中断其余邻居。
    /// </para>
    /// </summary>
    Task<int> FloodAsync(Message message, CancellationToken ct = default) => Task.FromResult(0);

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
