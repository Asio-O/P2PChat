using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using P2PChat.Chat.Handlers;
using P2PChat.Chat.Routing;
using P2PChat.Chat.Services;
using P2PChat.Core.Abstractions;
using P2PChat.Core.Enums;
using P2PChat.Core.Extensions;
using P2PChat.Core.Models;
using P2PChat.Crypto.Encryption;
using P2PChat.Integration.Tests.Support;
using Shouldly;

namespace P2PChat.Integration.Tests;

/// <summary>
/// 「对端长期公钥」链路 —— 见 task-4 / REPAIR-PLAN §阶段0 遗留 + HANDOFF §8 遗留 #2。
/// <para>
/// 缺陷：<c>NodeInfo.PublicKey</c> 对静态登记（<c>/add &lt;nodeId&gt; &lt;ip:port&gt;</c>）与盲连接
/// （<c>/connect &lt;ip:port&gt;</c>）的对端必然为空（<c>MainlineDhtService</c> 的每条解析路径也都返回空），
/// 而群邀请的群密钥包装需要成员公钥 → 建群必然失败，且被 <c>CreateGroupAsync</c> 的
/// <c>catch {}</c> 静默吞掉（REPAIR-PLAN B5：用户看到「/group create 没反应」，日志一行都没有）。
/// </para>
/// <para>
/// 本轮修复<b>不加线路字段</b>：对端的真实长期公钥取自<b>已验签的 <c>MessageEnvelope.SenderPublicKey</c></b>。
/// 该字段本就随每条消息上线、被 <c>ComputeSignedBytes</c> 纳入 ECDSA 签名覆盖、并被
/// <c>VerifyEnvelope</c> 强制与 <c>SenderId</c> 绑定 —— 比在 <c>KeyExchangeMessage</c> 载荷里新增一个
/// 无独立绑定的字段更强（后者还需自己再做一次派生校验，且会改变被签名的载荷字节）。
/// </para>
/// </summary>
public class LongTermPublicKeyTests
{
    private static AesGcmEncryptionService NewCrypto() =>
        new(NullLogger<AesGcmEncryptionService>.Instance);

    /// <summary>
    /// 正向：手工 <c>/add</c> 登记的对端只有 ip:port、<b>没有</b>公钥，也能被正常邀请进群，
    /// 并且对方真的用 ECDH 解出了群密钥。这正是修复前必然失败的场景（端到端走真实 TCP + 真实签名）。
    /// </summary>
    [Fact]
    public async Task 静态对端_无公钥_仍能被正常邀请进群且对方能解出群密钥()
    {
        await using var alice = NodeHarness.Start("alice");
        await using var bob = NodeHarness.Start("bob");

        // 精确复刻 /add <nodeId> <ip:port>：NodeId + 显式端点，**PublicKey 未知**。
        alice.Dht.RegisterStaticPeer(new NodeInfo
        {
            NodeId = bob.LocalNode.NodeId,
            EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, bob.Port),
            PublicKey = null,
            State = PeerState.Online
        });

        var group = await alice.Group.CreateGroupAsync("静态对端群", [bob.LocalNode.NodeId]);

        // 群本身照常建成（单个成员失败不阻断建群），且群密钥真的送达并被解开 ——
        // GroupInviteHandler 只有在 ECDH + AES-GCM 全部认证通过时才会写 keyStore。
        (await Wait.UntilAsync(() => bob.KeyStore.GetGroupKey(group.GroupId) is not null))
            .ShouldBeTrue("对端应已收到群邀请并解密出群密钥");

        bob.KeyStore.GetGroupKey(group.GroupId)!.ShouldBe(group.GroupKey,
            "对端解出的群密钥必须与创建者一致，否则群消息无法互通");

        // 主动握手学到的必须是对方**真实**的长期公钥。
        PeerPublicKeyRegistry.Shared.TryGet(bob.LocalNode.NodeId, out var learned)
            .ShouldBeTrue("主动密钥交换应已登记对端长期公钥");
        learned.ShouldBe(bob.Identity.PublicKey);
    }

    /// <summary>
    /// 正向：登记记录里已经带着可用公钥时，<b>不</b>发起额外握手（避免多余的往返与副作用）。
    /// </summary>
    [Fact]
    public async Task 登记记录已含公钥_直接使用_不额外握手()
    {
        var (service, _, dht, router, _) = BuildService();
        var memberKeyPair = NewCrypto().GenerateKeyPair();
        var member = new NodeInfo
        {
            NodeId = NodeId.FromPublicKey(memberKeyPair.PublicKey),
            EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 46601),
            PublicKey = memberKeyPair.PublicKey
        };
        dht.RegisterStaticPeer(member);

        var group = await service.CreateGroupAsync("已知公钥群", [member.NodeId]);

        router.Sent.ShouldNotContain(m => m is KeyExchangeMessage);
        router.Sent.OfType<GroupInviteMessage>().ShouldHaveSingleItem();
        group.GroupKey.ShouldBe(service.GetGroup(group.GroupId)!.GroupKey);
    }

    /// <summary>
    /// 负向：公钥缺失且对端不可达 → 群邀请被<b>显式</b>跳过，并留下日志。
    /// 修复前这里是 <c>catch {}</c>：用户看到「/group create 没反应」，日志里一行都没有。
    /// </summary>
    [Fact]
    public async Task 公钥取不到_群邀请被显式跳过_并留下告警日志而非静默()
    {
        var (service, _, dht, router, logs) = BuildService();

        // 对端在线记录里没有公钥，且探针拿不到响应（FakeTcpConnection 直接返回空）。
        var unreachable = new NodeInfo
        {
            NodeId = NodeId.FromPublicKey(NewCrypto().GenerateKeyPair().PublicKey),
            EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 46610),
            PublicKey = null,
            State = PeerState.Online
        };
        dht.RegisterStaticPeer(unreachable);

        var group = await service.CreateGroupAsync("取不到公钥的群", [unreachable.NodeId]);

        // 群本身仍应建成 —— 单个成员失败不得中断建群。
        service.GetGroup(group.GroupId).ShouldNotBeNull();
        router.Sent.OfType<GroupInviteMessage>().ShouldBeEmpty("包装不出群密钥就不得发出邀请");

        // 必须尝试过主动取公钥，而不是直接放弃。
        router.Sent.ShouldContain(m => m is KeyExchangeMessage,
            "公钥缺失时应主动握手一次，而不是静默跳过");

        logs.Entries.ShouldNotBeEmpty("静默吞掉是本次必须修掉的独立缺陷");
        logs.Entries.ShouldContain(e =>
            e.Level >= LogLevel.Warning && e.Message.Contains("长期公钥"));
    }

    /// <summary>
    /// 负向：探针拿到的响应信封被篡改时，对端公钥<b>不得</b>被采用 ——
    /// 否则等于允许任意对端指定任意公钥去包装群密钥。
    /// </summary>
    [Fact]
    public async Task 握手响应被篡改_拒绝采用该公钥包装群密钥()
    {
        foreach (var tamper in new[] { TamperKind.SenderIdPublicKeyMismatch, TamperKind.SignatureMismatch })
        {
            var crypto = NewCrypto();
            var keyStore = new InMemoryKeyStore(crypto);
            var localIdentity = keyStore.GetOrCreateIdentity();
            var logs = new CollectingLogger<GroupChatService>();

            var claimed = crypto.GenerateKeyPair();   // 对端声称的身份
            var impostor = crypto.GenerateKeyPair();   // 冒名者
            var target = NodeId.FromPublicKey(claimed.PublicKey);

            var dht = new FakeDhtService(new NodeInfo
            {
                NodeId = NodeId.FromPublicKey(localIdentity.PublicKey),
                EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 46500),
                PublicKey = localIdentity.PublicKey
            });
            dht.RegisterStaticPeer(new NodeInfo
            {
                NodeId = target,
                EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 46620),
                PublicKey = null
            });

            // 先造一条自洽的响应信封，再按篡改类型破坏其中一处不变式。
            var valid = BuildEnvelope(claimed, claimed.PublicKey, claimed.NodeId.ToByteArray());
            MessageEnvelope forged = tamper switch
            {
                // 公钥与 SenderId 不自洽 → 命中「NodeId 绑定」守卫
                TamperKind.SenderIdPublicKeyMismatch =>
                    valid with { SenderPublicKey = impostor.PublicKey },
                // 签名不再覆盖信封实际内容 → 命中 ECDSA 守卫
                _ => valid with { Signature = impostor.PublicKey[..64] }
            };

            var responder = new ScriptedResponder([forged]);
            var service = new GroupChatService(
                dht, responder, crypto, keyStore, new InMemoryGroupMetadataStore(), logs);

            var group = await service.CreateGroupAsync($"篡改群-{tamper}", [target]);

            service.GetGroup(group.GroupId).ShouldNotBeNull();
            responder.Sent.OfType<GroupInviteMessage>().ShouldBeEmpty(
                $"被篡改的握手响应（{tamper}）不得导致群邀请发出");
            PeerPublicKeyRegistry.Shared.TryGet(target, out _).ShouldBeFalse(
                $"被篡改的公钥绝不能被登记进表（{tamper}）");
            logs.Entries.ShouldContain(e => e.Level >= LogLevel.Warning);
        }
    }

    /// <summary>
    /// <see cref="PeerPublicKeyRegistry"/> 自身：公钥必须与登记的 NodeId 自洽，
    /// 否则拒绝写入/读取（防止「用 A 的 NodeId 查到 B 的公钥」→ 群密钥被包装给错误接收方）。
    /// </summary>
    [Fact]
    public void 公钥登记表_拒绝不自洽的公钥()
    {
        var registry = new PeerPublicKeyRegistry();
        var crypto = NewCrypto();
        var alice = crypto.GenerateKeyPair();
        var bob = crypto.GenerateKeyPair();
        var bobId = NodeId.FromPublicKey(bob.PublicKey);

        registry.Record(bobId.ToByteArray(), bob.PublicKey).ShouldBeTrue();
        registry.TryGet(bobId, out var got).ShouldBeTrue();
        got.ShouldBe(bob.PublicKey);

        // 用 A 的 NodeId 登记 B 的公钥 → 拒绝
        registry.Record(NodeId.FromPublicKey(alice.PublicKey).ToByteArray(), bob.PublicKey)
            .ShouldBeFalse();
        // 长度非法 → 拒绝
        registry.Record(bobId.ToByteArray(), new byte[32]).ShouldBeFalse();
        registry.Record(bobId.ToByteArray(), null).ShouldBeFalse();
        registry.Record(bobId.ToByteArray(), Array.Empty<byte>()).ShouldBeFalse();
        registry.Record(new byte[3], bob.PublicKey).ShouldBeFalse();
        registry.Record(null, bob.PublicKey).ShouldBeFalse();

        // 上述拒绝不得污染既有条目
        registry.TryGet(bobId, out _).ShouldBeTrue();
    }

    /// <summary>
    /// <see cref="KeyExchangeHandler"/> 会把信封里已验签的对端长期公钥登记进表 ——
    /// 即「对端首次与我们通信后，其长期公钥即被学到」。
    /// </summary>
    [Fact]
    public async Task 密钥交换处理器_从已验签信封登记对端长期公钥()
    {
        var crypto = NewCrypto();
        var localKeyStore = new InMemoryKeyStore(crypto);
        var registry = new PeerPublicKeyRegistry();
        var handler = new KeyExchangeHandler(
            crypto, localKeyStore, NullLogger<KeyExchangeHandler>.Instance, NewDhtStub(localKeyStore), registry);

        var peer = crypto.GenerateKeyPair();
        var request = new KeyExchangeMessage
        {
            SenderId = peer.NodeId.ToByteArray(),
            ConversationId = "hello",
            EphemeralPublicKey = crypto.GenerateKeyPair().PublicKey,
            IsResponse = false
        };
        var envelope = BuildEnvelope(peer, peer.PublicKey, peer.NodeId.ToByteArray(),
            new MessagePackSerializer().Serialize<Message>(request));

        var connection = new ScriptedResponder(Array.Empty<MessageEnvelope>());
        await handler.HandleAsync(request, connection, envelope, CancellationToken.None);

        registry.TryGet(NodeId.FromPublicKey(peer.PublicKey), out var learned)
            .ShouldBeTrue("应已登记对端长期公钥");
        learned.ShouldBe(peer.PublicKey);

        // 处理器回写的响应信封必须仍带**本机**真实身份（下游据此校验对端）。
        var written = connection.LastWrittenEnvelopes.ShouldHaveSingleItem();
        written.SenderId.ShouldBe(localKeyStore.GetOrCreateIdentity().NodeId.ToByteArray());
        written.SenderPublicKey.ShouldBe(localKeyStore.GetOrCreateIdentity().PublicKey);
    }

    /// <summary>
    /// 负向：信封缺少长期公钥时不得登记（否则登记表会被空值污染）。
    /// </summary>
    [Fact]
    public async Task 密钥交换处理器_信封缺少公钥时不登记()
    {
        var crypto = NewCrypto();
        var localKeyStore = new InMemoryKeyStore(crypto);
        var registry = new PeerPublicKeyRegistry();
        var handler = new KeyExchangeHandler(
            crypto, localKeyStore, NullLogger<KeyExchangeHandler>.Instance, NewDhtStub(localKeyStore), registry);

        var peer = crypto.GenerateKeyPair();
        var request = new KeyExchangeMessage
        {
            SenderId = peer.NodeId.ToByteArray(),
            ConversationId = "hello",
            EphemeralPublicKey = crypto.GenerateKeyPair().PublicKey,
            IsResponse = false
        };
        var envelope = BuildEnvelope(peer, peer.PublicKey, peer.NodeId.ToByteArray(),
            new MessagePackSerializer().Serialize<Message>(request)) with { SenderPublicKey = null };

        await handler.HandleAsync(request, new ScriptedResponder([]), envelope, CancellationToken.None);

        registry.TryGet(NodeId.FromPublicKey(peer.PublicKey), out _).ShouldBeFalse();
    }

    #region 测试脚手架

    /// <summary>
    /// KeyExchangeHandler 自 task-23 起需要 <c>IDhtService</c>（用于把「/connect 而来的对端」
    /// 反向登记为静态对端）。本组用例不测那条路径，给一个最简替身即可。
    /// </summary>
    private static FakeDhtService NewDhtStub(InMemoryKeyStore keyStore)
    {
        var identity = keyStore.GetOrCreateIdentity();
        return new FakeDhtService(new NodeInfo
        {
            NodeId = NodeId.FromPublicKey(identity.PublicKey),
            EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 46701),
            PublicKey = identity.PublicKey
        });
    }

    private enum TamperKind
    {
        SenderIdPublicKeyMismatch,
        SignatureMismatch
    }

    private static MessageEnvelope BuildEnvelope(
        KeyPair identity, byte[] senderPublicKey, byte[] senderId, byte[]? payload = null)
    {
        var unsigned = new MessageEnvelope
        {
            Version = 1,
            MessageType = MessageType.KeyExchange,
            SequenceNumber = 42,
            SenderId = senderId,
            SenderPublicKey = senderPublicKey,
            Payload = payload ?? new MessagePackSerializer().Serialize<Message>(new KeyExchangeMessage
            {
                SenderId = senderId,
                ConversationId = "probe",
                EphemeralPublicKey = Array.Empty<byte>(),
                IsResponse = true
            })
        };
        return MessageRouter.SignEnvelope(unsigned, identity, NewCrypto());
    }

    private static (GroupChatService Service, InMemoryKeyStore KeyStore, FakeDhtService Dht,
        RecordingMessageRouter Router, CollectingLogger<GroupChatService> Logs) BuildService()
    {
        var encryption = NewCrypto();
        var keyStore = new InMemoryKeyStore(encryption);
        var identity = keyStore.GetOrCreateIdentity();
        var local = new NodeInfo
        {
            NodeId = NodeId.FromPublicKey(identity.PublicKey),
            EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 46400),
            PublicKey = identity.PublicKey
        };
        var dht = new FakeDhtService(local);
        var router = new RecordingMessageRouter();
        var logs = new CollectingLogger<GroupChatService>();
        var service = new GroupChatService(
            dht, router, encryption, keyStore, new InMemoryGroupMetadataStore(), logs);
        return (service, keyStore, dht, router, logs);
    }

    /// <summary>
    /// 预置好响应信封的假连接 —— 探针写入请求后即可「收到」脚本里的响应。
    /// 同时记录处理器写出的信封，供断言使用。
    /// </summary>
    private sealed class ScriptedResponder : IMessageRouter, ITcpConnection
    {
        private readonly Queue<MessageEnvelope> _responses;

        public ScriptedResponder(IEnumerable<MessageEnvelope> responses) =>
            _responses = new Queue<MessageEnvelope>(responses);

        public List<Message> Sent { get; } = [];
        public List<MessageEnvelope> LastWrittenEnvelopes { get; } = [];

        // --- IMessageRouter ---
        public void RegisterHandler<T>(IMessageHandler<T> handler) where T : Message { }
        public void UnregisterHandler(MessageType messageType) { }
        public Task RouteIncomingAsync(MessageEnvelope e, ITcpConnection s, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task SendAsync(NodeInfo recipient, Message message, CancellationToken ct = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }

        public Task SendViaConnectionAsync(ITcpConnection connection, Message message, CancellationToken ct = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }

        public Task<ITcpConnection> GetOrCreateConnectionAsync(NodeInfo node, CancellationToken ct = default)
            => Task.FromResult<ITcpConnection>(this);

        public Task CloseConnectionAsync(byte[] nodeId) => Task.CompletedTask;

        // --- ITcpConnection ---
        public Guid ConnectionId { get; } = Guid.NewGuid();
        public System.Net.IPEndPoint RemoteEndPoint { get; } = new(System.Net.IPAddress.Loopback, 49999);
        public bool IsConnected => true;

        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            LastWrittenEnvelopes.Add(MessageRouter.DeserializeEnvelope(data));
            return Task.CompletedTask;
        }

        public Task<ReadOnlyMemory<byte>> ReceiveMessageAsync(CancellationToken ct = default)
        {
            if (_responses.Count == 0)
                return Task.FromResult(ReadOnlyMemory<byte>.Empty);

            var envelope = _responses.Dequeue();
            // 探针必须读到的是**响应**而不是自己的请求回显。
            if (envelope.MessageType != MessageType.KeyExchange)
                return Task.FromResult(ReadOnlyMemory<byte>.Empty);

            return Task.FromResult<ReadOnlyMemory<byte>>(
                MessageRouter.SerializeEnvelope(envelope));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>捕获日志的 ILogger，用于断言「不再静默」。</summary>
    private sealed class CollectingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }

    #endregion
}
