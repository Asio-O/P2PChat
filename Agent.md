# P2PChat — Agent.md

> 面向 AI Agent / 新加入开发者的项目全景文档。
> 覆盖：整体架构、模块职责、关键类与函数、依赖关系、构建/运行/测试/配置方式、AOT 约束与已知陷阱。

---

## 1. 项目概述

**P2PChat** 是一个去中心化的 P2P 终端聊天程序（.NET 11 / C#，Native-AOT 单文件发布，约 8.7 MB）。

| 维度 | 说明 |
|---|---|
| 定位 | 学习/研究 P2P 网络与 Native-AOT 实践的终端聊天工具 |
| 节点发现 | BitTorrent **Mainline DHT**（Kademlia + KRPC + Bencode over UDP） |
| 通信 | 节点间 **TCP 直连**（4 字节大端长度前缀成帧） |
| 加密 | ECDH P-256 协商 → HKDF-SHA256 派生 → **AES-256-GCM** 加解密 |
| 序列化 | **MessagePack**（源生成器，零反射） |
| 界面 | 零依赖**自绘控制台 TUI**（`ConsoleScreen` + `P2PChatTui`） |
| 发布 | `PublishAot=true`，`win-x64`，运行时无需安装 .NET |

**核心设计信条**：一切代码路径必须 **AOT 安全**（无反射、无动态代码生成、无 `MakeGenericType`），这是本项目绝大多数"反直觉"写法的根因。

---

## 2. 整体架构

### 2.1 分层视图

```
┌──────────────────────────────────────────────────────────────┐
│  P2PChat.App (Exe)                                           │
│  Program.cs — 唯一入口：配置 → 日志 → DI 装配 → 启动 → TUI    │
└───────────────┬──────────────────────────────────────────────┘
                │ 引用全部
   ┌────────────┼──────────────┬───────────────┬──────────────┐
   ▼            ▼              ▼               ▼              ▼
┌────────┐ ┌─────────┐ ┌─────────────┐ ┌────────────┐ ┌──────────┐
│  UI    │ │  Chat   │ │FileTransfer │ │ Networking │ │  Crypto  │
│ 自绘TUI│ │路由/会话│ │ 分块传输    │ │ DHT+传输   │ │ 加密/密钥│
└───┬────┘ └──┬───┬──┘ └──────┬──────┘ └─────┬──────┘ └────┬─────┘
    │         │   │           │              │             │
    └─────────┴───┴───────────┴──────────────┴─────────────┘
                             ▼
                  ┌────────────────────────┐
                  │     P2PChat.Core       │  ← 依赖图的根
                  │  Abstractions / Models │
                  │  Enums / Serialization │
                  └────────────────────────┘
```

> 注：`Chat` 与 `FileTransfer` 互不引用，二者通过 `Core` 的抽象接口 + `IMessageRouter` 解耦。

### 2.2 依赖关系矩阵

| 项目 | 依赖项目 | 关键 NuGet 包 |
|---|---|---|
| `P2PChat.Core` | —（根） | MessagePack |
| `P2PChat.Networking` | Core | Extensions.DI、Extensions.Logging |
| `P2PChat.Crypto` | Core | Extensions.DI、Extensions.Logging |
| `P2PChat.UI` | Core | Extensions.Logging |
| `P2PChat.Chat` | Core、Networking、Crypto | Extensions.Logging |
| `P2PChat.FileTransfer` | Core、Networking | Extensions.DI、Extensions.Logging |
| `P2PChat.App` | Core、Networking、Crypto、Chat、FileTransfer、UI | Extensions.Hosting、Serilog(Extensions.Logging/Console/File) |

> **约束**：不允许出现反向依赖或环形依赖。`Core` 只定义抽象与模型，不实现网络/加密/UI 逻辑。

### 2.3 运行时数据流

```
[出站] TUI 命令
   └─► IChatService / IFileTransferService / IGroupChatService
        └─► IDhtService.FindNodeAsync(target)      // UDP: 迭代 FIND_NODE
        └─► IKeyStore.GetSessionKey → 无则 KeyExchange
        └─► IEncryptionService.Encrypt (AES-256-GCM)
        └─► IMessageRouter.SendAsync(NodeInfo, Message)
             └─► TCP 连接池 → ITcpConnection.SendAsync → Socket
                  └─► Envelope(1B ver + 1B type + 4B seq + 20B senderId
                               + 16B msgId + 8B ts + payload)

[入站] Socket accept
   └─► Program.ProcessIncomingTcpAsync
        └─► MessageRouter.ProcessIncomingConnectionAsync（按连接循环读）
             └─► MessageRouter.RouteIncomingAsync(envelope, sender)
                  └─► ConcurrentDictionary<MessageType, IMessageHandler> 分发
                       └─► PrivateMessageHandler / KeyExchangeHandler / ...
                            └─► 解密 → Channel<ChatMessageEvent> → TUI 渲染
```

**关键点**：UI 与业务层通过 `System.Threading.Channels` 的 `IAsyncEnumerable<T>` 事件流解耦，而非回调或事件。

---

## 3. 模块职责详解

### 3.1 `P2PChat.Core` — 抽象与契约（根项目）

只包含接口、数据模型、枚举、序列化基础设施。**不含任何网络/IO 实现**。

#### `Abstractions/`（14 个接口）

| 接口 | 职责 | 关键成员 |
|---|---|---|
| `ISerializer` | 序列化抽象 | `Serialize<T>` / `Deserialize<T>`(byte[]/Span/Stream) |
| `IEncryptionService` | 加解密与密钥协商 | `GenerateKeyPair` / `DeriveSharedSecret` / `DeriveSessionKey` / `Encrypt` / `Decrypt` / `Sign` / `Verify` / `GenerateRandomKey` |
| `IKeyStore` | 密钥持久化 | `GetOrCreateIdentity` / `Get/Set/RemoveSessionKey` / `Get/Set/RemoveGroupKey` / `GetKnownGroupIds` |
| `IUdpTransport` | UDP 传输 | `SendAsync(byte[], IPEndPoint)` / `ReceiveAsync` → `UdpPacket` |
| `ITcpTransport` | TCP 监听与连接 | `StartListeningAsync` / `ConnectAsync` / `AcceptAsync` / `IncomingConnections` / `ListenPort` |
| `ITcpConnection` | 单条 TCP 通道 | `SendAsync(ReadOnlyMemory<byte>)` / `ReceiveMessageAsync` / `IsConnected` / `RemoteEndPoint` / `ConnectionId` |
| `IRoutingTable` | Kademlia 路由表 | `AddOrUpdate` / `GetClosestContacts(target, count)` / `GetAllContacts` / `Remove` / `BucketSize` |
| `IDhtService` | DHT 门面 | `BootstrapAsync` / `FindNodeAsync` / `StoreAsync` / `FindValueAsync` / `PingAsync` / `GetAllKnownNodes` / `OnPeerDiscovered` |
| `IMessageRouter` | 消息路由 | `RegisterHandler<T>` / `UnregisterHandler` / `RouteIncomingAsync` / `SendAsync` / `SendViaConnectionAsync` / `GetOrCreateConnectionAsync` / `CloseConnectionAsync` |
| `IMessageHandler` | 处理器契约 | 非泛型 `HandleAsync(Message, ITcpConnection, MessageEnvelope, ct)`；泛型 `IMessageHandler<T>` 提供默认接口方法转发 |
| `IChatService` | 私聊 | `SendPrivateMessageAsync` / `OnMessageReceived` / `HasSessionKey` |
| `IGroupChatService` | 群聊 | `CreateGroupAsync` / `SendGroupMessageAsync` / `GetKnownGroups` / `GetOnlineMembers` / `HandleInviteAsync` / `HandleNotifyAsync` |
| `IContactService` | 联系人 | `GetAllContactsAsync` / `Add` / `Remove` / `UpdateAlias` / `UpdateOnlineStatus` / `OnStatusChanged` |
| `IFileTransferService` | 文件传输 | `SendOfferAsync` / `AcceptTransferAsync` / `RejectTransferAsync` / `HandleFileMetaAsync` / `HandleFileChunkAsync` / `HandleFileAckAsync` / `OnProgressChanged` / `OnFileOfferReceived` |

#### `Models/`（19 个）

**`Message`（抽象基类，多态根）** — `[MessagePackObject]` + 8 个 `[Union]`：

| Union | 类型 | 关键字段 |
|---|---|---|
| 0 | `TextMessage` | `Content`, `IsGroup` |
| 1 | `FileMetaMessage` | `TransferId`, `FileName`, `FileSize`, `FileHash`, `ChunkSize`(默认 65536), `TotalChunks` |
| 2 | `FileChunkMessage` | `TransferId`, `ChunkIndex`, `Data` |
| 3 | `FileAckMessage` | `TransferId`, `Accepted`, `ErrorMessage` |
| 4 | `KeyExchangeMessage` | `EphemeralPublicKey`, `IsResponse` |
| 5 | `GroupInviteMessage` | `GroupId`, `GroupName`, `EncryptedGroupKey`, `MemberIds`, `SenderPublicKey?`, `EncryptedGroupKeyNonceAndTag?` |
| 6 | `GroupNotifyMessage` | `GroupId`, `Action`, `OperatorId` |
| 7 | `DeliveryAckMessage` | `AcknowledgedMessageId`, `Status` |

`Message` 公共字段：`MessageId`(Key 0, **`set`**)、`SenderId`(Key 1)、`Timestamp`(Key 2, **`set`**)、`ConversationId`(Key 3)。

> ⚠️ **`MessageId` / `Timestamp` 必须用 `set` 而非 `init`**：MsgPack 0.17 生成的反序列化在 `init` 属性 + 初始化器下会无条件重置为 `default`，`set` 访问器才能让 formatter 仅在键存在时赋值。

其他模型：`MessageEnvelope`、`NodeId`、`NodeInfo`、`Contact`、`GroupInfo`、`ChatMessageEvent`、`ContactStatusEvent`、`ConversationSummary`、`FileTransferProgress`、`PeerDiscoveryEventArgs`。

**`NodeId`**（`readonly record struct`，20 字节 / 160 bit）
- `FromPublicKey(byte[])` — SHA-1 派生
- `CreateRandom()` / `XorDistanceTo(other)` / `CommonPrefixLength(other)` / `ToHexString()` / `ToByteArray()`

**`NodeInfo`**（record）：`NodeId`、`EndPoint`、`ExternalEndPoint?`、`PublicKey`、`LastSeen`、`State`

**`MessageEnvelope`**（record）：`Version`(=1)、`MessageType`、`SequenceNumber`、`SenderId`、`MessageId`、`Timestamp`、`Payload`

#### `Enums/`
- `MessageType`：`Unknown=0, PrivateText=1, GroupText=2, FileMeta=3, FileChunk=4, FileAck=5, DhtRpc=6, KeyExchange=7, GroupInvite=8, GroupNotify=9, DeliveryAck=10`
- `PeerState`：`Offline=0, Online=1, Busy=2`
- `DhtMessageType`：`Ping=0, Store=1, FindNode=2, FindValue=3`

#### `Extensions/`
| 类型 | 职责 |
|---|---|
| `DataPath` | 数据目录解析。默认 **`用户主目录/.p2pc`**（`USERPROFILE` → `HOME` → `LocalApplicationData` 逐级回退）；可用 `P2PCHAT_DATA_DIR` 覆盖（多实例隔离）。成员：`Root`、`FolderName`、`OverrideEnvironmentVariable`、`GetPath(file)`、`GetDirectory(name)`、`SetRoot(path)`、`Reset()` |
| `SerializerExtensions.cs` → `MessagePackSerializer` | `ISerializer` 实现，静态 `Resolver` |
| `P2PChatMessagePackResolver` | 源生成 `IFormatterResolver` |
| `MessagePackResolverRegistry` | 下游 formatter 注册表 + `RegisteredResolverFallback` |

---

### 3.2 `P2PChat.Networking` — DHT 与传输

#### `Dht/MainlineDhtService.cs`（451 行，**实际的 DHT 实现**）

`IDhtService` 的真实实现，走 BitTorrent Mainline 协议（Bencode + KRPC over UDP）。

| 成员 | 说明 |
|---|---|
| `BootstrapAsync(ct)` | 向所有引导节点发 `find_node(self)`，填充路由表 |
| `StartReceivingAsync(ct)` | UDP 接收循环 → `HandleKrpcMessageAsync` |
| `FindNodeAsync(targetId, ct)` | 委托 `IterativeFindNodeAsync`（α 并行迭代查询） |
| `PingAsync(node, ct)` | `KrpcPingAsync` |
| `StoreAsync` / `FindValueAsync` | Mainline DHT 无此语义，当前为占位/未实现 |
| `KrpcQueryAsync(ep, method, args, ct)` | 通用 KRPC 请求：写 `_pending` TCS（transactionId 关联）+ **10 秒超时** + UDP 发送 |
| `HandleQueryAsync(query, args, ...)` | 响应入站 `ping` / `find_node` / `get_peers` / `announce_peer` |
| `IterativeFindNodeAsync(targetId, ct)` | α 并行度迭代收敛，`OnPeerDiscovered` 推事件 |
| `RefreshLoopAsync(ct)` | **每 15 分钟**刷新 k-bucket（`GetStaleBucketTarget` + 随机 ID 查询） |
| `NotifyAsync(node, method)` | 单向 KRPC 通知 |

- 紧凑节点格式：26 字节（20B NodeId + 4B IPv4 + 2B Port）—— `Bencode.ParseCompactNodes` / `EncodeCompactNode`
- UDP 错误处理：`UdpSocketErrorClassifier` 将 Windows ICMP port-unreachable（`ConnectionReset` 10054）识别为"对端不可达的正常反馈"，降级为 Debug 日志而非终止接收循环

#### `Dht/RoutingTable.cs`（139 行）
`IRoutingTable` 实现。`KBucket[160]` 数组（SHA-1 = 160 bit）。

| 方法 | 说明 |
|---|---|
| `AddOrUpdate(node)` | 按 `GetBucketIndex` 定位桶 |
| `GetClosestContacts(target, count)` | 按 XOR 距离排序取最近 count 个 |
| `GetAllContacts()` | 拍平所有桶 |
| `GetStaleBucketTarget()` | 找出最久未变更的桶，生成该桶内随机 ID（供刷新循环） |
| `GetBucketIndex(nodeId)` | = `_localNodeId.CommonPrefixLength(nodeId)`，取值 0..159 |
| `GenerateRandomIdInBucket(index)` | 生成落在指定桶内的随机 NodeId |
| `Count()` | 联系人总数 |

#### `Dht/KBucket.cs`（144 行）
单桶实现：`LinkedList<NodeInfo>` + `SemaphoreSlim(1,1)` 串行化。

- `AddOrMoveToTailAsync` — 已存在则移到尾部（LRU），**桶满则直接拒绝新节点**（不主动 PING 淘汰，简化实现）
- `RemoveAsync` / `GetAllContactsAsync` / `GetOldestAsync` / `ContainsAsync`
- `LastChanged` 时间戳供 `GetStaleBucketTarget` 使用

#### `Dht/Bencode.cs`（176 行）
纯静态 Bencode 编解码器（BitTorrent 线格式）。

- `Encode(object)` — 支持 `byte[]`(string) / `long`(int) / `List<object>`(list) / `Dictionary<string,object>`(dict)
- `Decode(byte[] | ReadOnlySpan<byte>)` — 游标式 `ref int consumed` 解析，零拷贝友好
- `B2B` / `B2I` / `Get<T>` — 解码结果强类型取值助手
- `ParseCompactNodes` / `EncodeCompactNode` — 26 字节紧凑节点格式

#### `Dht/KademliaDhtService.cs`（537 行）
**另一套 DHT 实现**，基于自定义 MessagePack RPC（`DhtRpcMessage` + `DhtMessageType`），含 `NodeInfoDto`、`StoreRequest`、内存 `StoredValues` 字典、`FindValue` 的 `[1]+value` / `[0]+contacts` 标记约定。

> ⚠️ **注意**：`Program.cs` 实际注入的是 `MainlineDhtService`，`KademliaDhtService` 目前**未被装配**（未见 DI 注册）。
> `DhtMessagePackFormatterAnchor` + `DhtMessagePackResolverRegistration` 是 AOT formatter 注册锚点。

#### `Transport/TcpConnection.cs`（161 行）
单条 TCP 通道，基于 `System.IO.Pipelines`。

```csharp
// 帧格式: [4字节 BigEndian 长度] + [负载]
public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
//   _sendLock(SemaphoreSlim 1,1) 保护：先发 4B 头，再发负载
public async Task<ReadOnlyMemory<byte>> ReceiveMessageAsync(CancellationToken ct)
//   从 Pipe 读取；不足 4B 或不足整帧 → AdvanceTo(buffer.Start, buffer.End) 等待更多数据
//   长度校验: <0 或 >100MB → InvalidOperationException
//   循环读取直至凑齐完整帧，返回 payload 的 ToArray()
```

- 构造时启动后台 `FillPipeAsync()`：`socket.ReceiveAsync` → `Pipe.Writer.Advance/FlushAsync`，读到 0 或异常则 `CompleteAsync`
- `IsConnected => !_disposed && _socket.Connected`
- `DisposeAsync`：完成 Pipe 双端 + 关闭 Socket

#### `Transport/TcpTransport.cs`（146 行）
- `StartListeningAsync(preferredPort)` → `BindWithRetry`：优先端口失败则**随机端口重试 20 次**，最后兜底 `bind(0)`
- `AcceptLoopAsync` — 每次都产出 `TcpConnection` 推入 **Bounded Channel(容量 100)**
- `ConnectAsync(endpoint)` / `AcceptAsync(ct)`（从 Channel 读）/ `IncomingConnections`（`ReadAllAsync`）

#### `Transport/UdpTransport.cs`（109 行）
- 构造函数直接 `new Socket(InterNetwork, Dgram, Udp)` 并 **`BindWithRetry`**（同上策略），`LocalEndPoint` 暴露实际端口
- `SendAsync(byte[], IPEndPoint, ct)` → `SendToAsync`
- `ReceiveAsync(ct)` → 每次分配 65536 缓冲区，`ReceiveFromAsync`，**保留 `SocketException` 原样上抛**（由 DHT 层按 `SocketErrorCode` 分类处理）
- 返回 `UdpPacket { Data, RemoteEndPoint }`

#### `Transport/UdpSocketErrorClassifier.cs`
判定 Windows ICMP port-unreachable（10054）等"对端不可达"是否属预期内的软错误。

---

### 3.3 `P2PChat.Crypto` — 加密与密钥存储

#### `Encryption/AesGcmEncryptionService.cs`（132 行）

| 方法 | 实现要点 |
|---|---|
| `GenerateKeyPair()` | **ECDH P-256** 密钥对 |
| `DeriveSharedSecret(localPriv, remotePub)` | P-256 ECDH 原始共享密钥 |
| `DeriveSessionKey(sharedSecret, salt?, info?)` | **HKDF-SHA256**，默认 `info = "P2PChat-session-key"` |
| `Encrypt(plaintext, key)` | **AES-256-GCM**，输出布局 `[nonce(12B)][ciphertext][tag(16B)]`，nonce 每次随机 |
| `Decrypt(ciphertext, key)` | 按同一布局切分；`tag` 校验失败抛异常 |
| `Sign` / `Verify` | **ECDSA** 签名/验签 |
| `GenerateRandomKey()` | 32 字节随机（群密钥等） |

#### `Keys/FileBackedKeyStore.cs`（208 行）
`IKeyStore` 实现，内存 `ConcurrentDictionary` + 文件持久化。

- 持久化文件（均在 `DataPath.Root` 即 `用户主目录/.p2pc` 下）：
  - `identity.json` — 长期身份密钥对
  - `session_keys.json` — 每对端会话密钥
  - `group_keys.json` — 每群组密钥
- `GetOrCreateIdentity()` — 首次调用生成并落盘，后续读缓存
- 序列化全部走 **`JsonContext`（`System.Text.Json` 源生成器，`[JsonSerializable]` 标注 `StoredKeyPair` 等 5 个类型）**，禁用反射
- `StoredModels.cs` — `StoredKeyPair`、`StoredSessionKey`、`StoredGroupKey` 等 DTO + `partial class JsonContext : JsonSerializerContext`
- `_saveLock` 保护文件写入

---

### 3.4 `P2PChat.Chat` — 路由、会话与处理器

#### `Routing/MessageRouter.cs`（219 行，**分发中枢**）

```csharp
private readonly ConcurrentDictionary<MessageType, IMessageHandler> _handlers;
private readonly ConcurrentDictionary<string, ITcpConnection> _connectionPool; // key: NodeId hex
private readonly ConcurrentDictionary<Guid, TaskCompletionSource<Message>> _pendingResponses;
private const int MaxConnectionsPerPeer = 3;
```

| 方法 | 说明 |
|---|---|
| `RegisterHandler<T>(IMessageHandler<T> h)` | 由 `T` 推断 `MessageType`（`GetMessageType` switch），注入 `_handlers` |
| `RouteIncomingAsync(envelope, sender, ct)` | 查 `_handlers` → 调**非泛型** `IMessageHandler.HandleAsync` 默认接口方法转发 |
| `SendAsync(recipient, message, ct)` | 取/建连接 → `SendViaConnectionAsync` |
| `SendViaConnectionAsync(conn, message, ct)` | 组 `MessageEnvelope` → `SerializeEnvelope` → `conn.SendAsync` |
| `GetOrCreateConnectionAsync(node, ct)` | 连接池命中且 `IsConnected` 则复用，否则 `ConnectAsync` 后入池 |
| `ProcessIncomingConnectionAsync(conn, ct)` | 单连接循环：`ReceiveMessageAsync` → `DeserializeEnvelope` → `RouteIncomingAsync` |
| `CloseConnectionAsync(nodeId)` | 摘除并释放连接 |

`SerializeEnvelope` / `DeserializeEnvelope` 使用 **`BinaryPrimitives` 手工大端读写**，不依赖任何序列化框架：

```
1B version | 1B MessageType | 4B BE Seq | 20B SenderId | 16B MessageId | 8B BE Timestamp | payload
```

> ⚠️ **AOT 关键决策（勿回退）**：`RouteIncomingAsync` **刻意不使用 `reflection GetMethod/Invoke`**。Native-AOT 下反射目标缺少静态调用点会被 ILC 裁剪，导致处理器**静默失效**（原实现产生 IL2075 警告）。改为泛型接口的默认接口方法转发。

#### `Handlers/`（5 个）

| 处理器 | 处理类型 | 行为 |
|---|---|---|
| `PrivateMessageHandler` | `PrivateText` | 从 `IKeyStore` 取会话密钥 → 校验长度 → Base64 解码 `Content` → AES-GCM 解密 → `ChatService.PublishMessageAsync`；**解密失败则丢弃并告警** |
| `GroupMessageHandler` | `GroupText` | 取群密钥解密 → 推事件 |
| `KeyExchangeHandler` | `KeyExchange` | `IsResponse=false`：生成临时密钥对 → ECDH+HKDF 派生会话密钥入 keyStore → **沿同一 TCP 连接回写 `IsResponse=true` 响应**（手工构造信封）。`IsResponse=true`：交由发起方读取路径处理 |
| `GroupInviteHandler` | `GroupInvite` | 解密 `EncryptedGroupKey` → 写入 keyStore → 调 `GroupChatService.HandleInviteAsync` |
| `GroupNotifyHandler` | `GroupNotify` | 调 `GroupChatService.HandleNotifyAsync` |

#### `Services/ChatService.cs`（218 行）
`IChatService` 实现，私聊主管道。

| 方法 | 说明 |
|---|---|
| `SendPrivateMessageAsync(recipientId, text, ct)` | `FindNodeAsync` 定位 → `EnsureSessionKeyAsync` → AES-GCM 加密 → Base64 存 `TextMessage.Content` → 本地推 `ChatMessageEvent(IsOutgoing=true)` |
| `PerformKeyExchangeAsync(recipient, identity, ct)` | 发 `KeyExchangeMessage`（临时公钥）→ `ReadKeyExchangeResponseAsync` **在同一连接上读响应** |
| `EnsureSessionKeyAsync(...)` | 每对端一把 `SemaphoreSlim`，避免并发重复密钥交换 |
| `ReadKeyExchangeResponseAsync(...)` | 非响应类型消息**交回 `MessageRouter`** 继续路由，不丢弃 |
| `HasSessionKey(peerId)` | 查询 keyStore |
| `OnMessageReceived` | `Channel<ChatMessageEvent>` 的 `ReadAllAsync()` |

#### `Services/GroupChatService.cs`（248 行）
`IGroupChatService` 实现。

- 常量：`AesGcmNonceLength=12`、`AesGcmTagLength=16`、`GroupKeyMetadataLength=28`
- `CreateGroupAsync(name, members, ct)` — **`SHA256` 派生 `groupId`**，随机 32B 群密钥，对**每个成员独立**用其会话密钥 AES-GCM 包装群密钥，分包 `GroupInviteMessage`
- `SendGroupMessageAsync(groupId, text, ct)` — 群密钥加密后向成员**扇出**
- `HandleInviteAsync(invite, ct)` — 从 keyStore 读取**解密后**的群密钥（**兼容旧版明文 32B 密钥**）
- `HandleNotifyAsync(notify, ct)` — 处理 `dissolve` 等群操作
- `GetKnownGroups` / `GetGroup` / `GetOnlineMembers`

**群密钥包装布局**：`EncryptedGroupKey` = 32B 密文主体；`EncryptedGroupKeyNonceAndTag` = 12B nonce + 16B tag

> ⚠️ **历史缺陷（已修）**：曾把密文主体当明文群密钥使用。现约定 `GroupInviteHandler` 先解密并写入 keyStore，`HandleInviteAsync` 只读解密结果。

#### `Services/ContactService.cs`（147 行）
`IContactService` 实现。`ConcurrentDictionary<string, Contact>`（key = NodeId hex）+ `contacts.json` 持久化（`JsonContext.Default.ListStoredContact`）+ `Channel<ContactStatusEvent>` 状态流。含 `FindByNodeId`。

---

### 3.5 `P2PChat.FileTransfer` — 分块文件传输

`Services/FileTransferService.cs`（357 行）

- `DefaultChunkSize = 65536`（64 KB）
- `SendOfferAsync(recipientId, filePath, ct)` → 计算 `FileHash`(SHA-256) → 发 `FileMetaMessage` → 返回 `TransferId`
- `AcceptTransferAsync(transferId, saveDirectory, ct)` / `RejectTransferAsync(transferId, reason?)`
- `HandleFileMetaAsync`（接收方）/ `HandleFileChunkAsync`（按 `ChunkIndex` 落序写入）/ `HandleFileAckAsync`
- `SendFileChunksAsync(state, ct)` — 发送循环
- `VerifyAndCompleteAsync(state)` — 全量 SHA-256 校验，不匹配则标记失败
- `ReportProgressAsync(state, ct)` — 推 `FileTransferProgress`
- 事件流：`OnProgressChanged`、`OnFileOfferReceived`

**`TransferState`**（record class）：`TransferId`、`FilePath`、`FileName`、`FileSize`、`FileHash`、`ChunkSize`、`TotalChunks`、`CompletedChunks`、`IsSender`、`RemoteNodeId`、`SaveDirectory`、`SavePath`、`Status`
**`TransferStatus`** 枚举：`WaitingAccept` / … （含 `connecting`/`transferring`/`completed`/`error` 语义）

---

### 3.6 `P2PChat.UI` — 自绘 TUI

#### `Views/ConsoleScreen.cs`（109 行）
低层绘制：`LeftPanelWidth = 30`，差分重绘（`_frame` 缓存 + `_drawn`）。

- `Draw(dhtStatus, chatTitle, contactLines, ...)` — 左侧联系人栏 + 右侧会话区
- `Short(text, len=8)` — 短 ID 显示
- `TextWidth` / `Fit` / `WrapInto` — **宽字符（CJK/全角）感知**的宽度计算与折行；`CharWidth(cp)` 判定全角区间
- ⚠️ 乱码/错位问题多源于此处宽度计算，改动需谨慎

#### `Views/P2PChatTui.cs`（449 行，主 UI 循环）
`sealed class P2PChatTui(...)` 为主构造函数形式，注入各服务。

- 常量：`MaxHistory = 500`、`SystemConversation = "__system__"`
- `RunAsync(ct)` — 主循环：读键 → 分流（`HandleKey` / `SubmitInput`）→ 后台事件消费 → `Render`
- **命令集**：`/msg` `/group` `/file` `/add` `/id` `/net` `/contacts` `/accept` `/reject` `/help` `/quit`
- **快捷键**：F1/F2/F3/F10、Tab、PgUp/PgDn
- **3 个后台循环**：`ProcessIncomingMessagesAsync`（聊天事件）、`ProcessFileOffersAsync`（文件 Offer）、`RefreshDhtPeriodicallyAsync`（**5 秒** DHT 状态刷新）
- `_inbox`（`ConcurrentQueue`）汇聚后台线程输出 → `DrainInbox()` 由 UI 线程消费（避免跨线程写控制台）
- `RunSelfTestAsync(ct)` — **`P2PCHAT_SELFTEST=1`** 非交互模式：打印节点 ID / 端口 / DHT 已知节点数后退出；等待时长由 **`P2PCHAT_SELFTEST_WAIT`** 控制
- `Render()` + `MarkDirty()` 脏标记驱动重绘
- ⚠️ `_plainMode` 线性降级路径：非交互终端下退化为逐行输出

---

### 3.7 `P2PChat.App` — 入口与装配

`Program.cs`（301 行）是唯一入口，装配顺序**不可随意调换**（依赖实际端口/身份）：

```
0. Windows 控制台 UTF-8: SetConsoleOutputCP(65001) + SetConsoleCP(65001) + Console.OutputEncoding
   （必须在任何输出之前 —— 双击 exe 时 Windows 默认用系统代码页如 GBK）
1. ConfigurationBuilder().AddEnvironmentVariables("P2PCHAT_").AddCommandLine(args)
2. Serilog: Console + File(DataPath.GetDirectory("logs")/p2pchat-.log, RollingInterval.Day)
3. ServiceCollection:
   - IConfiguration / Logging(AddSerilog)
   - 解析 P2PChat:UdpPort / TcpPort / KBucketSize / Alpha / BootstrapNodes
   - ISerializer(MessagePackSerializer) / IEncryptionService / IKeyStore
4. 临时 ServiceProvider（拿 ILoggerFactory + IKeyStore）
   - LoadKnownPeers() 读 peers.txt
   - 合并 4 个公共引导节点：
     router.bittorrent.com:6881 / router.utorrent.com:6881
     dht.transmissionbt.com:6881 / dht.aelitis.com:6881
   - keyStore.GetOrCreateIdentity() → NodeId.FromPublicKey() = 本地 ID
   - new UdpTransport(preferredUdpPort, ...)  → actualUdpPort
   - new TcpTransport(...); await StartListeningAsync(preferredTcpPort) → actualTcpPort
   - 构造 NodeInfo localNode（EndPoint 用 GetLocalIPAddress() + actualTcpPort）
   - 逐个解析引导节点（IP 直读 / 否则 Dns.GetHostAddressesAsync）
5. 注册剩余服务（用实际端口/身份）
   localNode / IUdpTransport / ITcpTransport / IRoutingTable / IDhtService(MainlineDhtService)
   Chat 层、FileTransfer 层、UI 层（等价于各层 AddP2PChatXxx 扩展）
6. BuildServiceProvider()
   - dhtService.BootstrapAsync()（有引导节点才发）
   - ((MainlineDhtService)dht).StartReceivingAsync(dhtCts.Token)
   - router.RegisterHandler(5 个 Handler)
   - _ = ProcessIncomingTcpAsync(tcpTransport, router, provider, appCts.Token)
   - await tui.RunAsync(appCts.Token)   ← 阻塞主线程
   - 退出：取消 CTS → SaveKnownPeers(peers.txt) → Log.CloseAndFlush()
```

辅助方法：
- `GetLocalIPAddress()` — `Socket.Connect("8.8.8.8", 65530)` 读 `LocalEndPoint` 取本机局域网 IP（失败回退 `IPAddress.Loopback`）
- `ProcessIncomingTcpAsync` — `AcceptAsync` 循环，每条连接 `_ = msgRouter.ProcessIncomingConnectionAsync(...)` 即发即忘
- `LoadKnownPeers` / `SaveKnownPeers` — `peers.txt` 每行 `host:port`，忽略 `#` 注释

其他文件：`app.manifest`（`activeCodePage = UTF-8`，Windows 10 1903+）、`Properties/PublishProfiles/FolderProfile.pubxml`。

> ⚠️ **`FolderProfile.pubxml` 已过时**：仍写着 `net10.0` 与 `net10.0\publish\win-x64\`，与当前 `net11.0` 不符。发布的权威路径以命令行/`csproj` 为准（见第 5 节）。修改前请先确认。

---

## 4. 序列化与 AOT 安全机制（项目最难的部分）

### 4.1 MessagePack Resolver 组合

`SerializerExtensions.cs` 中静态 `Resolver`：

```csharp
CompositeResolver.Create(
    P2PChatMessagePackResolver.Instance,        // 本项目源生成 formatter
    SourceGeneratedFormatterResolver.Instance,  // 源生成通用 formatter
    BuiltinResolver.Instance,                   // MessagePack 内置
    RegisteredResolverFallback.Instance);       // 下游运行时注册的 formatter（关键兜底）
```

> ⚠️ **必须** `new MessagePackSerializerOptions(Resolver)`。
> **不可**使用 `Options.Standard.WithResolver(...)` —— `Standard` 的静态构造引用 `StandardResolver`（含动态代码），会触发 **IL3053** 破坏 AOT。

### 4.2 `MessagePackResolverRegistry` + `RegisteredResolverFallback`

**问题**：泛型类型参数落在 CoreLib 时（如 `List<NodeInfoDto>`，`NodeInfoDto` 在 Networking 层），源生成器无法自动发现其 formatter。

**方案**：Core 提供 `[ModuleInitializer]` 驱动的注册表；下游项目在模块初始化时注册自己的 formatter，`RegisteredResolverFallback` 惰性查询（快照式）兜底解析。

### 4.3 AOT 禁忌清单（**改代码前必读**）

| 禁止 | 原因 | 替代 |
|---|---|---|
| `reflection GetMethod/Invoke` 调处理器 | ILC 裁剪 → **静默失效**（IL2075） | 泛型接口默认方法转发 |
| `MessagePackSerializerOptions.Standard` | IL3053 | `new MessagePackSerializerOptions(Resolver)` |
| `System.Text.Json` 反射序列化 | 运行时不支持 | `JsonContext` 源生成器（`JsonSerializerIsReflectionEnabledByDefault=false`） |
| `MakeGenericType` / `Activator.CreateInstance` 热路径 | 裁剪 + 动态代码 | 显式泛型/工厂 |

**验证方式**：`SerializerAotRoundTripTests.cs` 保证序列化往返在 AOT 语义下成立；`dotnet publish` 的 **IL trim/AOT 警告必须为 0**。

---

## 5. 运行方式

### 5.1 环境要求

- **.NET SDK 11**（`global.json` 固定在 `11.0.100-rc.1.26425.128`，`rollForward: latestFeature`，`allowPrerelease: true`）
- **Windows 编译 Native-AOT 需要 MSVC 工具链**（Visual Studio「使用 C++ 的桌面开发」工作负载）
- 其他平台可 `dotnet build`/`dotnet run`，但 AOT 发布目标目前为 `win-x64`

### 5.2 常用命令

```powershell
# 构建全部
dotnet build P2PChat.slnx

# 开发运行
dotnet run --project src/P2PChat.App

# 发布为原生单文件（默认即 Native-AOT）
dotnet publish src/P2PChat.App/P2PChat.App.csproj -c Release
# 产物: src/P2PChat.App/bin/Release/net11.0/win-x64/publish/P2PChat.App.exe

# 测试
dotnet test P2PChat.slnx

# 双进程端到端验证（真实拉起两个实例）
pwsh -NoProfile -File scripts/e2e-verify.ps1
```

### 5.3 配置项

**应用配置**（环境变量前缀 `P2PCHAT_`，嵌套键用 `__`；或命令行 `--P2PChat:UdpPort=20081`）：

| 键 | 说明 | 默认 |
|---|---|---|
| `P2PChat:UdpPort` | UDP 监听端口（DHT） | `0`（自动选择） |
| `P2PChat:TcpPort` | TCP 监听端口（聊天/文件） | `0`（自动选择） |
| `P2PChat:BootstrapNodes` | 自定义引导节点（可数组，`host:port`） | 4 个公共 DHT 引导节点 |
| `P2PChat:KBucketSize` | Kademlia k-bucket 容量 | `20` |
| `P2PChat:Alpha` | Kademlia 迭代查询并行度 α | `3` |

**额外环境变量**：

| 变量 | 说明 |
|---|---|
| `P2PCHAT_DATA_DIR` | **可选**，覆盖数据目录（默认 `用户主目录/.p2pc`）。**仅在同机多实例需要各自独立身份时才设置** |
| `P2PCHAT_SELFTEST=1` | 非交互自检：打印节点 ID / 数据目录 / 端口 / DHT 已知节点数后自行退出 |
| `P2PCHAT_SELFTEST_WAIT` | 自检模式等待 DHT 引导的毫秒数（脚本默认 30000） |

**同机双实例示例**：

```powershell
$env:P2PCHAT_DATA_DIR="$env:TEMP\nodeA"
$env:P2PCHAT_P2PChat__UdpPort="20081"
$env:P2PCHAT_P2PChat__TcpPort="20091"
.\P2PChat.App.exe
```

### 5.4 运行时数据文件

**默认目录：用户主目录下的 `.p2pc/`**（`%USERPROFILE%\.p2pc`；Unix 为 `$HOME/.p2pc`）。

选择该位置的原因：程序可能位于只读目录；覆盖发布 exe 不应连带删除身份密钥与聊天数据；同一用户的多份程序副本天然共享同一身份。

主目录解析顺序（`DataPath.ResolveHomeDirectory`）：`USERPROFILE` → `HOME` → `Environment.SpecialFolder.LocalApplicationData`，全部为空才抛异常。

| 文件（相对 `.p2pc/`） | 内容 |
|---|---|
| `identity.json` | 长期身份密钥对（ECDH P-256） |
| `session_keys.json` | 各对端会话密钥 |
| `group_keys.json` | 各群组密钥 |
| `contacts.json` | 联系人列表 |
| `peers.txt` | 已知节点（`host:port`/行），启动加载、退出保存 |
| `logs/p2pchat-YYYYMMDD.log` | Serilog 按日滚动日志 |

> 上述文件均在 `.gitignore` 中，**务必不要入库**（含私钥）。

### 5.5 验证脚本

| 脚本 | 用途 |
|---|---|
| `scripts/config-probe.ps1`（325 行） | 实测配置注入方式（`P2PCHAT_` 环境变量 vs 命令行）、数据目录隔离是否生效 |
| `scripts/e2e-verify.ps1`（473 行） | 双节点端到端：拉起 A/B 两实例，A 以 B 的 UDP 端口为唯一引导节点，断言 `A PING → B 响应 → A 引导完成` 的真实 UDP 往返 |

**两脚本共同的重要约束**：
- **严禁复制 exe**。所有节点共用同一个固定 exe 路径 —— 复制 exe 会让 Windows 防火墙对**每个新程序路径重复弹出安全警报**（已确认为用户投诉根因）。
- 数据隔离一律用 `P2PCHAT_DATA_DIR` 指向临时目录（**必须**，否则会污染用户真实的 `~/.p2pc`），端口隔离用 `P2PCHAT_P2PChat__UdpPort` / `__TcpPort`。
- 默认启用 `P2PCHAT_SELFTEST=1` 避免遗留挂死的 TUI 进程；`finally` 兜底回收自己启动的进程。
- 两脚本均要求 **PowerShell 7+**（`#Requires -Version 7.0`）。

---

## 6. 测试体系

| 项目 | 覆盖内容 |
|---|---|
| `P2PChat.Core.Tests` | `NodeIdTests`（FromPublicKey / XOR / CommonPrefix / hex）、`SerializationTests`、**`SerializerAotRoundTripTests`**（AOT 语义往返） |
| `P2PChat.Crypto.Tests` | `EncryptionTests`（AES-GCM 加解密、nonce 唯一性、篡改检测、ECDH/HKDF 一致性） |
| `P2PChat.Networking.Tests` | `BencodeTests`（编解码往返、边界与畸形输入） |
| `P2PChat.Chat.Tests` | 项目骨架（当前无 .cs 用例） |
| `P2PChat.Integration.Tests` | `CryptoRoundTripTests`(15)、`DhtRoutingTableTests`(17)、`FileTransferIntegrityTests`(10)、`GroupChatIntegrationTests`(11)、`KnownDefectsTests`(6)、`MessageUnionSerializationTests`(7)、`TwoNodeChatIntegrationTests`(9)。`Support/NodeHarness.cs` + `Support/TestDoubles.cs` 提供双节点测试夹具与桩件 |

**测试栈**：xUnit 2.9.3 + Moq 4.20.72 + Shouldly 4.3.0 + coverlet.collector 6.0.4 + Microsoft.NET.Test.Sdk 17.13.0

> ⚠️ `Z:\Worktrees\P2PChat\main-67b12e64\tests\P2PChat.Integration.Tests\DEFECTS.md` 是**内部缺陷记录，已在 `.gitignore` 中排除**，不进入公开仓库。

---

## 7. 网络协议速查

### TCP 帧
```
[4B BigEndian 长度][payload]        // 长度上限校验 100MB
```

### 消息信封（Envelope）
```
offset  size  field
0       1     Version (=1)
1       1     MessageType (enum byte)
2       4     SequenceNumber (BE uint32)
6       20    SenderId (NodeId raw bytes)
26      16    MessageId (Guid)
42      8     Timestamp (BE int64, Unix ms)
50      N     Payload (MessagePack 序列化的 Message 子类)
```

### AES-256-GCM 密文布局
```
[nonce 12B][ciphertext][tag 16B]
```

### 群密钥包装
```
EncryptedGroupKey             = 32B 密文主体（随机群密钥经成员会话密钥加密）
EncryptedGroupKeyNonceAndTag  = 12B nonce + 16B tag
```

### DHT 紧凑节点（Bencode）
```
[20B NodeId][4B IPv4][2B Port]      // 26 字节
```

### 密钥派生
```
ECDH(P-256) → sharedSecret
HKDF-SHA256(sharedSecret, info="P2PChat-session-key") → sessionKey(32B)
```

---

## 8. 关键约定与陷阱清单

### 8.1 必须遵守

1. **保持 AOT 安全** —— 见 4.3 禁忌清单。新增反射/动态代码几乎必然破坏发布。
2. **不要随意调换 `Program.cs` 装配顺序** —— 端口需先绑定才能构造 `NodeInfo`，身份需先加载才能算 `NodeId`。因此存在"临时 ServiceProvider"阶段。
3. **`init` vs `set`** —— 凡经 MessagePack 反序列化的属性，用 `set`；`init` 会被 MsgPack 0.17 无条件重置。
4. **DI 生命周期统一 Singleton** —— 所有服务注册为 `AddSingleton`，与连接池/Channel/密钥缓存的进程级语义一致。
5. **跨线程输出统一走 `_inbox`** —— TUI 不允许后台线程直接写控制台。
6. **数据文件不入库** —— `.p2pc/`、`data/`、`identity.json`、`session_keys.json`、`group_keys.json`、`contacts.json`、`peers.txt`、`logs/` 已在 `.gitignore`。
7. **运行时数据放 `用户主目录/.p2pc`，不写程序目录** —— 见 5.4。仅在同机多实例时才用 `P2PCHAT_DATA_DIR` 覆盖。

### 8.2 已知的宽松/待完善点

| 项 | 现状 |
|---|---|
| `KademliaDhtService` | 完整实现但**未被 `Program.cs` 装配**；实际使用 `MainlineDhtService` |
| `KBucket` 淘汰策略 | 桶满**直接拒新**，未实现"PING 最老节点、不响应才淘汰"的标准 Kademlia 行为 |
| `MainlineDhtService.StoreAsync` / `FindValueAsync` | Mainline DHT 无此语义，当前为占位 |
| `P2PChat.Chat.Tests` | 项目存在但无测试用例 |

### 8.2.1 已订正的缺陷（2026-09-17）

| 项 | 原状 | 处置 |
|---|---|---|
| `NodeInfo.PublicKey` 注释 | 写作 "Ed25519 公钥 (32字节)"，实际是 **ECDH nistP256 / SubjectPublicKeyInfo DER(91字节)** | 已改注释并加 remarks 说明，避免按注释误引入 Ed25519 |
| `IEncryptionService` 注释 | `GenerateKeyPair` 写 "X25519"；`Sign`/`Verify` 写 "Ed25519签名 (或ECDsa回退)" | 实际实现为 **nistP256 / ECDSA P-256 + SHA-256**，已改注释并在接口上标注算法约定 |
| `FolderProfile.pubxml` | 指向 `net10.0` 与 `net10.0\publish\win-x64\` | 已改为 `net11.0` 与 `net11.0\win-x64\publish\` |
| 数据目录位置 | `<程序目录>/data`，写只读安装目录会失败，且覆盖发布即丢身份 | 改为 `用户主目录/.p2pc`，`P2PCHAT_DATA_DIR` 降级为可选覆盖 |
| `AesGcmEncryptionService` 类注释 | 写作 "ECDH(X25519)密钥协商" | 实为 **nistP256**，已改并与接口注释对齐 |
| `KeyExchangeMessage` 注释 | `EphemeralPublicKey` 写作 "(X25519, 32字节)" | 实为 **P-256 SubjectPublicKeyInfo，91 字节**（与 `GroupInviteHandler.EcdhPublicKeyLength` 常量一致），已改 |

### 8.2.2 已核实无需改动

| 项 | 核实结论 |
|---|---|
| .NET 目标框架 | `global.json` 固定 SDK `11.0.100-rc.1.26425.128`（`rollForward: latestFeature`、`allowPrerelease: true`）；**12 个项目全部为 `net11.0`**，无遗漏。`Directory.Build.props` 统一声明，各 csproj 的重复声明属冗余但无害 |

### 8.3 层间调用规则

```
UI ──► 只依赖 Core 抽象（IChatService/IGroupChatService/IContactService/IFileTransferService/IDhtService/IContactService）
App ──► 唯一允许 new 具体类型的地方（除各层内部）
Chat/FileTransfer ──► 通过 IMessageRouter 发消息，不直接碰 Socket
Networking ──► 只依赖 Core 抽象，不知道 Chat/FileTransfer 存在
Crypto ──► 纯工具性，无网络依赖
Core ──► 不引用任何其他项目
```

**扩展新消息类型的完整步骤**：
1. `Core/Enums/MessageType.cs` 加枚举值
2. `Core/Models/` 新增 `XxxMessage : Message`，加 `[MessagePackObject]`
3. `Core/Models/Message.cs` 加 `[Union(N, typeof(XxxMessage))]`（**N 必须唯一且不冲突**）
4. `MessageRouter.GetMessageType` 的 switch 加分支
5. 在合适的层实现 `IMessageHandler<XxxMessage>`
6. `Program.cs` 注册处理器并 `router.RegisterHandler(...)`
7. `MessageUnionSerializationTests` 加往返用例

---

## 9. 代码库规模参考

```
src/ 共约 6375 行 C#（不含 obj/bin），最大文件：
  P2PChat.Networking/Dht/KademliaDhtService.cs   537
  P2PChat.Networking/Dht/MainlineDhtService.cs   451
  P2PChat.UI/Views/P2PChatTui.cs                 449
  P2PChat.FileTransfer/Services/FileTransferService.cs  357
  P2PChat.App/Program.cs                         301
  P2PChat.Chat/Services/GroupChatService.cs      248
  P2PChat.Chat/Routing/MessageRouter.cs          219
  P2PChat.Chat/Services/ChatService.cs           218
```

Git：单次初始提交 `06cc4e8 chore: 初始提交 — P2PChat 去中心化 P2P 终端聊天`，分支 `main` / `workbuddy/main-67b12e64`。

---

## 10. 快速上手路径（给 Agent 的最短路径）

1. **想改消息协议** → `Core/Models/Message.cs` + 子类 + `Enums/MessageType.cs` + `MessageRouter.GetMessageType`
2. **想改加密** → `Crypto/Encryption/AesGcmEncryptionService.cs`（注意密文布局会被 `Decrypt` 与 `GroupChatService` 依赖）
3. **想改节点发现** → `Networking/Dht/MainlineDhtService.cs` + `Bencode.cs`
4. **想加 TUI 命令** → `UI/Views/P2PChatTui.cs` 的 `ProcessCommandAsync` + `ShowHelp`
5. **想加配置项** → `Program.cs` 的 `chatConfig.GetValue<T>(...)` + README 配置表
6. **想调试双节点** → `scripts/e2e-verify.ps1`（记得别复制 exe，用 `P2PCHAT_DATA_DIR` 隔离到临时目录）
7. **想改数据存放位置** → `Core/Extensions/DataPath.cs`（默认 `用户主目录/.p2pc`；`GetPath` 取文件、`GetDirectory` 取子目录）
