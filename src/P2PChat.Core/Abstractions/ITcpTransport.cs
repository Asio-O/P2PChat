using System.Net;

namespace P2PChat.Core.Abstractions;

/// <summary>
/// TCP传输层 — 聊天和文件传输使用
/// </summary>
public interface ITcpTransport : IDisposable
{
    /// <summary>监听端口</summary>
    int ListenPort { get; }

    /// <summary>本地监听终结点</summary>
    IPEndPoint? LocalEndPoint { get; }

    /// <summary>
    /// 启动TCP监听
    /// </summary>
    Task StartListeningAsync(int port, CancellationToken ct = default);

    /// <summary>
    /// 连接到远端节点
    /// </summary>
    Task<ITcpConnection> ConnectAsync(IPEndPoint endpoint, CancellationToken ct = default);

    /// <summary>
    /// 等待并接受一个TCP连接
    /// </summary>
    Task<ITcpConnection> AcceptAsync(CancellationToken ct = default);

    /// <summary>
    /// 传入连接事件流
    /// </summary>
    IAsyncEnumerable<ITcpConnection> IncomingConnections { get; }
}
