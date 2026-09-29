using System.Net;
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
    public required ITcpTransport Tcp { get; init; }
    public required MessageRouter Router { get; init; }
    /// <summary>包装 <see cref="Router"/> 的捕获器，记录每次出站发送的真实 MessagePack 载荷（= MessageEnvelope.Payload）。</summary>
    public required PayloadCapturingMessageRouter CapturingRouter { get; init; }
    public required ChatService Chat { get; init; }

    /// <summary>
    /// 本节点的消息 handler 投递聊天事件用的发布器。
    /// <para>
    /// 它<b>必须</b>与 <see cref="Chat"/> 是同一个对象：事件只投递进 <c>ChatService</c> 持有的
    /// 那一条通道，UI 读的 <c>Chat.OnMessageReceived</c> 必须是同一条。若这里是另一个实例，
    /// 「消息收到但界面永远不显示」的缺陷就会复活，而所有断言 handler 输出的测试仍然全绿。
    /// <c>ChatEventDeliveryTests</c> 里有断言把这条不变量钉死。
    /// </para>
    /// </summary>
    public required IChatEventPublisher Events { get; init; }
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

    /// <summary>
    /// 本节点真实生效的入站重放防护。
    /// <para>
    /// 刻意<b>不允许</b>注入一个「永远放行」的替身：那等于测试环境里根本没有防护，
    /// 重放防护一旦被误删/改坏，用替身的用例照样全绿。默认就是真实的
    /// <see cref="MessageReplayGuard"/>（1h / 5min / 1024 每桶）。
    /// </para>
    /// </summary>
    public required MessageReplayGuard ReplayGuard { get; init; }

    /// <summary>
    /// 本节点<b>收到</b>的聊天事件（过滤掉自己发出去的本地回显）—— 这才是「别人发来的消息」，
    /// 也是绝大多数集成测试要断言的东西。
    /// <para>
    /// 之所以要过滤：出站消息的本地回显与入站消息现在共用同一条通道（UI 两者都要显示），
    /// 一个既发又收的节点（如双向私聊用例里的 alice）若直接读 <see cref="Chat"/> 的原始流，
    /// 会先读到自己刚发出去的那条。
    /// </para>
    /// </summary>
    public IAsyncEnumerable<ChatMessageEvent> IncomingMessages =>
        AsyncEnumerableFilter.Where(Chat.OnMessageReceived, e => !e.IsOutgoing);

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

    /// <param name="name">节点名（仅用于日志与断言可读性）。</param>
    /// <param name="loggerFactory">
    /// 可选日志工厂。传了就会同时喂给 <see cref="MessageRouter"/> 与
    /// <see cref="MessageReplayGuard"/>，用于断言「重放被拒」这件事在日志里可查。
    /// 不传则全程 <c>NullLogger</c>（默认行为，不影响任何既有调用方）。
    /// </param>
    /// <param name="connectionDecorator">
    /// 可选出站连接装饰器。在真实连接外套一层，用来抓取<b>真实上线的字节</b>
    /// （重放测试需要拿到攻击者录下的那一份原样字节）。
    /// </param>
    /// <param name="replayGuard">
    /// 可选重放防护。默认构造真实的 <see cref="MessageReplayGuard"/>；
    /// 测试需要固定时钟/自定义窗口时才显式传入。
    /// </param>
    public static NodeHarness Start(
        string name,
        ILoggerFactory? loggerFactory = null,
        Func<ITcpConnection, ITcpConnection>? connectionDecorator = null,
        MessageReplayGuard? replayGuard = null)
    {
        var encryption = new AesGcmEncryptionService(NullLogger<AesGcmEncryptionService>.Instance);
        var keyStore = new InMemoryKeyStore(encryption);
        var identity = keyStore.GetOrCreateIdentity();
        var nodeId = NodeId.FromPublicKey(identity.PublicKey);

        var factory = loggerFactory ?? NullLoggerFactory.Instance;
        var realTcp = new TcpTransport(
            factory.CreateLogger<TcpTransport>(), NullLoggerFactory.Instance);
        realTcp.StartListeningAsync(0).GetAwaiter().GetResult();
        ITcpTransport tcp = connectionDecorator is null
            ? realTcp
            : new DecoratingTcpTransport(realTcp, connectionDecorator);

        var serializer = new MessagePackSerializer();
        // 注入 IEncryptionService + IKeyStore —— MessageRouter 现在用长期 ECDSA 私钥签出站消息，
        // 并在 RouteIncomingAsync 入口验签。见 2026-09-21-message-signing。
        // 第三个注入项 IReplayGuard 是**必填**：漏配必须是编译错误，不能静默等于「关闭防护」。
        var guard = replayGuard ?? new MessageReplayGuard(factory.CreateLogger<MessageReplayGuard>());
        var router = new MessageRouter(
            tcp, serializer, encryption, keyStore, guard, factory.CreateLogger<MessageRouter>());
        var capturingRouter = new PayloadCapturingMessageRouter(router, serializer);
        var localNode = new NodeInfo
        {
            NodeId = nodeId,
            EndPoint = new IPEndPoint(IPAddress.Loopback, tcp.ListenPort),
            PublicKey = identity.PublicKey,
            State = PeerState.Online
        };
        var dht = new FakeDhtService(localNode);

        // ChatService 必须是**先**建出来的：它是整条聊天事件流的唯一持有者，
        // 两个消息 handler 要把解密后的事件投递进它持有的那条通道。
        // 见 REPAIR-PLAN B3 —— 此前两个 handler 各持一条私有通道，UI 读的是 ChatService 的，
        // 于是「收到」与「看到」之间没有任何连线（消息收到但界面永远空白）。
        var chat = new ChatService(dht, capturingRouter, encryption, keyStore,
            new MessageReplayGuard(NullLogger<MessageReplayGuard>.Instance),
            NullLogger<ChatService>.Instance);

        // 注意这里传的是同一个 chat 实例，不是新建一个「只当发布器用」的对象。
        var privateHandler = new PrivateMessageHandler(
            encryption, keyStore, chat, NullLogger<PrivateMessageHandler>.Instance);
        var groupHandler = new GroupMessageHandler(
            encryption, keyStore, chat, NullLogger<GroupMessageHandler>.Instance);
        // dht = 本 fixture 的 FakeDhtService（上方 L141 已建）。KeyExchangeHandler 需要它来把
        // 「主动 /connect 而来的对端」用**已验签的** envelope.SenderId + RemoteEndPoint 反向登记
        // 为静态对端（/connect 变双向，见 task-23）。
        var keyExchange = new KeyExchangeHandler(
            encryption, keyStore, NullLogger<KeyExchangeHandler>.Instance, dht);
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
            Chat = chat,
            Events = chat,
            Group = new GroupChatService(dht, router, encryption, keyStore, new InMemoryGroupMetadataStore()),
            Files = new FileTransferService(
                dht, router, encryption, keyStore, NullLogger<FileTransferService>.Instance),
            KeyStore = keyStore,
            Dht = dht,
            Encryption = encryption,
            PrivateHandler = privateHandler,
            GroupHandler = groupHandler,
            KeyExchangeHandler = keyExchange,
            InviteHandler = inviteHandler,
            NotifyHandler = notifyHandler,
            LocalNode = localNode,
            Identity = identity,
            ReplayGuard = guard
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
/// 出站连接装饰器 — 把真实 <see cref="ITcpTransport"/> 造出的每条连接套一层用户提供的包装。
/// <para>
/// 存在的理由：重放测试需要拿到<b>真实上线的字节</b>（攻击者录下的那一份）。
/// <c>MessageRouter</c> 自己负责签名与成帧，测试要复现重放就必须在它把字节写出去的那一刻截下来，
/// 而不是用测试重新签一遍 —— 后者测的是「重新签名」，不是「重放」。
/// </para>
/// </summary>
public sealed class DecoratingTcpTransport : ITcpTransport
{
    private readonly ITcpTransport _inner;
    private readonly Func<ITcpConnection, ITcpConnection> _decorate;

    public DecoratingTcpTransport(ITcpTransport inner, Func<ITcpConnection, ITcpConnection> decorate)
    {
        _inner = inner;
        _decorate = decorate;
    }

    public int ListenPort => _inner.ListenPort;

    public IPEndPoint? LocalEndPoint => _inner.LocalEndPoint;

    public IAsyncEnumerable<ITcpConnection> IncomingConnections => _inner.IncomingConnections;

    public Task StartListeningAsync(int port, CancellationToken ct = default)
        => _inner.StartListeningAsync(port, ct);

    public async Task<ITcpConnection> ConnectAsync(IPEndPoint endpoint, CancellationToken ct = default)
        => _decorate(await _inner.ConnectAsync(endpoint, ct));

    public Task<ITcpConnection> AcceptAsync(CancellationToken ct = default)
        => _inner.AcceptAsync(ct);

    public void Dispose() => _inner.Dispose();
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
