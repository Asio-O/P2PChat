using P2PChat.Core.Models;

namespace P2PChat.Core.Abstractions;

/// <summary>
/// 文件传输服务 — Offer/Accept/Reject流程、分块传输、进度跟踪
/// </summary>
public interface IFileTransferService
{
    /// <summary>
    /// 向指定节点发送文件传输Offer，使用默认分块大小。
    /// </summary>
    /// <returns>传输ID</returns>
    Task<string> SendOfferAsync(NodeId recipientId, string filePath, CancellationToken ct = default);

    /// <summary>
    /// 向指定节点发送文件传输Offer，并允许调用方指定分块大小。
    /// <para>
    /// 分块大小由发送方决定，并在 <see cref="FileMetaMessage.ChunkSize"/> 与本地
    /// <see cref="FileTransfer.Services.TransferState.ChunkSize"/> 中同源写入；
    /// 后续发送循环按该值分配读缓冲与切片。
    /// </para>
    /// </summary>
    /// <param name="recipientId">收件人节点ID</param>
    /// <param name="filePath">待发送的本地文件路径</param>
    /// <param name="chunkSize">分块大小（字节），必须为正数</param>
    /// <returns>传输ID</returns>
    Task<string> SendOfferAsync(NodeId recipientId, string filePath, int chunkSize, CancellationToken ct = default);

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
