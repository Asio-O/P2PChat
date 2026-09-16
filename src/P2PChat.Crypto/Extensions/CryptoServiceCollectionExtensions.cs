using Microsoft.Extensions.DependencyInjection;
using P2PChat.Core.Abstractions;
using P2PChat.Crypto.Encryption;
using P2PChat.Crypto.Keys;

namespace P2PChat.Crypto.Extensions;

/// <summary>
/// Crypto层DI注册扩展
/// </summary>
public static class CryptoServiceCollectionExtensions
{
    /// <summary>
    /// 注册Crypto层服务
    /// </summary>
    public static IServiceCollection AddP2PChatCrypto(this IServiceCollection services)
    {
        services.AddSingleton<IEncryptionService, AesGcmEncryptionService>();
        services.AddSingleton<IKeyStore, FileBackedKeyStore>();
        return services;
    }
}
