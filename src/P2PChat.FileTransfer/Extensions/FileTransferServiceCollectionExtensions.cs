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
    /// <remarks>
    /// <c>FileTransferService</c> 需要 <c>IKeyStore</c>（本机身份来源，必须与
    /// <c>MessageRouter</c> 写入信封 SenderId 的那份<b>同源</b>）。
    /// 这里用 <c>AddSingleton&lt;TService, TImpl&gt;()</c> 的<b>容器激活</b>形式，
    /// 构造参数由容器解析，因此<b>只要宿主注册了 <c>IKeyStore</c> 就无需改动本文件</b>；
    /// 漏注册会在解析时抛异常而不是静默降级（正因为它被设计成必填构造参数）。
    /// </remarks>
    public static IServiceCollection AddP2PChatFileTransfer(this IServiceCollection services)
    {
        services.AddSingleton<IFileTransferService, FileTransferService>();
        return services;
    }
}
