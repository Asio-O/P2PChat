using System.Net.Sockets;

namespace P2PChat.Networking.Transport;

/// <summary>
/// 可由 UDP 对端或网络路径正常触发的 socket 错误。
/// </summary>
internal static class UdpSocketErrorClassifier
{
    public static bool IsExpectedPeerError(SocketException exception) =>
        exception.SocketErrorCode is
            SocketError.ConnectionReset or
            SocketError.ConnectionRefused or
            SocketError.HostUnreachable or
            SocketError.NetworkUnreachable or
            SocketError.HostDown or
            SocketError.NetworkDown;
}
