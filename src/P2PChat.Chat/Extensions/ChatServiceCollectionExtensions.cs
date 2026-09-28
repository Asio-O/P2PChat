using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using P2PChat.Core.Abstractions;
using P2PChat.Chat.Handlers;
using P2PChat.Chat.Routing;
using P2PChat.Chat.Services;
using P2PChat.Crypto.Keys;

namespace P2PChat.Chat.Extensions;

/// <summary>
/// Chat层DI注册扩展
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>结构性事实：真实 App（<c>src/P2PChat.App/Program.cs</c>）从不调用本扩展方法。</b>
/// 它把 Chat 层的注册<b>内联复制</b>了一份（<c>Program.cs</c> 的 ConfigureServices 段），
/// 因此<b>下面这些注册在生产路径下全部不生效</b>，只服务于库消费方与未来的重构。
/// </para>
/// <para>
/// <b>改注册时必须两处同步核对</b>，只改一处就会出问题，而且
/// <b><c>dotnet build</c> 抓不到</b>（DI 解析失败是运行期的，编译与单测都可能全绿）：
/// 少注册一个接口 → App 启动时抛 <c>Unable to resolve service</c>；
/// 把同一个实现拆成两条 <c>AddSingleton&lt;IX, Impl&gt;(); AddSingleton&lt;IY, Impl&gt;();</c> →
/// 容器各建一个实例，「投递进 A、读取 B」，症状是功能静默失效。
/// </para>
/// </remarks>
public static class ChatServiceCollectionExtensions
{
    /// <summary>
    /// 注册Chat层服务
    /// </summary>
    public static IServiceCollection AddP2PChatChat(this IServiceCollection services)
    {
        // 重放防护（防「录下合法信封反复重放」）。见 2026-09-21-message-signing 的遗留缺口。
        // 用 TryAddSingleton 而不是 AddSingleton：本条是**默认值**（1 小时时间窗），
        // 调用方若要按配置覆盖，在 AddP2PChatChat() 之后再注册一条即可（后注册者胜）。
        // MessageRouter 的 replayGuard 形参是必填的，漏注册会编译不过——刻意不给默认值，
        // 避免「忘记注入」静默等于「关闭防护」。
        // （权威注册在 Program.cs，那条按 P2PChat:ReplayMaxAgeSeconds 解析；本条只是默认值。）
        services.TryAddSingleton<IReplayGuard>(sp => new MessageReplayGuard(
            sp.GetRequiredService<ILogger<MessageReplayGuard>>()));

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

        // 聊天服务 —— 整条聊天事件流的唯一持有者。
        //
        // ⚠️ 关键：IChatService 与 IChatEventPublisher 必须指向**同一个** ChatService 实例。
        // 若写成两条 AddSingleton<IChatService, ChatService>() + AddSingleton<IChatEventPublisher, ChatService>()，
        // 容器会各建一个实例：handler 把事件投递进 A 的通道，UI 读 B 的通道 —— 于是
        // 「收到的消息永远不显示」的缺陷原封不动地回来了，而且更难发现（所有单测仍然全绿）。
        //
        // 这里刻意用 AddSingleton 而**不是** TryAddSingleton：本方法若被调用，就应当是权威注册。
        // 用 TryAdd 的话，将来有人把 Program.cs 改成调用 AddP2PChatChat()，而 Program.cs 里
        // 那条内联的 AddSingleton<IChatEventPublisher> 已经先注册过了 —— TryAdd 会静默跳过，
        // 于是「程序员的本意」与「实际生效的」悄悄分叉，正是最难查的那类问题。
        // （另一个方向的隐患同理：若这两条被拆成两条 AddSingleton<IX, ChatService>()，就会出两个实例。）
        //
        // ⚠️ 提醒：本扩展方法在真实 App 路径下**不生效**（Program.cs 内联复制了全部注册），
        //    Program.cs 那边的三条注册才是权威的，两处必须同步维护。详见类型级 remarks。
        services.AddSingleton<ChatService>();
        services.AddSingleton<IChatService>(sp => sp.GetRequiredService<ChatService>());
        services.AddSingleton<IChatEventPublisher>(sp => sp.GetRequiredService<ChatService>());

        services.AddSingleton<IGroupChatService, GroupChatService>();
        services.AddSingleton<IContactService, ContactService>();

        return services;
    }
}
