using Microsoft.Extensions.DependencyInjection;
using P2PChat.Core.Abstractions;
using P2PChat.Chat.Handlers;
using P2PChat.Chat.Routing;
using P2PChat.Chat.Services;
using P2PChat.Crypto.Keys;

namespace P2PChat.Chat.Extensions;

/// <summary>
/// Chat层DI注册扩展
/// </summary>
public static class ChatServiceCollectionExtensions
{
    /// <summary>
    /// 注册Chat层服务
    /// </summary>
    public static IServiceCollection AddP2PChatChat(this IServiceCollection services)
    {
        // 消息路由器 (单例)
        services.AddSingleton<IMessageRouter, MessageRouter>();

        // 群组元数据持久化（~/.p2pc/groups.json）—— 见 2026-09-21-group-metadata-persistence。
        services.AddSingleton<IGroupMetadataStore, FileBackedGroupMetadataStore>();

        // 消息处理器
        services.AddSingleton<PrivateMessageHandler>();
        services.AddSingleton<GroupMessageHandler>();
        services.AddSingleton<KeyExchangeHandler>();
        services.AddSingleton<GroupInviteHandler>();
        services.AddSingleton<GroupNotifyHandler>();

        // 聊天服务
        services.AddSingleton<IChatService, ChatService>();
        services.AddSingleton<IGroupChatService, GroupChatService>();
        services.AddSingleton<IContactService, ContactService>();

        return services;
    }
}
