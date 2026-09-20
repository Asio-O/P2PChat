# P2PChat 跨设备不可用 — 分析与修复方案

> 工作文档（**不是** Agent Note）。定稿的决策请按 `notes/README.md` 转为 `proposed/` Agent Note。
> 分析日期：2026-09-20。依据：源码逐段通读 + 两台设备实测 + 全量测试运行结果。

## 结论摘要

**测试全绿（121 通过 / 0 失败 / 0 跳过），但两台设备不可用。** 这不是巧合，而是本方案要解决的第一层问题：
现有测试**在结构上无法发现**导致不可用的缺陷（见 B7）。

阻断级缺陷 4 个，其中 **B1 单独一条就足以让全部聊天功能不可用**。

---

## 实施进度

| 阶段 | 状态 | 说明 |
|---|---|---|
| **阶段 0** 立刻可测 | ✅ **已完成**（2026-09-20） | 见下方「阶段 0 实施记录」 |
| **阶段 4.1–4.3** 测试策略（防复发部分） | ✅ **已完成** | `NodeHarness.SenderId` 已改为复用生产代码的 `KeyPair.NodeId`；新增 25 条守卫断言 |
| 阶段 1 让发现真的工作 | ⬜ 未开始 | |
| 阶段 2 穿透 NAT | ⬜ 未开始 | 依赖「同局域网 / 跨网络」的确认 |
| 阶段 3 安全与完整性 | ⬜ 未开始 | |
| 阶段 4.4–4.6 测试策略（真实发现 / e2e） | ⬜ 未开始 | |
| 各阶段对应的 Agent Note | ✅ 阶段 0 已补 | 三份：`implemented/bug-fix` ×2 + `implemented/feature` ×1 |

阶段 0 的提交为 `bab0635`（含三份 Agent Note 与本文档）。

### 阶段 0 实施记录

| 编号 | 动作 | 落地位置 |
|---|---|---|
| 0.1 | `Contact.EndPoint`（`ip:port` 文本）+ 持久化字段 | `Contact.cs`、`StoredModels.cs`（`StoredContact.EndPoint`） |
| 0.1 | `/add <节点ID(hex,40位)> [ip:port] [别名]`，第二个 token 能解析成端点就当作端点，否则当别名 | `P2PChatTui.AddContactCommandAsync` |
| 0.1 | 新增 `EndpointText`（`ip:port` ⇄ `IPEndPoint`，只接受字面 IP，不做 DNS） | `Core/Extensions/EndpointText.cs` |
| 0.2 | `IDhtService.RegisterStaticPeer`；`FindNodeAsync` **静态对端优先**，再退迭代查找；静态登记不受路由表淘汰影响 | `IDhtService`、`MainlineDhtService`、`KademliaDhtService`、测试用 `FakeDhtService` |
| 0.2 | 启动时把带端点的持久化联系人登记为静态对端 | `Program.RegisterStaticPeersFromContacts` |
| 0.3 | `SenderId` 统一改为真实身份；唯一定义点 `KeyPair.NodeId`（= `NodeId.FromPublicKey`） | `IEncryptionService.cs`（新增 `KeyPair.NodeId`）、`ChatService`×2、`KeyExchangeHandler`、`GroupChatService`×3 |
| 0.4 | 会话密钥登记在**对端真实 NodeId** 之下（随 0.3 自然成立） | `KeyExchangeHandler.HandleAsync` |
| 0.5 | 会话键方向无关：新增 `ConversationId.ForPrivate(a,b)`；TUI 拆出 `_currentPeerId`（会话键不能反解出对端） | `Core/Models/ConversationId.cs`、`ChatService`、`P2PChatTui` |

**验证结果**

- `dotnet build`：0 错误 0 警告。
- `dotnet test`：**146 通过 / 0 失败 / 0 跳过**（阶段 0 前为 121）。
- 新增守卫：`IdentityAndEndpointTests`（6 条，含「不同节点的公钥前 20 字节确实相同」这一前提断言，
  把 B2 的机制钉死）、`EndpointTextTests`（19 条）。
- 进程级实测：预置带 `EndPoint` 的 `contacts.json` 启动真实 AOT 产物，
  日志出现 `登记静态对端: 00112233 @ 127.0.0.1:20081`，端点解析正确。
- 服务级实测：`静态对端_仅凭显式端点即可完成双向加密私聊` —— 全程不调用 `Discover`，
  仅靠显式端点完成双向加密私聊，且两侧会话键一致。

**阶段 0 的已知遗留**

- 静态对端的 `PublicKey` 为空（`Contact` 不存对端公钥）。私聊不受影响（ECDH 只用临时密钥），
  但**群邀请的群密钥包装需要公钥**，因此对手工添加的联系人发群邀请会失败，
  且当前被 `CreateGroupAsync` 的 `catch {}` 静默吞掉。修法：在 `KeyExchangeMessage` 中回送长期公钥
  （线路变更），归入阶段 3。
- `/connect <ip:port>`（不预先知道对端节点 ID 的直连）未实现，需要一次「hello」交换来学习对端身份。

---

## 一、断点定位（按用户实际操作顺序）

### B1. 节点发现不存在 —— 阻断级

`/msg` 的第一步就是 `ChatService.SendPrivateMessageAsync` 调 `_dht.FindNodeAsync(recipientId)`
（`src/P2PChat.Chat/Services/ChatService.cs:50`），失败即抛「目标节点未找到」。

而这条路径必然失败，三处原因叠加：

| 环节 | 事实 | 位置 |
|---|---|---|
| 从不宣告自己 | `StoreAsync` 空实现直接 `return`；`announce_peer` 只回成功响应不落库 | `MainlineDhtService.cs:94-98`、`:314-316` |
| 查值恒空 | `FindValueAsync` 恒返回 `null` | `:101-105` |
| 查找要求精确相等 | 迭代 `find_node` 后用 `c.NodeId.Equals(targetId)` 过滤；公网返回的是靠近该 ID 的 **BT 节点**，永远不含我们 | `:87-91` |

`FindNodeAsync` 是私聊、群聊扇出（`GroupChatService.cs:67`、`:123`）、文件传输的共同入口，
所以 B1 一条即导致**全部通信功能不可用**。

### B2. `SenderId` 是全世界同一串常量 —— 阻断级，且被测试结构性掩盖

`ChatService.cs:76`、`KeyExchangeHandler.cs:60`、`GroupChatService.cs:42/:45/:106` 全部写：

```csharp
SenderId = identity.PublicKey.Take(20).ToArray()   // 注释：「简化: 用公钥前20字节作ID」
```

公钥是 `ExportSubjectPublicKeyInfo()` 产出的 **P-256 SPKI DER**（91 字节），
其前 27 字节是**固定算法头**，与密钥内容无关。实测两个节点落盘的公钥：

```
nodeA: MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE UQ8VvJm...
nodeB: MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE l5Xxzmn...
```

前 36 个 base64 字符（=27 字节）逐字节相同。因此 `Take(20)` 取到的 20 字节
**对每个节点都完全一致**。而真正的节点身份是 `NodeId.FromPublicKey` = **SHA-1(公钥)**
（`NodeId.cs:27-31`），两者毫无关系。

**后果**（按严重度）：

1. **会话密钥槽位冲突。** `KeyExchangeHandler.cs:52` 用 `new NodeId(message.SenderId)` 作键，
   即所有对端共用同一个键。两个节点时侥幸可用；**第三个节点一接入就覆盖前一个的会话密钥**，
   导致先前对端的消息解密失败。
2. **联系人别名永远解析不到。** TUI 用 `contactService.FindByNodeId(chatEvent.SenderId)`
   查别名（`P2PChatTui.cs:349`），而联系人是用真实 NodeId 存的 → 恒返回 `null`，
   发送者显示为一串十六进制。
3. **群创建者身份全同**，`GroupInviteMessage.CreatorId` 失去意义。

**与文档契约冲突：** `Agent.md:595` 明确写 `SenderId (NodeId raw bytes)`，
实现写的是「公钥 DER 前 20 字节」。这是实现违反已声明的线路契约。

### B3. 收到的私聊消息永远不显示 —— 阻断级

`ConversationId` 的语义是**单向**的：`ChatService.cs:77` 设为 `recipientId.ToHexString()`
（= 收件人的 NodeId）。

于是接收端收到的消息 `ConversationId` = **自己的 NodeId**。
而 TUI 的会话桶用对端 ID 作键：`_currentConversationId = contact.NodeId.ToHexString()`
（`P2PChatTui.cs:134`、`:198`），取桶时也只查这个键（`CurrentMessages()`，`:412-419`）。

**净效果：消息到达 → 解密成功 → 投递进「自己 NodeId」这个桶 → 该桶永远无法被选中 → 永不显示。**
发送端显示正常（它用的正是收件人 ID），所以现象是「我发了，对方说没收到」。

### B4. 端点语义耦合 + NAT 完全没有处理 —— 阻断级（跨网络时）

- **端口耦合：** `NodeInfo.EndPoint` 由 `CreateRemoteNode` 从 **UDP 报文源端点**填充
  （`MainlineDhtService.cs:394-411`），却被 `MessageRouter.GetOrCreateConnectionAsync`
  用来**建立 TCP 连接**（`MessageRouter.cs:112`）。所以 `UdpPort` 必须等于 `TcpPort`，
  否则 TCP 打到对方的 UDP 端口被拒——一个隐性配置地雷。
- **`ExternalEndPoint` 是死字段：** `NodeInfo.cs:17` 声明了它并注释「用于NAT穿透」，
  但**全仓库无任何写入或读取**（已全量检索确认）。即 NAT 穿透从未实现。
- **对外广播的是内网地址：** `Program.cs:115` 的 `localNode.EndPoint` 用
  `GetLocalIPAddress()`，它经 `socket.Connect("8.8.8.8", ...)` 取到的是**本机内网 IP**。
  该端点会被放进路由表并通过 `find_node` 应答广播出去（`MainlineDhtService.cs:288-294`），
  对端拿到 `192.168.x.x` 后无法连入。

### B5. 群聊消息明文上线 —— 安全级

- 发送端：`GroupChatService.cs:112` `Content = text`（**明文**，未加密）。
- 接收端：`GroupMessageHandler.cs:50` `Content = message.Content`（**原样当明文**），
  群密钥只用于 `GetGroupKey(...) == null` 的存在性检查（`:40-46`）。

即：群密钥的分发是加密的（`EncryptGroupKeyForMember`，做得正确），
**但群消息本身全程明文**。README 宣称的端到端加密对群聊不成立。
另外群消息无签名，知道 GroupId 的人可任意注入。

### B6. 群组元数据不持久化 —— 功能级

`GroupChatService._groups` 是纯内存 `ConcurrentDictionary`，类内无任何文件 I/O。
`group_keys.json` 存了密钥，但重启后 `_groups` 为空 →
`/group list` 空、`/group send` 抛「群组不存在」（`:103`）。

### B7. 测试与真实链路脱节 —— 工程级，是 B1–B3 长期存在的根因

| 测试脚手架的做法 | 后果 |
|---|---|
| `NodeHarness.Discover(peer)` = `Dht.Register(peer.LocalNode)`（`NodeHarness.cs:111`）**直接注入对端信息** | 完全绕过真实发现 → **B1 永远不会被测出** |
| `NodeHarness.SenderId => Identity.PublicKey.Take(20)`（`:50`，注释「与 ChatService 一致」）**复刻了同一个 bug** | 测试与实现同错 → **B2 永远不会被测出** |
| 断言「双方会话密钥相等」而非「密钥存在正确的身份键下」 | 身份标识错误不可见 |
| 没有任何测试覆盖 TUI 的会话归桶 | **B3 永远不会被测出** |

补充：`KnownDefectsTests.cs` 的 6 个缺陷**现已全部修复**（测试从 skip 转为 pass 即为证据），
但文件里的 skip 文案仍描述旧代码——引用的
`ChatService.cs:116 SetSessionKey(recipient.NodeId, ephemeralKey.PrivateKey)` 和
`PrivateMessageHandler.cs:48-50`「Content 直接是明文」在当前源码中**已不存在**。
该文件已成为误导性文档。

---

## 二、修复方案

### 阶段 0 —— 立刻可测（目标：两台设备能连上并看到消息）

不依赖 DHT，先把用户从「完全不可用」拉出来。这一阶段同时修掉 B2、B3。

| 编号 | 动作 | 涉及文件 |
|---|---|---|
| 0.1 | `Contact` 增加可选 `EndPoint`；`/add <nodeId> <ip:port> [别名]`；新增 `/connect <ip:port>` | `Contact.cs`、`ContactService.cs`、`P2PChatTui.cs` |
| 0.2 | 端点解析优先级：**显式端点 > DHT `get_peers` > 路由表精确匹配** | `ChatService.cs`、`ContactService.cs` |
| 0.3 | **修 `SenderId`**：统一改为 `NodeId.FromPublicKey(identity.PublicKey).ToByteArray()`，共 5 处 | `ChatService.cs:76`、`:124`、`KeyExchangeHandler.cs:60`、`GroupChatService.cs:42/:45/:106` |
| 0.4 | 会话密钥键统一为**对端真实 NodeId**（随 0.3 自然成立，需回归确认） | `KeyExchangeHandler.cs:44/:52` |
| 0.5 | **修会话归桶**：`ConversationId` 改为方向无关的对端标识（私聊统一用「对端 NodeId」）；TUI 收消息时按 `SenderId` 归桶，不按 `ConversationId` | `ChatService.cs:77`、`P2PChatTui.cs:342-357`、`PrivateMessageHandler.cs:78` |

**验收：** 两台同网段设备，各自 `/add <对端ID> <对端IP:TCP端口>` → `/msg` 双向都能看到明文。
**注：** 0.3/0.5 改动线路语义，两台设备必须**同时**升级（无向后兼容必要，当前无可用版本）。

### 阶段 1 —— 让「发现」真的工作（恢复 README 宣称的能力）

| 编号 | 动作 |
|---|---|
| 1.1 | 实现 `announce_peer`：引导完成后 + 随既有 15 分钟刷新循环，以 `info_hash = 本节点 NodeId`（20 字节，正好等于 info_hash 长度）向最近 K 个节点先 `get_peers` 取 token 再宣告；`port` 取 **TCP 监听端口** |
| 1.2 | 新增 compact peer（`values`）解析——`Bencode` 目前只有 `ParseCompactNodes`/`EncodeCompactNode`，**没有** peers 解析 |
| 1.3 | `FindNodeAsync` 改为先 `get_peers(info_hash=targetId)` 命中 `values` 即返回，未命中再退路由表精确匹配 |
| 1.4 | **解耦端口语义**：`NodeInfo` 区分 `DhtEndPoint` 与 `TcpEndPoint`；`find_node` 应答携带 TCP 端口 → 消除 `UdpPort == TcpPort` 前提 |
| 1.5 | 移除 `BootstrapAsync` 首个成功即 `break` 的行为（`:73`），改为「任一成功即可，但其余仍尝试且失败不阻塞」 |

**验收：** 两台设备**不交换任何 IP**、仅靠公共引导节点启动，`/add <对方节点ID>` 后能互发。
**风险：** 宣告会把本机 IP 与端口公开到公共 DHT，需在 README 明说隐私代价。

### 阶段 2 —— 穿透 NAT（跨互联网必需；同局域网可延后）

| 编号 | 动作 |
|---|---|
| 2.1 | UPnP IGD / NAT-PMP 申请端口映射，**TCP 与 UDP 用同一端口号** |
| 2.2 | **真正填充 `ExternalEndPoint`**：以 DHT 应答中观测到的公网源地址回填（接收方记录 announce 报文源地址，这是 BitTorrent 在 NAT 后仍可被找到的机制） |
| 2.3 | 映射失败时降级为「仅公网可达一方可被连入」，并在 TUI 明示，不要静默失败 |
| 2.4 | （可选，成本高）中继节点或打洞 |

### 阶段 3 —— 安全与数据完整性

| 编号 | 动作 |
|---|---|
| 3.1 | **群消息加密**：用群密钥 AES-256-GCM，与私聊共用同一线路契约；`GroupMessageHandler` 解密 |
| 3.2 | **消息签名**：用长期 ECDSA 私钥对 `SenderId + 内容` 签名，接收端验签。当前 `SenderId` 完全可伪造，`IEncryptionService` 已有 `Sign`/`Verify` 但全仓库无调用点 |
| 3.3 | 群组元数据持久化（`groups.json`：GroupId/名称/成员/创建者） |
| 3.4 | 文件传输发送端读循环改用 `state.ChunkSize`（当前硬编码 `DefaultChunkSize`，`FileTransferService.cs:243`） |

### 阶段 4 —— 测试策略修复（建议与阶段 0 并行，防复发）

| 编号 | 动作 |
|---|---|
| 4.1 | `NodeHarness.SenderId` 改为真实 `NodeId.FromPublicKey` 派生，删掉「与 ChatService 一致」这条错误约定 |
| 4.2 | 新增断言：**两个 harness 的 SenderId 必须不同**（一条即可锁死 B2 复发） |
| 4.3 | 新增断言：接收端 `ChatMessageEvent.ConversationId` 必须能被 UI 用 `SenderId` 定位到同一会话桶（锁死 B3） |
| 4.4 | 新增真实发现测试：真实 `MainlineDhtService` + 本机引导节点，断言 `find_node → announce_peer → get_peers` 闭环 |
| 4.5 | 清理 `KnownDefectsTests.cs` 的失效 skip 文案，或整体转为「回归守卫」并去掉误导性的旧行号引用 |
| 4.6 | `scripts/e2e-verify.ps1` 增加一条端到端断言：两实例互发一条消息，接收端日志/输出出现明文 |

---

## 三、优先级建议

1. **阶段 0（0.3 + 0.5 最重要）+ 4.1~4.3** —— 把「不可用」变「可用」的最短路径，不引入新架构。
2. **阶段 1** —— 恢复 README 宣称能力的正解，也是「无中心服务器」成立的前提。
3. **阶段 2** —— 决定「跨互联网」是否可用。若两台设备在同一局域网，可延后。
4. **阶段 3** —— 安全与完整性。当前群聊明文上线属实质安全缺陷，建议不晚于阶段 1 之后处理。

## 四、需要确认的一个前提

**两台设备是同一局域网，还是分别在各自的家宽 / 移动网络？**

- 同网段 → 阶段 0 + 1 即可用，阶段 2 可延后。
- 跨网络 → 阶段 2 是硬需求；还需确认你能否在路由器上做端口映射（决定 2.1 用 UPnP 还是手工映射）。

这个答案会显著改变阶段 2 的必要性与工作量，因此建议先确认再开工。
