using System.Net;
using P2PChat.Core.Abstractions;
using P2PChat.Core.Enums;
using P2PChat.Core.Models;

namespace P2PChat.Integration.Tests.Support;

/// <summary>
/// 内存密钥存储 — 测试专用，避免触碰 msg 真实磁盘 (FileBackedKeyStore 依赖 DataPath 静态状态)。
/// 语义与 FileBackedKeyStore 一致。
/// </summary>
public sealed class InMemoryKeyStore : IKeyStore
{
    private readonly Dictionary<string, byte[]> _sessionKeys = [];
    private readonly Dictionary<string, byte[]> _groupKeys = [];
    private readonly IEncryptionService _encryption;
    private KeyPair? _identity;

    public InMemoryKeyStore(IEncryptionService encryption) => _encryption = encryption;

    public KeyPair GetOrCreateIdentity() => _identity ??= _encryption.GenerateKeyPair();

    public byte[]? GetSessionKey(NodeId peerId)
        => _sessionKeys.TryGetValue(peerId.ToHexString(), out var v) ? v : null;

    public void SetSessionKey(NodeId peerId, byte[] sharedSecret)
        => _sessionKeys[peerId.ToHexString()] = sharedSecret;

    public void RemoveSessionKey(NodeId peerId)
        => _sessionKeys.Remove(peerId.ToHexString());

    public byte[]? GetGroupKey(string groupId)
        => _groupKeys.TryGetValue(groupId, out var v) ? v : null;

    public void SetGroupKey(string groupId, byte[] key) => _groupKeys[groupId] = key;

    public void RemoveGroupKey(string groupId) => _groupKeys.Remove(groupId);

    public IReadOnlyList<string> GetKnownGroupIds() => _groupKeys.Keys.ToList();
}

/// <summary>
/// 内存群组元数据存储 — 测试专用，避免触碰真实磁盘 (FileBackedGroupMetadataStore 依赖 DataPath)。
/// 语义与生产实现一致；持久化相关测试请直接使用真实实现。
/// </summary>
public sealed class InMemoryGroupMetadataStore : IGroupMetadataStore
{
    private readonly Dictionary<string, GroupInfo> _groups = [];

    public IReadOnlyList<GroupInfo> LoadAll() => _groups.Values.ToList();

    public void Save(IEnumerable<GroupInfo> groups)
    {
        _groups.Clear();
        foreach (var g in groups) _groups[g.GroupId] = g;
    }

    public void Remove(string groupId) => _groups.Remove(groupId);
}

/// <summary>
/// 测试用 DHT 服务 — 只用一张预置的"节点ID → NodeInfo"表实现 FindNodeAsync，
/// 其余 DHT 行为 (网络发现) 不在集成测试范围内。
/// </summary>
public sealed class FakeDhtService : IDhtService
{
    private readonly Dictionary<string, NodeInfo> _nodes = [];

    public FakeDhtService(NodeInfo localNode) => LocalNode = localNode;

    public NodeInfo LocalNode { get; }

    /// <summary>预置一个可被发现的节点。</summary>
    public void Register(NodeInfo node) => _nodes[node.NodeId.ToHexString()] = node;

    public Task BootstrapAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<NodeInfo?> FindNodeAsync(NodeId targetId, CancellationToken ct = default)
        => Task.FromResult(_nodes.TryGetValue(targetId.ToHexString(), out var n) ? n : null);

    /// <summary>
    /// 登记静态对端 —— 与真实实现（<c>MainlineDhtService.RegisterStaticPeer</c>）语义一致：
    /// <c>FindNodeAsync</c> 必定能命中；且**同一 NodeId 端点不一致时保留先登记的端点，不覆盖**。
    /// <para>
    /// 契约来源：手工登记是用户带外的主动输入，而自报端点是对端在签名载荷里的自述
    /// （签名只能证明「这话是它说的」，无法判定它有没有撒谎）。替身若无条件覆盖，
    /// 集成测试跑的就是一个与产品不同的世界，断言也就失去意义。
    /// </para>
    /// </summary>
    public void RegisterStaticPeer(NodeInfo node)
    {
        var key = node.NodeId.ToHexString();
        if (_nodes.TryGetValue(key, out var existing) && !Equals(existing.EndPoint, node.EndPoint))
            return;   // 冲突：保留已登记的端点（产品侧同时会记 LogWarning）
        _nodes[key] = node;
    }

    public Task StoreAsync(byte[] key, byte[] value, CancellationToken ct = default) => Task.CompletedTask;

    public Task<byte[]?> FindValueAsync(byte[] key, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);

    public Task<bool> PingAsync(NodeInfo node, CancellationToken ct = default) => Task.FromResult(true);

    public IReadOnlyList<NodeInfo> GetAllKnownNodes() => _nodes.Values.ToList();

    public IAsyncEnumerable<PeerDiscoveryEventArgs> OnPeerDiscovered => EmptyAsync();

    private static async IAsyncEnumerable<PeerDiscoveryEventArgs> EmptyAsync()
    {
        await Task.CompletedTask;
        yield break;
    }
}

/// <summary>测试用 TCP 连接占位（用于 HandleAsync 的 sender 参数）。</summary>
public sealed class FakeTcpConnection : ITcpConnection
{
    public Guid ConnectionId { get; } = Guid.NewGuid();
    public IPEndPoint RemoteEndPoint { get; } = new(IPAddress.Loopback, 59999);
    public bool IsConnected => true;
    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => Task.CompletedTask;
    public Task<ReadOnlyMemory<byte>> ReceiveMessageAsync(CancellationToken ct = default)
        => Task.FromResult(ReadOnlyMemory<byte>.Empty);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// 记录型消息路由器 — 不真正发包，只记录 SendAsync 收到的消息。
/// 用于验证"服务层是否把正确的消息交给路由器"。
/// </summary>
public sealed class RecordingMessageRouter : IMessageRouter
{
    private readonly List<Message> _sent = [];
    private readonly List<MessageType> _registeredHandlers = [];

    public IReadOnlyList<Message> Sent
    {
        get { lock (_sent) return _sent.ToList(); }
    }

    public IReadOnlyList<MessageType> RegisteredHandlers
    {
        get { lock (_registeredHandlers) return _registeredHandlers.ToList(); }
    }

    /// <summary>清空已记录的消息（便于分阶段断言）。</summary>
    public void Clear()
    {
        lock (_sent) _sent.Clear();
    }

    public void RegisterHandler<T>(IMessageHandler<T> handler) where T : Message
    {
        lock (_registeredHandlers) _registeredHandlers.Add(handler.MessageType);
    }

    public void UnregisterHandler(MessageType messageType) { }

    public Task RouteIncomingAsync(MessageEnvelope envelope, ITcpConnection sender, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SendAsync(NodeInfo recipient, Message message, CancellationToken ct = default)
    {
        lock (_sent) _sent.Add(message);
        return Task.CompletedTask;
    }

    public Task SendViaConnectionAsync(ITcpConnection connection, Message message, CancellationToken ct = default)
    {
        lock (_sent) _sent.Add(message);
        return Task.CompletedTask;
    }

    public Task<ITcpConnection> GetOrCreateConnectionAsync(NodeInfo node, CancellationToken ct = default)
        => Task.FromResult<ITcpConnection>(new FakeTcpConnection());

    public Task CloseConnectionAsync(byte[] nodeId) => Task.CompletedTask;
}

/// <summary>
/// 捕获型聊天事件发布器 —— 直接单测消息 handler 时用（不经 DI、不经 ChatService）。
/// <para>
/// 存在的意义：handler 已不再持有自己的 <c>Channel&lt;ChatMessageEvent&gt;</c>，
/// 输出统一走 <see cref="IChatEventPublisher"/>。单测 handler 就必须提供一个发布器替身，
/// 并在替身上断言 —— 这也把「handler 必须通过发布器投递」变成了编译期事实。
/// </para>
/// </summary>
public sealed class CapturingChatEventPublisher : IChatEventPublisher
{
    private readonly System.Threading.Channels.Channel<ChatMessageEvent> _channel =
        System.Threading.Channels.Channel.CreateUnbounded<ChatMessageEvent>();

    public IAsyncEnumerable<ChatMessageEvent> Events => _channel.Reader.ReadAllAsync();

    /// <summary>已投递的事件（快照），用于「没有事件」这类否定断言。</summary>
    public IReadOnlyList<ChatMessageEvent> Published
    {
        get { lock (_published) return _published.ToList(); }
    }

    private readonly List<ChatMessageEvent> _published = [];

    public Task PublishAsync(ChatMessageEvent chatEvent, CancellationToken ct = default)
    {
        lock (_published) _published.Add(chatEvent);
        return _channel.Writer.WriteAsync(chatEvent, ct).AsTask();
    }
}

/// <summary>
/// IAsyncEnumerable 的同步过滤helper（测试项目不引入 System.Linq.Async，且不得用反射）。
/// </summary>
public static class AsyncEnumerableFilter
{
    public static async IAsyncEnumerable<T> Where<T>(
        IAsyncEnumerable<T> source,
        Func<T, bool> predicate,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in source.WithCancellation(ct).ConfigureAwait(false))
        {
            if (predicate(item))
                yield return item;
        }
    }
}

/// <summary>轮询等待帮助器：在超时内等待条件成立，返回是否成功。</summary>
public static class Wait
{
    public static async Task<bool> UntilAsync(Func<bool> condition, int timeoutMs = 10000, int intervalMs = 25)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            await Task.Delay(intervalMs);
        }
        return condition();
    }
}
