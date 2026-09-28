using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using P2PChat.Chat.Handlers;
using P2PChat.Chat.Routing;
using P2PChat.Chat.Services;
using P2PChat.Core.Abstractions;
using P2PChat.Core.Enums;
using P2PChat.Core.Extensions;
using P2PChat.Core.Models;
using P2PChat.Crypto.Encryption;
using P2PChat.FileTransfer.Services;
using P2PChat.Networking.Transport;

namespace P2PChat.Integration.Tests.Support;

/// <summary>
/// 单节点运行栈 — 真实 TcpTransport + 真实 MessageRouter + 真实序列化器 + 真实加解密 + 真实 handlers，
/// 只有 DHT 节点发现用 FakeDhtService 预置（真实 DHT 发现需要公网，不在本测试范围）。
/// 两个 NodeHarness 组成一次真实的两节点 TCP 交互。
/// </summary>
public sealed class NodeHarness : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();

    private NodeHarness(string name) => Name = name;

    public string Name { get; }
    public required TcpTransport Tcp { get; init; }
    public required MessageRouter Router { get; init; }
    /// <summary>包装 <see cref="Router"/> 的捕获器，记录每次出站发送的真实 MessagePack 载荷（= MessageEnvelope.Payload）。</summary>
    public required PayloadCapturingMessageRouter CapturingRouter { get; init; }
    public required ChatService Chat { get; init; }
    public required GroupChatService Group { get; init; }
    public required FileTransferService Files { get; init; }
    public required InMemoryKeyStore KeyStore { get; init; }
    public required FakeDhtService Dht { get; init; }
    public required AesGcmEncryptionService Encryption { get; init; }
    public required PrivateMessageHandler PrivateHandler { get; init; }
    public required GroupMessageHandler GroupHandler { get; init; }
    public required KeyExchangeHandler KeyExchangeHandler { get; init; }
    public required GroupInviteHandler InviteHandler { get; init; }
    public required GroupNotifyHandler NotifyHandler { get; init; }
    public required NodeInfo LocalNode { get; init; }
    public required KeyPair Identity { get; init; }

    /// <summary>监听端口（真实绑定的端口）。</summary>
    public int Port => Tcp.ListenPort;

    /// <summary>
    /// 消息层使用的 SenderId —— 必须与本节点真实身份一致。
    /// <para>
    /// 曾经写成 <c>Identity.PublicKey.Take(20)</c>，那是 P-256 SPKI DER 的固定算法头，
    /// 对每个节点都是同一串字节；测试脚手架这样复刻后，实现里的同一缺陷就永远测不出来。
    /// 现在直接复用生产代码的唯一定义点 <see cref="KeyPair.NodeId"/>。
    /// </para>
    /// </summary>
    public byte[] SenderId => Identity.NodeId.ToByteArray();

    public static NodeHarness Start(string name)
    {
        var encryption = new AesGcmEncryptionService(NullLogger<AesGcmEncryptionService>.Instance);
        var keyStore = new InMemoryKeyStore(encryption);
        var identity = keyStore.GetOrCreateIdentity();
        var nodeId = NodeId.FromPublicKey(identity.PublicKey);

        var tcp = new TcpTransport(NullLogger<TcpTransport>.Instance, NullLoggerFactory.Instance);
        tcp.StartListeningAsync(0).GetAwaiter().GetResult();

        var serializer = new MessagePackSerializer();
        // 注入 IEncryptionService + IKeyStore —— MessageRouter 现在用长期 ECDSA 私钥签出站消息，
        // 并在 RouteIncomingAsync 入口验签。见 2026-09-21-message-signing。
        var router = new MessageRouter(
            tcp, serializer, encryption, keyStore, NullLogger<MessageRouter>.Instance);
        var capturingRouter = new PayloadCapturingMessageRouter(router, serializer);
        var localNode = new NodeInfo
        {
            NodeId = nodeId,
            EndPoint = new IPEndPoint(IPAddress.Loopback, tcp.ListenPort),
            PublicKey = identity.PublicKey,
            State = PeerState.Online
        };
        var dht = new FakeDhtService(localNode);

        var privateHandler = new PrivateMessageHandler(encryption, keyStore, NullLogger<PrivateMessageHandler>.Instance);
        var groupHandler = new GroupMessageHandler(encryption, keyStore, NullLogger<GroupMessageHandler>.Instance);
        var keyExchange = new KeyExchangeHandler(encryption, keyStore, NullLogger<KeyExchangeHandler>.Instance);
        var inviteHandler = new GroupInviteHandler(encryption, keyStore, NullLogger<GroupInviteHandler>.Instance);
        var notifyHandler = new GroupNotifyHandler(NullLogger<GroupNotifyHandler>.Instance);

        router.RegisterHandler(privateHandler);
        router.RegisterHandler(groupHandler);
        router.RegisterHandler(keyExchange);
        router.RegisterHandler(inviteHandler);
        router.RegisterHandler(notifyHandler);

        var harness = new NodeHarness(name)
        {
            Tcp = tcp,
            Router = router,
            CapturingRouter = capturingRouter,
            Chat = new ChatService(dht, capturingRouter, encryption, keyStore, NullLogger<ChatService>.Instance),
            Group = new GroupChatService(dht, router, encryption, keyStore, new InMemoryGroupMetadataStore()),
            Files = new FileTransferService(dht, router, encryption, NullLogger<FileTransferService>.Instance),
            KeyStore = keyStore,
            Dht = dht,
            Encryption = encryption,
            PrivateHandler = privateHandler,
            GroupHandler = groupHandler,
            KeyExchangeHandler = keyExchange,
            InviteHandler = inviteHandler,
            NotifyHandler = notifyHandler,
            LocalNode = localNode,
            Identity = identity
        };

        _ = harness.AcceptLoopAsync();
        return harness;
    }

    /// <summary>让 A 通过 DHT 能"发现" B（真实发现需要公网，这里预置对端信息）。</summary>
    public void Discover(NodeHarness peer) => Dht.Register(peer.LocalNode);

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var connection = await Tcp.AcceptAsync(_cts.Token);
                _ = Router.ProcessIncomingConnectionAsync(connection, _cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch { break; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await _cts.CancelAsync(); } catch { }
        Tcp.Dispose();
        _cts.Dispose();
    }
}

/// <summary>
/// 包装真实 <see cref="MessageRouter"/> 的捕获器：每次出站发送都会记录 (消息对象, MessagePack 载荷字节)。
/// 载荷用与 MessageRouter 相同的 <see cref="ISerializer"/> 和相同的静态类型 <see cref="Message"/> 序列化，
/// 因此捕获到的字节就是真实写入 TCP 的 <c>MessageEnvelope.Payload</c>。
/// 用于端到端断言"线路上的内容不是明文"。
/// </summary>
public sealed class PayloadCapturingMessageRouter : IMessageRouter
{
    private readonly MessageRouter _inner;
    private readonly ISerializer _serializer;
    private readonly List<(Message Message, byte[] Payload)> _captured = [];

    public PayloadCapturingMessageRouter(MessageRouter inner, ISerializer serializer)
    {
        _inner = inner;
        _serializer = serializer;
    }

    public IReadOnlyList<(Message Message, byte[] Payload)> Captured
    {
        get { lock (_captured) return _captured.ToList(); }
    }

    public void Clear()
    {
        lock (_captured) _captured.Clear();
    }

    private void Capture(Message message)
    {
        var payload = _serializer.Serialize(message);
        lock (_captured) _captured.Add((message, payload));
    }

    public void RegisterHandler<T>(IMessageHandler<T> handler) where T : Message => _inner.RegisterHandler(handler);

    public void UnregisterHandler(MessageType messageType) => _inner.UnregisterHandler(messageType);

    public Task RouteIncomingAsync(MessageEnvelope envelope, ITcpConnection sender, CancellationToken ct = default)
        => _inner.RouteIncomingAsync(envelope, sender, ct);

    public async Task SendAsync(NodeInfo recipient, Message message, CancellationToken ct = default)
    {
        Capture(message);
        await _inner.SendAsync(recipient, message, ct);
    }

    public async Task SendViaConnectionAsync(ITcpConnection connection, Message message, CancellationToken ct = default)
    {
        Capture(message);
        await _inner.SendViaConnectionAsync(connection, message, ct);
    }

    public Task<ITcpConnection> GetOrCreateConnectionAsync(NodeInfo node, CancellationToken ct = default)
        => _inner.GetOrCreateConnectionAsync(node, ct);

    public Task CloseConnectionAsync(byte[] nodeId) => _inner.CloseConnectionAsync(nodeId);
}
