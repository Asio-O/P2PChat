using MessagePack;

namespace P2PChat.Core.Models;

/// <summary>
/// 文件传输确认消息
/// </summary>
[MessagePackObject]
public record FileAckMessage : Message
{
    /// <summary>传输ID</summary>
    [Key(10)]
    public required string TransferId { get; init; }

    /// <summary>是否接受</summary>
    [Key(11)]
    public bool Accepted { get; init; }

    /// <summary>拒绝原因</summary>
    [Key(12)]
    public string? ErrorMessage { get; init; }
}
