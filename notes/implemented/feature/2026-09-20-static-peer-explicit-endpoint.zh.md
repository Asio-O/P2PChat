# Agent Note: 显式端点直连（静态对端）

Status: implemented

## Problem

程序没有任何机制能让两个节点在**不依赖 DHT 发现**的前提下建立连接。唯一的路径是 `ChatService` → `IDhtService.FindNodeAsync(对端 NodeId)`，而这条路径在当前实现下必然失败（详见 [公共 DHT 无法发现对端节点](../../proposed/bug-fix/2026-09-20-public-dht-peer-discovery-gap.zh.md)）：

- 没有任何地方为节点宣告 `NodeId → 端点` 映射；
- `FindNodeAsync` 要求 NodeId **精确相等**，而公网 DHT 返回的是靠近该 ID 的 BitTorrent 节点；
- `Contact` 模型**没有端点字段**，`/add` 只接受「节点 ID + 别名」，因此连手工指定地址的逃生通道也不存在。

后果是私聊、群聊扇出、文件传输全部不可用，且用户无从自救 —— 这是「两台设备实测基本不可用」的直接原因。

## Decision

引入**显式端点**：用户可把对端的 `ip:port` 直接登记到联系人上，从而完全绕过 DHT 查找。

- `Contact.EndPoint`（`ip:port` 文本，可选）与持久化字段 `StoredContact.EndPoint`。
- `IContactService.AddContactAsync(nodeId, alias, endPoint)`；TUI 命令为
  `/add <节点ID(hex,40位)> [ip:port] [别名]` —— 第二个 token 若能解析成 `ip:port` 就当作端点，否则当作别名。
- 新增 `EndpointText`（`ip:port` ⇄ `IPEndPoint`）：只接受**字面 IP**（含 `[::1]:5000` 形式的 IPv6），
  端口须落在 1..65535，**不做 DNS 解析**。
- `IDhtService.RegisterStaticPeer(NodeInfo)`：登记端点已知的对端。`FindNodeAsync` **静态对端优先**，
  未命中再走迭代查找；静态登记存于独立字典，**不受 `KBucket` 淘汰影响**。
- 启动时 `Program.RegisterStaticPeersFromContacts` 把带端点的持久化联系人重新登记，因此重启后无需再次 `/add`。

优先级放在 `IDhtService.FindNodeAsync` 这一层，是因为它是 `ChatService`、`GroupChatService` 扇出、
`FileTransferService` 三者**共同的唯一入口**，在此解析可让三条业务线同时受益，无需各自引入联系人依赖。

## Alternatives considered

**把端点解析放进 `ChatService`（注入 `IContactService`）。** 否决：爆炸半径更大且层次错位。它要求改 `ChatService` 的构造函数（连带测试脚手架），而且 `GroupChatService` 与 `FileTransferService` 仍在 `FindNodeAsync` 上解析不到显式端点，还得再改两处。

**只把静态端点写进路由表，不加独立登记表。** 否决：`KBucket` 满时会淘汰最旧条目，静态登记的对端可能在运行中悄悄消失。独立字典不受淘汰影响，路由表只作为 `find_node` 应答的补充来源。

**自建 rendezvous 节点存放 `NodeId → 端点`。** 否决：等于把中心服务器请回来，与项目「去中心化、无中心服务器」的前提直接冲突；理由与 [公共 DHT 无法发现对端节点](../../proposed/bug-fix/2026-09-20-public-dht-peer-discovery-gap.zh.md) 中同一替代方案的否决一致。

## Consequences

- 两个节点只要知道对方的节点 ID 与 TCP 监听端口即可直连，**不需要任何发现机制**。
- 端点持久化并随启动自动登记，`/add` 一次即可长期使用。
- 静态对端同时入路由表，`find_node` 应答会把它们报给其他节点，对整体可发现性有轻微正向作用。
- **代价：需要带外交换信息。** 用户必须先通过其他渠道获得对端的节点 ID 与 `ip:port` 两项信息，
  这没有解决「只知道节点 ID 时如何连上」的问题 —— 那由 [公共 DHT 无法发现对端节点](../../proposed/bug-fix/2026-09-20-public-dht-peer-discovery-gap.zh.md) 承接。
- **代价：手工指定的端点不会自动更新。** 对端更换地址或端口后必须重新 `/add`。
- **已知缺口：群邀请对手工添加的联系人仍会失败。** 静态对端的 `PublicKey` 为空，而 `GroupChatService.EncryptGroupKeyForMember` 包装群密钥需要长期公钥；该失败会被 `CreateGroupAsync` 的 `catch {}` **静默吞掉**。修法是在 `KeyExchangeMessage` 中回送长期公钥（线路变更），不在本变更范围内。
- 守卫测试：`IdentityAndEndpointTests.静态对端_登记后无需DHT发现即可被FindNodeAsync命中`、
  `IdentityAndEndpointTests.静态对端_仅凭显式端点即可完成双向加密私聊`（全程不调用测试脚手架的 `Discover`）、
  以及 `EndpointTextTests` 的 19 条解析用例。

## Deferred

`/connect <ip:port>` —— 不预先知道对端节点 ID 的直连。它需要一次额外的「hello」交换来学习对端身份（例如借助 `KeyExchange` 的 `SenderId`），因此不属于本变更。显式端点已覆盖当前的双机联调需求。
