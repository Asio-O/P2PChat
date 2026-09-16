using P2PChat.Core.Models;

namespace P2PChat.Core.Abstractions;

/// <summary>
/// 文件传输服务 — Offer/Accept/Reject流程、分块传输、进度跟踪
/// </summary>
public interface IFileTransferService
{
    /// <summary>
    /// 向指定节点发送文件传输Offer
    /// </summary>
    /// <returns>传输ID</returns>
    Task<string> SendOfferAsync(NodeId recipientId, string filePath, CancellationToken ct = default);

    /// <summary>
    /// 接受文件传输
    /// </summary>
    /// <param name="transferId">传输ID</param>
    /// <param name="saveDirectory">保存目录</param>
    Task AcceptTransferAsync(string transferId, string saveDirectory, CancellationToken ct = default);

    /// <summary>
    /// 拒绝文件传输
    /// </summary>
    Task RejectTransferAsync(string transferId, string? reason = null, CancellationToken ct = default);

    /// <summary>
    /// 处理收到的文件Offer
    /// </summary>
    Task HandleFileMetaAsync(FileMetaMessage meta, ITcpConnection sender, CancellationToken ct = default);

    /// <summary>
    /// 处理文件分块
    /// </summary>
    Task HandleFileChunkAsync(FileChunkMessage chunk, CancellationToken ct = default);

    /// <summary>
    /// 处理文件确认
    /// </summary>
    Task HandleFileAckAsync(FileAckMessage ack, CancellationToken ct = default);

    /// <summary>
    /// 文件传输进度事件流
    /// </summary>
    IAsyncEnumerable<FileTransferProgress> OnProgressChanged { get; }

    /// <summary>
    /// 传入文件Offer事件流 (供UI弹出确认对话框)
    /// </summary>
    IAsyncEnumerable<FileMetaMessage> OnFileOfferReceived { get; }
}
