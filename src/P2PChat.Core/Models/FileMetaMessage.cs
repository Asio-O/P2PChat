using MessagePack;

namespace P2PChat.Core.Models;

/// <summary>
/// 文件传输元数据消息 (发送方Offer)
/// </summary>
[MessagePackObject]
public record FileMetaMessage : Message
{
    /// <summary>传输ID</summary>
    [Key(10)]
    public required string TransferId { get; init; }

    /// <summary>文件名</summary>
    [Key(11)]
    public required string FileName { get; init; }

    /// <summary>文件大小(字节)</summary>
    [Key(12)]
    public long FileSize { get; init; }

    /// <summary>文件SHA-256哈希</summary>
    [Key(13)]
    public required byte[] FileHash { get; init; }

    /// <summary>分块大小(字节)</summary>
    /// <remarks>set 而非 init：init 属性 + 初始化器会在反序列化时被重置为 default(MsgPack017)。</remarks>
    [Key(14)]
    public int ChunkSize { get; set; } = 65536;

    /// <summary>总分块数</summary>
    [Key(15)]
    public int TotalChunks { get; init; }
}
