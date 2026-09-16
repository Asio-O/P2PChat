using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using P2PChat.Core.Abstractions;

namespace P2PChat.Networking.Transport;

/// <summary>
/// UDP传输层实现 — 用于DHT通信
/// 端口被占用时自动尝试随机端口
/// </summary>
public class UdpTransport : IUdpTransport
{
    private readonly Socket _socket;
    private readonly ILogger<UdpTransport> _logger;
    private bool _disposed;

    public IPEndPoint LocalEndPoint { get; }

    /// <summary>
    /// 创建UDP传输，绑定指定端口。若端口被占用则自动尝试随机端口
    /// </summary>
    /// <param name="preferredPort">期望端口，0表示随机</param>
    public UdpTransport(int preferredPort, ILogger<UdpTransport> logger)
    {
        _logger = logger;
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        LocalEndPoint = BindWithRetry(_socket, preferredPort);
        _logger.LogInformation("UDP传输绑定到 {EndPoint}", LocalEndPoint);
    }

    private IPEndPoint BindWithRetry(Socket socket, int preferredPort)
    {
        const int maxRetries = 20;
        var rng = Random.Shared;

        // 先尝试期望端口
        if (preferredPort > 0)
        {
            try
            {
                var ep = new IPEndPoint(IPAddress.Any, preferredPort);
                socket.Bind(ep);
                return (IPEndPoint)socket.LocalEndPoint!;
            }
            catch (SocketException)
            {
                _logger.LogWarning("UDP端口 {Port} 被占用，尝试随机端口...", preferredPort);
            }
        }

        // 尝试随机端口
        for (int i = 0; i < maxRetries; i++)
        {
            var port = rng.Next(1024, 65536);
            try
            {
                var ep = new IPEndPoint(IPAddress.Any, port);
                socket.Bind(ep);
                return (IPEndPoint)socket.LocalEndPoint!;
            }
            catch (SocketException)
            {
                // 端口被占用，继续重试
            }
        }

        // 最终尝试系统分配
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        return (IPEndPoint)socket.LocalEndPoint!;
    }

    /// <inheritdoc />
    public async Task SendAsync(byte[] data, IPEndPoint endpoint, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _socket.SendToAsync(data, SocketFlags.None, endpoint, ct);
        _logger.LogTrace("UDP发送 {Bytes} 字节到 {Endpoint}", data.Length, endpoint);
    }

    /// <inheritdoc />
    public async Task<UdpPacket> ReceiveAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var buffer = new byte[65536];
        var remoteEp = new IPEndPoint(IPAddress.Any, 0) as EndPoint;
        // 保留 SocketException 原样上抛，由 DHT 接收循环按 SocketErrorCode 区分
        // 对端不可达的正常反馈与真正的 socket 故障；取消仍由 ct 传播为取消异常。
        var result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, remoteEp, ct);
        var data = new byte[result.ReceivedBytes];
        Array.Copy(buffer, data, result.ReceivedBytes);

        _logger.LogTrace("UDP收到 {Bytes} 字节 来自 {Endpoint}", data.Length, result.RemoteEndPoint);
        return new UdpPacket
        {
            Data = data,
            RemoteEndPoint = (IPEndPoint)result.RemoteEndPoint
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _socket.Close();
        _socket.Dispose();
        _logger.LogDebug("UDP传输已关闭");
    }
}
