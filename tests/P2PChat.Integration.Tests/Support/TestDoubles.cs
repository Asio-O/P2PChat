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
