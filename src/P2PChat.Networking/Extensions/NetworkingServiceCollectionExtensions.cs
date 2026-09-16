using Microsoft.Extensions.DependencyInjection;
using P2PChat.Core.Abstractions;
using P2PChat.Networking.Transport;

namespace P2PChat.Networking.Extensions;

/// <summary>
/// Networking层DI注册扩展
/// </summary>
public static class NetworkingServiceCollectionExtensions
{
    /// <summary>
    /// 注册Networking层服务
    /// </summary>
    public static IServiceCollection AddP2PChatNetworking(this IServiceCollection services, int udpPort, int tcpPort)
    {
        services.AddSingleton<IUdpTransport>(sp =>
        {
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<UdpTransport>>();
            return new UdpTransport(udpPort, logger);
        });

        services.AddSingleton<ITcpTransport>(sp =>
        {
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TcpTransport>>();
            var loggerFactory = sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>();
            return new TcpTransport(logger, loggerFactory);
        });

        return services;
    }
}
