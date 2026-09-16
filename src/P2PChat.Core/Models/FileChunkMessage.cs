using MessagePack;

namespace P2PChat.Core.Models;

/// <summary>
/// 文件分块消息
/// </summary>
[MessagePackObject]
public record FileChunkMessage : Message
{
    /// <summary>传输ID</summary>
    [Key(10)]
    public required string TransferId { get; init; }

    /// <summary>分块索引(从0开始)</summary>
    [Key(11)]
    public int ChunkIndex { get; init; }

    /// <summary>分块数据</summary>
    [Key(12)]
    public required byte[] Data { get; init; }
}
