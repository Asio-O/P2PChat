namespace P2PChat.Core.Models;

/// <summary>
/// 聊天消息事件 (传给UI层)
/// </summary>
public record ChatMessageEvent
{
    /// <summary>消息文本 (已解密)</summary>
    public required string Content { get; init; }

    /// <summary>发送者别名</summary>
    public string SenderAlias { get; init; } = "未知";

    /// <summary>发送者节点ID</summary>
    public required NodeId SenderId { get; init; }

    /// <summary>时间戳</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>会话ID</summary>
    public required string ConversationId { get; init; }

    /// <summary>是否为群聊</summary>
    public bool IsGroup { get; init; }

    /// <summary>是否为本机发送</summary>
    public bool IsOutgoing { get; init; }
}
