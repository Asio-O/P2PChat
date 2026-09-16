namespace P2PChat.Core.Models;

/// <summary>
/// 文件传输进度
/// </summary>
public record FileTransferProgress
{
    /// <summary>传输ID</summary>
    public required string TransferId { get; init; }

    /// <summary>文件名</summary>
    public required string FileName { get; init; }

    /// <summary>远程节点ID</summary>
    public required NodeId RemoteNodeId { get; init; }

    /// <summary>是否为发送方</summary>
    public bool IsSender { get; init; }

    /// <summary>总分块数</summary>
    public int TotalChunks { get; init; }

    /// <summary>已完成分块数</summary>
    public int CompletedChunks { get; init; }

    /// <summary>文件总大小</summary>
    public long TotalSize { get; init; }

    /// <summary>已传输字节数</summary>
    public long BytesTransferred { get; init; }

    /// <summary>传输状态: connecting/transferring/completed/error</summary>
    public required string Status { get; init; }

    /// <summary>错误信息</summary>
    public string? ErrorMessage { get; init; }
}
