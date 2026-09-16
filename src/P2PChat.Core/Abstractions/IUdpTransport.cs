using System.Net;

namespace P2PChat.Core.Abstractions;

/// <summary>
/// UDP传输层 — DHT通信使用
/// </summary>
public interface IUdpTransport : IDisposable
{
    /// <summary>本地监听终结点</summary>
    IPEndPoint LocalEndPoint { get; }

    /// <summary>
    /// 向指定终结点发送数据
    /// </summary>
    Task SendAsync(byte[] data, IPEndPoint endpoint, CancellationToken ct = default);

    /// <summary>
    /// 接收UDP数据包
    /// </summary>
    Task<UdpPacket> ReceiveAsync(CancellationToken ct = default);
}

/// <summary>
/// UDP接收数据包
/// </summary>
public record UdpPacket
{
    public required byte[] Data { get; init; }
    public required IPEndPoint RemoteEndPoint { get; init; }
}
