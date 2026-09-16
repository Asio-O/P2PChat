using MessagePack;

namespace P2PChat.Core.Models;

/// <summary>
/// 文字消息
/// </summary>
[MessagePackObject]
public record TextMessage : Message
{
    /// <summary>消息正文(纯文本)</summary>
    [Key(10)]
    public required string Content { get; init; }

    /// <summary>是否为群聊消息</summary>
    [Key(11)]
    public bool IsGroup { get; init; }
}
