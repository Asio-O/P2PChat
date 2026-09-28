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

namespace P2PChat.Integration.Tests;

/// <summary>
/// 历史缺陷回归守卫 —— 已修复缺陷的「锁死」测试集。
/// <para>
/// 来源：2026-09-17 之前的「已知缺陷」列表（6 个）。这些缺陷随 REPAIR-PLAN 阶段 0 / 阶段 1 / 阶段 3 全部修复。
/// 本文件保留并重写为「回归守卫」模式：每条测试断言「缺陷不重现」的具体契约，任一条改回旧实现即变红。
/// </para>
/// <para>
/// 旧版本曾在主断言失败时通过 <see cref="Xunit.Sdk.SkipException.ForSkip(string)"/> 把测试标记为 Skipped，
/// 并在跳过原因里写入当时源码位置（如 <c>ChatService.cs:116</c>）。修复完成后那些跳过分支已经不可达，
/// 仍引用旧行号/旧行为的描述属于「误导性文档」 —— 4.5 子任务删除了所有这些死分支，并把测试名/注释
/// 改成「已修复 — 锁死回归」的现时语义。
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

    /// <summary>
    /// 回归守卫 #1 — 密钥交换后双方会话密钥必须字节级一致。
    /// <para>
    /// 历史缺陷：ChatService.PerformKeyExchangeAsync 曾经把【本地临时私钥】直接当会话密钥写入 keyStore，
    /// 且从不回送 IsResponse=true 的响应 —— A 侧拿到的是既非共享密钥也对端无法推导的值。
    /// 修法：发起方暂存临时私钥 → 等待对端 IsResponse=true 的响应 → 用对端临时公钥做 ECDH → HKDF 派生会话密钥。
    /// 参见 <c>notes/implemented/bug-fix/2026-09-20-message-sender-identity.md</c>（SenderId 真实身份）
    /// 与 <c>notes/implemented/bug-fix/2026-09-21-message-signing.md</c>（SenderId 防冒名）。
    /// </para>
    /// </summary>
    [Fact]
    public async Task 回归守卫1_密钥交换后_双方会话密钥应当相同()
    {
        await using var alice = NodeHarness.Start("alice");
        await using var bob = NodeHarness.Start("bob");
        alice.Discover(bob);

        await alice.Chat.SendPrivateMessageAsync(bob.LocalNode.NodeId, "触发密钥交换");
        await Wait.UntilAsync(() => bob.KeyStore.GetSessionKey(new NodeId(alice.SenderId)) != null, 15000);

        var aliceKey = alice.KeyStore.GetSessionKey(bob.LocalNode.NodeId);
        var bobKey = bob.KeyStore.GetSessionKey(new NodeId(alice.SenderId));

        aliceKey.ShouldNotBeNull();
        bobKey.ShouldNotBeNull();
        aliceKey.ShouldBe(bobKey, "双方会话密钥必须字节级一致 —— 这是 ECDH+HKDF 派生路径正确的最小证据");
    }

    /// <summary>
    /// 回归守卫 #2 — 私聊处理器收到密文后必须解密还原明文，绝不能原样转发 Base64。
    /// <para>
    /// 历史缺陷：PrivateMessageHandler 曾经只校验「会话密钥存在」而不实际调用 Decrypt，
    /// 导致 Content 原样（Base64 密文）落到 ChatMessageEvent，UI 看到的是一串不可读的 Base64。
    /// </para>
    /// </summary>
    [Fact]
    public async Task 回归守卫2_私聊处理器应当解密收到的密文()
    {
        var encryption = new AesGcmEncryptionService(NullLogger<AesGcmEncryptionService>.Instance);
        var keyStore = new InMemoryKeyStore(encryption);
        var senderId = SenderId20();
        var sessionKey = encryption.GenerateRandomKey();
        keyStore.SetSessionKey(new NodeId(senderId), sessionKey);

        const string plaintext = "这是需要被解密的私聊内容";
        var content = Convert.ToBase64String(
            encryption.Encrypt(Encoding.UTF8.GetBytes(plaintext), sessionKey));

        // handler 不再自建事件通道，输出统一走 IChatEventPublisher —— 用捕获型发布器断言。
        var events = new CapturingChatEventPublisher();
        var handler = new PrivateMessageHandler(
            encryption, keyStore, events, NullLogger<PrivateMessageHandler>.Instance);
        await handler.HandleAsync(
            new TextMessage { SenderId = senderId, ConversationId = "c", Content = content },
            new FakeTcpConnection(),
            Envelope(MessageType.PrivateText));

        ChatMessageEvent? received = null;
        using (var cts = new CancellationTokenSource(5000))
        {
            await foreach (var evt in events.Events.WithCancellation(cts.Token))
            {
                received = evt;
                break;
            }
        }

        received.ShouldNotBeNull();
        received.Content.ShouldBe(plaintext, "密文必须被 AES-256-GCM 解密还原成明文");
        received.Content.ShouldNotBe(content, "Content 不得保留为 Base64 密文");
    }

    /// <summary>
    /// 回归守卫 #3 — 群组邀请处理器必须接受合法加密邀请并写入 32B 群密钥，
    /// 同时必须丢弃「无元数据 / 非法公钥长度 / 缺 nonce-tag」等历史遗留明文回退路径。
    /// <para>
    /// 双向覆盖：正向（合法邀请被接受）+ 负向（旧格式邀请被拒绝且不发布 OnInviteReceived）。
    /// </para>
    /// </summary>
    [Fact]
    public async Task 回归守卫3_群组邀请处理器应当存储群组密钥_并拒绝旧格式明文回退路径()
    {
        await using var alice = NodeHarness.Start("alice");
        await using var bob = NodeHarness.Start("bob");
        alice.Discover(bob);

        // ---- 正向：真实加密邀请必须被接受 ---------------------------------
        var group = await alice.Group.CreateGroupAsync("加密邀请群", [bob.LocalNode.NodeId]);

        var stored = await WaitForGroupKeyAsync(bob, group.GroupId);
        stored.ShouldNotBeNull("合法的加密邀请必须被接受并写入 keyStore");
        stored!.Length.ShouldBe(32);
        stored.ShouldBe(group.GroupKey,   // 接收方密钥必须逐字节等于发送方使用的 32B 群密钥
            "接收方密钥必须逐字节等于发送方使用的 32B 群密钥");

        // ---- 负向（安全加固回归守卫）：旧格式明文邀请必须被丢弃 ------------
        // 一旦有人把"无元数据的 32B 明文 / 非法公钥长度 / 缺 nonce-tag"这些兼容回退路径改回来，
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

    /// <summary>
    /// 回归守卫 #4 — 文件传输实际分块大小必须等于元数据声明的 ChunkSize（不再用 65536 默认值）。
    /// <para>
    /// 历史缺陷：FileTransferService.HandleFileMetaAsync 完全忽略 meta.ChunkSize，
    /// HandleFileChunkAsync 用默认 65536 计算写入偏移 —— 任意非默认 ChunkSize 都会被写到错位偏移，
    /// 字节级重组失败、SHA-256 校验失败。修复后 state.ChunkSize 必须来自 meta.ChunkSize。
    /// </para>
    /// </summary>
    [Fact]
    public async Task 回归守卫4_文件传输应当采用元数据声明的分块大小()
    {
        var root = Path.Combine(Path.GetTempPath(), "p2pchat-regression-4-" + Guid.NewGuid().ToString("N"));
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
            const string transferId = "regression-4";
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
            saved.ShouldBe(data,
                $"接收端文件必须字节级等于源文件：meta.ChunkSize={customChunk} 必须真实控制偏移与分块");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// 回归守卫 #5 — 端到端私聊：线路上传输的 TextMessage.Content 必须是 AES-256-GCM 密文的 Base64，
    /// 且接收端必须能解密还原明文。
    /// <para>
    /// 历史缺陷：曾经把明文直接放到 Content 字段上线，捕获的 MessagePack 字节里能直接看到 UTF-8 序列。
    /// 修复后：发送端走 AES-256-GCM 加密 → Base64；接收端解密；线路不得包含明文 UTF-8。
    /// </para>
    /// </summary>
    [Fact]
    public async Task 回归守卫5_端到端私聊_线路载荷必须是密文且接收端解密后内容一致()
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
        leaked.ShouldBeEmpty(
            $"线路上不得包含明文 UTF-8 序列 —— 当前捕获到 {leaked.Count} 条载荷明文泄漏");

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
            catch (FormatException)
            {
                throw new ShouldAssertException("线路载荷不符合密文契约：TextMessage.Content 不是合法 Base64");
            }

            cipher.Length.ShouldBe(
                12 + Encoding.UTF8.GetByteCount(plaintext) + 16,
                "AES-256-GCM 密文应为 [12B nonce][密文][16B tag]");
        }

        // (3) 接收端必须解密成功，且内容与原文逐字节一致
        var received = await ReceiveOneAsync(bob.IncomingMessages);
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

    /// <summary>
    /// 回归守卫 #6 — 文件传输最后一块的完成校验不得在 FileStream 句柄仍持有时触发二次打开。
    /// <para>
    /// 历史缺陷：HandleFileChunkAsync 在 <c>await using var fs = new FileStream(...)</c> 作用域尚未释放时
    /// 就调用 VerifyAndCompleteAsync，后者用 File.OpenRead 再次打开同一文件；Windows 上两个句柄的
    /// FileShare 不兼容（Write+Share.Read vs Read+Share.Read）→ IOException。修复后校验发生在 using 之外。
    /// </para>
    /// </summary>
    [Fact]
    public async Task 回归守卫6_文件传输最后一块的完成校验_不应抛文件占用异常()
    {
        var root = Path.Combine(Path.GetTempPath(), "p2pchat-regression-6-" + Guid.NewGuid().ToString("N"));
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
            const string transferId = "regression-6";
            var saveDir = Path.Combine(root, "recv");

            await receiver.HandleFileMetaAsync(new FileMetaMessage
            {
                SenderId = SenderId20(), ConversationId = "c", TransferId = transferId,
                FileName = "e.bin", FileSize = data.Length, FileHash = SHA256.HashData(data),
                ChunkSize = 65536, TotalChunks = 1
            }, new FakeTcpConnection());
            await receiver.AcceptTransferAsync(transferId, saveDir);

            // HandleFileChunkAsync 内部若再开 FileStream 读校验，FileShare 不兼容 → IOException
            await receiver.HandleFileChunkAsync(new FileChunkMessage
            {
                SenderId = SenderId20(), ConversationId = "c", TransferId = transferId,
                ChunkIndex = 0, Data = data
            });

            // 状态必须走到 completed（不再因 IOException 落入 error）
            using var cts = new CancellationTokenSource(10000);
            await foreach (var progress in receiver.OnProgressChanged.WithCancellation(cts.Token))
            {
                if (progress.Status is "completed" or "error")
                {
                    progress.Status.ShouldBe("completed",
                        "最后一块落盘后状态必须是 completed，不得因文件占用异常陷入 error");
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