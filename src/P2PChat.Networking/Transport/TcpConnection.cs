using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using P2PChat.Core.Abstractions;

namespace P2PChat.Networking.Transport;

/// <summary>
/// TCP连接实现 — 单个端到端通道
/// 成帧协议: [4字节BigEndian消息总长度] + [负载]
/// 使用 System.IO.Pipelines 高性能异步I/O
/// </summary>
public class TcpConnection : ITcpConnection
{
    private readonly Socket _socket;
    private readonly ILogger<TcpConnection> _logger;
    private readonly Pipe _pipe;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private bool _disposed;

    public Guid ConnectionId { get; } = Guid.NewGuid();
    public IPEndPoint RemoteEndPoint { get; }
    public bool IsConnected => !_disposed && _socket.Connected;

    public TcpConnection(Socket socket, ILogger<TcpConnection> logger)
    {
        _socket = socket;
        _logger = logger;
        _pipe = new Pipe();
        RemoteEndPoint = (IPEndPoint)_socket.RemoteEndPoint!;

        // 启动后台读取管道
        _ = FillPipeAsync();
    }

    /// <summary>
    /// 后台任务：从Socket读取数据填充到Pipe
    /// </summary>
    private async Task FillPipeAsync()
    {
        const int minimumBufferSize = 512;
        while (true)
        {
            try
            {
                var memory = _pipe.Writer.GetMemory(minimumBufferSize);
                var bytesRead = await _socket.ReceiveAsync(memory, SocketFlags.None);
                if (bytesRead == 0)
                    break; // 连接关闭

                _pipe.Writer.Advance(bytesRead);
            }
            catch (SocketException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            var flushResult = await _pipe.Writer.FlushAsync();
            if (flushResult.IsCompleted)
                break;
        }

        await _pipe.Writer.CompleteAsync();
        _logger.LogDebug("TCP管道写入完成: {RemoteEndPoint}", RemoteEndPoint);
    }

    /// <inheritdoc />
    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _sendLock.WaitAsync(ct);
        try
        {
            // 长度前缀: 4字节BigEndian
            var length = data.Length;
            var header = new byte[4];
            header[0] = (byte)(length >> 24);
            header[1] = (byte)(length >> 16);
            header[2] = (byte)(length >> 8);
            header[3] = (byte)length;

            // 发送头部
            await _socket.SendAsync(header, SocketFlags.None, ct);
            // 发送负载
            await _socket.SendAsync(data, SocketFlags.None, ct);

            _logger.LogTrace("TCP发送 {Bytes} 字节到 {Endpoint}", length, RemoteEndPoint);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ReadOnlyMemory<byte>> ReceiveMessageAsync(CancellationToken ct = default)
    {
        // 尝试读取4字节长度头部
        while (true)
        {
            var readResult = await _pipe.Reader.ReadAsync(ct);
            var buffer = readResult.Buffer;

            if (buffer.Length < 4)
            {
                _pipe.Reader.AdvanceTo(buffer.Start, buffer.End);
                if (readResult.IsCompleted)
                    throw new InvalidOperationException("连接已关闭，无法读取完整消息头部");
                continue;
            }

            // 解析长度 (BigEndian) — ReadOnlySequence不支持Span，拷贝到栈上
            Span<byte> headerSpan = stackalloc byte[4];
            buffer.Slice(0, 4).CopyTo(headerSpan);
            int messageLength = (headerSpan[0] << 24) |
                                (headerSpan[1] << 16) |
                                (headerSpan[2] << 8) |
                                headerSpan[3];

            if (messageLength < 0 || messageLength > 100 * 1024 * 1024) // 最大100MB
            {
                throw new InvalidOperationException($"非法消息长度: {messageLength}");
            }

            var totalFrameLength = 4 + messageLength;
            if (buffer.Length < totalFrameLength)
            {
                _pipe.Reader.AdvanceTo(buffer.Start, buffer.End);
                if (readResult.IsCompleted)
                    throw new InvalidOperationException("连接已关闭，消息不完整");
                continue;
            }

            // 提取负载
            var payload = buffer.Slice(4, messageLength);
            var result = payload.ToArray();
            _pipe.Reader.AdvanceTo(buffer.GetPosition(totalFrameLength));
            return result;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _sendLock.Dispose();
        _pipe.Writer.Complete();
        _pipe.Reader.Complete();
        _socket.Close();
        _socket.Dispose();
        _logger.LogDebug("TCP连接已关闭: {RemoteEndPoint}", RemoteEndPoint);
        await Task.CompletedTask;
    }
}
