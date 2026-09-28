# P2PChat — Agent.md

> 面向 AI Agent / 新加入开发者的项目全景文档。
> 覆盖：整体架构、模块职责、关键类与函数、依赖关系、构建/运行/测试/配置方式、AOT 约束与已知陷阱。

---

## 1. 项目概述

**P2PChat** 是一个去中心化的 P2P 终端聊天程序（.NET 11 / C#，Native-AOT 单文件发布，约 8.7 MB）。

| 维度 | 说明 |
|---|---|
| 定位 | 学习/研究 P2P 网络与 Native-AOT 实践的终端聊天工具 |
| 节点发现 | BitTorrent **Mainline DHT**（Kademlia + KRPC + Bencode over UDP），`announce_peer` / `get_peers` + 自定义 `p2pc_peers` 扩展字段 |
| 通信 | 节点间 **TCP 直连**（4 字节大端长度前缀成帧） |
| 加密 | ECDH P-256 协商 → HKDF-SHA256 派生 → **AES-256-GCM** 加解密（**私聊与群聊共用同一线路契约**） |
| 完整性 | 长期 ECDSA P-256 **逐信封签名**（含 `SenderId`/公钥/时间戳/载荷），接收端强制验签（阶段 3.2） |
| NAT | 启动时 **UPnP IGD** 申请 TCP/UDP 同号端口映射（裸 HTTP/1.1 over SSDP + SOAP，保 AOT）；失败时明确降级提示 |
| 直连 | `/add <节点ID> <ip:port>` 静态对端 / `/connect <ip:port>` 盲连（hello 握手取真实 NodeId） |
| 序列化 | **MessagePack**（源生成器，零反射） |
| 界面 | 零依赖**自绘控制台 TUI**（`ConsoleScreen` + `P2PChatTui`），支持 `P2PCHAT_PLAIN=1` 线性模式 |
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
             └─► MessageRouter.SignEnvelope（ECDSA P-256 覆盖除签名字段外的整个信封）
              └─► TCP 连接池 → ITcpConnection.SendAsync → Socket
                   └─► Envelope(1B ver + 1B type + 4B seq + 20B senderId
                                + 16B msgId + 8B ts
                                + 4B pkLen + pk + 4B sigLen + sig + payload)

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

#### `Abstractions/`（16 个接口）

| 接口 | 职责 | 关键成员 |
|---|---|---|
| `ISerializer` | 序列化抽象 | `Serialize<T>` / `Deserialize<T>`(byte[]/Span/Stream) |
| `IEncryptionService` | 加解密与密钥协商 | `GenerateKeyPair` / `DeriveSharedSecret` / `DeriveSessionKey` / `Encrypt` / `Decrypt` / `Sign` / `Verify` / `GenerateRandomKey` |
| `IKeyStore` | 密钥持久化 | `GetOrCreateIdentity` / `Get/Set/RemoveSessionKey` / `Get/Set/RemoveGroupKey` / `GetKnownGroupIds` |
| `IUdpTransport` | UDP 传输 | `SendAsync(byte[], IPEndPoint)` / `ReceiveAsync` → `UdpPacket` |
| `ITcpTransport` | TCP 监听与连接 | `StartListeningAsync` / `ConnectAsync` / `AcceptAsync` / `IncomingConnections` / `ListenPort` |
| `ITcpConnection` | 单条 TCP 通道 | `SendAsync(ReadOnlyMemory<byte>)` / `ReceiveMessageAsync` / `IsConnected` / `RemoteEndPoint` / `ConnectionId` |
| `IRoutingTable` | Kademlia 路由表 | `AddOrUpdate` / `GetClosestContacts(target, count)` / `GetAllContacts` / `Remove` / `BucketSize` |
| `IDhtService` | DHT 门面 | `BootstrapAsync` / `FindNodeAsync` / **`RegisterStaticPeer`** / `StoreAsync` / `FindValueAsync` / `PingAsync` / `GetAllKnownNodes` / `OnPeerDiscovered` / **`AnnouncedPeerCount`** / **`LastAnnounceUtc`** / **`NatMappingState`** / **`LocalExternalEndPoint`** |
| `IMessageRouter` | 消息路由 | `RegisterHandler<T>` / `UnregisterHandler` / `RouteIncomingAsync` / `SendAsync` / `SendViaConnectionAsync` / `GetOrCreateConnectionAsync` / `CloseConnectionAsync` |
| `IMessageHandler` | 处理器契约 | 非泛型 `HandleAsync(Message, ITcpConnection, MessageEnvelope, ct)`；泛型 `IMessageHandler<T>` 提供默认接口方法转发 |
| `IChatService` | 私聊 | `SendPrivateMessageAsync` / `OnMessageReceived` / `HasSessionKey` |
| `IGroupChatService` | 群聊 | `CreateGroupAsync` / `SendGroupMessageAsync` / `GetKnownGroups` / `GetOnlineMembers` / `HandleInviteAsync` / `HandleNotifyAsync` |
| `IGroupMetadataStore` | **群组元数据持久化**（阶段 3.3） | `LoadAll()` / `Save(IEnumerable<GroupInfo>)` / `Remove(groupId)` |
| `IContactService` | 联系人 | `GetAllContactsAsync` / `Add` / `Remove` / `UpdateAlias` / `UpdateOnlineStatus` / `OnStatusChanged` |
| `IFileTransferService` | 文件传输 | `SendOfferAsync`(含 `chunkSize` 重载) / `AcceptTransferAsync` / `RejectTransferAsync` / `HandleFileMetaAsync` / `HandleFileChunkAsync` / `HandleFileAckAsync` / `OnProgressChanged` / `OnFileOfferReceived` |
| `IUpnpClient` | **UPnP IGD 端口映射**（阶段 2.1） | `TryMapAsync(port, leaseSeconds, ct)` → `UpnpMapping?` / `RemoveAsync(port, ct)` |

> **`IDhtService` 的 4 个观测成员是「带默认实现的接口属性」**（默认 `0` / `DateTime.MinValue` / `NatMappingState.NotAttempted` / `null`），目的是让 UI 不必引用 `P2PChat.Networking`。但它们**只由 `MainlineDhtService` 真正填充**：
> - `AnnouncedPeerCount` / `LastAnnounceUtc` ← 阶段 1.1 宣告结果
> - `NatMappingState` / `LocalExternalEndPoint` ← 阶段 2.2 `ApplyMapping(UpnpMapping?)` 结果
>
> ⚠️ 实测注意：`MainlineDhtService` 另有**公开属性 `AnnouncedNodeCount`**（累计成功宣告数），但**没有**覆写接口的 `AnnouncedPeerCount`；通过 `IDhtService` 引用读到的是默认值 `0`（`P2PChatTui` 自检输出即走接口）。写断言请以具体类型为准。
> `AnnounceNowAsync()`（立即触发一次宣告）只在 `MainlineDhtService` 上，**不在接口上**，测试需先向下转型。

#### `Models/`（20 个）

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

**`NodeInfo`**（record）：`NodeId`、`EndPoint`、`DhtEndPoint?`、`ExternalEndPoint?`、`PublicKey`、`LastSeen`、`State`

> ⚠️ **三个端点字段的语义（阶段 1.4 拆分之一改，勿回退成单端点）**：
> - `EndPoint`（required）= **TCP** 端点，对端据此建聊天/文件连接（`MessageRouter` 只认它）。本机由 `Program.cs` 填 `GetLocalIPAddress() + actualTcpPort`；远端由 `announce_peer` 的 `port` 字段或 `find_node` 的紧凑格式填。
> - `DhtEndPoint`（可选）= KRPC 报文的 **UDP 源地址**，仅 `MainlineDhtService` 在收到查询/响应时填。无 NAT 时与 `EndPoint` 相同。
> - `ExternalEndPoint`（可选）= 预留的「外部观察到的公网入口」。**实测：`src/**` 中没有任何一处给 `NodeInfo.ExternalEndPoint` 赋值**；阶段 2 的 UPnP 结果实际落在 `IDhtService.LocalExternalEndPoint`（`MainlineDhtService._localExternalEndPoint`）与 `MainlineDhtService.ListAnnouncedPeers()` 返回的 `ExternalEndPoint` 元组分量上。`Program.cs` 里「`ApplyMapping` 更新 `LocalNode.ExternalEndPoint`」的注释与实现不符（`LocalNode` 是只读属性）。

**`MessageEnvelope`**（record）：`Version`(=1)、`MessageType`、`SequenceNumber`、`SenderId`、`MessageId`、`Timestamp`、**`SenderPublicKey?`**、**`Signature?`**、`Payload`

> ⚠️ **阶段 3.2 起信封必须带签名**：`SenderPublicKey`（P-256 SPKI DER，约 91B）与 `Signature`（ECDSA P-256/SHA-256，约 64B）**任一缺失即被 `MessageRouter.RouteIncomingAsync` 在入口拒绝并告警**。线路格式因此不再是「50 字节固定头 + Payload」，见 §7。

#### `Enums/`
- `MessageType`：`Unknown=0, PrivateText=1, GroupText=2, FileMeta=3, FileChunk=4, FileAck=5, DhtRpc=6, KeyExchange=7, GroupInvite=8, GroupNotify=9, DeliveryAck=10`
- `PeerState`：`Offline=0, Online=1, Busy=2`
- `DhtMessageType`：`Ping=0, Store=1, FindNode=2, FindValue=3`

#### `Extensions/`
| 类型 | 职责 |
|---|---|
| `DataPath` | 数据目录解析。默认 **`用户主目录/.p2pc`**（`USERPROFILE` → `HOME` → `LocalApplicationData` 逐级回退）；可用 `P2PCHAT_DATA_DIR` 覆盖（多实例隔离）。成员：`Root`、`FolderName`、`OverrideEnvironmentVariable`、`GetPath(file)`、`GetDirectory(name)`、`SetRoot(path)`、`Reset()` |
| `EndpointText` | `ip:port` 字面量的 `TryParse` / `Format`。**刻意不做 DNS**——`/connect`、静态对端、联系人端点都只接受字面 `ip:port`，避免把「主机名解析」混进「连谁」的决策 |
| **`EnvelopeCodec`** | **信封线路编解码的唯一真相源**（`Serialize` / `Deserialize` / `ComputeSignedBytes`）。`MessageRouter.SerializeEnvelope` / `DeserializeEnvelope` 只是它的薄委托；`P2PChatTui.ReadHelloResponseAsync` 也必须走它 —— 详见本节末尾与 §7 |
| `SerializerExtensions.cs` → `MessagePackSerializer` | `ISerializer` 实现，静态 `Resolver` |
| `P2PChatMessagePackResolver` | 源生成 `IFormatterResolver` |
| `MessagePackResolverRegistry` | 下游 formatter 注册表 + `RegisteredResolverFallback` |

##### `Core/Extensions/EnvelopeCodec.cs`（197 行，**线路编解码的唯一真相源**）

`static class EnvelopeCodec` —— 固定头读写 + 变长字段长度前缀，`BinaryPrimitives` 手工大端，**零反射**。

| 成员 | 说明 |
|---|---|
| `FixedHeaderSize` = **50** | `1 + 1 + 4 + NodeId.Size(20) + 16 + 8`。用 `static readonly` 而非 `const`，因为 `NodeId.Size` 不是编译期常量 |
| `MaxFieldLength` = 100 MB | 单个变长字段的上限（TCP 帧之外再兜一层） |
| `Serialize(envelope)` / `Deserialize(rawData)` | 线路布局见 §7 |
| `ComputeSignedBytes(envelope)` | 待签字节序，见 §7 |

> 🔑 **为什么收敛到 Core（务必保持）**：编解码逻辑历史上被复制过多份 —— `MessageRouter` 内嵌一份，`P2PChatTui.ReadHelloResponseAsync` 又抄了一份。阶段 3.2 引入签名后**只改了 `MessageRouter` 那份**，TUI 那份仍按 50 字节固定头直接切 `Payload`，于是 **`/connect <ip:port>` 盲连的 hello 响应必然解析失败**（真实载荷前多了公钥长度前缀与签名前缀）。
> 修法不是「再同步一次」，而是把编解码放进依赖图的根（Core），让 Chat 与 UI 都引用同一份实现，从结构上消除再次漂移。
> `MessageRouter.SerializeEnvelope` / `DeserializeEnvelope` 现在只是**向后兼容的薄委托**，新代码直接调 `EnvelopeCodec`。
>
> **边界校验（`ReadLengthPrefixed` 三重防护，缺一不可）**：剩余字节够读 4 字节长度前缀 → 声明长度 ≤ `MaxFieldLength` → 声明长度 ≤ 实际剩余字节。**全程用 `long` 运算**，避免 `uint`→`int` 在长度 > `int.MaxValue` 时截断成负数。任一不过抛 `InvalidDataException`（宁可整条消息被丢弃，也不要一个畸形长度前缀就分配任意大小的数组）。

---

### 3.2 `P2PChat.Networking` — DHT 与传输

#### `Dht/MainlineDhtService.cs`（约 984 行，**实际的 DHT 实现**）

`IDhtService` 的真实实现，走 BitTorrent Mainline 协议（Bencode + KRPC over UDP）。

**协议扩展（与公网引导节点的兼容性前提）**：除标准 `ping` / `find_node` / `get_peers` / `announce_peer` 外，额外用两个**非标准字段**支撑「P2PChat 节点之间互相宣告与解析」：

| 字段 | 出现在 | 载荷 |
|---|---|---|
| `p2pc_peers` | `get_peers` **应答** | `list<26B>`，每条 `[20B NodeId][4B IPv4][2B TCP Port BE]` |
| `p2pc_id` | `announce_peer` **请求** | 与标准 `id` 冗余相同的 20B NodeId |

公网引导节点不解析这两个字段，只作回包中转 —— **它们自己的路由表里永远不会存下 P2PChat 节点**。因此「互相宣告」只在两台都是 P2PChat 且彼此进入对方最近 K 邻居时才成立。

| 成员 | 说明 |
|---|---|
| `BootstrapAsync(ct)` | **阶段 1.5**：遍历**全部**引导节点 PING；任一成功即触发一次 `IterativeFindNodeAsync` 迭代查找（`firstSuccess` 只作一次性门闩），其余节点继续尝试且失败只记 Debug。全部失败才告警。**引导结束后立即 `TryAnnounceAsync()` 一次**（不等 15 分钟），随后启动 `RefreshLoopAsync` |
| `StartReceivingAsync(ct)` | UDP 接收循环 → `HandleKrpcMessageAsync` |
| `FindNodeAsync(targetId, ct)` | **三段式（阶段 1.3）**：① `_staticPeers` 精确命中即返回 → ② `ResolveViaGetPeersAsync`（对最近 K=8 个节点发 `get_peers(info_hash=targetId)`，解析 `p2pc_peers`/6B `values`）→ ③ 路由表精确匹配兜底 |
| `RegisterStaticPeer(node)` | 阶段 0/1：登记「端点已知、无需 DHT」的静态对端，**同时入路由表**以便 `find_node` 应答能把它报给别人。不受路由表淘汰影响 |
| `PingAsync(node, ct)` | `KrpcPingAsync` |
| `StoreAsync` / `FindValueAsync` | Mainline DHT 无此语义，当前为占位/未实现（`StoreAsync` 直接 `Task.CompletedTask`） |
| `KrpcQueryAsync(ep, method, args, ct)` | 通用 KRPC 请求：写 `_pending` TCS（transactionId 关联）+ **10 秒超时** + UDP 发送 |
| `HandleQueryAsync(query, args, ...)` | 响应入站 `ping` / `find_node` / `get_peers` / `announce_peer`。**注意**：每次都会用包源端点 `CreateRemoteNode` 把对端塞进路由表 |
| `IterativeFindNodeAsync(targetId, ct)` | α 并行度迭代收敛，`NotifyAsync` 推事件 |
| `RefreshLoopAsync(ct)` | **每 15 分钟**两件事：① `GetStaleBucketTarget` 桶刷新 + 随机 ID 查询；② **`TryAnnounceAsync()` 重新宣告**（最近 K 邻居可能已变） |
| `AnnounceNowAsync(ct)` | **公开的**立即宣告入口（阶段 1.1），供测试断言，不必等 15 分钟。**不在 `IDhtService` 上** |
| `TryAnnounceAsync(ct)`（private） | 对最近 K=8 节点并发：先 `get_peers` 取 token → 再 `announce_peer(port = 本机 TCP 端口, token, p2pc_id)`；成功数累加到 `AnnouncedNodeCount`，有成功则刷新 `LastAnnounceUtc` |
| `ApplyMapping(UpnpMapping?)` | **阶段 2.2**：`null` → `NatMappingState.Unavailable`；非 null → 写入 `_localExternalEndPoint`、`NatMappingState.Mapped`，并**立即补一次 `AnnounceNowAsync`** 让公网尽早看到公网入口 |
| `ListAnnouncedPeers()` | 列出已收到的宣告（`PeerId` + `EndPoint` + `DhtEndPoint` + `ExternalEndPoint` 四元组），供状态栏/自检观察 |
| `NotifyAsync(node, method)` | 单向 KRPC 通知 → `PeerDiscoveryEventArgs` |

- 紧凑节点格式：26 字节（20B NodeId + 4B IPv4 + 2B Port BE）—— `Bencode.ParseCompactNodes` / `EncodeCompactNode`
- `announce_peer` **token 校验**：`_issuedTokens` 按 `ip:port` 记录随机 8 字节 token，校验不过则丢弃（不实现 token 有效期）
- UDP 错误处理：`UdpSocketErrorClassifier` 将 Windows ICMP port-unreachable（`ConnectionReset` 10054）识别为"对端不可达的正常反馈"，降级为 Debug 日志而非终止接收循环

> ⚠️ **隐私代价（阶段 1 引入，README 已同步）**：`announce_peer` 会把本机 **TCP 端口** 与（隐含的）公网可达性公开到**公共** DHT 网络。启动即自动宣告（引导后一次 + 每 15 分钟一次），**用户没有开关**。`Peers.txt` 里的公共引导节点会把这条信息散布到 BitTorrent 网络的任意第三方节点。

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

#### `Dht/Bencode.cs`（204 行）
纯静态 Bencode 编解码器（BitTorrent 线格式）。

- `Encode(object)` — 支持 `string` / `byte[]` / `int` / `long` / `List<object>`(list) / `Dictionary<string,object>`(dict)；dict 按 **Ordinal 字典序**输出（Mainline DHT 要求）
- `Decode(byte[] | ReadOnlySpan<byte>)` — 游标式 `ref int consumed` 解析，零拷贝友好
- `B2B` / `B2I` / `Get<T>` — 解码结果强类型取值助手
- `ParseCompactNodes` / `EncodeCompactNode` — 26 字节紧凑节点格式（`find_node` 应答的 `nodes`）
- **`ParseCompactPeers26`（阶段 1.2 新增）** — 解析 `p2pc_peers` / 标准 `values` 的**连续**字节流，每 26 字节切一条，布局与 `ParseCompactNodes` 完全相同

> 💡 **为什么需要 `ParseCompactPeers26`**：BitTorrent 标准的 `get_peers.values` 只有 6 字节 `[4B IPv4][2B Port]`，**拿不到宣告方的 NodeId**，无法与「我正在找的 NodeId」做匹配。P2PChat 因此自造 26 字节的 `p2pc_peers`（多带 20B NodeId）。`MainlineDhtService.FlattenByteList` 先把 bencode `list<object>` 里的多个 `byte[]` 拼成连续数组，再交给它按 26 切块。
> 兼容降级：公共节点返回的 6 字节 `values` 走 `KrpcGetPeersAsync` 的 else 分支，此时**用查询对端的 NodeId 当作条目 NodeId**（peer-as-bootstrap 的自然延续）。

#### `Dht/KademliaDhtService.cs`（558 行）
**另一套 DHT 实现**，基于自定义 MessagePack RPC（`DhtRpcMessage` + `DhtMessageType`），含 `NodeInfoDto`、`StoreRequest`、内存 `StoredValues` 字典、`FindValue` 的 `[1]+value` / `[0]+contacts` 标记约定。实现了 `IDhtService.RegisterStaticPeer`，但**未实现** `AnnouncedPeerCount` / `LastAnnounceUtc` / `NatMappingState` / `LocalExternalEndPoint`（走接口默认实现）。

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

#### `Transport/UpnpClient.cs`（361 行，**阶段 2.1 新增**）
`IUpnpClient` 实现 —— UPnP IGD 端口映射客户端。

> ⚠️ **刻意不用 COM / `NATUPnP` / Windows NAT 枚举**：COM 互操作会引入动态代码/运行时封送，破坏 **Native-AOT**。因此这里**手写裸 HTTP/1.1 over SSDP + SOAP**。

协议路径：

| 步 | 动作 | 超时 |
|---|---|---|
| 1 | UDP 广播 `M-SEARCH * HTTP/1.1` → `239.255.255.250:1900`，`ST = urn:schemas-upnp-org:device:InternetGatewayDevice:1`，`MX: 2` | 3 秒 |
| 2 | 从响应 header 取 `Location:`（大小写不敏感，**不做 XML 解析**），TCP 连接该 URL | — |
| 3 | SOAP POST `AddPortMapping`，**TCP 与 UDP 各一次**，外网端口与内网端口**同号**；任一失败即把成功的那个 `DeletePortMapping` 撤掉（不留半边映射） | 5 秒 |
| 4 | SOAP POST `GetExternalIPAddress`，用 `IndexOf("<NewExternalIPAddress>")` **字符串切片**取 IP（刻意不引入 `XmlDocument` / LINQ-to-XML，保 AOT） | 5 秒 |

- 服务类型固定 `urn:schemas-upnp-org:service:WANIPConn:1`；`SOAPAction` 由 body 里 `<u:Action>` 提取
- 成功返回 `UpnpMapping(ExternalEndPoint, Gateway, LeaseDuration)`；失败返回 `null`
- **所有失败模式（无 UPnP / SSDP 无响应 / SOAP 非 2xx / 解析失败）一律记 Debug 并返回 null，不抛不阻塞**。失败时由 `Program.cs` 打 Warning、TUI 明示「仅同网段可达」
- `RemoveAsync(port, ct)` 配对删除 TCP + UDP；进程退出路径调用，仅在 `TryMapAsync` 成功时调用

---

### 3.3 `P2PChat.Crypto` — 加密与密钥存储

#### `Encryption/AesGcmEncryptionService.cs`（137 行）

| 方法 | 实现要点 |
|---|---|
| `GenerateKeyPair()` | **ECDH P-256** 密钥对 |
| `DeriveSharedSecret(localPriv, remotePub)` | P-256 ECDH 原始共享密钥 |
| `DeriveSessionKey(sharedSecret, salt?, info?)` | **HKDF-SHA256**，默认 `info = "P2PChat-session-key"` |
| `Encrypt(plaintext, key)` | **AES-256-GCM**，输出布局 `[nonce(12B)][ciphertext][tag(16B)]`，nonce 每次随机 |
| `Decrypt(ciphertext, key)` | 按同一布局切分；`tag` 校验失败抛异常 |
| `Sign` / `Verify` | **ECDSA** 签名/验签 |
| `GenerateRandomKey()` | 32 字节随机（群密钥等） |

#### `Keys/FileBackedKeyStore.cs`（209 行）
`IKeyStore` 实现，内存 `ConcurrentDictionary` + 文件持久化。

- 持久化文件（均在 `_storeDirectory` = `DataPath.Root` 即 `用户主目录/.p2pc` 下）：
  - `identity.json` — 长期身份密钥对
  - `session_keys.json` — 每对端会话密钥
  - `group_keys.json` — 每群组密钥
  - ⚠️ **不含 `contacts.json`** —— 联系人由 `Chat/Services/ContactService` 自己落盘，但复用本文件里的 `JsonContext`（见 §8.3 层间规则）
- `GetOrCreateIdentity()` — 首次调用生成并落盘，后续读缓存
- 序列化全部走 **`JsonContext`（`System.Text.Json` 源生成器，`[JsonSerializable]` 标注 `StoredKeyPair` / `Dictionary<string,string>` / `StoredContact` / `List<StoredContact>` / `StoredGroup` / `List<StoredGroup>` 共 6 个类型）**，禁用反射
- `StoredModels.cs` — `StoredContact`（含 `EndPoint?`）、`StoredGroup`、`StoredKeyPair`、`StoredSessionKey`、`StoredGroupKey` 等 DTO + `partial class JsonContext : JsonSerializerContext`
- `_saveLock` 保护文件写入

#### `Keys/FileBackedGroupMetadataStore.cs`（165 行，**阶段 3.3 新增**）
`IGroupMetadataStore` 实现 —— 把 `GroupInfo` 全字段持久化到 **`~/.p2pc/groups.json`**。

| 成员 | 说明 |
|---|---|
| `LoadAll()` | 文件不存在 → 空集合；解析失败 → Warning + 空集合（**沿用 `FileBackedKeyStore` 的「加载失败静默吞」容错约定**）。逐条校验群密钥 base64 解码后必须 **32 字节**，否则跳过该群 |
| `Save(IEnumerable<GroupInfo>)` | **全量覆盖写**，`_saveLock` 保护，`WriteIndented = true` |
| `Remove(groupId)` | `LoadAll()` → 过滤 → `Save()`；无匹配项则直接返回（不产生无意义写盘） |

- 落盘格式：`CreatorIdHex` / `MemberIdsHexList` 走**大写 hex**（与 `NodeId.ToHexString` 风格一致），`GroupKeyBase64` 走 base64（与 `group_keys.json` 同源）
- 放在 **Crypto 层**而非 Chat 层：`Chat` 不需要知道文件路径细节，`Core` 只定义接口（见 §8.3 层间规则）

---

### 3.4 `P2PChat.Chat` — 路由、会话与处理器

#### `Routing/MessageRouter.cs`（304 行，**分发中枢**）

```csharp
private readonly ConcurrentDictionary<MessageType, IMessageHandler> _handlers;
private readonly ConcurrentDictionary<string, ITcpConnection> _connectionPool; // key: NodeId hex
private readonly ConcurrentDictionary<Guid, TaskCompletionSource<Message>> _pendingResponses;
private const int MaxConnectionsPerPeer = 3;
```

| 方法 | 说明 |
|---|---|
| `RegisterHandler<T>(IMessageHandler<T> h)` | 由 `T` 推断 `MessageType`（`GetMessageType` switch），注入 `_handlers` |
| `RouteIncomingAsync(envelope, sender, ct)` | **先验签**（`VerifyEnvelope`，见下）→ 查 `_handlers` → 调**非泛型** `IMessageHandler.HandleAsync` 默认接口方法转发 |
| `SendAsync(recipient, message, ct)` | 取/建连接 → `SendViaConnectionAsync` |
| `SendViaConnectionAsync(conn, message, ct)` | 组 `MessageEnvelope`（**强制 `SenderId = identity.NodeId`**，不再让 `Message.SenderId` 决定）→ `SignEnvelope` → `SerializeEnvelope` → `conn.SendAsync` |
| `GetOrCreateConnectionAsync(node, ct)` | 连接池命中且 `IsConnected` 则复用，否则 `ConnectAsync(node.EndPoint)` 后入池 |
| `ProcessIncomingConnectionAsync(conn, ct)` | 单连接循环：`ReceiveMessageAsync` → `DeserializeEnvelope` → `RouteIncomingAsync` |
| `CloseConnectionAsync(nodeId)` | 摘除并释放连接 |
| **`static SignEnvelope(envelope, identity, encryption)`** | **阶段 3.2**：用长期私钥 ECDSA 签名，写回 `Signature`。公开为 `static` 是为了让 `KeyExchangeHandler`（刻意不依赖 router）复用**同一份签名规则** |
| **`SignEnvelopeWith(envelope)`** | 实例版本：先从 `keyStore` 取身份并回填 `SenderId` / `SenderPublicKey`，再 `SignEnvelope` |
| **`static VerifyEnvelope(envelope, encryption, out failureReason)`** | 三道门禁，见下 |
| `static SerializeEnvelope` / `DeserializeEnvelope` | **薄委托** `Core.Extensions.EnvelopeCodec`（详见 §3.1 末尾） |

**入站验签三道门禁（`VerifyEnvelopeCore`，任一不过即丢弃 + Warning）**：

1. `Signature` 为 null/空 → 「缺少签名」
2. `SenderPublicKey` 为 null/空 → 「缺少发送方公钥」
3. `NodeId.FromPublicKey(SenderPublicKey) != SenderId` → 「SenderId 与 SenderPublicKey 不匹配」（**防 `SenderId` 冒名**）
4. `encryption.Verify(...)` 失败 → 「ECDSA 验签失败」

> ⚠️ **AOT 关键决策（勿回退）**：`RouteIncomingAsync` **刻意不使用 `reflection GetMethod/Invoke`**。Native-AOT 下反射目标缺少静态调用点会被 ILC 裁剪，导致处理器**静默失效**（原实现产生 IL2075 警告）。改为泛型接口的默认接口方法转发。
>
> ⚠️ **协议兼容性破坏性变更**：验签是**强制**的。任何未升级到阶段 3.2 的对端发来的信封（无 `SenderPublicKey`/`Signature`）会被直接丢弃 —— 老版本节点与新版本节点**无法互通**。

#### `Handlers/`（5 个）

| 处理器 | 处理类型 | 行为 |
|---|---|---|
| `PrivateMessageHandler` | `PrivateText` | 从 `IKeyStore` 取会话密钥 → 校验长度 → Base64 解码 `Content` → AES-GCM 解密 → `ChatService.PublishMessageAsync`；**解密失败则丢弃并告警** |
| `GroupMessageHandler` | `GroupText` | 取群密钥 → Base64 解码 `Content` → AES-GCM 解密 → UTF-8 还原明文 → 推事件；**解密失败则丢弃并告警**（与 `PrivateMessageHandler` 对称；见 `notes/implemented/bug-fix/2026-09-21-group-message-encryption`） |
| `KeyExchangeHandler` | `KeyExchange` | ① `RecordPeerPublicKey`：从**信封**的 `SenderPublicKey`（已被 `MessageRouter` 验签并与 `SenderId` 绑定）登记对端长期公钥 —— 不用载荷里那个对端可控的 `SenderId` 给公钥定身份。② `IsResponse=false`：生成临时密钥对 → ECDH+HKDF 派生会话密钥入 keyStore → **沿同一 TCP 连接回写 `IsResponse=true` 响应**，信封走 `MessageRouter.SignEnvelope` + `MessageRouter.SerializeEnvelope`（该 handler 刻意不注入 `MessageRouter`，但签名规则必须一致，故用静态方法集中）。③ `IsResponse=true`：仅记 Debug，由发起方在**同一出站连接**上完成派生（keyStore 里不允许存临时私钥） |

> 🆕 **`PeerPublicKeyRegistry`（本轮改动中）** —— 与 `KeyExchangeHandler` 同文件的 `sealed class`：`ConcurrentDictionary<NodeIdHex, byte[]>` + `PeerPublicKeyRegistry.Shared` 进程级默认单例（**未注册 DI**，测试可显式注入独立实例）。
> `Record(claimedNodeId, pubKey)` / `TryGet(peerId, out pubKey)` 都会**重新校验 `SHA-1(公钥) == 登记的 NodeId`**（常量 `P256SubjectPublicKeyInfoLength = 91`），自洽性被破坏的条目会被丢弃并清除 —— 表内不可能出现「用 A 的 NodeId 查到 B 的公钥」这种会把群密钥包装给错误接收方的条目。
| `GroupInviteHandler` | `GroupInvite` | 解密 `EncryptedGroupKey` → 写入 keyStore → 调 `GroupChatService.HandleInviteAsync` |
| `GroupNotifyHandler` | `GroupNotify` | 调 `GroupChatService.HandleNotifyAsync` |

#### `Services/ChatService.cs`（202 行）
`IChatService` 实现，私聊主管道。

| 方法 | 说明 |
|---|---|
| `SendPrivateMessageAsync(recipientId, text, ct)` | `FindNodeAsync` 定位（未命中抛 `InvalidOperationException`）→ `EnsureSessionKeyAsync` → 校验 `sessionKey.Length == 32` → AES-GCM 加密 → Base64 存 `TextMessage.Content` → 本地推 `ChatMessageEvent(IsOutgoing=true)` |
| `PerformKeyExchangeAsync(recipient, identity, ct)` | 发 `KeyExchangeMessage`（临时公钥）→ `ReadKeyExchangeResponseAsync` **在同一连接上读响应** |
| `EnsureSessionKeyAsync(...)` | 每对端一把 `SemaphoreSlim`，避免并发重复密钥交换 |
| `ReadKeyExchangeResponseAsync(...)` | 非响应类型消息**交回 `MessageRouter`** 继续路由，不丢弃 |
| `HasSessionKey(peerId)` | 查询 keyStore |
| `OnMessageReceived` | `Channel<ChatMessageEvent>` 的 `ReadAllAsync()` |

> ⚠️ **`ConversationId` 必须是方向无关的**：`ConversationId.ForPrivate(identity.NodeId, recipientId)` 把本机与对端两个 ID 拼接。历史缺陷曾写成 `recipientId`，导致接收端的消息落进一个 UI 永远选不中的会话桶。UI 侧 `PrivateConversationKey` 必须调用**同一个**函数。

#### `Services/GroupChatService.cs`（约 549 行）
`IGroupChatService` 实现。构造时注入 `IGroupMetadataStore`（阶段 3.3）。

- 常量：`AesGcmNonceLength=12`、`AesGcmTagLength=16`、`GroupKeyMetadataLength=28`
- `LoadPersistedGroups()`（**构造函数里调用**）— 从 `IGroupMetadataStore.LoadAll()` 恢复群组；**仅当 keyStore 缺该群密钥时才回填**，不覆盖已有值；加载异常静默吞
- `CreateGroupAsync(name, members, ct)` — `groupId = SHA256("{name}:{creatorNodeIdHex}:{unixMs}")` 转小写 hex（创建者必须是本节点**真实身份** `NodeId.FromPublicKey`，**不可**用 `PublicKey.Take(20)`——那是 P-256 SPKI DER 的固定头，对每个节点都一样），随机 32B 群密钥，对**每个成员独立**包装群密钥（见下），发 `GroupInviteMessage`；**落盘 `~/.p2pc/groups.json`**
- `SendGroupMessageAsync(groupId, text, ct)` — **AES-256-GCM 加密 `Content`**（群密钥直接当 32B key）后向成员**扇出**，跳过自己（见 `notes/implemented/bug-fix/2026-09-21-group-message-encryption`）
- `HandleInviteAsync(invite, ct)` — 从 keyStore 读取**已解密**的群密钥（**兼容旧版「32B 明文且无 SenderPublicKey/NonceAndTag」的本地调用**），写入 `_groups` + keyStore 并落盘
- `HandleNotifyAsync(notify, ct)` — `dissolve` 时同步从 `_groups`/keyStore/`groups.json` 三处移除
- `PersistGroups()` — `IGroupMetadataStore.Save(_groups.Values)`，异常只 Warning
- `GetKnownGroups` / `GetGroup` / `GetOnlineMembers`

**群密钥包装（`EncryptGroupKeyForMember`）**：
- **正常路径**：用「本机长期私钥 + 成员长期公钥」`DeriveSharedSecret`(P-256 ECDH) → `DeriveSessionKey`(HKDF-SHA256) 得 32B 包装密钥
- **兼容路径**：若成员公钥长度为 `LegacyPublicKeyLength`(32，旧版/测试节点)，无法导入 SPKI DER，退化为 `SHA256(公钥)` 直接当包装密钥
- 两种路径都用 AES-GCM，**明文群密钥恒为 32B → 密文主体恒为 32B**（否则抛 `CryptographicException`）

> 🆕 **成员公钥三级解析 `ResolveMemberPublicKeyAsync`（本轮改动中，代码仍在变动）** —— 静态/盲连接入的对端 `NodeInfo.PublicKey` 必然为空（`/add` 只能给 `ip:port`，`MainlineDhtService` 的每条解析路径也都返回 `Array.Empty<byte>()`），但包邀请又必须用对方公钥做 ECDH。故逐级降级：
> 1. `NodeInfo.PublicKey` 里自带的公钥（`IsUsablePublicKey` 校验）
> 2. `PeerPublicKeyRegistry` 里密钥交换阶段登记的对端公钥
> 3. **主动发一次 `KeyExchangeMessage`**（`ProbeMemberPublicKeyAsync`），从对端**已验签的响应信封**里取它的长期公钥
>
> 三级都拿不到才返回 `null`，由调用方**显式记 Error 并跳过该成员**（不再静默）。同一节点的探测用 `_probeLocks` 串行化 —— 否则两个调用方会互相抢读路由器连接池里那条共享连接上的信封（与 `ChatService._exchangeLocks` 同一理由）。
> 刻意**不新增线路字段**：信封早已携带同一个公钥且签名覆盖它，新增载荷字段反而要自己再做一次身份绑定，还会改变被签名的载荷字节。

**群密钥包装布局**：`EncryptedGroupKey` = 32B 密文主体；`EncryptedGroupKeyNonceAndTag` = 12B nonce + 16B tag

> ⚠️ **历史缺陷（已修）**：曾把密文主体当明文群密钥使用。现约定 `GroupInviteHandler` 先解密并写入 keyStore，`HandleInviteAsync` 只读解密结果。

#### `Services/ContactService.cs`（152 行）
`IContactService` 实现。`ConcurrentDictionary<string, Contact>`（key = NodeId hex）+ `contacts.json` 持久化（`JsonContext.Default.ListStoredContact`）+ `Channel<ContactStatusEvent>` 状态流。含 `FindByNodeId`、`AddContactAsync(nodeId, alias, endPointText?)`。

---

### 3.5 `P2PChat.FileTransfer` — 分块文件传输

`Services/FileTransferService.cs`（376 行）

- `DefaultChunkSize = 65536`（64 KB）
- `SendOfferAsync(recipientId, filePath, ct)` → `SendOfferAsync(recipientId, filePath, DefaultChunkSize, ct)`
- **`SendOfferAsync(recipientId, filePath, chunkSize, ct)`** — 发送方决定分块大小，写入 `FileMetaMessage.ChunkSize` 与 `TransferState.ChunkSize`（**同源**），并据此算 `TotalChunks`
- `AcceptTransferAsync(transferId, saveDirectory, ct)` / `RejectTransferAsync(transferId, reason?)`
- `HandleFileMetaAsync`（接收方；`meta.ChunkSize <= 0` 时 Warn 并回退 `DefaultChunkSize`）/ `HandleFileChunkAsync`（按 `ChunkIndex * state.ChunkSize` 定位落序写入）/ `HandleFileAckAsync`
- `SendFileChunksAsync(state, ct)` — 发送循环，**读缓冲与切片都严格按 `state.ChunkSize`**（阶段 3.4 修复：此前硬编码 `DefaultChunkSize`，发送方指定的分块会被忽略）
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

#### `Views/P2PChatTui.cs`（798 行，主 UI 循环）
`sealed class P2PChatTui(...)` 为主构造函数形式，注入各服务。

- 常量：`MaxHistory = 500`、`SystemConversation = "__system__"`
- `RunAsync(ct)` — 主循环：`DrainInbox` → `Render` → `ReadKeyOrNull` → 分流（`HandleKey` / plain 模式的 `SubmitLine`）→ 50ms 节拍
- **命令集**：`/msg`（`/pm`）`/group` `/file` `/add` `/connect` `/id`（`/me`）`/net`（`/dht`）`/contacts` `/accept` `/reject` `/help` `/quit`（`/exit`）
- **快捷键**：F1/F2/F3/F10、Ctrl+C、Tab、Shift+Tab、PgUp/PgDn、↑/↓
- **4 个后台循环**：`ProcessIncomingMessagesAsync`（聊天事件）、`ProcessFileOffersAsync`（文件 Offer）、`RefreshDhtPeriodicallyAsync`（**5 秒** DHT 状态刷新）、**`ReadPlainInputAsync`（仅 plain 模式）**
- `_inbox`（`ConcurrentQueue`）汇聚后台线程输出 → `DrainInbox()` 由 UI 线程消费（避免跨线程写控制台）
- `RefreshDhtStatus()` — 状态栏 `DHT: {在线}/{总数} | ID:{短ID} | 联系人:{n}`
- `RunSelfTestAsync(ct)` — **`P2PCHAT_SELFTEST=1`** 非交互模式，打印：节点 ID / 数据目录 / 监听端口 / DHT 已知节点数 / 联系人数 / 群组数 / **宣告状态（`AnnouncedPeerCount` + `LastAnnounceUtc`）** / **UPnP 状态（`NatMappingState` + `LocalExternalEndPoint`）**，然后退出；等待时长由 **`P2PCHAT_SELFTEST_WAIT`**（默认 3000ms）控制
- `Render()` + `MarkDirty()` 脏标记驱动重绘

#### 线性（plain）模式 —— `P2PCHAT_PLAIN=1`

| 方面 | 行为 |
|---|---|
| 触发 | 显式设 `P2PCHAT_PLAIN=1`；或 `Console.Clear` / `ReadKey` 抛异常时**自动降级** |
| 输出 | 跳过全屏渲染，改为逐行 `Console.WriteLine`（`[短ID] 正文`） |
| 输入 | 无按键事件 → `ReadKeyOrNull()` 恒返回 `null`；改由后台循环 `ReadPlainInputAsync` 阻塞在 `Console.ReadLine()`，把整行投递进 `Channel<string> _plainInput` |
| 分派 | 主循环 `TryRead` 到整行后调 `SubmitLine(line)` —— **与交互模式的 `SubmitInput()` 走完全相同的命令分派**，两条路径语义一致 |
| EOF | `Console.ReadLine()` 返回 `null`（脚本注入完毕后关闭管道）→ 结束通道，`TryRead` 恒 `false`，不空转 |
| 用途 | `scripts/e2e-verify.ps1` 之类的脚本可直接抓 stdout 里的**明文**做断言（明文只在本地事件/解密后出现，链路上永远是密文） |

> ⚠️ **`RunAsync` 的顺序很关键**：`P2PCHAT_SELFTEST=1` 在 `P2PCHAT_PLAIN` 判断之后**立即返回**，不会进入阻塞输入循环 —— 自检与 plain 模式互斥。

#### `/connect <ip:port>` —— 盲连（`ConnectCommandAsync`）

不需要预先知道对端 NodeId 的直连路径：

1. 构造**随机 NodeId + 已知 EndPoint** 的临时 `NodeInfo`，用 `router.GetOrCreateConnectionAsync` 建一条 TCP 连接（10 秒超时）
2. 发一条 `KeyExchangeMessage{IsResponse=false}` 当作 **hello**
3. 同一连接上 `ReadHelloResponseAsync` 读响应 → **`response.SenderId` 就是对端真实 NodeId**（阶段 3.2 后该值已被验签保证不可伪造）
4. 用本地临时私钥 + 响应中的对端临时公钥 `DeriveSharedSecret`→`DeriveSessionKey`，写入 keyStore
5. 以「真实 NodeId + 已知 ip:port」**重新 `RegisterStaticPeer`**，并在 `IContactService` 落盘（别名 `peer-{短ID}`）

> ⚠️ **`ReadHelloResponseAsync` 必须用 `Core.Extensions.EnvelopeCodec.Deserialize`**。它历史上抄了一份 50 字节固定头的切分逻辑，阶段 3.2 加签名后没有同步，导致 `/connect` 的 hello 响应**必然解析失败**。编解码已收敛到 Core（详见 §3.1 末尾）。
>
> ℹ️ `/connect` 这里拿不到对端的**长期**公钥（响应信封被读走后只在本次调用栈内可见），因此登记静态对端时 `PublicKey` 是占位值 —— 群邀请路径靠 `PeerPublicKeyRegistry` 的三级解析补齐（见 §3.4 `GroupChatService`）。

> 临时连接用完即弃：连接池以 NodeId 为键，下一次 `/msg` 会用「真实 NodeId」重新建连。
> `/add <节点ID> <ip:port> [别名]` 走的是更短的路径：显式端点直接 `RegisterStaticPeer`，不经过 hello。

---

---

### 3.7 `P2PChat.App` — 入口与装配

`Program.cs`（393 行）是唯一入口，装配顺序**不可随意调换**（依赖实际端口/身份/UPnP 结果）：

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
   - 合并：配置 BootstrapNodes + peers.txt + 4 个公共引导节点（Distinct 去重）
      router.bittorrent.com:6881 / router.utorrent.com:6881
      dht.transmissionbt.com:6881 / dht.aelitis.com:6881
   - keyStore.GetOrCreateIdentity() → NodeId.FromPublicKey() = 本地 ID
   - new UdpTransport(preferredUdpPort, ...)  → actualUdpPort
   - new TcpTransport(...); await StartListeningAsync(preferredTcpPort) → actualTcpPort
   - GetLocalIPAddress()
   - 构造 NodeInfo localNode（EndPoint = 本机 IP + actualTcpPort；DhtEndPoint / ExternalEndPoint 不填）
   - ★ 阶段 2.1/2.3：new UpnpClient(logger, localIp) → TryMapAsync(actualTcpPort)
     · 成功 → Information 日志；失败 → Warning 明示「仅同网段可达，改用 /add 或 /connect」
     · 抛异常也一律吞掉，按「未映射」处理
   - 逐个解析引导节点（IP 直读 / 否则 Dns.GetHostAddressesAsync，失败 Warning 跳过）
5. 注册剩余服务（用实际端口/身份）
   localNode / IUdpTransport / ITcpTransport / IUpnpClient / IRoutingTable /
   IDhtService(MainlineDhtService, bootstrapEndpoints, alpha)
   Chat 层（IMessageRouter + IGroupMetadataStore(FileBackedGroupMetadataStore) + 5 个 Handler
   + IChatService / IGroupChatService / IContactService）、FileTransfer 层、UI 层
   （等价于各层 AddP2PChatXxx 扩展；注意 Program.cs 目前是**手工逐条注册**，没有调用扩展方法）
6. BuildServiceProvider()
   - dhtService.BootstrapAsync()（有引导节点才发；内部已触发首次 announce_peer）
   - ★ RegisterStaticPeersFromContacts(...)：把 contacts.json 里**带显式端点**的联系人逐个
     RegisterStaticPeer → 重启后无需重新 /add 即可直连
   - ((MainlineDhtService)dht).StartReceivingAsync(dhtCts.Token)
   - ★ dht.ApplyMapping(upnpMapping)（阶段 2.2：写 LocalExternalEndPoint + NatMappingState；
     成功时内部立即补一次 AnnounceNowAsync）
   - router.RegisterHandler(5 个 Handler)
   - _ = ProcessIncomingTcpAsync(tcpTransport, router, provider, appCts.Token)
   - await tui.RunAsync(appCts.Token)   ← 阻塞主线程
   - 退出：取消 CTS → SaveKnownPeers(peers.txt)
   - finally: ★ 仅当 upnpMapping != null → upnpClient.RemoveAsync(actualTcpPort) 撤销映射 → Log.CloseAndFlush()
```

> ⚠️ **顺序约束**：
> 1. `localNode` 必须在 `actualTcpPort` 之后构造 —— `announce_peer` 宣告的就是这个 TCP 端口。
> 2. **UPnP 探测必须在 `BuildServiceProvider()` 之前做完** —— 它是阻塞的（SSDP 3s + SOAP 5s），放在临时 provider 阶段可以复用现成的 `loggerFactory`。
> 3. **`ApplyMapping` 必须在 `BootstrapAsync` 之后** —— 首次宣告可能已经跑完，此时补一次才能让公网看到映射后的入口。
> 4. **撤销映射只在成功时做** —— 否则路由器找不到对应映射会直接拒响应，白白拖长退出。
> 5. ⚠️ `ApplyMapping` 的注释写着「更新 `LocalNode.ExternalEndPoint`」，但 `MainlineDhtService.LocalNode` 是只读属性，实际只写了服务自身的 `_localExternalEndPoint`；`NodeInfo.ExternalEndPoint` **至今无人赋值**。

辅助方法：
- `GetLocalIPAddress()` — `Socket.Connect("8.8.8.8", 65530)` 读 `LocalEndPoint` 取本机局域网 IP（失败回退 `IPAddress.Loopback`）
- `ProcessIncomingTcpAsync` — `AcceptAsync` 循环，每条连接 `_ = msgRouter.ProcessIncomingConnectionAsync(...)` 即发即忘
- `RegisterStaticPeersFromContacts` — 从 `IContactService` 读联系人，`EndPoint` 能被 `EndpointText.TryParse` 解析才 `RegisterStaticPeer`；解析不了的记 Warning 跳过，**不阻断启动**
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
| `P2PCHAT_SELFTEST=1` | 非交互自检：打印节点 ID / 数据目录 / 端口 / DHT 已知节点数 / 联系人数 / 群组数 / **宣告状态** / **UPnP 状态** 后自行退出 |
| `P2PCHAT_SELFTEST_WAIT` | 自检模式等待 DHT 引导的毫秒数（默认 3000，脚本通常用 30000） |
| `P2PCHAT_PLAIN=1` | **线性输出模式**：跳过全屏渲染，逐行写 stdout；stdin 改为**按行读取**（后台循环 `ReadPlainInputAsync`），命令语义与交互模式完全一致。供 `scripts/e2e-verify.ps1` 抓明文做端到端断言，也适合 stdout 被重定向 / CI 场景 |
| `P2PCHAT_SELFTEST=1` 与 `P2PCHAT_PLAIN=1` | **互斥**：`RunAsync` 先判 plain 再判 selftest，selftest 为 1 时直接返回，不进入任何输入循环 |

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

| 文件（相对 `.p2pc/`） | 内容 | 写入方 |
|---|---|---|
| `identity.json` | 长期身份密钥对（ECDH P-256） | `FileBackedKeyStore` |
| `session_keys.json` | 各对端会话密钥 | `FileBackedKeyStore` |
| `group_keys.json` | 各群组密钥 | `FileBackedKeyStore` |
| `groups.json` | **群组元数据**（阶段 3.3 新增）：GroupId / 群名 / 创建者 / 成员名单 / 群密钥(base64) / 创建时间 | `FileBackedGroupMetadataStore` |
| `contacts.json` | 联系人列表（含可选的显式 `ip:port`） | `ContactService`（复用 Crypto 的 `JsonContext`） |
| `peers.txt` | 已知节点（`host:port`/行），启动加载、退出保存 | `Program.LoadKnownPeers` / `SaveKnownPeers` |
| `logs/p2pchat-YYYYMMDD.log` | Serilog 按日滚动日志 | `Program.cs` Serilog 配置 |

> `groups.json` 与 `group_keys.json` **内容有重叠但用途不同**：前者是「群组目录」（重启后 UI 能列出群名/成员），后者是密钥库。`GroupChatService` 启动时从 `groups.json` 恢复群组，**仅在 keyStore 缺该群密钥时才回填**，避免覆盖更新的密钥。

> 上述文件均在 `.gitignore` 中，**务必不要入库**（含私钥）。

### 5.5 验证脚本

| 脚本 | 用途 |
|---|---|
| `scripts/config-probe.ps1`（326 行） | 实测配置注入方式（`P2PCHAT_` 环境变量 vs 命令行）、数据目录隔离是否生效 |
| `scripts/e2e-verify.ps1`（586 行） | 双节点端到端：拉起 A/B 两实例，A 以 B 的 UDP 端口为唯一引导节点，断言 `A PING → B 响应 → A 引导完成` 的真实 UDP 往返 |

**两脚本共同的重要约束**：
- **严禁复制 exe**。所有节点共用同一个固定 exe 路径 —— 复制 exe 会让 Windows 防火墙对**每个新程序路径重复弹出安全警报**（已确认为用户投诉根因）。
- 数据隔离一律用 `P2PCHAT_DATA_DIR` 指向临时目录（**必须**，否则会污染用户真实的 `~/.p2pc`），端口隔离用 `P2PCHAT_P2PChat__UdpPort` / `__TcpPort`。
- 默认启用 `P2PCHAT_SELFTEST=1` 避免遗留挂死的 TUI 进程；`finally` 兜底回收自己启动的进程。
- 两脚本均要求 **PowerShell 7+**（`#Requires -Version 7.0`）。

---

## 6. 测试体系

| 项目 | 覆盖内容 |
|---|---|
| `P2PChat.Core.Tests` | `NodeIdTests`（FromPublicKey / XOR / CommonPrefix / hex）、`EndpointTextTests`、`SerializationTests`、**`SerializerAotRoundTripTests`**（AOT 语义往返） |
| `P2PChat.Crypto.Tests` | `EncryptionTests`（AES-GCM 加解密、nonce 唯一性、篡改检测、ECDH/HKDF 一致性、Sign/Verify） |
| `P2PChat.Networking.Tests` | `BencodeTests`（编解码往返、边界与畸形输入、**`ParseCompactPeers26`**）、`RealDiscoveryTests`（真实 KRPC 闭环）、`UpnpClientTests` |
| `P2PChat.Chat.Tests` | 项目骨架（当前无 .cs 用例） |
| `P2PChat.Integration.Tests` | 多节点夹具（`Support/NodeHarness.cs` + `Support/TestDoubles.cs`）驱动的：`CryptoRoundTripTests`、`DhtRoutingTableTests`、`FileTransferIntegrityTests`、`GroupChatIntegrationTests`、`IdentityAndEndpointTests`、`KnownDefectsTests`、`MessageSigningTests`、`MessageUnionSerializationTests`、`TwoNodeChatIntegrationTests` |

**当前规模（实测 `dotnet test`，2026-09-21）**：**179 通过 / 0 失败 / 0 跳过**
—— Core 37 / Crypto 8 / Integration 115 / Networking 19；`dotnet build` 0 错 0 警。

**测试栈**：xUnit 2.9.3 + Moq 4.20.72 + Shouldly 4.3.0 + coverlet.collector 6.0.4 + Microsoft.NET.Test.Sdk 17.13.0

> ⚠️ `DEFECTS.md`（位于 `tests/P2PChat.Integration.Tests/`）是**内部缺陷记录，已在 `.gitignore` 中排除**，不进入公开仓库。
> ⚠️ **`DEFECTS.md` / `HANDOFF.md` §9 里的行号是「缺陷发现时」的快照**，加固后早已漂移。**核实任何行号都必须现场 `grep`/`read` 源码**，不要照抄文档里的行号。

---

## 7. 网络协议速查

### TCP 帧
```
[4B BigEndian 长度][payload]        // 长度上限校验 100MB
```

### 消息信封（Envelope）—— ⚠️ **阶段 3.2 已升级，旧布局失效**

```
offset  size  field
0       1     Version (=1)
1       1     MessageType (enum byte)
2       4     SequenceNumber (BE uint32)
6       20    SenderId (NodeId raw bytes)
26      16    MessageId (Guid)
42      8     Timestamp (BE int64, Unix ms)
50      4     SenderPublicKeyLength (BE uint32, 通常 91)
54      N     SenderPublicKey (P-256 SPKI DER)
54+N    4     SignatureLength (BE uint32, 通常 64)
58+N    N     Signature (ECDSA P-256 / SHA-256, DER)
58+2N   M     Payload (MessagePack 序列化的 Message 子类)
```

**待签字节（`EnvelopeCodec.ComputeSignedBytes`）** —— 与线路布局的差别只有一处：

```
Version(1B) || MessageType(1B) || Seq(4B BE) || SenderId(20B) || MessageId(16B)
  || Timestamp(8B BE) || 4B BE 公钥长度 || 公钥 || Payload
```

> 💡 换句话说：**签名覆盖除「签名自身」以外的全部信封内容**，包括 `SenderId`、`SenderPublicKey`、`MessageId`、`Timestamp` 与 `Payload`。长度前缀本身也在待签范围内。
> 验证顺序见 `MessageRouter.VerifyEnvelopeCore`：缺签名 → 缺公钥 → `SenderId != SHA1(SenderPublicKey)` → ECDSA 验签。任一不过即在 `RouteIncomingAsync` 入口丢弃并打 Warning。
> **收发的唯一实现是 `Core/Extensions/EnvelopeCodec.cs`**（见 §3.1 末尾）—— 不要在任何其他文件里重写这套切分逻辑。

> ⚠️ **与旧版的兼容性是破坏性的**：无 `SenderPublicKey` / `Signature` 的旧版信封一律被拒。升级任一端即等于要求两端同时升级。

### AES-256-GCM 密文布局
```
[nonce 12B][ciphertext][tag 16B]
```

### 群密钥包装
```
EncryptedGroupKey             = 32B 密文主体（随机群密钥经「ECDH+HKDF(成员长期公钥)」得到的包装密钥加密）
EncryptedGroupKeyNonceAndTag  = 12B nonce + 16B tag
```
> 兼容降级：成员 `PublicKey.Length == 32`（旧版/测试节点）时包装密钥退化为 `SHA256(PublicKey)`。

### DHT 紧凑节点 / 紧凑对端（Bencode）
```
find_node.nodes / get_peers.p2pc_peers 条目:  [20B NodeId][4B IPv4][2B Port BE]   // 26 字节
get_peers.values（BitTorrent 标准）:            [4B IPv4][2B Port BE]              //  6 字节，无 NodeId
```
> `p2pc_peers` 是 `list<26B>`（每条独立 bencode string）；`nodes`/`values` 是**一整块连续字节流**。
> P2PChat 节点之间只用 26 字节格式（要靠 NodeId 匹配目标）；6 字节 `values` 只来自公共节点，此时**用查询对端的 NodeId 顶替**。

### DHT token（announce_peer）
```
8B 随机 token，按 "ip:port" 记录在 _issuedTokens；无有效期
```

### 密钥派生
```
ECDH(P-256) → sharedSecret
HKDF-SHA256(sharedSecret, info="P2PChat-session-key") → sessionKey(32B)

群密钥包装（另起一条）：
ECDH(P-256, 本机长期私钥 + 成员长期公钥) → sharedSecret
HKDF-SHA256(sharedSecret, info="P2PChat-session-key") → wrappingKey(32B)
AES-256-GCM(wrappingKey) → 32B 密文主体 + 12B nonce + 16B tag

消息签名：
待签字节（见上） → ECDSA P-256 / SHA-256 → ~64B DER 签名
```

---

## 8. 关键约定与陷阱清单

### 8.1 必须遵守

1. **保持 AOT 安全** —— 见 4.3 禁忌清单。新增反射/动态代码几乎必然破坏发布。
2. **不要随意调换 `Program.cs` 装配顺序** —— 端口需先绑定才能构造 `NodeInfo`，身份需先加载才能算 `NodeId`。因此存在"临时 ServiceProvider"阶段。
3. **`init` vs `set`** —— 凡经 MessagePack 反序列化的属性，用 `set`；`init` 会被 MsgPack 0.17 无条件重置。
4. **DI 生命周期统一 Singleton** —— 所有服务注册为 `AddSingleton`，与连接池/Channel/密钥缓存的进程级语义一致。
5. **跨线程输出统一走 `_inbox`** —— TUI 不允许后台线程直接写控制台。
6. **数据文件不入库** —— `.p2pc/`、`data/`、`identity.json`、`session_keys.json`、`group_keys.json`、`groups.json`、`contacts.json`、`peers.txt`、`logs/` 已在 `.gitignore`。
7. **运行时数据放 `用户主目录/.p2pc`，不写程序目录** —— 见 5.4。仅在同机多实例时才用 `P2PCHAT_DATA_DIR` 覆盖。
8. **改线路格式只能改 `Core/Extensions/EnvelopeCodec.cs`** —— `MessageRouter.SerializeEnvelope` / `DeserializeEnvelope` 只是薄委托，`P2PChatTui.ReadHelloResponseAsync` 也调它。**任何地方都不要再写第二份切分逻辑**（已经因此坏过 `/connect`）。另外 `KeyExchangeHandler` 手工构造信封，必须走 `MessageRouter.SignEnvelope` 以保证签名规则唯一。
9. **新增出站路径必须签名** —— 任何绕过 `MessageRouter.SendViaConnectionAsync` 直接发信标的代码，产出的信封都会被对端在入口拒绝。
10. **不要在文档里引用未核实的行号** —— `DEFECTS.md` / `HANDOFF.md` 里的行号是缺陷发现时的快照，早已漂移。写文档前先 `grep` 源码。

### 8.2 已知的宽松/待完善点

| 项 | 现状 |
|---|---|
| `KademliaDhtService` | 完整实现但**未被 `Program.cs` 装配**；实际使用 `MainlineDhtService`。它只实现了 `IDhtService.RegisterStaticPeer`，其余新增观测成员走接口默认值 |
| `KBucket` 淘汰策略 | 桶满**直接拒新**，未实现「PING 最老节点、不响应才淘汰」的标准 Kademlia 行为 |
| `MainlineDhtService.StoreAsync` / `FindValueAsync` | Mainline DHT 无此语义，当前为占位（`StoreAsync` 直接返回 `CompletedTask`，`FindValueAsync` 返回 `null`） |
| **`NodeInfo.ExternalEndPoint`** | 字段存在但 **`src/**` 中无任何赋值点**。阶段 2 的 UPnP 结果落在 `IDhtService.LocalExternalEndPoint` / `MainlineDhtService._localExternalEndPoint`，`Program.cs` 里「`ApplyMapping` 更新 `LocalNode.ExternalEndPoint`」的注释与实现不符 |
| **`IDhtService.AnnouncedPeerCount`** | `MainlineDhtService` 只暴露了公开属性 **`AnnouncedNodeCount`**，未覆写接口属性 `AnnouncedPeerCount`；经 `IDhtService` 引用读到的是默认 `0`（TUI 自检输出即走接口） |
| **`IDhtService.AnnounceNowAsync`** | 不在接口上，只在 `MainlineDhtService` 上；调用/测试需先向下转型 |
| **NAT-PMP** | 阶段 2.1 只做了 **UPnP IGD**，NAT-PMP（`5351/udp`）未实现 |
| **UPnP 映射撤销** | 只在进程正常退出路径撤销；崩溃/强杀会留映射到租约过期（默认 3600s） |
| **宣告不可关闭** | 阶段 1.1 的 `announce_peer` **没有开关**，启动即自动执行并每 15 分钟重复（见 §3.2 隐私代价说明） |
| **端口映射假设同号** | `UpnpClient` 强制 TCP/UDP 用同一端口号；UDP 与 TCP 端口不同时不适用 |
| **`PeerPublicKeyRegistry` 未注册 DI** | 它是 `KeyExchangeHandler.cs` 里的进程级 `Shared` 单例，靠构造函数可选参数注入。测试可显式传独立实例避免跨用例污染，但生产装配路径上没有 DI 条目 |
| **`P2PChat.Chat.Tests`** | 项目存在但无测试用例 |
| **老版本互通** | 验签是强制的，阶段 3.2 前后两端**无法互通** |

### 8.2.1 已订正的缺陷（2026-09-17）

| 项 | 原状 | 处置 |
|---|---|---|
| `NodeInfo.PublicKey` 注释 | 写作 "Ed25519 公钥 (32字节)"，实际是 **ECDH nistP256 / SubjectPublicKeyInfo DER(91字节)** | 已改注释并加 remarks 说明，避免按注释误引入 Ed25519 |
| `IEncryptionService` 注释 | `GenerateKeyPair` 写 "X25519"；`Sign`/`Verify` 写 "Ed25519签名 (或ECDsa回退)" | 实际实现为 **nistP256 / ECDSA P-256 + SHA-256**，已改注释并在接口上标注算法约定 |
| `FolderProfile.pubxml` | 指向 `net10.0` 与 `net10.0\publish\win-x64\` | 已改为 `net11.0` 与 `net11.0\win-x64\publish\` |
| 数据目录位置 | `<程序目录>/data`，写只读安装目录会失败，且覆盖发布即丢身份 | 改为 `用户主目录/.p2pc`，`P2PCHAT_DATA_DIR` 降级为可选覆盖 |
| `AesGcmEncryptionService` 类注释 | 写作 "ECDH(X25519)密钥协商" | 实为 **nistP256**，已改并与接口注释对齐 |
| `KeyExchangeMessage` 注释 | `EphemeralPublicKey` 写作 "(X25519, 32字节)" | 实为 **P-256 SubjectPublicKeyInfo，91 字节**（与 `GroupInviteHandler.EcdhPublicKeyLength` 常量一致），已改 |

### 8.2.2 已订正的缺陷（2026-09-20 / 09-21，REPAIR-PLAN 阶段 0–3）

> 编号沿用 `REPAIR-PLAN.md` 的阶段编号。证据见 `notes/implemented/`。

| 编号 | 项 | 原状 | 处置 |
|---|---|---|---|
| **B2** | `SenderId` 全局同值 | 用 `PublicKey.Take(20)` 当 NodeId —— 那是 P-256 SPKI DER 的固定头，**每个节点都一样** | 全部改为 `NodeId.FromPublicKey(identity.PublicKey)`；`MessageRouter` 出站时**强制覆盖** `SenderId`，验签时再校验公钥派生值与 `SenderId` 一致 |
| **B3** | 私聊消息投错会话桶 | `ConversationId` 用 `recipientId`，方向相关，接收端落进 UI 选不中的桶 | 统一为 `ConversationId.ForPrivate(本机, 对端)`（方向无关），UI 与 `ChatService` 共用同一函数 |
| **B1** | 从不宣告自己 | `StoreAsync` 空实现；`announce_peer` 只回成功响应不落库 | 实现真实 `announce_peer` 闭环：`get_peers` 取 token → `announce_peer(port=本机 TCP 端口)` → 对端 `RecordAnnounce` 落 `_peerCache` |
| **1.1** | 宣告时机 | 只在 15 分钟刷新周期里 | 引导完成后**立即**宣告一次，之后随 `RefreshLoopAsync` 每 15 分钟重宣告（最近 K 邻居可能已变） |
| **1.2** | peers 解析缺失 | `Bencode` 只有 `ParseCompactNodes`/`EncodeCompactNode` | 新增 `ParseCompactPeers26`；标准 6 字节 `values` 走「用查询对端 NodeId 顶替」的兼容降级 |
| **1.3** | `FindNodeAsync` 只迭代 find_node | 公共 DHT 上没人宣告 `NodeId→端点`，迭代必不命中 | 改为三段式：静态对端 → `get_peers(info_hash=targetId)` → 路由表精确匹配 |
| **1.4** | 端点语义耦合 | `NodeInfo` 单端点，隐含「UDP 端口 == TCP 端口」前提 | 拆分 `EndPoint`（TCP）/ `DhtEndPoint`（KRPC 源地址）；宣告与查找分别走 TCP 端口与 UDP 源地址 |
| **1.5** | `BootstrapAsync` 首个成功即 `break` | 指定自定义引导节点后，公共 DHT 引导完全不会执行 | 改为「任一成功即进入迭代查找，但其余节点继续尝试且失败不阻塞」 |
| **2.1** | NAT 完全没处理 | 无任何打洞/映射，跨网络不可达 | 新增 `IUpnpClient` + `Transport/UpnpClient.cs`（裸 HTTP/1.1 over SSDP + SOAP，**刻意不用 COM 以保 AOT**），TCP/UDP 同号映射 |
| **2.2** | UPnP 结果无处可查 | — | `MainlineDhtService.ApplyMapping` 写 `LocalExternalEndPoint` + `NatMappingState`，成功时补一次 `AnnounceNowAsync`；⚠️ `NodeInfo.ExternalEndPoint` 本身仍无人赋值 |
| **2.3** | 映射失败静默 | 用户不知道可达范围 | 失败打 Warning，TUI 自检输出显式区分 `NotAttempted` / `Mapped` / `Unavailable` 三态 |
| **3.1** | 群聊消息明文上线 | `TextMessage.Content` 直接是明文，README「端到端加密对群聊成立」是假的 | `SendGroupMessageAsync` 改为 AES-256-GCM 加密 + Base64，`GroupMessageHandler` 对称解密；解密失败丢弃并告警 |
| **3.2** | `SenderId` 完全可伪造 | `IEncryptionService` 有 `Sign`/`Verify` 但全仓库无调用点 | 信封新增 `SenderPublicKey` + `Signature`，出站 `SignEnvelope`、入站 `VerifyEnvelope` 四道门禁；**线路格式升级**（§7） |
| **3.3** | 群组元数据不持久化 | 重启后群名/成员全丢 | 新增 `IGroupMetadataStore` + `FileBackedGroupMetadataStore`（`~/.p2pc/groups.json`），放在 Crypto 层避免 Chat 反向依赖文件路径 |
| **3.4** | 文件分块大小被忽略 | `SendFileChunksAsync` 硬编码 `DefaultChunkSize`，忽略 `state.ChunkSize` | 发送循环的读缓冲与切片都改用 `state.ChunkSize`；接收侧对 `ChunkSize <= 0` 回退并 Warn |
| **4.x** | 测试与真实链路脱节 | `NodeHarness.SenderId` 复用同一常量，掩盖了 B2 | `NodeHarness` 改用真实 `KeyPair.NodeId`；新增「两个 harness 的 SenderId 必须不同」等守卫断言，并新增 `RealDiscoveryTests` / `MessageSigningTests` / `UpnpClientTests` |
| **🆕** | 信封格式被抄了三份 | `MessageRouter` 内嵌一份、`P2PChatTui.ReadHelloResponseAsync` 又抄一份（仍按 50 字节固定头切 Payload） | 编解码收敛到 `Core/Extensions/EnvelopeCodec.cs`，两处改为薄委托；补 `MaxFieldLength` 与三重边界校验（长度前缀截断 / 超上限 / 超剩余字节），失败抛 `InvalidDataException` |
| **🆕** | 群邀请需要成员长期公钥 | 静态/盲连接入的对端 `NodeInfo.PublicKey` 恒为空（`/add` 只能给 `ip:port`，DHT 各解析路径都返回 `Array.Empty<byte>()`） | `PeerPublicKeyRegistry` 从**已验签的信封**登记对端公钥；`GroupChatService.ResolveMemberPublicKeyAsync` 三级降级（`NodeInfo.PublicKey` → 登记表 → **主动发一次 `KeyExchangeMessage` 探测**），拿不到则显式记 Error 并跳过该成员。**（本轮改动中）** |

### 8.2.3 已核实无需改动

| 项 | 核实结论 |
|---|---|
| .NET 目标框架 | `global.json` 固定 SDK `11.0.100-rc.1.26425.128`（`rollForward: latestFeature`、`allowPrerelease: true`）；**12 个项目全部为 `net11.0`**，无遗漏。`Directory.Build.props` 统一声明，各 csproj 的重复声明属冗余但无害 |

### 8.3 层间调用规则

```
UI ──► 只依赖 Core 抽象（IChatService/IGroupChatService/IContactService/IFileTransferService/IDhtService/IUpnpClient）
App ──► 唯一允许 new 具体类型的地方（除各层内部）
Chat/FileTransfer ──► 通过 IMessageRouter 发消息，不直接碰 Socket
Networking ──► 只依赖 Core 抽象，不知道 Chat/FileTransfer 存在
Crypto ──► 纯工具性，无网络依赖
Core ──► 不引用任何其他项目
```

> ⚠️ **两处「看起来违反」但其实有意的跨层**：
> 1. `FileBackedGroupMetadataStore` 在 **Crypto** 层实现 `IGroupMetadataStore`（接口在 Core）。这样 `Chat` 只依赖接口、不需要知道 `~/.p2pc/groups.json` 这个路径，也不需要 `System.Text.Json` 源生成上下文。
> 2. `ContactService`（Chat 层）自己写 `contacts.json`，却复用了 **Crypto** 的 `JsonContext`（AOT 源生成器不能跨程序集自动发现）。改 `JsonContext` 时记得同时覆盖这两处消费方。
> 3. `KeyExchangeHandler`（Chat 层）**静态调用** `MessageRouter.SignEnvelope` / `SerializeEnvelope`。同属 Chat 程序集，不违反分层；用静态方法而非注入是为了避免循环依赖并保留「在收到请求的同一 TCP 连接上直接回写」的能力。
> 4. **`Core/Extensions/EnvelopeCodec.cs` 是唯一允许定义信封线路格式的地方** —— `Chat` 与 `UI` 都调它。历史上 `MessageRouter` 与 `P2PChatTui` 各抄了一份，加签名后只有前者被更新，`/connect` 因此坏掉。把格式放在依赖图的根，从结构上杜绝再次漂移（详见 §3.1 末尾）。

**扩展新消息类型的完整步骤**：
1. `Core/Enums/MessageType.cs` 加枚举值
2. `Core/Models/` 新增 `XxxMessage : Message`，加 `[MessagePackObject]`
3. `Core/Models/Message.cs` 加 `[Union(N, typeof(XxxMessage))]`（**N 必须唯一且不冲突**）
4. `MessageRouter.GetMessageType` 的 switch 加分支
5. 在合适的层实现 `IMessageHandler<XxxMessage>`
6. `Program.cs` 注册处理器并 `router.RegisterHandler(...)`
7. `MessageUnionSerializationTests` 加往返用例
8. **若该消息要在同一条连接上手工回写**，必须用 `MessageRouter.SignEnvelope` + `MessageRouter.SerializeEnvelope`，否则对端验签必过不了

---

## 9. 代码库规模参考

```
src/ 共约 9142 行 C#（不含 obj/bin），最大文件：
  P2PChat.Networking/Dht/MainlineDhtService.cs   984
  P2PChat.UI/Views/P2PChatTui.cs                 798
  P2PChat.Networking/Dht/KademliaDhtService.cs   558
  P2PChat.Chat/Services/GroupChatService.cs      549
  P2PChat.App/Program.cs                         393
  P2PChat.FileTransfer/Services/FileTransferService.cs  376
  P2PChat.Networking/Transport/UpnpClient.cs     361
  P2PChat.Chat/Routing/MessageRouter.cs          304
  P2PChat.Chat/Handlers/KeyExchangeHandler.cs    241
  P2PChat.Crypto/Keys/FileBackedKeyStore.cs      209
  P2PChat.Networking/Dht/Bencode.cs              204
  P2PChat.Chat/Services/ChatService.cs           202
  P2PChat.Core/Extensions/EnvelopeCodec.cs       197
  P2PChat.Crypto/Keys/FileBackedGroupMetadataStore.cs  165
```

> ⚠️ **行数只是量级参考，随时漂移** —— 修缺陷时大改单个文件是常态（`MainlineDhtService` 从 451 → 984 就是 REPAIR-PLAN 阶段 1–2 的累积）。**判断"某功能在哪实现"要靠文件名与符号名，不要靠行号。**

测试代码量（不含 obj/bin）：`Integration` ≈ 3145 行 / `Networking` ≈ 837 行 / `Core` ≈ 549 行 / `Crypto` ≈ 108 行；`P2PChat.Chat.Tests` 目前只有 `.csproj`。

Git 历史：`06cc4e8` 初始提交 → `c971054` 数据目录迁移 → `21cba0c` 引入 Agent Note → `bab0635` 阶段 0 修复 → `3833d19` 文档。分支 `main`。

---

## 10. 快速上手路径（给 Agent 的最短路径）

1. **想改消息协议** → `Core/Models/Message.cs` + 子类 + `Enums/MessageType.cs` + `MessageRouter.GetMessageType`；**改线路格式只能改 `Core/Extensions/EnvelopeCodec.cs`**，并同步更新 §7
2. **想改签名/验签** → `MessageRouter.SignEnvelope` / `VerifyEnvelopeCore` + `EnvelopeCodec.ComputeSignedBytes`（改动会让新老节点互不兼容）
3. **想改加密** → `Crypto/Encryption/AesGcmEncryptionService.cs`（密文布局会被 `Decrypt` 与 `GroupChatService` 依赖）
4. **想改节点发现** → `Networking/Dht/MainlineDhtService.cs` + `Bencode.cs`（含 `announce_peer` / `get_peers` 闭环与 `p2pc_peers` 扩展字段）
5. **想改 NAT 穿透** → `Networking/Transport/UpnpClient.cs` + `MainlineDhtService.ApplyMapping`；**注意是手写 SOAP，没有 COM，不能引入 XML 解析库**
6. **想加 TUI 命令** → `UI/Views/P2PChatTui.cs` 的 `ProcessCommandAsync` + `ShowHelp`（plain 模式与交互模式共用 `SubmitLine` 分派）
7. **想加配置项** → `Program.cs` 的 `chatConfig.GetValue<T>(...)` + README 配置表
8. **想调试双节点** → `scripts/e2e-verify.ps1`（记得别复制 exe，用 `P2PCHAT_DATA_DIR` 隔离到临时目录，`P2PCHAT_PLAIN=1` 抓明文）
9. **想改数据存放位置** → `Core/Extensions/DataPath.cs`（默认 `用户主目录/.p2pc`；`GetPath` 取文件、`GetDirectory` 取子目录）
10. **想加测试夹具** → `tests/P2PChat.Integration.Tests/Support/NodeHarness.cs`（`SenderId` 必须是真实 `KeyPair.NodeId`，不要用共享常量）
