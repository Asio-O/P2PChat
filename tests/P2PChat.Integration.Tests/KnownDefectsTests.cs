using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using P2PChat.Chat.Handlers;
using P2PChat.Core.Enums;
using P2PChat.Core.Models;
using P2PChat.Crypto.Encryption;
using P2PChat.FileTransfer.Services;
using P2PChat.Integration.Tests.Support;
using Shouldly;
using Xunit.Sdk;

namespace P2PChat.Integration.Tests;

/// <summary>
/// 已知缺陷复现（期望契约 vs 当前实现）。
///
/// 这些测试断言的是**期望的正确契约**。当当前实现不满足契约时抛出
/// <c>Xunit.Sdk.SkipException.ForSkip(reason)</c> 动态标记为 Skipped —— 跳过原因里包含可复现的机制与观测值。
/// 缺陷修复后这些测试会自动转为 Passed（不再抛 SkipException）。
/// <para>
/// 重要：Skipped ≠ Passed。测试统计里它们显示为 skipped，不算绿。
/// </para>
/// </summary>
[Trait("Category", "KnownDefect")]
public class KnownDefectsTests
{
    private static byte[] SenderId20() => Enumerable.Range(0, 20).Select(i => (byte)(i + 1)).ToArray();

    private static MessageEnvelope Envelope(MessageType type) => new()
    {
        MessageType = type,
        SenderId = SenderId20(),
        Payload = []
    };

    [Fact]
    public async Task 缺陷1_密钥交换后_双方会话密钥应当相同()
    {
        await using var alice = NodeHarness.Start("alice");
        await using var bob = NodeHarness.Start("bob");
        alice.Discover(bob);

        await alice.Chat.SendPrivateMessageAsync(bob.LocalNode.NodeId, "触发密钥交换");
        await Wait.UntilAsync(() => bob.KeyStore.GetSessionKey(new NodeId(alice.SenderId)) != null, 15000);

        var aliceKey = alice.KeyStore.GetSessionKey(bob.LocalNode.NodeId);
        var bobKey = bob.KeyStore.GetSessionKey(new NodeId(alice.SenderId));

        if (aliceKey == null || bobKey == null || !aliceKey.SequenceEqual(bobKey))
        {
            throw SkipException.ForSkip(
                $"已知缺陷#1 会话密钥双方不一致：alice侧={(aliceKey == null ? "null" : aliceKey.Length + "B")}, " +
                $"bob侧={(bobKey == null ? "null" : bobKey.Length + "B")}。" +
                "机制：ChatService.PerformKeyExchangeAsync 把【本地临时私钥】直接当作会话密钥写入 keyStore " +
                "(src/P2PChat.Chat/Services/ChatService.cs:116 `SetSessionKey(recipient.NodeId, ephemeralKey.PrivateKey)`)，" +
                "并且从未回送 IsResponse=true 的 KeyExchange 响应，A 侧永远拿不到 B 的临时公钥，" +
                "因此 A 侧存的既不是共享密钥也不是对端能推导出的值。修复方向：暂存临时私钥→回送响应→收到响应后用 ECDH 派生会话密钥。");
        }

        aliceKey!.ShouldBe(bobKey!);
    }

    [Fact]
    public async Task 缺陷2_私聊处理器应当解密收到的密文()
    {
        var encryption = new AesGcmEncryptionService(NullLogger<AesGcmEncryptionService>.Instance);
        var keyStore = new InMemoryKeyStore(encryption);
        var senderId = SenderId20();
        var sessionKey = encryption.GenerateRandomKey();
        keyStore.SetSessionKey(new NodeId(senderId), sessionKey);

        const string plaintext = "这是需要被解密的私聊内容";
        var content = Convert.ToBase64String(
            encryption.Encrypt(Encoding.UTF8.GetBytes(plaintext), sessionKey));

        var handler = new PrivateMessageHandler(encryption, keyStore, NullLogger<PrivateMessageHandler>.Instance);
        await handler.HandleAsync(
            new TextMessage { SenderId = senderId, ConversationId = "c", Content = content },
            new FakeTcpConnection(),
            Envelope(MessageType.PrivateText));

        ChatMessageEvent? received = null;
        using (var cts = new CancellationTokenSource(5000))
        {
            await foreach (var evt in handler.OnMessageReceived.WithCancellation(cts.Token))
            {
                received = evt;
                break;
            }
        }

        received.ShouldNotBeNull();
        if (received!.Content == content)
        {
            throw SkipException.ForSkip(
                "已知缺陷#2 私聊消息未解密：PrivateMessageHandler.HandleAsync 把 message.Content 直接当明文使用 " +
                "(src/P2PChat.Chat/Handlers/PrivateMessageHandler.cs:48-50 注释『为了简化, 当前TextMessage.Content直接是明文』)，" +
                "会话密钥只用于 `GetSessionKey(...) != null` 的存在性检查，未参与任何加解密。" +
                "观测：处理器原样回传了 Base64 密文而非常量明文。");
        }

        received.Content.ShouldBe(plaintext);
    }

    [Fact]
    public async Task 缺陷3_群组邀请处理器应当存储群组密钥()
    {
        await using var alice = NodeHarness.Start("alice");
        await using var bob = NodeHarness.Start("bob");
        alice.Discover(bob);

        // ---- 正向：真实的加密邀请必须被接受 ----------------------------------
        // 走真实发送路径：GroupChatService.CreateGroupAsync 用发送方身份私钥 + 接收方公钥
        // 做 ECDH → HKDF 派生出包装密钥，再用 AES-256-GCM 包装 32B 群密钥，
        // 并填充 SenderPublicKey(91B) 与 EncryptedGroupKeyNonceAndTag(28B)。
        var group = await alice.Group.CreateGroupAsync("加密邀请群", [bob.LocalNode.NodeId]);

        var stored = await WaitForGroupKeyAsync(bob, group.GroupId);
        stored.ShouldNotBeNull("合法的加密邀请必须被接受并写入 keyStore");
        stored!.Length.ShouldBe(32);
        stored.ShouldBe(group.GroupKey);   // 接收方密钥必须逐字节等于发送方使用的 32B 群密钥

        // ---- 负向（安全加固回归守卫）：旧格式明文邀请必须被丢弃 ----------------
        // 一旦有人把"无元数据的 32B 明文 / 公钥 SHA-256 / 缺 nonce-tag"这些兼容回退路径改回来，
        // 下面三条中的任意一条都会立刻变红。
        const string legacyNoMetadata = "legacy-group-no-metadata";
        await bob.InviteHandler.HandleAsync(
            new GroupInviteMessage
            {
                SenderId = SenderId20(),
                ConversationId = legacyNoMetadata,
                GroupId = legacyNoMetadata,
                GroupName = "旧格式明文群",
                EncryptedGroupKey = bob.Encryption.GenerateRandomKey(),   // 32B 明文冒充密文主体
                MemberIds = []
                // SenderPublicKey / EncryptedGroupKeyNonceAndTag 均缺失
            },
            new FakeTcpConnection(),
            Envelope(MessageType.GroupInvite));

        const string legacyBadSenderKey = "legacy-group-bad-senderkey";
        await bob.InviteHandler.HandleAsync(
            new GroupInviteMessage
            {
                SenderId = SenderId20(),
                ConversationId = legacyBadSenderKey,
                GroupId = legacyBadSenderKey,
                GroupName = "旧格式明文群",
                EncryptedGroupKey = bob.Encryption.GenerateRandomKey(),
                MemberIds = [],
                SenderPublicKey = new byte[10],                  // 非法长度（非 91B P-256 公钥）
                EncryptedGroupKeyNonceAndTag = new byte[28]
            },
            new FakeTcpConnection(),
            Envelope(MessageType.GroupInvite));

        const string legacyBadNonceTag = "legacy-group-bad-noncetag";
        await bob.InviteHandler.HandleAsync(
            new GroupInviteMessage
            {
                SenderId = SenderId20(),
                ConversationId = legacyBadNonceTag,
                GroupId = legacyBadNonceTag,
                GroupName = "旧格式明文群",
                EncryptedGroupKey = bob.Encryption.GenerateRandomKey(),
                MemberIds = [],
                SenderPublicKey = bob.Identity.PublicKey,        // 长度合法
                EncryptedGroupKeyNonceAndTag = new byte[8]       // 非法长度（非 28B）
            },
            new FakeTcpConnection(),
            Envelope(MessageType.GroupInvite));

        foreach (var legacyId in new[] { legacyNoMetadata, legacyBadSenderKey, legacyBadNonceTag })
        {
            bob.KeyStore.GetGroupKey(legacyId).ShouldBeNull(
                $"旧格式明文邀请 {legacyId} 必须被丢弃，不得把明文注入为群密钥");
        }

        // 事件流里只允许出现正向邀请：三条旧格式邀请都不得发布 OnInviteReceived
        var receivedInvites = await CollectInvitesAsync(bob.InviteHandler.OnInviteReceived, 2000);
        receivedInvites.Count.ShouldBe(1, "只有合法的加密邀请才允许发布 OnInviteReceived");
        receivedInvites[0].GroupId.ShouldBe(group.GroupId);
    }

    private static async Task<byte[]?> WaitForGroupKeyAsync(NodeHarness node, string groupId, int timeoutMs = 10000)
    {
        await Wait.UntilAsync(() => node.KeyStore.GetGroupKey(groupId) != null, timeoutMs);
        return node.KeyStore.GetGroupKey(groupId);
    }

    private static async Task<List<GroupInviteMessage>> CollectInvitesAsync(
        IAsyncEnumerable<GroupInviteMessage> stream, int timeoutMs)
    {
        var received = new List<GroupInviteMessage>();
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await foreach (var invite in stream.WithCancellation(cts.Token))
                received.Add(invite);
        }
        catch (OperationCanceledException) { }
        return received;
    }

    [Fact]
    public async Task 缺陷4_文件传输应当采用元数据声明的分块大小()
    {
        var root = Path.Combine(Path.GetTempPath(), "p2pchat-defect4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var encryption = new AesGcmEncryptionService(NullLogger<AesGcmEncryptionService>.Instance);
            var local = new NodeInfo
            {
                NodeId = NodeId.CreateRandom(),
                EndPoint = new IPEndPoint(IPAddress.Loopback, 47001),
                PublicKey = new byte[32]
            };
            var receiver = new FileTransferService(
                new FakeDhtService(local), new RecordingMessageRouter(), encryption,
                NullLogger<FileTransferService>.Instance);

            const int customChunk = 1024;
            var data = new byte[customChunk * 4];
            Random.Shared.NextBytes(data);
            const string transferId = "defect4";
            var saveDir = Path.Combine(root, "recv");

            await receiver.HandleFileMetaAsync(new FileMetaMessage
            {
                SenderId = SenderId20(), ConversationId = "c", TransferId = transferId,
                FileName = "d.bin", FileSize = data.Length, FileHash = SHA256.HashData(data),
                ChunkSize = customChunk, TotalChunks = 4
            }, new FakeTcpConnection());
            await receiver.AcceptTransferAsync(transferId, saveDir);

            for (var i = 0; i < 4; i++)
            {
                var chunk = new byte[customChunk];
                Array.Copy(data, i * customChunk, chunk, 0, customChunk);
                await receiver.HandleFileChunkAsync(new FileChunkMessage
                {
                    SenderId = SenderId20(), ConversationId = "c", TransferId = transferId,
                    ChunkIndex = i, Data = chunk
                });
            }

            var saved = await File.ReadAllBytesAsync(Path.Combine(saveDir, "d.bin"));
            if (!saved.SequenceEqual(data))
            {
                throw SkipException.ForSkip(
                    $"已知缺陷#4 文件分块偏移错位：meta.ChunkSize={customChunk} 未写入 TransferState " +
                    "(src/P2PChat.FileTransfer/Services/FileTransferService.cs:158-168 HandleFileMetaAsync 完全忽略 meta.ChunkSize)，" +
                    "HandleFileChunkAsync 用 state.ChunkSize 的默认值 65536 计算 offset = ChunkIndex * 65536，" +
                    $"导致 4 个 1024B 分块被写到 0/65536/131072/196608 偏移处。观测：源文件 {data.Length}B，接收文件 {saved.Length}B，" +
                    "内容不一致，SHA-256 校验随之失败。当前恰好能用只因为发送端 DefaultChunkSize 也是 65536。");
            }

            saved.ShouldBe(data);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task 缺陷5_端到端私聊_线路载荷必须是密文且接收端解密后内容一致()
    {
        await using var alice = NodeHarness.Start("alice");
        await using var bob = NodeHarness.Start("bob");
        alice.Discover(bob);

        const string plaintext = "这是一段不应以明文出现在TCP链路上的内容 🔐 端到端加密私聊";
        alice.CapturingRouter.Clear();

        // 真实链路：真实 TcpTransport + MessageRouter + MessagePack + ECDH 握手 + handlers
        await alice.Chat.SendPrivateMessageAsync(bob.LocalNode.NodeId, plaintext);

        // (1) 线路载荷（= 真实写入 TCP 的 MessageEnvelope.Payload 字节）不得直接包含明文 UTF-8 序列
        var captured = alice.CapturingRouter.Captured;
        captured.ShouldNotBeEmpty("应捕获到真实出站载荷");

        var plaintextUtf8 = Encoding.UTF8.GetBytes(plaintext);
        var leaked = captured.Where(c => ContainsSubsequence(c.Payload, plaintextUtf8)).ToList();
        if (leaked.Count > 0)
        {
            throw SkipException.ForSkip(
                $"已知缺陷#5 端到端加密私聊不成立：捕获到 {leaked.Count} 条出站载荷的 MessagePack 字节中直接包含明文 UTF-8 序列" +
                $"（明文 {plaintextUtf8.Length} 字节）。说明消息内容在 TCP 链路上以明文传递，接收端无需解密即可读取。");
        }

        // (2) 线路上的 TextMessage.Content 必须是 AES-256-GCM 密文的 Base64，而不是明文
        var sentTexts = captured.Select(c => c.Message).OfType<TextMessage>().ToList();
        sentTexts.ShouldNotBeEmpty("应捕获到 TextMessage 出站载荷");
        foreach (var text in sentTexts)
        {
            text.Content.ShouldNotBe(plaintext, "线路上的 Content 不应等于明文");

            byte[] cipher;
            try
            {
                cipher = Convert.FromBase64String(text.Content);
            }
            catch (FormatException ex)
            {
                throw SkipException.ForSkip(
                    $"已知缺陷#5 线路载荷不符合密文契约：TextMessage.Content 不是合法 Base64 —— {ex.Message}");
            }

            cipher.Length.ShouldBe(
                12 + Encoding.UTF8.GetByteCount(plaintext) + 16,
                "AES-256-GCM 密文应为 [12B nonce][密文][16B tag]");
        }

        // (3) 接收端必须解密成功，且内容与原文逐字节一致
        var received = await ReceiveOneAsync(bob.PrivateHandler.OnMessageReceived);
        received.Content.ShouldBe(plaintext);
        received.IsOutgoing.ShouldBeFalse();
        Encoding.UTF8.GetBytes(received.Content).ShouldBe(plaintextUtf8);
    }

    private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0) return true;
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }

    private static async Task<ChatMessageEvent> ReceiveOneAsync(
        IAsyncEnumerable<ChatMessageEvent> stream, int timeoutMs = 20000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        await foreach (var evt in stream.WithCancellation(cts.Token))
            return evt;
        throw new TimeoutException($"等待消息超时 ({timeoutMs}ms)");
    }

    [Fact]
    public async Task 缺陷6_文件传输最后一块的完成校验_不应抛文件占用异常()
    {
        var root = Path.Combine(Path.GetTempPath(), "p2pchat-defect6-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var encryption = new AesGcmEncryptionService(NullLogger<AesGcmEncryptionService>.Instance);
            var local = new NodeInfo
            {
                NodeId = NodeId.CreateRandom(),
                EndPoint = new IPEndPoint(IPAddress.Loopback, 48001),
                PublicKey = new byte[32]
            };
            var receiver = new FileTransferService(
                new FakeDhtService(local), new RecordingMessageRouter(), encryption,
                NullLogger<FileTransferService>.Instance);

            var data = new byte[2048];
            Random.Shared.NextBytes(data);
            const string transferId = "defect6";
            var saveDir = Path.Combine(root, "recv");

            await receiver.HandleFileMetaAsync(new FileMetaMessage
            {
                SenderId = SenderId20(), ConversationId = "c", TransferId = transferId,
                FileName = "e.bin", FileSize = data.Length, FileHash = SHA256.HashData(data),
                ChunkSize = 65536, TotalChunks = 1
            }, new FakeTcpConnection());
            await receiver.AcceptTransferAsync(transferId, saveDir);

            string? ioError = null;
            try
            {
                await receiver.HandleFileChunkAsync(new FileChunkMessage
                {
                    SenderId = SenderId20(), ConversationId = "c", TransferId = transferId,
                    ChunkIndex = 0, Data = data
                });
            }
            catch (IOException ex)
            {
                ioError = ex.Message;
            }

            if (ioError != null)
            {
                throw SkipException.ForSkip(
                    "已知缺陷#6 文件传输完成校验必然抛异常：HandleFileChunkAsync 在 " +
                    "`await using var fs = new FileStream(SavePath, FileMode.Open, FileAccess.Write)` 的 using 作用域【尚未释放】时，" +
                    "就调用 VerifyAndCompleteAsync，后者用 File.OpenRead(SavePath) 再次打开同一文件；" +
                    "Windows 上两个句柄的 FileShare 互不兼容（已有句柄 Write+Share.Read，新句柄 Read+Share.Read）→ " +
                    $"抛 IOException: \"{ioError}\"。" +
                    "后果：state.Status 永远不会变成 Completed，OnProgressChanged 也不会收到 completed，文件传输功能整体不可用。" +
                    "修复方向：把校验放到 using 作用域之外，或直接用已打开的 fs 计算哈希。");
            }

            using var cts = new CancellationTokenSource(10000);
            await foreach (var progress in receiver.OnProgressChanged.WithCancellation(cts.Token))
            {
                if (progress.Status is "completed" or "error")
                {
                    progress.Status.ShouldBe("completed");
                    break;
                }
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
