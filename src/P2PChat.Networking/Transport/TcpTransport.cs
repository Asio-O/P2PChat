using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using P2PChat.Core.Abstractions;

namespace P2PChat.Networking.Transport;

/// <summary>
/// TCP传输层实现 — 用于聊天和文件传输
/// 使用长度前缀成帧: 4字节BigEndian长度 + 负载
/// </summary>
public class TcpTransport : ITcpTransport
{
    private readonly ILogger<TcpTransport> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private Socket? _listener;
    private CancellationTokenSource? _listenCts;
    private readonly Channel<ITcpConnection> _incomingChannel;
    private bool _disposed;

    public int ListenPort { get; private set; }
    public IPEndPoint? LocalEndPoint { get; private set; }
    public IAsyncEnumerable<ITcpConnection> IncomingConnections => _incomingChannel.Reader.ReadAllAsync();

    public TcpTransport(ILogger<TcpTransport> logger, ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _incomingChannel = Channel.CreateBounded<ITcpConnection>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    /// <inheritdoc />
    public Task StartListeningAsync(int preferredPort, CancellationToken ct = default)
    {
        _listenCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var ep = BindWithRetry(_listener, preferredPort);
        _listener.Listen(100);
        LocalEndPoint = ep;
        ListenPort = ep.Port;
        _logger.LogInformation("TCP监听已启动: {EndPoint}", LocalEndPoint);

        // 后台接受连接
        _ = AcceptLoopAsync(_listenCts.Token);

        return Task.CompletedTask;
    }

    private IPEndPoint BindWithRetry(Socket socket, int preferredPort)
    {
        const int maxRetries = 20;
        var rng = Random.Shared;

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
                _logger.LogWarning("TCP端口 {Port} 被占用，尝试随机端口...", preferredPort);
            }
        }

        for (int i = 0; i < maxRetries; i++)
        {
            var port = rng.Next(1024, 65536);
            try
            {
                var ep = new IPEndPoint(IPAddress.Any, port);
                socket.Bind(ep);
                return (IPEndPoint)socket.LocalEndPoint!;
            }
            catch (SocketException) { }
        }

        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        return (IPEndPoint)socket.LocalEndPoint!;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            try
            {
                var socket = await _listener.AcceptAsync(ct);
                var connection = new TcpConnection(
                    socket,
                    _loggerFactory.CreateLogger<TcpConnection>()
                );
                _logger.LogDebug("TCP入站连接: {EndPoint}", connection.RemoteEndPoint);
                await _incomingChannel.Writer.WriteAsync(connection, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TCP监听异常");
                if (!ct.IsCancellationRequested)
                    await Task.Delay(1000, ct);
            }
        }
    }

    /// <inheritdoc />
    public async Task<ITcpConnection> ConnectAsync(IPEndPoint endpoint, CancellationToken ct = default)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(endpoint, ct);
        var connection = new TcpConnection(
            socket,
            _loggerFactory.CreateLogger<TcpConnection>()
        );
        _logger.LogDebug("TCP出站连接: {EndPoint}", connection.RemoteEndPoint);
        return connection;
    }

    /// <inheritdoc />
    public async Task<ITcpConnection> AcceptAsync(CancellationToken ct = default)
    {
        return await _incomingChannel.Reader.ReadAsync(ct);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _listenCts?.Cancel();
        _listener?.Close();
        _listener?.Dispose();
        _listenCts?.Dispose();
        _logger.LogDebug("TCP传输已关闭");
    }
}
