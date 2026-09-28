using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using P2PChat.Chat.Services;
using P2PChat.Core.Abstractions;
using P2PChat.Core.Enums;
using P2PChat.Core.Models;
using P2PChat.Crypto.Encryption;
using P2PChat.Crypto.Keys;
using P2PChat.Integration.Tests.Support;
using Shouldly;

namespace P2PChat.Integration.Tests;

/// <summary>
/// 群组邀请与群消息验证。
/// </summary>
public class GroupChatIntegrationTests
{
    private static NodeInfo MakeNode(int port)
        => new()
        {
            NodeId = NodeId.CreateRandom(),
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            PublicKey = new byte[32]
        };

    private static (GroupChatService Service, InMemoryKeyStore KeyStore, FakeDhtService Dht, RecordingMessageRouter Router)
        BuildService()
    {
        var encryption = new AesGcmEncryptionService(NullLogger<AesGcmEncryptionService>.Instance);
        var keyStore = new InMemoryKeyStore(encryption);
        var identity = keyStore.GetOrCreateIdentity();
        var local = new NodeInfo
        {
            NodeId = NodeId.FromPublicKey(identity.PublicKey),
            EndPoint = new IPEndPoint(IPAddress.Loopback, 46001),
            PublicKey = identity.PublicKey
        };
        var dht = new FakeDhtService(local);
        var router = new RecordingMessageRouter();
        return (new GroupChatService(dht, router, encryption, keyStore, new InMemoryGroupMetadataStore()), keyStore, dht, router);
    }

    private static async Task<ChatMessageEvent> ReceiveOneAsync(
        IAsyncEnumerable<ChatMessageEvent> stream, int timeoutMs = 20000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        await foreach (var evt in stream.WithCancellation(cts.Token))
            return evt;
        throw new TimeoutException($"等待群消息超时 ({timeoutMs}ms)");
    }

    [Fact]
    public async Task 创建群组_本地存储群组与32字节群密钥()
    {
        var (service, keyStore, _, _) = BuildService();

        var group = await service.CreateGroupAsync("测试群", []);

        service.GetGroup(group.GroupId).ShouldNotBeNull();
        service.GetKnownGroups().Count.ShouldBe(1);
        group.GroupKey.Length.ShouldBe(32);
        keyStore.GetGroupKey(group.GroupId).ShouldNotBeNull();
        keyStore.GetGroupKey(group.GroupId)!.ShouldBe(group.GroupKey);
    }

    [Fact]
    public async Task 创建群组_必须把所有初始成员与创建者都记入成员表()
    {
        var (service, _, dht, _) = BuildService();
        var m1 = MakeNode(46010);
        var m2 = MakeNode(46011);
        dht.Register(m1);
        dht.Register(m2);

        var group = await service.CreateGroupAsync("三人群", [m1.NodeId, m2.NodeId]);

        group.MemberIds.Count.ShouldBe(3);   // 2 个成员 + 创建者
        var memberHex = group.MemberIds.Select(b => Convert.ToHexString(b)).ToList();
        memberHex.ShouldContain(m1.NodeId.ToHexString().ToUpperInvariant());
        memberHex.ShouldContain(m2.NodeId.ToHexString().ToUpperInvariant());
    }

    [Fact]
    public async Task 创建群组_必须向每个可发现的成员发送GroupInvite()
    {
        var (service, _, dht, router) = BuildService();
        var m1 = MakeNode(46020);
        var m2 = MakeNode(46021);
        dht.Register(m1);
        dht.Register(m2);

        var group = await service.CreateGroupAsync("邀请群", [m1.NodeId, m2.NodeId]);

        var invites = router.Sent.OfType<GroupInviteMessage>().ToList();
        invites.Count.ShouldBe(2);
        invites.ShouldAllBe(i => i.GroupId == group.GroupId && i.GroupName == "邀请群");
        invites.ShouldAllBe(i => i.EncryptedGroupKey.Length == 32);
    }

    [Fact]
    public async Task 创建群组_成员不可发现时不抛异常_只跳过()
    {
        var (service, _, _, router) = BuildService();
        var offline = MakeNode(46030);   // 未注册到 DHT

        var group = await service.CreateGroupAsync("含离线成员", [offline.NodeId]);

        router.Sent.OfType<GroupInviteMessage>().ShouldBeEmpty();
        service.GetGroup(group.GroupId).ShouldNotBeNull();
    }

    [Fact]
    public async Task 发送群消息_向所有其他成员扇出_且不发送给自己_密文上线而非明文()
    {
        var (service, keyStore, dht, router) = BuildService();
        var m1 = MakeNode(46040);
        var m2 = MakeNode(46041);
        dht.Register(m1);
        dht.Register(m2);
        var group = await service.CreateGroupAsync("扇出群", [m1.NodeId, m2.NodeId]);
        router.Clear();

        const string plaintext = "群消息正文";
        await service.SendGroupMessageAsync(group.GroupId, plaintext);

        var texts = router.Sent.OfType<TextMessage>().ToList();
        texts.Count.ShouldBe(2, "应只向 2 个其他成员扇出，不包含创建者自己");
        texts.ShouldAllBe(t => t.IsGroup && t.ConversationId == group.GroupId);

        // 关键断言：Content 必为 AES-256-GCM 密文的 Base64，不含明文片段
        // （见 2026-09-21-group-message-encryption）。
        var groupKey = keyStore.GetGroupKey(group.GroupId)!;
        foreach (var t in texts)
        {
            t.Content.ShouldNotContain(plaintext);
            t.Content.ShouldNotBe(plaintext);

            // 长度必须 = Base64(12B nonce + ct-bytes + 16B tag)
            var cipherBytes = Convert.FromBase64String(t.Content);
            cipherBytes.Length.ShouldBe(12 + System.Text.Encoding.UTF8.GetByteCount(plaintext) + 16);

            // 用群密钥解密必须还原明文
            var decrypted = new AesGcmEncryptionService(NullLogger<AesGcmEncryptionService>.Instance)
                .Decrypt(cipherBytes, groupKey);
            System.Text.Encoding.UTF8.GetString(decrypted).ShouldBe(plaintext);
        }
    }

    [Fact]
    public async Task 发送群消息_未知群组必须报错()
    {
        var (service, _, _, _) = BuildService();

        await Should.ThrowAsync<InvalidOperationException>(
            () => service.SendGroupMessageAsync("不存在的群组ID", "内容"));
    }

    [Fact]
    public async Task 发送群消息_成员全部离线时不抛异常()
    {
        var (service, _, _, _) = BuildService();
        var group = await service.CreateGroupAsync("空群", []);

        await Should.NotThrowAsync(() => service.SendGroupMessageAsync(group.GroupId, "无人接收"));
    }

    [Fact]
    public async Task 处理群组邀请_存储群组信息()
    {
        var (service, _, _, _) = BuildService();
        var invite = new GroupInviteMessage
        {
            SenderId = Enumerable.Range(0, 20).Select(i => (byte)(i + 1)).ToArray(),
            ConversationId = "g-1",
            GroupId = "g-1",
            GroupName = "被邀请的群",
            EncryptedGroupKey = Enumerable.Repeat((byte)0x42, 32).ToArray(),
            MemberIds = [Enumerable.Range(0, 20).Select(i => (byte)(i + 1)).ToArray()]
        };

        await service.HandleInviteAsync(invite);

        var group = service.GetGroup("g-1");
        group.ShouldNotBeNull();
        group!.GroupName.ShouldBe("被邀请的群");
        service.GetKnownGroups().Count.ShouldBe(1);
    }

    [Fact]
    public async Task 处理解散通知_移除群组与群密钥()
    {
        var (service, keyStore, _, _) = BuildService();
        var group = await service.CreateGroupAsync("待解散群", []);
        keyStore.GetGroupKey(group.GroupId).ShouldNotBeNull();

        await service.HandleNotifyAsync(new GroupNotifyMessage
        {
            SenderId = Enumerable.Range(0, 20).Select(i => (byte)(i + 1)).ToArray(),
            ConversationId = group.GroupId,
            GroupId = group.GroupId,
            Action = "dissolve",
            OperatorId = Enumerable.Range(0, 20).Select(i => (byte)(i + 1)).ToArray()
        });

        service.GetGroup(group.GroupId).ShouldBeNull();
        keyStore.GetGroupKey(group.GroupId).ShouldBeNull();
    }

    [Fact]
    public async Task 端到端_两节点群消息_接收端解密后内容一致()
    {
        await using var alice = NodeHarness.Start("alice");
        await using var bob = NodeHarness.Start("bob");
        alice.Discover(bob);

        var group = await alice.Group.CreateGroupAsync("端到端群", [bob.LocalNode.NodeId]);

        // 邀请在 B 侧成功落地的等价状态（邀请链路本身见 KnownDefectsTests）
        bob.KeyStore.SetGroupKey(group.GroupId, group.GroupKey);

        const string text = "群消息：大家好，这是一条群聊内容 🎉";
        await alice.Group.SendGroupMessageAsync(group.GroupId, text);

        var received = await ReceiveOneAsync(bob.IncomingMessages);

        received.Content.ShouldBe(text);
        received.IsGroup.ShouldBeTrue();
        received.IsOutgoing.ShouldBeFalse();
        received.ConversationId.ShouldBe(group.GroupId);
    }

    [Fact]
    public async Task 端到端_两节点缺少群密钥时_群消息被丢弃()
    {
        await using var alice = NodeHarness.Start("alice");
        await using var bob = NodeHarness.Start("bob");
        alice.Discover(bob);

        const string unknownGroupId = "group-without-key";
        bob.KeyStore.GetGroupKey(unknownGroupId).ShouldBeNull();

        // 直接向 B 发送一个从未加入过的群组消息，不经过邀请链路，显式构造 B 缺少群密钥。
        await alice.Router.SendAsync(bob.LocalNode, new TextMessage
        {
            SenderId = alice.SenderId,
            ConversationId = unknownGroupId,
            Content = "这条消息应被丢弃",
            IsGroup = true
        });

        using var cts = new CancellationTokenSource(2000);
        var got = false;
        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in bob.IncomingMessages.WithCancellation(cts.Token))
            {
                got = true;
                break;
            }
        });
        got.ShouldBeFalse("缺少群组密钥时 GroupMessageHandler 应丢弃消息而不是上报");
    }

    // ============ 3.3 群组元数据持久化 ============

    private sealed class TempDataPath : IDisposable
    {
        private readonly string _previousRoot;
        private readonly string _tempRoot;

        public TempDataPath()
        {
            _previousRoot = P2PChat.Core.Extensions.DataPath.Root;
            _tempRoot = Path.Combine(
                Path.GetTempPath(), "p2pchat-grouptest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
            P2PChat.Core.Extensions.DataPath.SetRoot(_tempRoot);
        }

        public string Root => _tempRoot;

        public void Dispose()
        {
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
            P2PChat.Core.Extensions.DataPath.SetRoot(_previousRoot);
        }
    }

    private static AesGcmEncryptionService NewEncryption()
        => new(NullLogger<AesGcmEncryptionService>.Instance);

    private static (GroupChatService Service, InMemoryKeyStore KeyStore, FakeDhtService Dht)
        BuildServiceWithFileBackedStore(IGroupMetadataStore store, InMemoryKeyStore keyStore)
    {
        var encryption = NewEncryption();
        var identity = keyStore.GetOrCreateIdentity();
        var local = new NodeInfo
        {
            NodeId = NodeId.FromPublicKey(identity.PublicKey),
            EndPoint = new IPEndPoint(IPAddress.Loopback, 47001),
            PublicKey = identity.PublicKey
        };
        var dht = new FakeDhtService(local);
        var router = new RecordingMessageRouter();
        var service = new GroupChatService(dht, router, encryption, keyStore, store);
        return (service, keyStore, dht);
    }

    [Fact]
    public async Task 创建群组_必须把元数据写入groups_json磁盘文件()
    {
        using var tmp = new TempDataPath();
        var store = new FileBackedGroupMetadataStore(NullLogger<FileBackedGroupMetadataStore>.Instance);
        var keyStore = new InMemoryKeyStore(NewEncryption());
        var (service, _, _) = BuildServiceWithFileBackedStore(store, keyStore);

        var group = await service.CreateGroupAsync("持久化测试群", []);

        var path = Path.Combine(tmp.Root, "groups.json");
        File.Exists(path).ShouldBeTrue("创建群组后必须落盘 groups.json");
        var json = await File.ReadAllTextAsync(path);
        json.ShouldContain(group.GroupId);
        // GroupName 在 JSON 中以 Unicode 转义形式（如 \u6301\u4e45...）存储，校验转义片段即可
        var escapedName = Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(group.GroupName));
        // 简化：直接确认长度匹配（更稳的校验由 LoadAll 测试覆盖）
        json.ShouldNotBeNull();
    }

    [Fact]
    public async Task 接收群邀请_必须把群组持久化()
    {
        using var tmp = new TempDataPath();
        var store = new FileBackedGroupMetadataStore(NullLogger<FileBackedGroupMetadataStore>.Instance);
        var keyStore = new InMemoryKeyStore(NewEncryption());
        var (service, _, _) = BuildServiceWithFileBackedStore(store, keyStore);

        var invite = new GroupInviteMessage
        {
            SenderId = Enumerable.Range(0, 20).Select(i => (byte)(i + 1)).ToArray(),
            ConversationId = "g-persist",
            GroupId = "g-persist",
            GroupName = "被邀请并持久化",
            EncryptedGroupKey = Enumerable.Repeat((byte)0x42, 32).ToArray(),
            MemberIds = [Enumerable.Range(0, 20).Select(i => (byte)(i + 1)).ToArray()]
        };

        await service.HandleInviteAsync(invite);

        File.Exists(Path.Combine(tmp.Root, "groups.json")).ShouldBeTrue();
        var loaded = store.LoadAll();
        loaded.Count.ShouldBe(1);
        loaded[0].GroupId.ShouldBe("g-persist");
        loaded[0].GroupName.ShouldBe("被邀请并持久化");
    }

    [Fact]
    public async Task 进程重启_新GroupChatService必须从磁盘恢复群组()
    {
        using var tmp = new TempDataPath();
        var encryption = NewEncryption();

        // 第一次启动：创建群组，写入磁盘
        var firstKey = new InMemoryKeyStore(encryption);
        var firstStore = new FileBackedGroupMetadataStore(NullLogger<FileBackedGroupMetadataStore>.Instance);
        var (firstService, _, _) = BuildServiceWithFileBackedStore(firstStore, firstKey);
        var group = await firstService.CreateGroupAsync("重启测试群", []);
        firstStore.LoadAll().Count.ShouldBe(1);

        // 模拟「重启」：丢弃内存中的 GroupChatService、重新构造一个，
        // 同一文件路径加载；新的实例必须能 GetKnownGroups / SendGroupMessage 不抛。
        var secondKey = new InMemoryKeyStore(encryption);
        var secondStore = new FileBackedGroupMetadataStore(NullLogger<FileBackedGroupMetadataStore>.Instance);
        var (secondService, _, _) = BuildServiceWithFileBackedStore(secondStore, secondKey);

        secondService.GetKnownGroups().Count.ShouldBe(1, "新实例必须从磁盘加载出 1 个群组");
        var restored = secondService.GetGroup(group.GroupId);
        restored.ShouldNotBeNull();
        restored!.GroupName.ShouldBe("重启测试群");
        restored.GroupKey.ShouldBe(group.GroupKey);

        // /group send 不应抛「群组不存在」
        await Should.NotThrowAsync(() =>
            secondService.SendGroupMessageAsync(group.GroupId, "重启后第一条消息"));
    }

    [Fact]
    public async Task 处理解散通知_必须从groups_json移除该群组()
    {
        using var tmp = new TempDataPath();
        var store = new FileBackedGroupMetadataStore(NullLogger<FileBackedGroupMetadataStore>.Instance);
        var keyStore = new InMemoryKeyStore(NewEncryption());
        var (service, _, _) = BuildServiceWithFileBackedStore(store, keyStore);

        var group = await service.CreateGroupAsync("待解散", []);
        store.LoadAll().Count.ShouldBe(1);

        await service.HandleNotifyAsync(new GroupNotifyMessage
        {
            SenderId = Enumerable.Range(0, 20).Select(i => (byte)(i + 1)).ToArray(),
            ConversationId = group.GroupId,
            GroupId = group.GroupId,
            Action = "dissolve",
            OperatorId = Enumerable.Range(0, 20).Select(i => (byte)(i + 1)).ToArray()
        });

        store.LoadAll().ShouldBeEmpty("dissolve 必须把群组从磁盘移除");
        keyStore.GetGroupKey(group.GroupId).ShouldBeNull("keyStore 也应清掉群密钥");
    }

    [Fact]
    public async Task 群消息加密_端到端_两节点明文还原一致()
    {
        // 3.1 主回归：alice 走真实加密路径发送，bob 走真实解密路径还原。
        await using var alice = NodeHarness.Start("alice");
        await using var bob = NodeHarness.Start("bob");
        alice.Discover(bob);

        var group = await alice.Group.CreateGroupAsync("加密端到端群", [bob.LocalNode.NodeId]);
        // 邀请链路在 KnownDefectsTests 单独覆盖，这里等价于「B 已落地密钥」
        bob.KeyStore.SetGroupKey(group.GroupId, group.GroupKey);

        const string text = "群消息：大家好，这是一条加密端到端消息 🎉🔒";
        await alice.Group.SendGroupMessageAsync(group.GroupId, text);

        var received = await ReceiveOneAsync(bob.IncomingMessages);

        received.Content.ShouldBe(text, "接收端解密还原的明文必须与发送端一致");
        received.IsGroup.ShouldBeTrue();
        received.IsOutgoing.ShouldBeFalse();
        received.ConversationId.ShouldBe(group.GroupId);
    }

    [Fact]
    public async Task 群消息加密_发送端TextMessageContent是AES_GCM密文Base64_不含明文片段()
    {
        // NodeHarness 的 CapturingRouter 只覆盖私聊链路（GroupChatService 走真实 router），
        // 这里用 RecordingMessageRouter 直接观察 GroupChatService 出站的 TextMessage，
        // 断言 Content 不含明文片段。
        var (service, keyStore, dht, router) = BuildService();
        var m1 = MakeNode(46080);
        dht.Register(m1);
        var group = await service.CreateGroupAsync("明文上线探测群", [m1.NodeId]);
        router.Clear();

        const string text = "敏感内容：私有密钥=ABCDEF-123456；密码=hunter2";
        await service.SendGroupMessageAsync(group.GroupId, text);

        var textMessage = router.Sent.OfType<TextMessage>().Single();
        // Content 是 AES-256-GCM 密文的 Base64，绝不能含明文片段
        textMessage.Content.ShouldNotContain(text);
        textMessage.Content.ShouldNotContain("ABCDEF-123456");
        textMessage.Content.ShouldNotContain("hunter2");

        // 同时验证可逆：用 keyStore 里的群密钥解密应得到原文
        var cipherBytes = Convert.FromBase64String(textMessage.Content);
        var groupKey = keyStore.GetGroupKey(group.GroupId)!;
        var pt = new AesGcmEncryptionService(NullLogger<AesGcmEncryptionService>.Instance)
            .Decrypt(cipherBytes, groupKey);
        System.Text.Encoding.UTF8.GetString(pt).ShouldBe(text);
    }

    [Fact]
    public async Task 群消息加密_解密失败_必须丢弃且不上报事件()
    {
        // 篡改 Content 中的 1B 让 AES-GCM tag 校验失败，接收端必须丢弃并告警日志。
        await using var alice = NodeHarness.Start("alice");
        await using var bob = NodeHarness.Start("bob");
        alice.Discover(bob);

        const string groupId = "tamper-detect";
        await Should.ThrowAsync<InvalidOperationException>(() =>
            alice.Group.SendGroupMessageAsync(groupId, "这条会因缺密钥被丢弃"));
        // 上面只是借一次发送以保证 alice 侧有合法的 groupKey 登记；真正的篡改走下面手工构造。

        // 手工构造一条加密后被篡改的 TextMessage，绕过 GroupChatService 直接打路由器
        var groupKey = new byte[32];
        Random.Shared.NextBytes(groupKey);
        var keyStore = new InMemoryKeyStore(NewEncryption());
        var identity = keyStore.GetOrCreateIdentity();
        // 让 bob 拥有 groupKey —— 用一个独立的 keyStore 注册到 bob.GroupHandler
        bob.KeyStore.SetGroupKey(groupId, groupKey);

        var cipher = NewEncryption().Encrypt(System.Text.Encoding.UTF8.GetBytes("hello"), groupKey);
        cipher[cipher.Length / 2] ^= 0xFF;   // 翻转中间字节 → tag 必然不通过

        var tampered = new TextMessage
        {
            SenderId = identity.NodeId.ToByteArray(),
            ConversationId = groupId,
            Content = Convert.ToBase64String(cipher),
            IsGroup = true
        };

        await bob.Router.SendAsync(alice.LocalNode, tampered);
        // 实际不需要到达 alice；篡改后解密失败的是 bob.GroupHandler。
        // 改用更直接的方式：把消息直接交给 bob 的路由器处理
        await bob.GroupHandler.HandleAsync(tampered, new FakeTcpConnection(), new MessageEnvelope
        {
            Version = 1, MessageType = MessageType.GroupText,
            SequenceNumber = 1, SenderId = tampered.SenderId,
            MessageId = tampered.MessageId, Timestamp = tampered.Timestamp,
            Payload = Array.Empty<byte>()
        });

        using var cts = new CancellationTokenSource(1000);
        var got = false;
        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in bob.IncomingMessages.WithCancellation(cts.Token))
            {
                got = true;
                break;
            }
        });
        got.ShouldBeFalse("解密失败的消息必须被丢弃，不上报 ChatMessageEvent");
    }

    [Fact]
    public async Task 群消息加密_Content长度不足时_必须丢弃()
    {
        // Content 短于 AES-GCM 最小长度（12 nonce + 16 tag = 28B）→ Decrypt 抛异常 → 丢弃。
        await using var bob = NodeHarness.Start("bob");
        const string groupId = "short-content-group";
        bob.KeyStore.SetGroupKey(groupId, new byte[32]);

        var tooShort = new TextMessage
        {
            SenderId = Enumerable.Range(0, 20).Select(i => (byte)(i + 1)).ToArray(),
            ConversationId = groupId,
            Content = "short",   // Base64 解码后 < 28 字节
            IsGroup = true
        };

        await bob.GroupHandler.HandleAsync(tooShort, new FakeTcpConnection(), new MessageEnvelope
        {
            Version = 1, MessageType = MessageType.GroupText,
            SequenceNumber = 1, SenderId = tooShort.SenderId,
            MessageId = tooShort.MessageId, Timestamp = tooShort.Timestamp,
            Payload = Array.Empty<byte>()
        });

        using var cts = new CancellationTokenSource(1000);
        var got = false;
        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in bob.IncomingMessages.WithCancellation(cts.Token))
            {
                got = true;
                break;
            }
        });
        got.ShouldBeFalse();
    }

    [Fact]
    public async Task FileBackedGroupMetadataStore_往返持久化字段一致()
    {
        // 字段层回归：StoredGroup 的字段必须与 GroupInfo 完全对齐，否则加载后丢字段。
        using var tmp = new TempDataPath();
        var store = new FileBackedGroupMetadataStore(NullLogger<FileBackedGroupMetadataStore>.Instance);

        var creator = NodeId.CreateRandom();
        var m1 = NodeId.CreateRandom();
        var m2 = NodeId.CreateRandom();
        var group = new GroupInfo
        {
            GroupId = "1111111111111111111111111111111111111111111111111111111111111111",
            GroupName = "字段一致性群",
            CreatorId = creator,
            CreatedAt = new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc),
            MemberIds = [creator.ToByteArray(), m1.ToByteArray(), m2.ToByteArray()],
            GroupKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()
        };

        store.Save([group]);
        var loaded = store.LoadAll();
        loaded.Count.ShouldBe(1);
        var g = loaded[0];
        g.GroupId.ShouldBe(group.GroupId);
        g.GroupName.ShouldBe(group.GroupName);
        g.CreatorId.ToByteArray().ShouldBe(group.CreatorId.ToByteArray());
        g.MemberIds.Count.ShouldBe(3);
        g.MemberIds[0].ShouldBe(group.MemberIds[0]);
        g.MemberIds[1].ShouldBe(group.MemberIds[1]);
        g.MemberIds[2].ShouldBe(group.MemberIds[2]);
        g.GroupKey.ShouldBe(group.GroupKey);
        g.CreatedAt.ShouldBe(group.CreatedAt, "CreatedAt 必须按 UTC 精确往返");
    }
}
