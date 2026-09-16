namespace P2PChat.Core.Models;

/// <summary>
/// 会话摘要 (用于UI列表显示)
/// </summary>
public record ConversationSummary
{
    /// <summary>会话ID</summary>
    public required string Id { get; init; }

    /// <summary>显示名称</summary>
    public required string DisplayName { get; init; }

    /// <summary>是否为群聊</summary>
    public bool IsGroup { get; init; }

    /// <summary>最后一条消息(预览)</summary>
    public string? LastMessage { get; init; }

    /// <summary>最后活跃时间</summary>
    public DateTime LastActiveTime { get; init; } = DateTime.UtcNow;

    /// <summary>未读消息数</summary>
    public int UnreadCount { get; init; }
}
