using Microsoft.Extensions.DependencyInjection;
using P2PChat.Core.Abstractions;
using P2PChat.FileTransfer.Services;

namespace P2PChat.FileTransfer.Extensions;

/// <summary>
/// FileTransfer层DI注册扩展
/// </summary>
public static class FileTransferServiceCollectionExtensions
{
    /// <summary>
    /// 注册FileTransfer层服务
    /// </summary>
    public static IServiceCollection AddP2PChatFileTransfer(this IServiceCollection services)
    {
        services.AddSingleton<IFileTransferService, FileTransferService>();
        return services;
    }
}
