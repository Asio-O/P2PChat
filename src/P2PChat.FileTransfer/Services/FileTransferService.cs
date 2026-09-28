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
    private readonly IKeyStore _keyStore;
    private readonly ILogger<FileTransferService> _logger;

    // 活跃的传输状态管理
    private readonly ConcurrentDictionary<string, TransferState> _activeTransfers = new();
    private readonly Channel<FileTransferProgress> _progressChannel;
    private readonly Channel<FileMetaMessage> _offerChannel;

    private const int DefaultChunkSize = 65536; // 64KB

    public IAsyncEnumerable<FileTransferProgress> OnProgressChanged => _progressChannel.Reader.ReadAllAsync();
    public IAsyncEnumerable<FileMetaMessage> OnFileOfferReceived => _offerChannel.Reader.ReadAllAsync();

    /// <param name="keyStore">
    /// 本机身份来源，<b>必填</b>。用于让本服务写进载荷的 SenderId 与
    /// <c>MessageRouter</c> 强制写入信封 SenderId 的那份<b>同源</b> ——
    /// 两个独立真相源一旦分叉，载荷会被入站的「载荷/信封 SenderId 一致性」检查整条拒掉。
    /// 刻意不给默认值：漏注入会静默退回到错误的身份来源。
    /// </param>
    public FileTransferService(
        IDhtService dht,
        IMessageRouter router,
        IEncryptionService encryption,
        IKeyStore keyStore,
        ILogger<FileTransferService> logger)
    {
        _dht = dht;
        _router = router;
        _encryption = encryption;
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _logger = logger;
        _progressChannel = Channel.CreateUnbounded<FileTransferProgress>();
        _offerChannel = Channel.CreateUnbounded<FileMetaMessage>();
    }

    /// <inheritdoc />
    public async Task<string> SendOfferAsync(NodeId recipientId, string filePath, CancellationToken ct = default)
        => await SendOfferAsync(recipientId, filePath, DefaultChunkSize, ct);

    /// <summary>
    /// 发送文件传输 Offer，并允许指定分块大小。
    /// <para>
    /// 分块大小由发送方决定，并在 <see cref="FileMetaMessage.ChunkSize"/> 与本地
    /// <see cref="TransferState.ChunkSize"/> 中同源写入；后续发送循环按该值切片。
    /// </para>
    /// </summary>
    public async Task<string> SendOfferAsync(
        NodeId recipientId, string filePath, int chunkSize, CancellationToken ct = default)
    {
        if (!System.IO.File.Exists(filePath))
            throw new FileNotFoundException("文件不存在", filePath);
        if (chunkSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkSize), "分块大小必须为正数");

        var recipient = await _dht.FindNodeAsync(recipientId, ct)
            ?? throw new InvalidOperationException("目标节点未找到");

        var fileInfo = new System.IO.FileInfo(filePath);
        var fileHash = await SHA256.HashDataAsync(
            System.IO.File.OpenRead(filePath), ct);

        var totalChunks = (int)Math.Ceiling((double)fileInfo.Length / chunkSize);
        var transferId = Guid.NewGuid().ToString("N");

        var meta = new FileMetaMessage
        {
            SenderId = GetLocalNodeId(),
            ConversationId = recipientId.ToHexString(),
            TransferId = transferId,
            FileName = fileInfo.Name,
            FileSize = fileInfo.Length,
            FileHash = fileHash,
            ChunkSize = chunkSize,
            TotalChunks = totalChunks
        };

        // 存储传输状态
        // state.ChunkSize 与 meta.ChunkSize 同源（同一表达式），发送循环按 state.ChunkSize 切片；
        // 此前 SendFileChunksAsync 硬编码 DefaultChunkSize 而忽略 state.ChunkSize，
        // 导致任何非默认分块大小实际上仍按 65536 发送 —— 见 2026-09-21-filetransfer-chunksize。
        _activeTransfers[transferId] = new TransferState
        {
            TransferId = transferId,
            FilePath = filePath,
            FileSize = fileInfo.Length,
            ChunkSize = chunkSize,
            TotalChunks = totalChunks,
            IsSender = true,
            RemoteNodeId = recipientId
        };

        await _router.SendAsync(recipient, meta, ct);
        _logger.LogInformation("文件Offer已发送: {File} ({Size}字节, ChunkSize={ChunkSize}) -> {Recipient}",
            fileInfo.Name, fileInfo.Length, chunkSize, recipientId.ToHexString()[..8]);

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

        // 严格按 state.ChunkSize 分配读缓冲与切片；之前硬编码 DefaultChunkSize 意味着
        // 任何非默认分块大小都不会真正生效 —— 见 2026-09-21-filetransfer-chunksize。
        var buffer = new byte[state.ChunkSize];
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

    /// <summary>
    /// 本节点身份 —— <b>与 <c>MessageRouter</c> 强制写入信封 <c>SenderId</c> 的那份同源</b>。
    /// <para>
    /// 这里曾经取自 <c>_dht.LocalNode.NodeId</c>，那是一个**独立的真相源**：<c>MessageRouter</c>
    /// 发送时用的是 <c>_keyStore.GetOrCreateIdentity().NodeId</c>，两者今天相等**只是因为
    /// <c>Program.cs</c> 在装配时用同一个 <c>IKeyStore</c> 单例派生了 DHT 的 LocalNode
    /// 并冻结进那条不可变的 <c>NodeInfo</c> 记录里。
    /// </para>
    /// <para>
    /// 一旦两者分叉（身份文件被重新生成、或任何代码用另一个 keyStore 构造 DHT LocalNode），
    /// 本方法产出的载荷 SenderId 就与信封不自洽，而
    /// <c>MessageRouter.RouteIncomingAsync</c> 的「载荷 SenderId ≠ 信封 SenderId 即拒」
    /// 会把它整条拒掉 —— 用户看到的是「文件传不过去」，日志里只有一条与「文件」毫无关系的
    /// 告警，现场极难归因。这与 <c>KeyExchangeHandler</c> 曾经的载荷/信封不一致是**同一族**缺陷。
    /// </para>
    /// <para>
    /// 刻意<b>在发送时读取</b>而不是缓存到字段：与 <c>MessageRouter</c> 的读取时机对齐，
    /// 身份若真的发生变更，两边会看到同一个值，而不是一方停留在旧值。
    /// </para>
    /// </summary>
    private byte[] GetLocalNodeId()
    {
        return _keyStore.GetOrCreateIdentity().NodeId.ToByteArray();
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
