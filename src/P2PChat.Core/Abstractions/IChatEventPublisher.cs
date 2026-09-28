using P2PChat.Core.Models;

namespace P2PChat.Core.Abstractions;

/// <summary>
/// 聊天事件发布器 —— <b>整条聊天消息事件流的唯一出口</b>。
/// <para>
/// 为什么要有这个接口（见 REPAIR-PLAN B3）：消息处理器此前各自持有<b>私有的</b>
/// <c>Channel&lt;ChatMessageEvent&gt;</c>，而 UI 读的是 <see cref="IChatService.OnMessageReceived"/>
/// （另一个通道）。于是「handler 确实收到并解密了消息」与「用户看到这条消息」之间没有任何连线：
/// <c>PublishMessageAsync</c> 在 <c>src/**</c> 里零调用者。净效果是
/// <b>能看到自己发出去的消息，永远看不到任何人发来的消息</b>，私聊与群聊皆然。
/// </para>
/// <para>
/// 为什么不直接让 handler 依赖 <see cref="IChatService"/>：那是「协议处理器反向依赖业务服务」，
/// 会把 handler 和私聊的发送/密钥协商逻辑绑死。用单方法接口把「事件总线」这件事显式化，
/// handler 只知道「往事件流里投递」，不知道谁在读。
/// </para>
/// <para>
/// <b>唯一来源铁律</b>：任何组件都不得再自建 <c>Channel&lt;ChatMessageEvent&gt;</c>。
/// 出现第二个通道，就是这个缺陷的复发。
/// </para>
/// </summary>
public interface IChatEventPublisher
{
    /// <summary>
    /// 把一条聊天事件投递到事件流（供 UI 消费）。
    /// </summary>
    /// <param name="chatEvent">已解密、已还原为明文的聊天事件。</param>
    /// <param name="ct">取消标记。</param>
    Task PublishAsync(ChatMessageEvent chatEvent, CancellationToken ct = default);
}
