# Agent Note: 公共 DHT 无法发现对端节点

Status: proposed

## Problem

README 把「借助公共 Mainline DHT 引导与发现节点」列为程序的核心特性，但当前**只有「引导」能工作，「发现」不能**：两个节点除非其中一方把另一方配成引导节点，否则永远无法互相找到，`/msg` 必然失败并报 `目标节点未找到`。

链路上有三处缺口，必须同时成立才构成这个现象：

1. **没有任何地方向外宣告本节点的 `NodeId → 端点` 映射。**
   `MainlineDhtService.StoreAsync` 是空实现，直接 `return Task.CompletedTask`（`src/P2PChat.Networking/Dht/MainlineDhtService.cs:94-98`）；`FindValueAsync` 恒返回 `null`（`:101-105`）；收到 `announce_peer` 时只回一个成功响应、不落库，注释写明「不实现, 但返回成功响应避免被拉黑」（`:314-316`）。全仓库没有任何调用点会为本节点发起 `announce_peer`。

2. **查找要求 `NodeId` 精确相等，而公网 DHT 给不出这个结果。**
   `FindNodeAsync` 先做迭代 `find_node`，再以 `c.NodeId.Equals(targetId)` 过滤（`:87-91`）。公网节点返回的是**靠近该 ID 的 BitTorrent 节点**，我们的节点从未注册到任何地方，因此精确匹配不可能命中。`ChatService.SendPrivateMessageAsync` 正是走这条路径（`src/P2PChat.Chat/Services/ChatService.cs:50`）。

3. **没有手工指定端点的逃生通道。**
   `Contact` 只有 `NodeId` / `Alias` / 状态字段，**没有端点字段**（`src/P2PChat.Core/Models/Contact.cs`）；`/add` 只接受「节点 ID + 别名」（`src/P2PChat.UI/Views/P2PChatTui.cs:251-261`），发送时端点**只能**来自 DHT 查找结果。

唯一目前可行的路径是**把对方当引导节点**：`HandleQueryAsync` 会把发来查询的节点加入路由表（`:268-272`），而 `KrpcPingAsync` 的响应让发起方也拿到对方的 `NodeId` 与端点，双方因此互相可见。此路径经实测确认可用，但它要求双方事先交换 IP —— 恰恰是「无中心服务器」想要避免的事。

实测证据（2026-09-20，同机双实例，跑的是已发布的 Native-AOT 产物）：节点 A 取 `UdpPort = TcpPort = 20081` 并使用公共引导节点，节点 B 取 `UdpPort = TcpPort = 20092` 且 `BootstrapNodes = [127.0.0.1:20081]`。B 的日志为 `引导节点 127.0.0.1:20081 响应成功` → `路由表: 1节点`；退出后两侧 `peers.txt` 互证为 A = `127.0.0.1:20092`、B = `127.0.0.1:20081`。即「互相可见」只在 B 以 A 为引导节点时才成立。同一次实测还确认了两个附带事实：`BootstrapAsync` 在首个引导节点成功后即 `break`（`:73`），因此指定自定义引导节点后公共 DHT 引导完全不会执行；以及 `UdpPort != TcpPort` 时消息无法送达（见 `## Announced endpoint semantics`）。

## Proposal

补齐 Mainline DHT 的 `announce_peer` / `get_peers` 双向语义，让节点能通过公共 DHT 自我宣告与解析。

- **宣告**：引导完成后，以及此后随既有刷新循环（`RefreshLoopAsync`，15 分钟周期，`:428-440`）一起，以 `info_hash = 本节点 NodeId`（20 字节）向路由表中最近的 K 个节点先 `get_peers` 取 token，再 `announce_peer`。宣告的 `port` 取 **TCP 监听端口**。
- **解析**：`FindNodeAsync` 先以 `info_hash = targetId` 走 `get_peers`，命中 `values`（BitTorrent 紧凑节点格式，6 字节 IP + 端口）时直接据此构造 `NodeInfo` 并返回；未命中再退回现有的路由表精确匹配。
- **本地兜底**：给 `Contact` 增加可选 `EndPoint`，`/add <节点ID> <ip:port> [别名]` 支持显式指定。解析优先级为「显式端点 > DHT `get_peers` > 路由表精确匹配」。这条路径同时让局域网联调与双实例测试不再依赖「引导节点即对端」的技巧。

## Announced endpoint semantics

`NodeInfo.EndPoint` 目前**同时承担两个语义**，这是必须一并修掉的耦合：

- 它由 `CreateRemoteNode` 从 **UDP 报文源端点**填充（`:394-411`）；
- 却被 `MessageRouter.GetOrCreateConnectionAsync` 用来**建立 TCP 连接**（`src/P2PChat.Chat/Routing/MessageRouter.cs:112`）。

于是对端的 UDP 端口被当成 TCP 端口使用。当前只能靠「把 `UdpPort` 与 `TcpPort` 配成同一个值」绕开，否则 TCP 会打到对方的 UDP 端口并被拒绝 —— 这是一个配置地雷。

本提案把「对外宣告的 TCP 端点」变成 DHT 上的显式字段（`announce_peer` 的 `port`），`NodeInfo` 相应地区分 DHT 端点与 TCP 端点，`UdpPort == TcpPort` 不再构成隐含前提。

宣告语义需要澄清一点：Mainline DHT 的接收方以 **announce 报文的源地址**记录对端，因此 NAT 后的节点宣告的是其公网映射地址，这也是 BitTorrent 客户端在 NAT 后仍能被找到的机制。但本提案**不解决** NAT 转发本身——宣告的端口仍需在 NAT 上有映射（UPnP / NAT-PMP 或手工转发）才能被连入。

## Alternatives considered

**维持现状，把「对端即引导节点」写成使用说明。** 否决：这是把一次性的手工 IP 交换固化成产品语义。它使跨网络的两个用户无法按节点 ID 互相找到，直接违背 README 宣称的「无中心服务器 / 发现节点」，并让「发现」这一能力实际上不存在。

**只做手工 `ip:port` 联系人的兜底，不实现 DHT 宣告。** 否决作为**唯一**方案：它仍要求双方事先交换地址，回答不了「只知道对方节点 ID 时怎么连上」。保留为补充路径——它便宜、可靠，且在公共 DHT 被网络策略阻断时是唯一可用的手段。

**自建一个存放 `NodeId → 端点` 的会合（rendezvous）节点。** 否决：这等于把中心服务器请回来，与本项目「去中心化、无中心服务器」的前提直接冲突。

**把端点编码进节点 ID 或从公钥派生。** 否决：`NodeId` 只有 20 字节，装不下地址；且节点 ID 是长期身份，不能随网络位置变化。

**要求 `UdpPort` 必须等于 `TcpPort`，并写进文档。** 否决：把一个实现耦合升格为对用户的约束。正确做法是把 TCP 端口显式放到线上（见 `## Announced endpoint semantics`），而不是让用户猜。

**沿用 `find_node` 迭代、仅放宽为「取最近的若干节点」而不做精确匹配。** 否决：最近邻是 BitTorrent 节点集合，与我们的对端无关；放宽匹配只会返回错误的地址。

## Acceptance criteria

- 两个全新身份、**未交换任何 IP**、仅通过公共引导节点启动的实例，在各自 `/add <对方节点ID>` 后能完成私聊往返；判定以接收端出现解密后的明文为准。
- 在 `UdpPort` 与 `TcpPort` 取**不同**值时，上述往返仍然成功，证明端口耦合已解除。
- 公共 DHT 不可达时，`/msg` 以明确错误返回而非静默挂起，日志中可见宣告失败的原因。
- `P2PCHAT_SELFTEST` 输出新增本节点对外宣告状态（已宣告节点数 / 最近一次宣告时间），使无人值守验证可断言。
- 既有的「对端即引导节点」路径保持可用，`scripts/e2e-verify.ps1` 不得回归。

## Risks

- **公共 DHT 是尽力而为的存储。** 节点可能限流、丢弃 `announce_peer`，或在重启后丢失记录；宣告与解析都必须把「查不到」当作正常结果处理，并配合周期重宣告。
- **隐私代价：** 宣告会把本节点的 IP 与端口公开到公共 DHT 上。这是从「不宣告」走向「可被发现」必然付出的代价，需要在 README 中明说。
- **NAT 后的可连入性仍未解决。** 宣告拿到的是公网映射地址，但若 NAT 未转发该端口，对端仍连不上；本提案不含 UPnP / NAT-PMP 或打洞。
- **AOT 约束：** 新增的 bencode 解析路径必须保持无反射；`values` 与 `token` 的解析需要扩展既有的 `Bencode` 手工解码器。
- **实现顺序风险：** 「宣告」与「解析」必须同批落地，否则会出现单边宣告、对方仍解析不到的半成品状态。
- **验收环境依赖：** `## Acceptance criteria` 第一条依赖公共 DHT 可达；在受限网络中需退化为局域网双实例 + 显式端点的等价验收。
