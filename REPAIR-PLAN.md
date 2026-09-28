# P2PChat 跨设备不可用 — 分析与修复方案

> 工作文档（**不是** Agent Note）。定稿的决策请按 `notes/README.md` 转为 `proposed/` Agent Note。
> 分析日期：2026-09-20。依据：源码逐段通读 + 两台设备实测 + 全量测试运行结果。

## 结论摘要

**测试全绿（121 通过 / 0 失败 / 0 跳过），但两台设备不可用。** 这不是巧合，而是本方案要解决的第一层问题：
现有测试**在结构上无法发现**导致不可用的缺陷（见 B7）。

阻断级缺陷 4 个，其中 **B1 单独一条就足以让全部聊天功能不可用**。

---

## 实施进度

> 🕐 **进度状态：阶段 0–4 于 2026-09-28 本轮收口（e2e 实测 `PASS=41 / FAIL=0`）。**
> 本轮同时修掉了「收到的消息从不显示」（B3 同类复发）与入站重放防护。
> 下方各阶段的 ✅ 反映的是**本轮收口时的实测状态**；若后续再有改动，**引用其门禁数字前请重新跑一遍**。

> 🚧 **一个前提问题至今没有答案，直接影响上表的阶段 2 结论。**
>
> 本文 §四「需要确认的一个前提」问的是：**两台目标设备是同一局域网，还是分别在各自的家宽 / 移动网络？**
> 该问题自 2026-09-20 提出后一直**未获用户答复**（同一问题在 `HANDOFF.md` §8 遗留 #1 中跟踪，标记为「🔴 阻塞验收」）。
> 它决定阶段 2 是硬需求还是可以降级的锦上添花。
>
> 阶段 2 之所以能在答案未知的情况下标 ✅，是因为实现走了**尽力而为**路线：
> UPnP 成功则映射，失败则在 TUI 自检块**显式**打印「无 UPnP —— 仅同网段/公网可达方可主动连入」，
> 并始终保留 `/add <ip:port>` 与 `/connect <ip:port>` 两条手工接入通道。
> **本文档不替这个问题下结论**，也不据「已实现」反推「用户场景已覆盖」——验收前请先向用户确认。

| 阶段 | 状态 | 说明 |
|---|---|---|
| **阶段 0** 立刻可测 | ✅ **已完成**（2026-09-20） | 见下方「阶段 0 实施记录」 |
| **阶段 1** 让发现真的工作 | ✅ **已完成**（2026-09-20） | `announce_peer`/`get_peers` 真实闭环 + compact peer 解析 + TCP/DHT 端口语义解耦 + 引导不再「首胜即停」。见「阶段 1 实施记录」。决策：[`notes/implemented/bug-fix/2026-09-20-public-dht-peer-discovery-gap.zh.md`](notes/implemented/bug-fix/2026-09-20-public-dht-peer-discovery-gap.zh.md) |
| **阶段 2** 穿透 NAT | ✅ **已完成（2.4 除外）**（2026-09-21） | UPnP IGD（裸 SSDP + SOAP，刻意不用 COM 以保 AOT）TCP/UDP 同端口映射、退出时释放租约、TUI 显式降级提示。**2.4 中继 / 打洞未做**（原标注为可选）。跨网络建连链**已闭合** —— `announce_peer` 用「UDP 包源 IP + 宣告的 TCP 端口」直接拼出公网可达的 `NodeInfo.EndPoint`，见「阶段 2.2 / 2.2b」。决策：[`notes/implemented/bug-fix/2026-09-21-nat-traversal.zh.md`](notes/implemented/bug-fix/2026-09-21-nat-traversal.zh.md) |
| **阶段 3** 安全与完整性 | ✅ **已完成**（2026-09-21） | 3.1 群消息 AES-256-GCM 加密 / 3.2 长期 ECDSA 消息签名（含四道入口关卡）/ 3.3 群组元数据持久化 / 3.4 文件分块改用 `state.ChunkSize`。见「阶段 3 实施记录」。决策：[群消息加密](notes/implemented/bug-fix/2026-09-21-group-message-encryption.zh.md)、[消息签名](notes/implemented/bug-fix/2026-09-21-message-signing.zh.md)、[群元数据持久化](notes/implemented/bug-fix/2026-09-21-group-metadata-persistence.zh.md)、[文件分块大小](notes/implemented/bug-fix/2026-09-21-filetransfer-chunksize.zh.md) |
| **阶段 4.1–4.3** 测试策略（防复发部分） | ✅ **已完成** | `NodeHarness.SenderId` 已改为复用生产代码的 `KeyPair.NodeId`；新增 25 条守卫断言 |
| **阶段 4.4–4.6** 测试策略（真实发现 / e2e） | ✅ **已完成**（2026-09-28 收口） | 4.4 `RealDiscoveryTests` 真实 KRPC 闭环 / 4.5 `KnownDefectsTests` 6 条回归守卫（死 skip 分支已删）/ 4.6 `e2e-verify.ps1` 明文往返断言 A29–A32 + A29a/A29b/A33/A34。**最新 e2e 实测 `PASS=41 / FAIL=0 / SKIP=0`，退出码 0**（起始基线 32/5）。⚠️ **上一轮记的 `PASS=39/FAIL=0` 对「消息显示」是假绿灯**，见下方与 `HANDOFF.md` §8.1.2 |
| 各阶段对应的 Agent Note | ✅ 阶段 0–3 已补 | 阶段 0–3 + `/connect` 共 **10 组**（`implemented/bug-fix` ×8 + `implemented/feature` ×2）；全库 `notes/` 下已无 `Status: proposed` |

阶段 0 的提交为 `bab0635`（含三份 Agent Note 与本文档）；阶段 1–3 与阶段 4 的代码**目前仍在工作区未提交**，
提交号待收口时补（见 [HANDOFF.md](HANDOFF.md)）。

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

> 📌 **以上两条的现状（2026-09-28 追记，上文按当时原样保留）：**
> 第二条 `/connect <ip:port>` 已在 2026-09-21 落地，见
> [`notes/implemented/feature/2026-09-21-blind-connect.zh.md`](notes/implemented/feature/2026-09-21-blind-connect.zh.md)。
> 第一条群邀请公钥问题**仍未解决**——补长期公钥属线路变更，需双方同时升级；
> 跟踪于 `HANDOFF.md` §8 遗留 #2。

### 阶段 1 实施记录

> ⚠️ **关于本节行号 —— 引用前请先读这段。**
> `src/P2PChat.Networking/Dht/MainlineDhtService.cs` 于 2026-09-28 被 task-5（bootstrap-perf）
> 并发修改（并发引导 + 引导期短超时），**本节初稿写入的该文件行号当日起全部失效**，
> 且该 agent 收工前仍会继续漂移。因此本节**已改为引用方法名**（`TryAnnounceAsync`、`FindNodeAsync` …）——
> 方法名不受行号漂移影响，**以此为准**。
> 其余文件（`NodeInfo.cs`、`Bencode.cs`、`UpnpClient.cs`、`Program.cs`、`GroupChatService.cs`、
> `MessageRouter.cs`、`MessageEnvelope.cs`、`FileTransferService.cs`、各测试文件）当轮无并发修改，行号已逐条 grep 核对。
> **若确需 `MainlineDhtService.cs` 的行号，请自行 grep 复核，不要直接引用本文档。**

落地决策：[`notes/implemented/bug-fix/2026-09-20-public-dht-peer-discovery-gap.zh.md`](notes/implemented/bug-fix/2026-09-20-public-dht-peer-discovery-gap.zh.md)

| 编号 | 动作 | 落地位置 | 验证结果 |
|---|---|---|---|
| 1.1 | 实现 `announce_peer` 宣告：引导完成后 + 随既有 15 分钟刷新循环，以 `info_hash = 本节点 NodeId`（20 字节，正好等于 info_hash 长度）向路由表最近 K=8 个节点**先 `get_peers` 取 token 再宣告**；宣告的 `port` 取 **TCP 监听端口**。token 按 per-remote 随机 8 字节、长期有效（不实现失效期） | 发送侧 `MainlineDhtService.TryAnnounceAsync` / `MainlineDhtService.KrpcAnnouncePeerAsync`；触发点 `BootstrapAsync` 引导后立即宣告、`RefreshLoopAsync` 周期驱动；接收侧 `HandleQueryAsync` 的 `announce_peer` 分支校验 token 后落 `RecordAnnounce` | `RealDiscoveryTests.AnnounceNow_双向互宣告后_两端都能通过getPeers解析对端`（`RealDiscoveryTests.cs:128`） |
| 1.1b | 宣告的可观测 / 可测入口：`AnnounceNowAsync`（测试不必等 15 分钟周期）、`AnnouncedPeerCount`、`LastAnnounceUtc` | `MainlineDhtService.AnnounceNowAsync`、`IDhtService.AnnouncedPeerCount`（`IDhtService.cs:70`）、`IDhtService.LastAnnounceUtc`（`:75`）；TUI 自检块展示（`P2PChatTui.cs:132`） | `dotnet build` 0 警；TUI 启动自检可见 |
| 1.2 | 新增 compact peer（`p2pc_peers`）解析：**26 字节/条目 = NodeId 20B + IPv4 4B + TCP port 2B**。比 BitTorrent 标准的 6 字节 `values` 多 20 字节 NodeId，好让接收方把 `ip:port` 映射回具体 NodeId；公网引导节点不解析该字段，视为未发现 | `Bencode.ParseCompactPeers26`（`Bencode.cs:187`）、`MainlineDhtService.FlattenByteList`（拼接后调用该解析） | `BencodeTests` 3 条（`BencodeTests.cs:85`、`:114`、`:130`） |
| 1.3 | `FindNodeAsync` 改为三级优先：**静态对端 → `get_peers(info_hash=targetId)` 命中 `p2pc_peers` 即返回 → 路由表精确匹配兜底** | `MainlineDhtService.FindNodeAsync` / `MainlineDhtService.ResolveViaGetPeersAsync` | `RealDiscoveryTests` 4 条全绿（`RealDiscoveryTests.cs:78` / `:128` / `:195` / `:235`） |
| 1.4 | **解耦端口语义**：`NodeInfo` 区分 `EndPoint`（**TCP**，供 `MessageRouter` 建连）与 `DhtEndPoint`（**KRPC 报文源地址**），消除 `UdpPort == TcpPort` 这一隐性配置地雷 | `NodeInfo.DhtEndPoint`（`NodeInfo.cs:46`）、`MainlineDhtService.CreateRemoteNode`、`MainlineDhtService.ResolveViaGetPeersAsync` 命中处回填 | build / test 全绿 |
| 1.5 | 移除 `BootstrapAsync`「首个成功即 `break`」，改为**遍历全部引导节点**、任一成功即可进入迭代查询、单个失败不阻塞其余 | `MainlineDhtService.BootstrapAsync`（方法内有注释「1.5 行为：尝试所有引导节点」） | `RealDiscoveryTests.Bootstrap_全部引导节点不可达时_不抛异常`（`:195`）；**代价见下方「阶段 1–3 的已知遗留」** |

### 阶段 2 实施记录

落地决策：[`notes/implemented/bug-fix/2026-09-21-nat-traversal.zh.md`](notes/implemented/bug-fix/2026-09-21-nat-traversal.zh.md)

| 编号 | 动作 | 落地位置 | 验证结果 |
|---|---|---|---|
| 2.1 | UPnP IGD 申请端口映射，**TCP 与 UDP 申请同一端口号**；任一协议失败即整体判失败，并回滚已成功的那一半映射；进程退出时 `RemoveAsync` 释放租约。**裸 HTTP/1.1 over SSDP + SOAP，刻意不用 COM** 以保 AOT 兼容 | `IUpnpClient`（`IUpnpClient.cs:39`）、`UpnpClient.TryMapAsync`（`UpnpClient.cs:49`，内含 TCP/UDP 两次 `AddPortMappingAsync` 与半成功回滚）、`UpnpClient.RemoveAsync`（`:102`） | `UpnpClientTests` 6 条（`UpnpClientTests.cs:46`–`:123`），含「无网络 / 端口非法时返回 null 且不抛」 |
| 2.1b | 启动时探测并把结果应用到 DHT 服务；退出时在 `finally` 中撤销映射（仅映射成功时才尝试，否则路由器会因找不到映射而拒响应） | `Program.cs:124`–`:145`（探测 + 失败告警）、`Program.cs:235`（`ApplyMapping`）、`Program.cs:271`–`:283`（`finally` 中 `RemoveAsync`） | build 0 错 0 警 |
| 2.2 | **回填对端的公网入口**：接收方用「**UDP 包源 IP + 宣告的 TCP 端口**」拼出对端在 NAT 后的可达地址（BitTorrent 在 NAT 后仍可被找到的机制），并落进 `peerCache` | `MainlineDhtService` 的 `announce_peer` case：`tcpEp = new IPEndPoint(remoteEp.Address, announcedPort)`，再 `RecordAnnounce(ih, senderId, tcpEp, remoteEp, remoteEp)`。NAT 后主机的出站 UDP 包，源地址在公网节点侧即**公网 IP**；宣告端口取自 `TryAnnounceAsync` 的 `LocalNode.EndPoint.Port`（= `actualTcpPort`），UPnP 为**同号映射**，故该端口即公网端口 | `RealDiscoveryTests.AnnouncePeer_收到后_peerCache的ExternalEndPoint_等于包源地址`（`:235`）。**建连链已闭合**，见 2.2b |
| 2.2b | `NodeInfo.ExternalEndPoint` **是死字段**（零赋值点），但**不影响建连** | 2.2 的回填实际落在 `AnnouncedPeer.ExternalEndPoint`（私有 record struct，经 `ListAnnouncedPeers()` 暴露），**而非** `NodeInfo.ExternalEndPoint`（`NodeInfo.cs:49`）。而 `MessageRouter` 只读 `NodeInfo.EndPoint` 是**正确的** —— 该字段经 2.2 已含公网 IP 与公网端口 | 2026-09-28 复核确认。**性质是接口冗余 / 文档误导，不是功能缺陷**；建议删除该死字段或补齐语义，**而非**补连连逻辑。这同时印证**阶段 1.4 端口语义解耦实现正确**（`EndPoint`(TCP) 与 `DhtEndPoint`(UDP 包源) 确为两个语义） |
| 2.3 | 映射失败时**降级并显式告知，不静默失败**：本机侧记录三态 `NatMappingState` + `LocalExternalEndPoint`；TUI 自检块直接打印 UPnP 状态与降级文案 | `MainlineDhtService.ApplyMapping` / `.NatMappingState` / `.LocalExternalEndPoint`；`P2PChatTui.cs:133`–`:142`（`Mapped` / `Unavailable` / `NotAttempted` 三态文案） | `Program.cs:144` 无映射时打 Warning 并提示 `/add` `/connect` 兜底 |
| 2.4 | 中继节点 / UDP 打洞 | **未做** | REPAIR-PLAN 原标注为「可选、成本高」；见下方「阶段 1–3 的已知遗留」 |

### 阶段 3 实施记录

| 编号 | 动作 | 落地位置 | 验证结果 |
|---|---|---|---|
| 3.1 | **群消息加密**：出口用群密钥做 AES-256-GCM，`Content` 存 `Base64([12B nonce][ct][16B tag])`；接收端解密还原，**解密失败即丢弃 + 告警、不上报** `ChatMessageEvent`（与私聊处理器对称） | 发送 `GroupChatService.SendGroupMessageAsync`（`GroupChatService.cs:154` 加密、`:160` Base64 落 `Content`）；接收 `GroupMessageHandler.HandleAsync`（`GroupMessageHandler.cs:51`–`:65`） | `GroupChatIntegrationTests` 4 条（`GroupChatIntegrationTests.cs:412` / `:435` / `:464` / `:520`），含「发送端 `TextMessage.Content` 是 AES-GCM 密文 Base64 且不含明文片段」 |
| 3.2 | **消息签名**：用长期 ECDSA 私钥对整条信封签名。线路格式升级为「固定头 + 4B 大端公钥长度 + N + 4B 大端签名长度 + N + Payload」。出站 `SendViaConnectionAsync` 强制以真实 `NodeId` 签名；入站 `RouteIncomingAsync` 设四道关卡（签名存在 / 公钥存在 / `NodeId.FromPublicKey(公钥) == SenderId` / ECDSA 验签），任一失败即丢弃并告警 | `MessageEnvelope.SenderPublicKey`（`MessageEnvelope.cs:38`）与 `.Signature`（`:46`）；`MessageRouter.SignEnvelope`（`MessageRouter.cs:198`）、`VerifyEnvelope`（`:231`）、`VerifyEnvelopeCore`（`:237`）、`SerializeEnvelope`（`:310`）、`DeserializeEnvelope`（`:337`）、出站 `SendViaConnectionAsync`（`:106`）、入站 `RouteIncomingAsync`（`:59`） | `MessageSigningTests` 8 条（`MessageSigningTests.cs:40`–`:299`），含「篡改 SenderId / 篡改 Payload 均验签失败且不产生 `ChatMessageEvent`」 |
| 3.3 | **群组元数据持久化**：`groups.json`（`~/.p2pc/`）存 GroupId / 名称 / 成员 / 创建者 / 群密钥；启动时加载，`CreateGroup`、`HandleInvite`、dissolve 三处落盘 | `IGroupMetadataStore`（`IGroupMetadataStore.cs:16`）、`FileBackedGroupMetadataStore`（`FileBackedGroupMetadataStore.cs:23`）、DI 注册（`ChatServiceCollectionExtensions.cs:24`、`Program.cs:198`） | `GroupChatIntegrationTests` 5 条（`:301` / `:331` / `:358` / `:388` / `:557`），含「进程重启后新 `GroupChatService` 必须从磁盘恢复群组」 |
| 3.4 | 文件传输发送端读循环改用 `state.ChunkSize`，不再硬编码 `DefaultChunkSize` | `FileTransferService.SendFileChunksAsync`：读缓冲按 `state.ChunkSize` 分配（`FileTransferService.cs:262`）、偏移计算同步（`:211`） | `FileTransferIntegrityTests` 2 条（`FileTransferIntegrityTests.cs:333` / `:400`）+ `KnownDefectsTests.回归守卫4`（`KnownDefectsTests.cs:223`） |

### 阶段 4.4–4.6 实施记录（测试策略收尾）

| 编号 | 动作 | 落地位置 | 验证结果 |
|---|---|---|---|
| 4.4 | 真实发现闭环测试：拉起两个**真实** `MainlineDhtService`（绑定不同 UDP 端口），跑通 `find_node → announce_peer → get_peers` 三条真实 KRPC 报文路径。**不预置任何发现路径**——这正是它与既有 `FakeDhtService` 集成测试的区别，也是 B1 能被测出的前提 | `tests/P2PChat.Networking.Tests/RealDiscoveryTests.cs`，4 条：`:78`（单向依赖 B 可发现 A）、`:128`（双向互宣告后两端互解析）、`:195`（全部引导节点不可达时不抛）、`:235`（ExternalEndPoint 等于包源地址） | 4 条全绿 |
| 4.5 | 清理 `KnownDefectsTests.cs`：删除 6 个 `SkipException` 动态跳过分支，以及其中引用**已不存在代码行号**的失效文案；6 条测试重写为「回归守卫」真实断言 | `tests/P2PChat.Integration.Tests/KnownDefectsTests.cs`，6 条：`:51` / `:76` / `:117` / `:223` / `:284` / `:367` | 6 条全 pass，**0 skip**（改回旧实现任一条即变红） |
| 4.6 | e2e 明文往返断言：Phase 2 拉起 nodeA2 / nodeB2 两实例互发一条带随机明文的消息，断言接收端日志出现该明文 | `scripts/e2e-verify.ps1` A29 / A29a / A29b / A30 / A31 / A32 —— A29a 守「预置的 `contacts.json` 启动后仍在」、A29b 守「是单层数组 + `StoredContact` 对象」、A32 守「nodeA2 的**聊天事件行**若含明文**必须**标为『我』」 | ✅ **实测通过** —— e2e `PASS=39 / FAIL=0 / SKIP=0`，退出码 0。**A32 是修正了一条错误断言（原前提把正确的本地回显当成 bug），语义变严格而非放宽**，详见 `HANDOFF.md` §4.4 |

### 验证结果（2026-09-28 本轮重放防护轮收口，全部为实测值）

- `dotnet build P2PChat.slnx -t:Rebuild`：**0 个错误 0 个警告**。
- `dotnet test P2PChat.slnx`：**340 通过 / 0 失败 / 0 跳过**
  （演进轨迹：阶段 0 前 121 → 阶段 0 后 146 → 中途 179 → 上一轮 297 → **本轮 340**）。
  分项目：`Crypto.Tests` 8 / `Core.Tests` 52 / `Chat.Tests` **110**（原 0）/ `Integration.Tests` **147** / `Networking.Tests` 23。
- **AOT 发布**：`dotnet publish -c Release` 成功，**我方代码 0 条** IL/AOT 警告；
- 阶段 0 之后新增的守卫测试：`RealDiscoveryTests` 4 条、`UpnpClientTests` 6 条、`MessageSigningTests` 8 条、
  `BencodeTests` compact-peer 3 条、群消息加密 4 条、群元数据持久化 5 条、文件 ChunkSize 2 条、
  `KnownDefectsTests` 回归守卫 6 条、`Chat.Tests` 80 条、`EnvelopeCodec` 边界测试 15 条、
  `ContactService.LoadContacts` 回归 3 条、plain 模式分派测试若干。
- **e2e（2026-09-28，Lead 亲自跑）**：`PASS=41 / FAIL=0 / SKIP=0`，**退出码 0**（起始基线 32/5）。
  过程中发现并修掉 e2e 脚本**连续三个静默失败**（预置 `contacts.json` 被删 / `ConvertTo-Json` 单元素塌缩 /
  `-AsArray` 造成嵌套数组），并新增 A29a / A29b 两条前置断言让这类静默失败**当场变红**。
  详见 `HANDOFF.md` §4.3–§4.5。
  ⚠️ **上一轮记的 `PASS=39 / FAIL=0` 对「消息显示」而言是假绿灯** ——
  A30/A31 可被一条含明文的 Serilog 日志行满足，而当时「消息从不显示」缺陷仍在。
  本轮修复唯一事件源后 `PASS=41` 才同时覆盖显示通路。详见 `HANDOFF.md` §8.1.2。
- **AOT 发布（2026-09-28，Lead 执行）**：`dotnet publish -c Release` 成功，**我方代码 0 条** IL/AOT 警告；
  产物另有 **4 条第三方程序集警告**（`MessagePack.dll` 的 IL3053 + IL2104、`Serilog.dll` 的 IL2104）。
  ⚠️ **不要把这一格简写成「0」** —— 它是「第三方自身代码路径不 trim 友好、我们不走那些路径」这一
  **人工判断**的结果，不是工具自动判定的门禁达成。详见 `HANDOFF.md` §3.1。

> 📌 上述为 **2026-09-28 本轮收口实测值**。再次改动代码后**引用前请重新跑一遍**。
> 权威性来自新鲜度，不来自排版工整。

### 已知遗留（2026-09-28，本轮重放防护轮已收口）

- 🔴 **REPAIR-PLAN §四的前提问题仍未获用户答复**（「同局域网 vs 跨网络」）——
  见本文顶部提示。**已向用户询问 3 次（2026-09-28）仍未答复。** 这是唯一**阻塞验收**的开放项。见 `HANDOFF.md` §8 #1。
  ⚠️ **代码完备 ≠ 覆盖了用户的实际场景** —— 不要因为「实现已完成」就把它关掉。
- 🔴 **`/connect` 的 hello 响应全程不验签 —— 未经认证的对端引入**（2026-09-28 浮现）。
  任何抢在真节点前应答的主机都会被无条件信任并登记为静态对端。
  修法与验收标准固化在共享任务板 `task-17`，本文不重复以免与任务描述漂移。见 `HANDOFF.md` §8 #3。
- ⚪ **`P2PChatTui` 仍无行为级测试**：`Chat.Tests` 已从 0 填到 110 条，但 TUI 主循环仍无行为级覆盖。
- ⚪ **本轮全部改动尚未提交**。
- ⚪ **阶段 2.4（中继 / 打洞）未做** —— 原标注为可选。
- ⚪ `NodeInfo.ExternalEndPoint` 是死字段（零赋值点），但**不影响建连** ——
  `announce_peer` 分支已用「UDP 包源 IP + 宣告的 TCP 端口」拼出公网可达的 `NodeInfo.EndPoint`，
  链路是闭合的；`MessageRouter` 只读 `NodeInfo.EndPoint` 是**正确的**。

### ✅ 本轮已关闭（残余风险见 `HANDOFF.md` §8.1.1，部署前务必先读）

- **入站重放防护**：`MessageReplayGuard`（`IReplayGuard` 唯一实现）接入 `MessageRouter`
  （验签之后、投递之前），重放键为**已被签名覆盖的 `MessageId`**，配置项
  `P2PChat:ReplayMaxAgeSeconds`（默认 3600）。34 条新增测试全绿，含 14 处变异验证。
  **⚠️ 残余风险五条**（环形淘汰窗口 / 对端桶 LRU 淘汰 / `<=0` 关掉整个时间窗 /
  去重状态纯内存重启即失 / 挡不住流量重定向与 DoS）见 `HANDOFF.md` §8.1.1。
- 🔴 **「收到的消息从不显示」—— B3 同类缺陷复发，本轮修复并由 e2e 验证**：
  `PrivateMessageHandler` / `GroupMessageHandler` 各自持有**私有** `Channel<ChatMessageEvent>`，
  TUI 读的是 `ChatService._messageChannel`，**三者互不相通**，且 `PublishMessageAsync` 在 `src/**`
  **零调用者** ⇒ 用户看得到自己发的，**永远看不到任何人发来的**（私聊 + 群聊同时中招）。
  修法：新增 `IChatEventPublisher` 作为**唯一事件源**，`src` 内该 Channel 由 3 处降为 1 处。
  **潜伏机制比缺陷本身更值得记 —— 见 `HANDOFF.md` §8.1.2 与 §7.3 的不署名教训**
  （(a) 测试侧：断言绿了但**对象**错了；(b) 文档侧：证据绿了但**范围**错了 —— 同一个陷阱的两个面）。
  **性质是接口冗余 / 文档误导，不是功能缺陷** —— 建议删除该死字段或补齐其语义，
  而**不是**去补连连逻辑。详见「阶段 2.2b」与 `HANDOFF.md` §8 #6。

### 阶段 0 / 1 / 2 / 3 的历史遗留（收口时状态）

- **阶段 1.5 的引导耗时**（本机实测单节点约 30s）：✅ **已解决** —— 并发引导 + 引导期短超时，
  **40.02s → 3.01s（13.3×）**，且**未退回「首个成功即 break」**，D7 修复（引导成功节点入路由表）保留。
- **阶段 2 的前提仍未确认**：见本文顶部提示与 §四。UPnP 成功不等于用户的两台设备真的在同一可达网络里。
- **群邀请公钥缺失**（源自阶段 0 遗留，第 1 条）：✅ **已解决** ——
  `KeyExchangeMessage` 回送长期公钥（线路变更，双方须同时升级），且 `CreateGroupAsync` 的
  静默 `catch {}` 已改为带告警日志。
- **阶段 3.2 消息签名的重放缺口**：✅ **已解决**（本轮），残余风险见上节与 `HANDOFF.md` §8.1.1。
- **阶段 3 收消息投递断链**（B3 同类复发）：✅ **已解决**（本轮）——
  `IChatEventPublisher` 成为唯一事件源。
  ⚠️ **本条同时构成对 B7 的一次自我修正** —— B7 当初判断「测试与真实链路脱节」是对的，
  但当时以为根因是「测试读错了对象还全绿」的**脚手架捷径**；
  实际上还存在更隐蔽的一层：**测试与 e2e 都绿，而产品链路是断的** ——
  因为两者的断言都落在**错误的对象**上。详见 `HANDOFF.md` §8.1.2。

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

> 🚧 **截至 2026-09-28，这个问题仍未得到用户答复。** 阶段 1 / 2 / 3 的代码虽已全部落地，
> 但**不能据此推断用户的实际两台设备场景已被覆盖**。同一问题在 `HANDOFF.md` §8 遗留 #1 中跟踪
> （标记为「🔴 阻塞验收」），验收前必须先问清楚。

**两台设备是同一局域网，还是分别在各自的家宽 / 移动网络？**

- 同网段 → 阶段 0 + 1 即可用，阶段 2 可延后。
- 跨网络 → 阶段 2 是硬需求；还需确认你能否在路由器上做端口映射（决定 2.1 用 UPnP 还是手工映射）。

这个答案会显著改变阶段 2 的必要性与工作量，因此建议先确认再开工。




