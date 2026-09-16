using System.Net;

namespace P2PChat.Core.Abstractions;

/// <summary>
/// TCP连接 — 单个端到端TCP通道
/// </summary>
public interface ITcpConnection : IAsyncDisposable
{
    /// <summary>连接ID</summary>
    Guid ConnectionId { get; }

    /// <summary>远端终结点</summary>
    IPEndPoint RemoteEndPoint { get; }

    /// <summary>连接是否存活</summary>
    bool IsConnected { get; }

    /// <summary>
    /// 发送消息 (长度前缀成帧: 4字节BigEndian长度+负载)
    /// </summary>
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>
    /// 接收一条完整消息
    /// </summary>
    Task<ReadOnlyMemory<byte>> ReceiveMessageAsync(CancellationToken ct = default);
}
