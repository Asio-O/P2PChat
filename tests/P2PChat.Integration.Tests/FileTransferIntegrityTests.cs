using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using P2PChat.Core.Models;
using P2PChat.Crypto.Encryption;
using P2PChat.FileTransfer.Services;
using P2PChat.Integration.Tests.Support;
using Shouldly;

namespace P2PChat.Integration.Tests;

/// <summary>
/// 文件分块传输完整性（含 SHA-256 校验）验证。
/// 接收侧走真实 FileTransferService：HandleFileMetaAsync → AcceptTransferAsync → HandleFileChunkAsync × N → 校验。
/// </summary>
public class FileTransferIntegrityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "p2pchat-filetest-" + Guid.NewGuid().ToString("N"));

    public FileTransferIntegrityTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private const int ChunkSize = 65536;

    private static byte[] SenderId() => Enumerable.Range(0, 20).Select(i => (byte)(i + 1)).ToArray();

    private (FileTransferService Service, FakeDhtService Dht, RecordingMessageRouter Router, NodeInfo Local, NodeInfo Remote)
        BuildReceiver()
    {
        var encryption = new AesGcmEncryptionService(NullLogger<AesGcmEncryptionService>.Instance);
        var local = new NodeInfo
        {
            NodeId = NodeId.CreateRandom(),
            EndPoint = new IPEndPoint(IPAddress.Loopback, 45001),
            PublicKey = new byte[32]
        };
        var remote = new NodeInfo
        {
            // 必须与 meta.SenderId 推导出的 NodeId 一致，AcceptTransferAsync/RejectTransferAsync
            // 才能通过 _dht.FindNodeAsync(state.RemoteNodeId) 找到对端
            NodeId = new NodeId(SenderId()),
            EndPoint = new IPEndPoint(IPAddress.Loopback, 45002),
            PublicKey = new byte[32]
        };
        var dht = new FakeDhtService(local);
        var router = new RecordingMessageRouter();
        var service = new FileTransferService(
            dht, router, encryption, NullLogger<FileTransferService>.Instance);
        return (service, dht, router, local, remote);
    }

    /// <summary>按固定 ChunkSize 分块并把所有分块投递给接收方（模拟收到 FileMeta 后逐块到达）。</summary>
    private static async Task SendAllChunksAsync(
        FileTransferService receiver, string transferId, byte[] data, byte[]? senderId = null)
    {
        var total = (int)Math.Ceiling((double)data.Length / ChunkSize);
        for (var i = 0; i < total; i++)
        {
            var take = Math.Min(ChunkSize, data.Length - i * ChunkSize);
            var chunk = new byte[take];
            Array.Copy(data, i * ChunkSize, chunk, 0, take);

            await receiver.HandleFileChunkAsync(new FileChunkMessage
            {
                SenderId = senderId ?? SenderId(),
                ConversationId = "conv",
                TransferId = transferId,
                ChunkIndex = i,
                Data = chunk
            });
        }
    }

    private static async Task<FileTransferProgress> WaitTerminalAsync(
        FileTransferService service, int timeoutMs = 15000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await foreach (var progress in service.OnProgressChanged.WithCancellation(cts.Token))
            {
                if (progress.Status is "completed" or "error") return progress;
            }
        }
        catch (OperationCanceledException) { }
        throw new TimeoutException($"等待文件传输终态超时 ({timeoutMs}ms)");
    }

    [Fact]
    public async Task 分块传输_接收文件与源文件字节完全一致_校验通过()
    {
        var (receiver, _, _, _, _) = BuildReceiver();
        var data = new byte[200_000];               // 4 个分块（含最后一个不完整块）
        Random.Shared.NextBytes(data);
        var hash = SHA256.HashData(data);
        const string transferId = "t-roundtrip";
        var saveDir = Path.Combine(_root, "recv");

        await receiver.HandleFileMetaAsync(new FileMetaMessage
        {
            SenderId = SenderId(), ConversationId = "conv", TransferId = transferId,
            FileName = "source.bin", FileSize = data.Length, FileHash = hash,
            ChunkSize = ChunkSize, TotalChunks = 4
        }, new FakeTcpConnection());

        await receiver.AcceptTransferAsync(transferId, saveDir);
        await SendAllChunksAsync(receiver, transferId, data);

        var terminal = await WaitTerminalAsync(receiver);
        terminal.Status.ShouldBe("completed");
        terminal.CompletedChunks.ShouldBe(4);
        terminal.TotalChunks.ShouldBe(4);
        terminal.TotalSize.ShouldBe(data.Length);

        var saved = await File.ReadAllBytesAsync(Path.Combine(saveDir, "source.bin"));
        saved.ShouldBe(data);
        SHA256.HashData(saved).ShouldBe(hash);
    }

    [Fact]
    public async Task 分块传输_单分块小文件_内容一致()
    {
        var (receiver, _, _, _, _) = BuildReceiver();
        var data = "小文件内容：P2PChat 端到端测试 🚀"u8.ToArray();
        var hash = SHA256.HashData(data);
        const string transferId = "t-small";
        var saveDir = Path.Combine(_root, "recv-small");

        await receiver.HandleFileMetaAsync(new FileMetaMessage
        {
            SenderId = SenderId(), ConversationId = "conv", TransferId = transferId,
            FileName = "small.txt", FileSize = data.Length, FileHash = hash,
            ChunkSize = ChunkSize, TotalChunks = 1
        }, new FakeTcpConnection());

        await receiver.AcceptTransferAsync(transferId, saveDir);
        await SendAllChunksAsync(receiver, transferId, data);

        (await WaitTerminalAsync(receiver)).Status.ShouldBe("completed");
        (await File.ReadAllBytesAsync(Path.Combine(saveDir, "small.txt"))).ShouldBe(data);
    }

    [Fact]
    public async Task 分块传输_元数据哈希与实际文件不匹配_校验失败并进入error状态()
    {
        var (receiver, _, _, _, _) = BuildReceiver();
        var data = new byte[100_000];
        Random.Shared.NextBytes(data);
        const string transferId = "t-badhash";
        var saveDir = Path.Combine(_root, "recv-badhash");
        var wrongHash = SHA256.HashData("这是与文件内容无关的错误哈希"u8.ToArray());

        await receiver.HandleFileMetaAsync(new FileMetaMessage
        {
            SenderId = SenderId(), ConversationId = "conv", TransferId = transferId,
            FileName = "bad.bin", FileSize = data.Length, FileHash = wrongHash,
            ChunkSize = ChunkSize, TotalChunks = 2
        }, new FakeTcpConnection());

        await receiver.AcceptTransferAsync(transferId, saveDir);
        await SendAllChunksAsync(receiver, transferId, data);

        (await WaitTerminalAsync(receiver)).Status.ShouldBe("error");
    }

    [Fact]
    public async Task 分块传输_文件内容损坏但哈希未变_校验必须拦截()
    {
        var (receiver, _, _, _, _) = BuildReceiver();
        var data = new byte[80_000];
        Random.Shared.NextBytes(data);
        var hash = SHA256.HashData(data);
        const string transferId = "t-corrupt";
        var saveDir = Path.Combine(_root, "recv-corrupt");

        await receiver.HandleFileMetaAsync(new FileMetaMessage
        {
            SenderId = SenderId(), ConversationId = "conv", TransferId = transferId,
            FileName = "corrupt.bin", FileSize = data.Length, FileHash = hash,
            ChunkSize = ChunkSize, TotalChunks = 2
        }, new FakeTcpConnection());
        await receiver.AcceptTransferAsync(transferId, saveDir);

        // 第二个分块内容被改写（长度不变）→ 文件哈希必然不匹配
        var corrupted = (byte[])data.Clone();
        corrupted[^1] ^= 0xFF;
        await SendAllChunksAsync(receiver, transferId, corrupted);

        (await WaitTerminalAsync(receiver)).Status.ShouldBe("error");
    }

    [Fact]
    public async Task 接受传输时_若对端可发现_必须回送Accepted的FileAck()
    {
        var (receiver, dht, router, _, remote) = BuildReceiver();
        dht.Register(remote);
        const string transferId = "t-ack";
        var saveDir = Path.Combine(_root, "recv-ack");

        await receiver.HandleFileMetaAsync(new FileMetaMessage
        {
            SenderId = SenderId(), ConversationId = "conv", TransferId = transferId,
            FileName = "ack.bin", FileSize = 10, FileHash = SHA256.HashData(new byte[10]),
            ChunkSize = ChunkSize, TotalChunks = 1
        }, new FakeTcpConnection());

        await receiver.AcceptTransferAsync(transferId, saveDir);

        var ack = router.Sent.OfType<FileAckMessage>().Single();
        ack.TransferId.ShouldBe(transferId);
        ack.Accepted.ShouldBeTrue();
    }

    [Fact]
    public async Task 拒绝传输时_必须回送Accepted为false的FileAck()
    {
        var (receiver, dht, router, _, remote) = BuildReceiver();
        dht.Register(remote);
        const string transferId = "t-reject";

        await receiver.HandleFileMetaAsync(new FileMetaMessage
        {
            SenderId = SenderId(), ConversationId = "conv", TransferId = transferId,
            FileName = "reject.bin", FileSize = 10, FileHash = SHA256.HashData(new byte[10]),
            ChunkSize = ChunkSize, TotalChunks = 1
        }, new FakeTcpConnection());

        await receiver.RejectTransferAsync(transferId, "用户拒绝");

        var ack = router.Sent.OfType<FileAckMessage>().Single();
        ack.Accepted.ShouldBeFalse();
        ack.ErrorMessage.ShouldBe("用户拒绝");
    }

    [Fact]
    public async Task 收到Offer时_必须发布到OnFileOfferReceived事件流()
    {
        var (receiver, _, _, _, _) = BuildReceiver();
        const string transferId = "t-offer";

        await receiver.HandleFileMetaAsync(new FileMetaMessage
        {
            SenderId = SenderId(), ConversationId = "conv", TransferId = transferId,
            FileName = "offer.bin", FileSize = 2048, FileHash = SHA256.HashData(new byte[2048]),
            ChunkSize = ChunkSize, TotalChunks = 1
        }, new FakeTcpConnection());

        using var cts = new CancellationTokenSource(5000);
        await foreach (var offer in receiver.OnFileOfferReceived.WithCancellation(cts.Token))
        {
            offer.TransferId.ShouldBe(transferId);
            offer.FileName.ShouldBe("offer.bin");
            offer.FileSize.ShouldBe(2048);
            break;
        }
    }

    [Fact]
    public async Task 未接受传输时_分块必须被丢弃而不是写入文件()
    {
        var (receiver, _, _, _, _) = BuildReceiver();
        const string transferId = "t-notaccepted";

        await receiver.HandleFileMetaAsync(new FileMetaMessage
        {
            SenderId = SenderId(), ConversationId = "conv", TransferId = transferId,
            FileName = "pending.bin", FileSize = 100, FileHash = SHA256.HashData(new byte[100]),
            ChunkSize = ChunkSize, TotalChunks = 1
        }, new FakeTcpConnection());

        // 未调用 AcceptTransferAsync → SavePath 为 null → 分块应被忽略且不抛异常
        await Should.NotThrowAsync(() => receiver.HandleFileChunkAsync(new FileChunkMessage
        {
            SenderId = SenderId(), ConversationId = "conv", TransferId = transferId,
            ChunkIndex = 0, Data = new byte[100]
        }));

        Directory.Exists(_root).ShouldBeTrue();
        Directory.GetFiles(_root, "pending.bin", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task 分块写入_投递全部分块但不触发完成校验时_文件字节与源完全一致()
    {
        // 声明 TotalChunks 比实际多 1，从而投递完所有数据也不进入 VerifyAndCompleteAsync，
        // 用于独立验证"按 ChunkIndex * ChunkSize 偏移写入"的分块逻辑本身是否正确
        // （完成校验路径被已知缺陷#6 阻塞，见 KnownDefectsTests）。
        var (receiver, _, _, _, _) = BuildReceiver();
        var data = new byte[100_000];
        Random.Shared.NextBytes(data);
        const string transferId = "t-partial";
        var saveDir = Path.Combine(_root, "recv-partial");

        await receiver.HandleFileMetaAsync(new FileMetaMessage
        {
            SenderId = SenderId(), ConversationId = "conv", TransferId = transferId,
            FileName = "partial.bin", FileSize = data.Length, FileHash = SHA256.HashData(data),
            ChunkSize = ChunkSize, TotalChunks = 3
        }, new FakeTcpConnection());
        await receiver.AcceptTransferAsync(transferId, saveDir);

        await SendAllChunksAsync(receiver, transferId, data);   // 实际只有 2 块 (65536 + 34464)

        var saved = await File.ReadAllBytesAsync(Path.Combine(saveDir, "partial.bin"));
        saved.ShouldBe(data);
        SHA256.HashData(saved).ShouldBe(SHA256.HashData(data));
    }

    [Fact]
    public async Task 未知传输ID的分块_必须被安全忽略()
    {
        var (receiver, _, _, _, _) = BuildReceiver();

        await Should.NotThrowAsync(() => receiver.HandleFileChunkAsync(new FileChunkMessage
        {
            SenderId = SenderId(), ConversationId = "conv", TransferId = "不存在的传输",
            ChunkIndex = 0, Data = new byte[10]
        }));

        await Should.NotThrowAsync(() => receiver.HandleFileAckAsync(new FileAckMessage
        {
            SenderId = SenderId(), ConversationId = "conv", TransferId = "不存在的传输", Accepted = true
        }));
    }

    [Fact]
    public async Task 发送方_SendOfferAsync_非默认ChunkSize_必须同步写入state并以该大小实际切片()
    {
        // 2026-09-21-filetransfer-chunksize：发送循环此前硬编码 DefaultChunkSize (65536)，
        // 即便 meta 与 state.ChunkSize 都声明 4096，实际切片仍按 65536 进行。本测试
        // 不依赖接收端 RPC 层（FileMeta/Chunk/Ack 处理器在生产路径上仍未接入路由器，详见
        // REPAIR-PLAN §阶段 4 留口）；只观察 SendOfferAsync 产生的 meta 与 HandleFileAckAsync
        // 触发的 SendFileChunksAsync 实际切片大小。RecordingMessageRouter 替代真实 TCP，
        // 模拟对端回送 Accepted 的 FileAck。
        var (sender, dht, router, _, remote) = BuildReceiver();
        dht.Register(remote);
        router.Clear();

        const int customChunkSize = 4096;
        const int fileLength = 200_000;        // 200KB → 49 个 4096 块（最后一块 1376B）
        var data = new byte[fileLength];
        Random.Shared.NextBytes(data);
        var hash = SHA256.HashData(data);

        var srcPath = Path.Combine(_root, "src-sender-chunksize.bin");
        await File.WriteAllBytesAsync(srcPath, data);

        // 1. SendOfferAsync 写入 meta：ChunkSize 必须真实声明调用方传入的值（不再回退到 65536）
        var transferId = await sender.SendOfferAsync(remote.NodeId, srcPath, customChunkSize);

        var meta = router.Sent.OfType<FileMetaMessage>().Single();
        meta.ChunkSize.ShouldBe(customChunkSize);
        meta.TotalChunks.ShouldBe((int)Math.Ceiling((double)fileLength / customChunkSize));
        meta.FileHash.ShouldBe(hash);

        // 2. 模拟对端回送 Accepted 的 FileAck → 触发 SendFileChunksAsync
        router.Clear();
        await sender.HandleFileAckAsync(new FileAckMessage
        {
            SenderId = SenderId(),
            ConversationId = remote.NodeId.ToHexString(),
            TransferId = transferId,
            Accepted = true
        });

        // 等待发送循环把所有分块全部送入 router
        var expectedChunks = (int)Math.Ceiling((double)fileLength / customChunkSize);
        await Wait.UntilAsync(
            () => router.Sent.OfType<FileChunkMessage>().Count() >= expectedChunks,
            timeoutMs: 15000);

        // 3. 捕获到的分块数量与文件长度 / ChunkSize 完全一致；每块大小严格等于 state.ChunkSize
        var chunks = router.Sent.OfType<FileChunkMessage>()
            .OrderBy(c => c.ChunkIndex).ToList();
        chunks.Count.ShouldBe(expectedChunks);
        for (var i = 0; i < chunks.Count - 1; i++)
        {
            chunks[i].ChunkIndex.ShouldBe(i);
            chunks[i].Data.Length.ShouldBe(customChunkSize,
                $"第 {i} 个分块大小必须严格等于 state.ChunkSize={customChunkSize}（之前硬编码 DefaultChunkSize=65536）");
        }
        chunks[^1].Data.Length.ShouldBe(fileLength - (expectedChunks - 1) * customChunkSize,
            "最后一个分块为文件剩余字节");

        // 4. 字节级重组应与源文件一致（与原文件 SHA-256 一致）
        var reassembled = new byte[fileLength];
        foreach (var c in chunks)
            Array.Copy(c.Data, 0, reassembled, c.ChunkIndex * customChunkSize, c.Data.Length);
        reassembled.ShouldBe(data);
        SHA256.HashData(reassembled).ShouldBe(hash);
    }

    [Fact]
    public async Task 发送方_SendOfferAsync_默认路径_回归基线分块大小65536()
    {
        // 不传 chunkSize 时走默认路径：行为应与阶段 0 的 146 通过基线一致。
        var (sender, dht, router, _, remote) = BuildReceiver();
        dht.Register(remote);
        router.Clear();

        const int fileLength = 200_000;        // 200KB → 4 个 65536 块（最后一块 3392B）
        var data = new byte[fileLength];
        Random.Shared.NextBytes(data);
        var srcPath = Path.Combine(_root, "src-sender-default.bin");
        await File.WriteAllBytesAsync(srcPath, data);

        var transferId = await sender.SendOfferAsync(remote.NodeId, srcPath);

        var meta = router.Sent.OfType<FileMetaMessage>().Single();
        meta.ChunkSize.ShouldBe(65536, "默认路径必须等于 DefaultChunkSize");
        meta.TotalChunks.ShouldBe(4);

        router.Clear();
        await sender.HandleFileAckAsync(new FileAckMessage
        {
            SenderId = SenderId(),
            ConversationId = remote.NodeId.ToHexString(),
            TransferId = transferId,
            Accepted = true
        });

        await Wait.UntilAsync(
            () => router.Sent.OfType<FileChunkMessage>().Count() >= 4,
            timeoutMs: 15000);

        var chunks = router.Sent.OfType<FileChunkMessage>()
            .OrderBy(c => c.ChunkIndex).ToList();
        chunks.Count.ShouldBe(4);
        for (var i = 0; i < chunks.Count - 1; i++)
            chunks[i].Data.Length.ShouldBe(65536);
        chunks[^1].Data.Length.ShouldBe(3392);
    }
}
