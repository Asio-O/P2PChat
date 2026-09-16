using P2PChat.Core.Models;

namespace P2PChat.Core.Abstractions;

/// <summary>
/// 聊天服务 — 私聊消息收发
/// </summary>
public interface IChatService
{
    /// <summary>
    /// 发送私聊文字消息
    /// </summary>
    /// <param name="recipientId">接收者节点ID</param>
    /// <param name="text">消息文本</param>
    Task SendPrivateMessageAsync(NodeId recipientId, string text, CancellationToken ct = default);

    /// <summary>
    /// 接收到的新消息事件流
    /// </summary>
    IAsyncEnumerable<ChatMessageEvent> OnMessageReceived { get; }

    /// <summary>
    /// 获取私聊会话密钥状态
    /// </summary>
    bool HasSessionKey(NodeId peerId);
}
