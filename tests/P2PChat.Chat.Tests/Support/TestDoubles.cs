using System.Net;
using System.Text.Json;
using P2PChat.Core.Abstractions;
using P2PChat.Core.Enums;
using P2PChat.Core.Models;
using P2PChat.Crypto.Keys;
using P2PChat.Core.Extensions;

namespace P2PChat.Chat.Tests.Support;

/// <summary>
/// 内存密钥存储 — 测试专用，避免触碰真实磁盘（<c>FileBackedKeyStore</c> 依赖 <see cref="DataPath"/> 静态状态）。
/// 语义与生产实现一致，并额外记录调用以断言「是否发生了密钥交换」。
/// </summary>
public sealed class InMemoryKeyStore : IKeyStore
{
    private readonly Dictionary<string, byte[]> _sessionKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _groupKeys = new(StringComparer.Ordinal);
    private readonly IEncryptionService _encryption;
    private KeyPair? _identity;

    public InMemoryKeyStore(IEncryptionService encryption) => _encryption = encryption;

    /// <summary>构造时直接指定身份，便于断言「信封 SenderId 必须是本节点真实身份」。</summary>
    public InMemoryKeyStore(IEncryptionService encryption, KeyPair identity)
    {
        _encryption = encryption;
        _identity = identity;
    }

    /// <summary>已登记的会话密钥对端（hex），用于断言交换是否真的发生过。</summary>
    public List<string> SessionKeyWrites { get; } = [];

    public KeyPair GetOrCreateIdentity() => _identity ??= _encryption.GenerateKeyPair();

    public byte[]? GetSessionKey(NodeId peerId)
        => _sessionKeys.TryGetValue(peerId.ToHexString(), out var v) ? v : null;

    public void SetSessionKey(NodeId peerId, byte[] sharedSecret)
    {
        _sessionKeys[peerId.ToHexString()] = sharedSecret;
        SessionKeyWrites.Add(peerId.ToHexString());
    }

    public void RemoveSessionKey(NodeId peerId) => _sessionKeys.Remove(peerId.ToHexString());

    public byte[]? GetGroupKey(string groupId)
        => _groupKeys.TryGetValue(groupId, out var v) ? v : null;

    public void SetGroupKey(string groupId, byte[] key) => _groupKeys[groupId] = key;

    public void RemoveGroupKey(string groupId) => _groupKeys.Remove(groupId);

    public IReadOnlyList<string> GetKnownGroupIds() => _groupKeys.Keys.ToList();
}

/// <summary>
/// 内存群组元数据存储 — 记录 <see cref="IGroupMetadataStore"/> 的每次写入，便于断言落盘时机与内容。
/// </summary>
public sealed class RecordingGroupMetadataStore : IGroupMetadataStore
{
    private readonly Dictionary<string, GroupInfo> _groups = new(StringComparer.Ordinal);

    /// <summary>被显式移除过的群组 ID。</summary>
    public List<string> Removed { get; } = [];

    /// <summary>Save 调用次数。</summary>
    public int SaveCount { get; private set; }

    public IReadOnlyList<GroupInfo> LoadAll() => _groups.Values.ToList();

    public void Save(IEnumerable<GroupInfo> groups)
    {
        _groups.Clear();
        foreach (var g in groups) _groups[g.GroupId] = g;
        SaveCount++;
    }

    public void Remove(string groupId)
    {
        Removed.Add(groupId);
        _groups.Remove(groupId);
    }

    /// <summary>预置一个磁盘上已存在的群组（模拟重启前的持久化状态）。</summary>
    public void Seed(GroupInfo group) => _groups[group.GroupId] = group;

    public GroupInfo? Stored(string groupId)
        => _groups.TryGetValue(groupId, out var g) ? g : null;
}

/// <summary>
/// 桩 DHT 服务 — 只实现「节点 ID → NodeInfo」的查表，并把查询记录下来。
/// <para>
/// 端点解析优先级（显式端点 &gt; 静态对端 &gt; DHT 路由表）由 <c>MainlineDhtService</c> 实现，
/// 已由 <c>IdentityAndEndpointTests.静态对端_*</c> 覆盖；本替身只负责把「DHT 解析结果」
/// 原样交给 Chat 层，用来断言 Chat 层不会私自改写端点。
/// </para>
/// </summary>
public sealed class StubDhtService : IDhtService
{
    private readonly Dictionary<string, NodeInfo> _nodes = new(StringComparer.Ordinal);

    public StubDhtService(NodeInfo localNode) => LocalNode = localNode;

    public NodeInfo LocalNode { get; }

    /// <summary>被查询过的目标节点 ID（hex），按调用顺序。</summary>
    public List<string> FindNodeCalls { get; } = [];

    /// <summary>登记一个可被 FindNodeAsync 命中的节点。</summary>
    public StubDhtService Register(NodeInfo node)
    {
        _nodes[node.NodeId.ToHexString()] = node;
        return this;
    }

    public Task BootstrapAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<NodeInfo?> FindNodeAsync(NodeId targetId, CancellationToken ct = default)
    {
        FindNodeCalls.Add(targetId.ToHexString());
        return Task.FromResult(_nodes.TryGetValue(targetId.ToHexString(), out var n) ? n : null);
    }

    public void RegisterStaticPeer(NodeInfo node) => _nodes[node.NodeId.ToHexString()] = node;

    public Task StoreAsync(byte[] key, byte[] value, CancellationToken ct = default) => Task.CompletedTask;

    public Task<byte[]?> FindValueAsync(byte[] key, CancellationToken ct = default)
        => Task.FromResult<byte[]?>(null);

    public Task<bool> PingAsync(NodeInfo node, CancellationToken ct = default) => Task.FromResult(true);

    public IReadOnlyList<NodeInfo> GetAllKnownNodes() => _nodes.Values.ToList();

    public IAsyncEnumerable<PeerDiscoveryEventArgs> OnPeerDiscovered => EmptyAsync();

    private static async IAsyncEnumerable<PeerDiscoveryEventArgs> EmptyAsync()
    {
        await Task.CompletedTask;
        yield break;
    }
}

/// <summary>
/// 可控的 TCP 连接替身。
/// <para>
/// <list type="bullet">
///   <item>记录所有发出的字节帧（供断言信封线路格式）；</item>
///   <item>可预置入站帧，或由 <see cref="OnSend"/> 回调在收到出站帧时生成响应（模拟对端）；</item>
///   <item><see cref="IsConnected"/> 可手动置为 false，触发路由器的重连分支。</item>
/// </list>
/// </para>
/// </summary>
public sealed class FakeTcpConnection : ITcpConnection
{
    private readonly Queue<ReadOnlyMemory<byte>> _inbound = new();
    private readonly object _gate = new();
    private bool _isConnected = true;

    public FakeTcpConnection(IPEndPoint? remote = null, Guid? connectionId = null)
    {
        RemoteEndPoint = remote ?? new IPEndPoint(IPAddress.Loopback, 59999);
        ConnectionId = connectionId ?? Guid.NewGuid();
    }

    public Guid ConnectionId { get; }

    public IPEndPoint RemoteEndPoint { get; }

    public bool IsConnected
    {
        get { lock (_gate) return _isConnected; }
        set { lock (_gate) _isConnected = value; }
    }

    public bool Disposed { get; private set; }

    /// <summary>本连接已发出的全部字节帧，按发送顺序。</summary>
    public List<byte[]> SentFrames { get; } = [];

    /// <summary>收到出站帧时触发的回调（用于在测试中扮演对端）。</summary>
    public Func<ReadOnlyMemory<byte>, Task>? OnSend { get; set; }

    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var copy = data.ToArray();
        lock (_gate)
        {
            if (!_isConnected) throw new InvalidOperationException("连接已关闭");
            SentFrames.Add(copy);
        }
        return OnSend?.Invoke(copy) ?? Task.CompletedTask;
    }

    public Task<ReadOnlyMemory<byte>> ReceiveMessageAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_inbound.Count > 0) return Task.FromResult(_inbound.Dequeue());
        }
        ct.ThrowIfCancellationRequested();
        throw new InvalidOperationException("没有可用的入站数据");
    }

    /// <summary>把一帧数据排入本连接的入站队列（扮演对端发来的报文）。</summary>
    public void Enqueue(ReadOnlyMemory<byte> frame)
    {
        lock (_gate) _inbound.Enqueue(frame);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        IsConnected = false;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// 桩 TCP 传输层 — 每次 <see cref="ConnectAsync"/> 造一条新连接并计数，
/// 用于断言 <c>MessageRouter</c> 的连接池是否真的复用了已有连接。
/// </summary>
public sealed class FakeTcpTransport : ITcpTransport
{
    private readonly Dictionary<IPEndPoint, FakeTcpConnection> _byEndpoint = [];

    public int ConnectCount { get; private set; }

    public int ListenPort { get; private set; }

    public IPEndPoint? LocalEndPoint { get; private set; }

    /// <summary>
    /// 连接工厂钩子 —— 让测试预置一条已接好「对端」回调的连接。
    /// 未设置时按端点复用同一条 <see cref="FakeTcpConnection"/>。
    /// </summary>
    public Func<IPEndPoint, ITcpConnection>? ConnectionFactory { get; set; }

    /// <summary>按端点取（或建）一条连接 —— 同一端点复用同一条连接实例。</summary>
    public Task<ITcpConnection> ConnectAsync(IPEndPoint endpoint, CancellationToken ct = default)
    {
        ConnectCount++;
        if (ConnectionFactory is not null)
            return Task.FromResult(ConnectionFactory(endpoint));
        if (!_byEndpoint.TryGetValue(endpoint, out var conn))
        {
            conn = new FakeTcpConnection(endpoint);
            _byEndpoint[endpoint] = conn;
        }
        return Task.FromResult<ITcpConnection>(conn);
    }

    public Task StartListeningAsync(int port, CancellationToken ct = default)
    {
        ListenPort = port;
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, port);
        return Task.CompletedTask;
    }

    public Task<ITcpConnection> AcceptAsync(CancellationToken ct = default)
        => Task.FromResult<ITcpConnection>(new FakeTcpConnection());

    public IAsyncEnumerable<ITcpConnection> IncomingConnections => EmptyAsync();

    public void Dispose() { }

    private static async IAsyncEnumerable<ITcpConnection> EmptyAsync()
    {
        await Task.CompletedTask;
        yield break;
    }
}

/// <summary>
/// 记录型消息路由器 — 不真正发包，只记录出站消息与入站路由。
/// 用于断言「服务层把什么交给了路由器」。
/// </summary>
public sealed class RecordingMessageRouter : IMessageRouter
{
    private readonly List<Message> _sent = [];
    private readonly List<NodeInfo> _recipients = [];
    private readonly object _gate = new();
    private readonly List<MessageEnvelope> _routed = [];
    private readonly List<(ITcpConnection Connection, Message Message)> _viaConnection = [];

    /// <summary>出站消息（按发送顺序）。</summary>
    public IReadOnlyList<Message> Sent
    {
        get { lock (_gate) return _sent.ToList(); }
    }

    /// <summary>与 <see cref="Sent"/> 一一对应的收件人 NodeInfo。</summary>
    public IReadOnlyList<NodeInfo> Recipients
    {
        get { lock (_gate) return _recipients.ToList(); }
    }

    /// <summary>被路由回本路由器（而非分发到处理器）的入站信封。</summary>
    public IReadOnlyList<MessageEnvelope> RoutedIn
    {
        get { lock (_gate) return _routed.ToList(); }
    }

    /// <summary>走「已有连接」发出的 (连接, 消息) 记录。</summary>
    public IReadOnlyList<(ITcpConnection Connection, Message Message)> SentViaConnection
    {
        get { lock (_gate) return _viaConnection.ToList(); }
    }

    public List<MessageType> RegisteredHandlers { get; } = [];

    public ITcpConnection Connection { get; set; } = new FakeTcpConnection();

    public void Clear()
    {
        lock (_gate) { _sent.Clear(); _recipients.Clear(); }
    }

    public void RegisterHandler<T>(IMessageHandler<T> handler) where T : Message
        => RegisteredHandlers.Add(handler.MessageType);

    public void UnregisterHandler(MessageType messageType)
        => RegisteredHandlers.Remove(messageType);

    public Task RouteIncomingAsync(MessageEnvelope envelope, ITcpConnection sender, CancellationToken ct = default)
    {
        lock (_gate) _routed.Add(envelope);
        return Task.CompletedTask;
    }

    public Task SendAsync(NodeInfo recipient, Message message, CancellationToken ct = default)
    {
        lock (_gate) { _sent.Add(message); _recipients.Add(recipient); }
        return Task.CompletedTask;
    }

    public Task SendViaConnectionAsync(ITcpConnection connection, Message message, CancellationToken ct = default)
    {
        lock (_gate) { _sent.Add(message); _viaConnection.Add((connection, message)); }
        return Task.CompletedTask;
    }

    public Task<ITcpConnection> GetOrCreateConnectionAsync(NodeInfo node, CancellationToken ct = default)
        => Task.FromResult(Connection);

    public Task CloseConnectionAsync(byte[] nodeId) => Task.CompletedTask;
}

/// <summary>
/// 临时数据目录 — 把 <see cref="DataPath"/> 的全局静态根切到一次性目录。
/// <para>
/// <c>ContactService</c> 在构造时就把 <c>contacts.json</c> 路径解析成字段，
/// 所以本类型必须「先切根、再构造服务」，并在断言完成后还原。
/// 因为 <c>DataPath.Root</c> 是进程级静态状态，本测试程序集已关闭 xUnit 并行
/// （见 <c>TestAssembly.cs</c>）。
/// </para>
/// </summary>
public sealed class TempDataDir : IDisposable
{
    private readonly string _previous;

    public TempDataDir()
    {
        _previous = DataPath.Root;
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "p2pc-chat-tests", Guid.NewGuid().ToString("N"));
        DataPath.SetRoot(Path);
    }

    /// <summary>本次测试独享的数据目录。</summary>
    public string Path { get; }

    public string ContactsFile => System.IO.Path.Combine(Path, "contacts.json");

    /// <summary>直接写入一份 contacts.json（用于模拟已损坏/他人写坏的磁盘状态）。</summary>
    public void WriteContactsFile(string rawJson)
        => System.IO.File.WriteAllText(ContactsFile, rawJson);

    public void Dispose()
    {
        DataPath.SetRoot(_previous);
        try
        {
            if (System.IO.Directory.Exists(Path))
                System.IO.Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论。
        }
    }
}

/// <summary>
/// 把 <see cref="IEncryptionService"/> 的会话密钥派生结果裁剪到指定长度的装饰器。
/// <para>
/// 存在的理由：<c>IEncryptionService</c> 含有 <c>ReadOnlySpan&lt;byte&gt;</c> 参数的方法，
/// Castle/Moq 的动态代理无法处理 ref struct，因此不能用 Mock 来制造「派生出非 32 字节密钥」
/// 这一异常分支；本装饰器显式转发全部成员，只改一处返回值。
/// </para>
/// </summary>
public sealed class TruncatingEncryptionService : IEncryptionService
{
    private readonly IEncryptionService _inner;
    private readonly int _sessionKeyLength;

    public TruncatingEncryptionService(IEncryptionService inner, int sessionKeyLength)
    {
        _inner = inner;
        _sessionKeyLength = sessionKeyLength;
    }

    public KeyPair GenerateKeyPair() => _inner.GenerateKeyPair();

    public byte[] DeriveSharedSecret(byte[] localPrivateKey, byte[] remotePublicKey)
        => _inner.DeriveSharedSecret(localPrivateKey, remotePublicKey);

    public byte[] DeriveSessionKey(byte[] sharedSecret, byte[]? salt = null, byte[]? info = null)
    {
        var real = _inner.DeriveSessionKey(sharedSecret, salt, info);
        return real.Take(_sessionKeyLength).ToArray();
    }

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key)
        => _inner.Encrypt(plaintext, key);

    public byte[] Decrypt(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> key)
        => _inner.Decrypt(ciphertext, key);

    public byte[] Sign(ReadOnlySpan<byte> data, ReadOnlySpan<byte> privateKey)
        => _inner.Sign(data, privateKey);

    public bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> publicKey)
        => _inner.Verify(data, signature, publicKey);

    public byte[] GenerateRandomKey() => _inner.GenerateRandomKey();
}

/// <summary>
/// 异步事件流读取帮助器。
/// 刻意不引入 System.Linq.Async（只为拿一个 <c>FirstAsync</c> 不值得多一个依赖）。
/// </summary>
public static class AsyncStream
{
    /// <summary>
    /// 在超时内读取事件流的第一项。
    /// 流在超时内一直为空时抛 <see cref="TimeoutException"/>（而不是 <c>OperationCanceledException</c>），
    /// 这样「断言没有事件到达」也能写成一条可读的用例。
    /// </summary>
    public static async Task<T> FirstAsync<T>(IAsyncEnumerable<T> stream, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await foreach (var item in stream.WithCancellation(cts.Token))
                return item;
        }
        catch (OperationCanceledException)
        {
            // 落到下面统一按超时处理
        }
        throw new TimeoutException($"等待事件流超时 ({timeoutMs}ms)");
    }
}

/// <summary>
/// 构造测试节点信息的小工具。
/// </summary>
public static class TestNodes{
    /// <summary>造一个只有 32 字节「旧版/测试 DHT」公钥的节点。</summary>
    public static NodeInfo LegacyPublicKeyNode(NodeId? nodeId = null, int port = 45000)
        => new()
        {
            NodeId = nodeId ?? NodeId.CreateRandom(),
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            PublicKey = new byte[32]
        };

    /// <summary>造一个携带真实 P-256 SubjectPublicKeyInfo 公钥的节点。</summary>
    public static NodeInfo RealKeyPairNode(KeyPair keyPair, int port = 45000)
        => new()
        {
            NodeId = NodeId.FromPublicKey(keyPair.PublicKey),
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            PublicKey = keyPair.PublicKey
        };

    /// <summary>用源生成器上下文（<c>JsonContext</c>）把联系人写成 contacts.json 的真实形态。</summary>
    public static string BuildContactsJson(params StoredContact[] contacts)
        => JsonSerializer.Serialize(contacts.ToList(), JsonContext.Default.ListStoredContact);

    /// <summary>
    /// 故意写出**顶层是单个对象**（而非数组）的 contacts.json —— 复现最常见的手改失误。
    /// 与 <see cref="BuildContactsJson"/> 同源，只是不套数组。
    /// </summary>
    public static string BuildSingleContactObjectJson(StoredContact contact)
        => JsonSerializer.Serialize(contact, JsonContext.Default.StoredContact);
}
