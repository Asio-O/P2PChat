# Agent Note: 公共 DHT 无法发现对端节点

Status: implemented

## Problem

README 把「借助公共 Mainline DHT 引导与发现节点」列为程序的核心特性，但当前**只有「引导」能工作，「发现」不能**：两个节点除非其中一方把另一方配成引导节点，否则永远无法互相找到，`/msg` 必然失败并报 `目标节点未找到`。

链路上有三处缺口，必须同时成立才构成这个现象：

1. **没有任何地方向外宣告本节点的 `NodeId → 端点` 映射。**
   `MainlineDhtService.StoreAsync` 是空实现，直接 `return Task.CompletedTask`；`FindValueAsync` 恒返回 `null`；收到 `announce_peer` 时只回一个成功响应、不落库。全仓库没有任何调用点会为本节点发起 `announce_peer`。

2. **查找要求 `NodeId` 精确相等，而公网 DHT 给不出这个结果。**
   `FindNodeAsync` 先做迭代 `find_node`，再以 `c.NodeId.Equals(targetId)` 过滤。公网节点返回的是**靠近该 ID 的 BitTorrent 节点**，我们的节点从未注册到任何地方，因此精确匹配不可能命中。`ChatService.SendPrivateMessageAsync` 正是走这条路径。

3. **没有手工指定端点的逃生通道。**
   `Contact` 只有 `NodeId` / `Alias` / 状态字段，`/add` 只接受「节点 ID + 别名」，发送时端点**只能**来自 DHT 查找结果。

唯一目前可行的路径是**把对方当引导节点**：双方事先交换 IP 后互相 PING。本期让发现机制在该协议本身（Mainline DHT KRPC）上自给自足，同时保留手工 `ip:port` 兜底。

## Decision

补齐 Mainline DHT 的 `announce_peer` / `get_peers` 双向语义，让节点能通过公共 DHT 自我宣告与解析；同时显式区分 TCP / DHT 端点语义，去除 `UdpPort == TcpPort` 的隐含前提。

- **宣告（`MainlineDhtService.TryAnnounceAsync`）**：以 `info_hash = 本节点 NodeId`（20 字节）向路由表最近 K=8 个节点先 `get_peers` 取 token，再 `announce_peer`。宣告的 `port` 取 **TCP 监听端口**（即 `LocalNode.EndPoint.Port`，由 `Program.cs` 在拿到 `actualTcpPort` 后写入）。
- **解析（`MainlineDhtService.FindNodeAsync` + `ResolveViaGetPeersAsync`）**：先以 `info_hash = targetId` 走 `get_peers`，命中 `p2pc_peers` 即返回该 NodeInfo；未命中再退回路由表精确匹配。
- **非标准字段**：用 `p2pc_peers` 携带 26 字节/条目（NodeId 20B + IPv4 4B + TCP port 2B），比 BitTorrent 标准的 6 字节 `values` 多 20 字节 NodeId，让接收方把 IP:port 映射回具体 NodeId。公网引导节点不解析该字段，视为未发现。
- **Token**：按 KRPC 协议要求，per-remote 随机 8 字节 token（`_issuedTokens` 字典），长期有效，不实现失效期。
- **本地兜底**：`Contact.EndPoint` 保留 `/add <节点ID> <ip:port>` 路径，触发 `IDhtService.RegisterStaticPeer` 让 `FindNodeAsync` 直接命中（这是上一阶段引入的，本阶段不改动）。
- **端点解耦**：`NodeInfo` 新增可选 `DhtEndPoint`（KRPC 报文 UDP 源端点，由 `MainlineDhtService` 写入），保留 `EndPoint` 但语义文档化为「TCP 端点」，`MessageRouter.GetOrCreateConnectionAsync` 继续读 `EndPoint` 建 TCP 连接。`UdpPort != TcpPort` 不再构成前置条件。
- **Bootstrap 行为修正**（`MainlineDhtService.BootstrapAsync`）：移除「首个成功即 `break`」的旧行为，改为尝试所有引导节点，任一成功即进入迭代查询；失败仅记 Debug 不抛。
- **宣告状态对外可观测**：`IDhtService` 新增 `AnnouncedPeerCount` 与 `LastAnnounceUtc`（默认实现为 `0` / `MinValue`），`P2PCHAT_SELFTEST` 自检块新增「宣告状态」行。

宣告 / 解析循环均由 `BootstrapAsync` 同步触发一次（不等 15 分钟首启间隔），并随既有 `RefreshLoopAsync`（15 分钟周期）每轮重新宣告一次。

## Announced endpoint semantics

`NodeInfo.EndPoint` 仍是 TCP 端点（`MessageRouter.GetOrCreateConnectionAsync` 据此建 TCP 连接）。本机由 `Program.cs` 在拿到 `actualTcpPort` 后写入；远端则由宣告方放入 DHT 的 `announce_peer.port` 字段填充。

`NodeInfo.DhtEndPoint`（新增，可选）是 KRPC 报文（UDP）的源地址，由 `MainlineDhtService.CreateRemoteNode` 在收到对端报文时一并填入。无 NAT 时与 `EndPoint` 相等；NAT 后等于对端在公网上的入口地址（NAT 穿透后的外网映射），是为 Phase 2（task-4）预留的字段。

## P2PC protocol extensions

- **`p2pc_peers`**（`get_peers` 应答中携带）：`bencode list<bytes>`，每条目 26 字节 `[20B NodeId][4B IPv4][2B TCP Port BE]`。
- **`p2pc_id`**（`announce_peer` 请求中冗余带上）：与标准 `id` 字段同值；显式声明本节点使用 P2PC 扩展协议。

## Alternatives considered

**维持现状，把「对端即引导节点」写成使用说明。** 否决：把一次性的手工 IP 交换固化成产品语义，违背 README 的「无中心服务器 / 互相发现」承诺。

**只做手工 `ip:port` 联系人的兜底，不实现 DHT 宣告。** 否决作为**唯一**方案：仍要求双方事先交换地址，回答不了「只知道对方节点 ID 时怎么连上」。保留为补充路径。

**自建一个存放 `NodeId → 端点` 的会合（rendezvous）节点。** 否决：与「去中心化、无中心服务器」的前提直接冲突。

**把端点编码进节点 ID 或从公钥派生。** 否决：`NodeId` 只有 20 字节，装不下地址；且节点 ID 是长期身份，不能随网络位置变化。

**要求 `UdpPort` 必须等于 `TcpPort`，并写进文档。** 否决：把一个实现耦合升格为对用户的约束。正确做法是把 TCP 端口显式放到线上（见 `## Announced endpoint semantics`）。

**沿用 `find_node` 迭代、仅放宽为「取最近的若干节点」而不做精确匹配。** 否决：最近邻是 BitTorrent 节点集合，与我们的对端无关；放宽匹配只会返回错误的地址。

**用 BitTorrent 标准 6 字节 `values`（仅 IP+port）做对端身份。** 否决：标准 `values` 不携带 NodeId，无法把 IP:port 映射回「我正在找的 NodeId」。故扩展为 26 字节 `p2pc_peers`（NodeId + IP + port）。

## Consequences

**赢得：** `announce_peer` / `get_peers` 闭环覆盖 `find_node → announce_peer → get_peers` 全路径；宣告状态由 `IDhtService` 公开，`P2PCHAT_SELFTEST` 可断言；端点语义解耦，TCP / UDP 端口不再隐含相等；bootstrap 失败不再让「首节点成功即终止」截断后续引导。

**付出：**
- 公共 DHT 是尽力而为存储，节点可能限流、丢弃 `announce_peer`，或在重启后丢失记录；宣告与解析都必须把「查不到」当作正常结果处理，并配合周期重宣告。
- 宣告会把本节点的 IP 与端口公开到公共 DHT 上 —— 这是从「不宣告」走向「可被发现」必然付出的代价，需在 README 中明说。
- NAT 后的可连入性仍未解决 —— 宣告拿到的是公网映射地址，但若 NAT 未转发该端口，对端仍连不上；本期不含 UPnP / NAT-PMP 或打洞（task-4）。
- AOT 约束：新增的 bencode 解析路径（`ParseCompactPeers26`、`FlattenByteList`）保持无反射；扩展字段不进 MessagePack（KRPC 自带 bencode 编码路径，不受影响）。
- 实现顺序：「宣告」与「解析」必须同批落地，否则会出现单边宣告、对方仍解析不到的半成品状态 —— 本期一次性提交。

## Verification

- `dotnet build P2PChat.slnx` —— 在 `discovery-engineer` 写入范围内（`src/P2PChat.Networking`、`src/P2PChat.Core`、`src/P2PChat.UI`、`src/P2PChat.App`）0 错 0 警。
- `dotnet test P2PChat.slnx` —— 在 `discovery-engineer` 写入范围内的测试项目全绿：
  - `P2PChat.Networking.Tests`：12 通过（6 旧 BencodeTests + 3 新 `ParseCompactPeers26` 边界用例 + 3 新 `RealDiscoveryTests`）。
  - `P2PChat.Core.Tests`：37 通过（不变）。
  - `P2PChat.Crypto.Tests`：8 通过（不变）。
- `RealDiscoveryTests` 三条用例覆盖 `find_node → announce_peer → get_peers` 闭环：
  - `Bootstrap_单向依赖_B可发现A` —— 拉起两个真实 `MainlineDhtService`（端口 40001/40002，回环），B 以 A 为引导；A 的路由表在 B 引导后必须包含 B 的真实 NodeId（B 的 PING 路径已通过该断言验证）。
  - `AnnounceNow_双向互宣告后_两端都能通过getPeers解析对端` —— 同样回环 + 互宣告，断言双向 `FindNodeAsync(对端 NodeId)` 都返回含正确 TCP 端口的 `NodeInfo`。
  - `Bootstrap_全部引导节点不可达时_不抛异常` —— 引导全失败时不抛、`GetAllKnownNodes` 为空、`FindNodeAsync(随机)` 返 null。
- 所有测试**不依赖真实公网 DHT**，全部用 `IPAddress.Loopback` + 固定端口模拟。

## Deferred

- **NAT 转发 / 外部可达性** —— `DhtEndPoint` 字段已就位，但实际回填与 UPnP 申请映射归 Phase 2（task-4）。
- **`/msg` 显式错误提示** —— `ChatService.SendPrivateMessageAsync` 在 `FindNodeAsync` 失败时已抛 `InvalidOperationException("目标节点未找到: ...")`，TUI 会以 `AddSystemMessage($"发送失败: {ex.Message}")` 显示。该路径未在本期调整文案，按既有 `ChatMessageEvent` / `FindNodeAsync` 语义保留。