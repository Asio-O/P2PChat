using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using P2PChat.Core.Abstractions;
using P2PChat.Core.Models;

namespace P2PChat.FileTransfer.Services;

/// <summary>
/// 文件传输服务 — Offer/Accept/Reject、分块传输、进度跟踪
/// </summary>
public class FileTransferService : IFileTransferService
{
    private readonly IDhtService _dht;
    private readonly IMessageRouter _router;
    private readonly IEncryptionService _encryption;
    private readonly ILogger<FileTransferService> _logger;

    // 活跃的传输状态管理
    private readonly ConcurrentDictionary<string, TransferState> _activeTransfers = new();
    private readonly Channel<FileTransferProgress> _progressChannel;
    private readonly Channel<FileMetaMessage> _offerChannel;

    private const int DefaultChunkSize = 65536; // 64KB

    public IAsyncEnumerable<FileTransferProgress> OnProgressChanged => _progressChannel.Reader.ReadAllAsync();
    public IAsyncEnumerable<FileMetaMessage> OnFileOfferReceived => _offerChannel.Reader.ReadAllAsync();

    public FileTransferService(
        IDhtService dht,
        IMessageRouter router,
        IEncryptionService encryption,
        ILogger<FileTransferService> logger)
    {
        _dht = dht;
        _router = router;
        _encryption = encryption;
        _logger = logger;
        _progressChannel = Channel.CreateUnbounded<FileTransferProgress>();
        _offerChannel = Channel.CreateUnbounded<FileMetaMessage>();
    }

    /// <inheritdoc />
    public async Task<string> SendOfferAsync(NodeId recipientId, string filePath, CancellationToken ct = default)
    {
        if (!System.IO.File.Exists(filePath))
            throw new FileNotFoundException("文件不存在", filePath);

        var recipient = await _dht.FindNodeAsync(recipientId, ct)
            ?? throw new InvalidOperationException("目标节点未找到");

        var fileInfo = new System.IO.FileInfo(filePath);
        var fileHash = await SHA256.HashDataAsync(
            System.IO.File.OpenRead(filePath), ct);

        var totalChunks = (int)Math.Ceiling((double)fileInfo.Length / DefaultChunkSize);
        var transferId = Guid.NewGuid().ToString("N");

        var meta = new FileMetaMessage
        {
            SenderId = GetLocalNodeId(),
            ConversationId = recipientId.ToHexString(),
            TransferId = transferId,
            FileName = fileInfo.Name,
            FileSize = fileInfo.Length,
            FileHash = fileHash,
            ChunkSize = DefaultChunkSize,
            TotalChunks = totalChunks
        };

        // 存储传输状态
        _activeTransfers[transferId] = new TransferState
        {
            TransferId = transferId,
            FilePath = filePath,
            FileSize = fileInfo.Length,
            TotalChunks = totalChunks,
            IsSender = true,
            RemoteNodeId = recipientId
        };

        await _router.SendAsync(recipient, meta, ct);
        _logger.LogInformation("文件Offer已发送: {File} ({Size}字节) -> {Recipient}",
            fileInfo.Name, fileInfo.Length, recipientId.ToHexString()[..8]);

        return transferId;
    }

    /// <inheritdoc />
    public async Task AcceptTransferAsync(string transferId, string saveDirectory, CancellationToken ct = default)
    {
        if (!_activeTransfers.TryGetValue(transferId, out var state))
            throw new InvalidOperationException("传输不存在: " + transferId);

        state.SaveDirectory = saveDirectory;
        state.Status = TransferStatus.Transferring;

        var fileName = state.FileName ?? "received_file";
        state.SavePath = Path.Combine(saveDirectory, fileName);

        // 确保目录存在
        Directory.CreateDirectory(saveDirectory);

        // 预分配文件空间
        await using var fs = new FileStream(state.SavePath, FileMode.Create, FileAccess.Write);
        fs.SetLength(state.FileSize);

        // 发送接受确认
        var recipientId = state.RemoteNodeId;
        var recipient = await _dht.FindNodeAsync(recipientId, ct);
        if (recipient != null)
        {
            var ack = new FileAckMessage
            {
                SenderId = GetLocalNodeId(),
                ConversationId = recipientId.ToHexString(),
                TransferId = transferId,
                Accepted = true
            };
            await _router.SendAsync(recipient, ack, ct);
        }

        _logger.LogInformation("文件传输已接受: {TransferId} -> {Path}", transferId, state.SavePath);
    }

    /// <inheritdoc />
    public async Task RejectTransferAsync(string transferId, string? reason = null, CancellationToken ct = default)
    {
        if (!_activeTransfers.TryGetValue(transferId, out var state))
            return;

        state.Status = TransferStatus.Error;
        var recipientId = state.RemoteNodeId;
        var recipient = await _dht.FindNodeAsync(recipientId, ct);
        if (recipient != null)
        {
            var ack = new FileAckMessage
            {
                SenderId = GetLocalNodeId(),
                ConversationId = recipientId.ToHexString(),
                TransferId = transferId,
                Accepted = false,
                ErrorMessage = reason ?? "用户拒绝"
            };
            await _router.SendAsync(recipient, ack, ct);
        }

        _logger.LogInformation("文件传输已拒绝: {TransferId}", transferId);
    }

    /// <inheritdoc />
    public async Task HandleFileMetaAsync(FileMetaMessage meta, ITcpConnection sender, CancellationToken ct = default)
    {
        var transferId = meta.TransferId;
        var senderNodeId = new NodeId(meta.SenderId);
        var chunkSize = meta.ChunkSize > 0 ? meta.ChunkSize : DefaultChunkSize;

        if (meta.ChunkSize <= 0)
        {
            _logger.LogWarning("收到无效的文件分块大小 {ChunkSize}: {TransferId}，回退到默认值 {DefaultChunkSize}",
                meta.ChunkSize, transferId, DefaultChunkSize);
        }

        _activeTransfers[transferId] = new TransferState
        {
            TransferId = transferId,
            FileName = meta.FileName,
            FileSize = meta.FileSize,
            FileHash = meta.FileHash,
            ChunkSize = chunkSize,
            TotalChunks = meta.TotalChunks,
            IsSender = false,
            RemoteNodeId = senderNodeId,
            Status = TransferStatus.WaitingAccept
        };

        _logger.LogInformation("收到文件Offer: {File} ({Size}字节) 来自 {Sender}",
            meta.FileName, meta.FileSize, senderNodeId.ToHexString()[..8]);

        // 通知UI层弹出确认对话框
        await _offerChannel.Writer.WriteAsync(meta, ct);
    }

    /// <inheritdoc />
    public async Task HandleFileChunkAsync(FileChunkMessage chunk, CancellationToken ct = default)
    {
        if (!_activeTransfers.TryGetValue(chunk.TransferId, out var state))
            return;

        if (state.SavePath == null) return;

        // 写入分块数据到指定偏移位置
        var offset = (long)chunk.ChunkIndex * state.ChunkSize;
        await using (var fs = new FileStream(state.SavePath, FileMode.Open, FileAccess.Write))
        {
            fs.Seek(offset, SeekOrigin.Begin);
            await fs.WriteAsync(chunk.Data, ct);
        }

        state.CompletedChunks++;

        // 报告进度
        await ReportProgressAsync(state);

        // 检查是否完成
        if (state.CompletedChunks >= state.TotalChunks)
        {
            await VerifyAndCompleteAsync(state);
        }
    }

    /// <inheritdoc />
    public async Task HandleFileAckAsync(FileAckMessage ack, CancellationToken ct = default)
    {
        if (!_activeTransfers.TryGetValue(ack.TransferId, out var state))
            return;

        if (ack.Accepted)
        {
            state.Status = TransferStatus.Transferring;
            // 开始分块发送
            _ = SendFileChunksAsync(state, ct);
        }
        else
        {
            state.Status = TransferStatus.Error;
            _logger.LogInformation("文件传输被拒绝: {TransferId}, 原因: {Reason}",
                ack.TransferId, ack.ErrorMessage);
        }

        await ReportProgressAsync(state);
    }

    #region 内部方法

    private async Task SendFileChunksAsync(TransferState state, CancellationToken ct)
    {
        var recipientId = state.RemoteNodeId;
        var recipient = await _dht.FindNodeAsync(recipientId, ct);
        if (recipient == null) return;

        var buffer = new byte[DefaultChunkSize];
        await using var fs = System.IO.File.OpenRead(state.FilePath!);
        int bytesRead;
        int chunkIndex = 0;

        while ((bytesRead = await fs.ReadAsync(buffer, ct)) > 0)
        {
            var chunkData = new byte[bytesRead];
            Array.Copy(buffer, chunkData, bytesRead);

            var chunk = new FileChunkMessage
            {
                SenderId = GetLocalNodeId(),
                ConversationId = recipientId.ToHexString(),
                TransferId = state.TransferId,
                ChunkIndex = chunkIndex,
                Data = chunkData
            };

            await _router.SendAsync(recipient, chunk, ct);
            chunkIndex++;
            state.CompletedChunks = chunkIndex;

            await ReportProgressAsync(state);

            // 简单的流控: 每发送10个分块等待一下
            if (chunkIndex % 10 == 0)
                await Task.Delay(10, ct);
        }

        _logger.LogInformation("文件发送完成: {TransferId}, {Chunks} 个分块",
            state.TransferId, chunkIndex);
    }

    private async Task VerifyAndCompleteAsync(TransferState state)
    {
        if (state.SavePath == null) return;

        var actualHash = await SHA256.HashDataAsync(
            System.IO.File.OpenRead(state.SavePath));

        if (state.FileHash != null && !actualHash.AsSpan().SequenceEqual(state.FileHash))
        {
            state.Status = TransferStatus.Error;
            _logger.LogWarning("文件校验失败: {TransferId}", state.TransferId);
        }
        else
        {
            state.Status = TransferStatus.Completed;
            _logger.LogInformation("文件传输完成并校验通过: {TransferId}", state.TransferId);
        }

        await ReportProgressAsync(state);
    }

    private async Task ReportProgressAsync(TransferState state)
    {
        var progress = new FileTransferProgress
        {
            TransferId = state.TransferId,
            FileName = state.FileName ?? "unknown",
            RemoteNodeId = state.RemoteNodeId,
            IsSender = state.IsSender,
            TotalChunks = state.TotalChunks,
            CompletedChunks = state.CompletedChunks,
            TotalSize = state.FileSize,
            BytesTransferred = (long)state.CompletedChunks * state.ChunkSize,
            Status = state.Status switch
            {
                TransferStatus.WaitingAccept => "connecting",
                TransferStatus.Transferring => "transferring",
                TransferStatus.Completed => "completed",
                TransferStatus.Error => "error",
                _ => "unknown"
            }
        };

        await _progressChannel.Writer.WriteAsync(progress);
    }

    private byte[] GetLocalNodeId()
    {
        return _dht.LocalNode.NodeId.ToByteArray();
    }

    #endregion
}

/// <summary>
/// 传输状态
/// </summary>
public class TransferState
{
    public required string TransferId { get; init; }
    public string? FilePath { get; init; }
    public string? FileName { get; init; }
    public long FileSize { get; init; }
    public byte[]? FileHash { get; init; }
    public int ChunkSize { get; set; } = 65536;
    public int TotalChunks { get; init; }
    public int CompletedChunks { get; set; }
    public bool IsSender { get; init; }
    public required NodeId RemoteNodeId { get; init; }
    public string? SaveDirectory { get; set; }
    public string? SavePath { get; set; }
    public TransferStatus Status { get; set; } = TransferStatus.WaitingAccept;
}

public enum TransferStatus
{
    WaitingAccept,
    Transferring,
    Completed,
    Error
}
