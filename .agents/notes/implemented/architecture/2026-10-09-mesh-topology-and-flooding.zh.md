# Agent Note: 数据平面改为全连接 mesh —— 聊天消息泛洪，点对点不再是唯一路径

Status: implemented

## Problem

数据平面曾是**按需点对点**。发送消息时解析对端端点、新建一条 TCP 连接、单播该帧（`MessageRouter.GetOrCreateConnectionAsync` + `SendAsync`）。没有任何节点转发消息，因此消息只有在发送方能与收件方建立直连时才能送达。

这个缺口早有记录：[NAT 穿透](../bug-fix/2026-09-21-nat-traversal.md)否决了**中继服务器**（特权基础设施，与「无中心服务器」前提冲突）并推迟了打洞；[协助节点穿透与 IPv6](../feature/2026-09-29-helper-assisted-traversal-and-ipv6.md)把协助明确限定在「建立连接，不中继流量」。那份提案的 `## Risks` 点出了本项目从未摆脱的张力：打洞失败时**没有兜底**，连接就是建立不起来。

本决策改变数据平面，让消息可以借道其它节点，而不要求到收件方的直连路径。它并不复活被否决的中继服务器 —— 区别写在 `## Decision`。

## Decision

**每个节点与其已知对端维持常驻 TCP 连接（全连接 mesh），聊天消息在其中泛洪。** 实施前与用户确认了两个选择：全连接连接策略，以及**所有**聊天消息（私聊 + 群聊）泛洪、而非仅群聊。

各项机制，每一项都刻意复用已有之物：

- **转发原样复用信封，逐字节不重签。** `RouteIncomingAsync` 通过 `MessageRouter.ForwardAsync` 转发已验证的信封，不重签名、不改 `SequenceNumber` / `Timestamp`。接收方针对**原始发送者**验签与去重。线格式零变更：新旧节点互操作。
- **环路终止来自既有重放防护，不来自 TTL。** `MessageReplayGuard` 按原始 `SenderId` 分桶并记录已接受的 `MessageId`（`PeerKeyOf`），因此经多条路径到达的消息每节点只接受一次，每节点对每条消息最多转发一次。泛洪在任意形状的 mesh 上自终止。
- **只有聊天载荷泛洪。** `PrivateText` 与 `GroupText` 转发。`KeyExchange` 保持请求-响应（响应必须沿请求离开的那条连接返回）、`FileMeta` / `FileChunk` / `FileAck` 保持点对点（大流量）、`GroupInvite` / `GroupNotify` / `DeliveryAck` 保持定向。白名单在 `MessageRouter.IsFloodable`。
- **连接池维持既有不变量：只含出站连接。** 入站连接**不**进池 —— 出站连接的发起方对它没有持久读循环：密钥交换响应由 `ChatService` 直接读，握手之后那个方向无人读。若让入站连接进池，泛洪会「反向复用」一条读端已停止的连接，消息静默丢失。因此双向可达靠每对节点各建一条出站连接（两侧各拨一条）；mesh 维护器补拨缺失的那条。
- **私聊新增收件人校验。** 泛洪把私聊路由到不相关节点，其中一些持有与发送者的会话密钥、能解密它。`PrivateMessageHandler` 现在在解密与发布前要求 `message.ConversationId == ConversationId.ForPrivate(myNodeId, senderId)`，于是「能解密」不再等于「是收件人」。群消息无需此校验 —— 非成员解密群密钥失败，处理器本就丢弃。
- **维护器让 mesh 保持存活。** `MeshTopologyService`（新增，`src/P2PChat.Chat/Mesh/`）按 `P2PChat:Mesh:MaintainIntervalSeconds`（默认 30）轮转，把静态对端种子与 `IDhtService.GetAllKnownNodes()` 合并，对任何「已知但无活跃连接」的对端拨号，并发上限 4。它不握手 —— mesh 连接只是 TCP 通道，会话密钥仍由 `ChatService.EnsureSessionKeyAsync` 按需协商。它不发现 —— DHT 平面不动。
- **配置显式。** `P2PChat:Mesh:FloodingEnabled`（默认 `true`）进入 `MessageRouter` 构造参数，置 `false` 时关闭转发 —— 行为对照 / 回退阀，不是安全边界。`P2PChat:Mesh:MaintainIntervalSeconds`（默认 30）驱动维护器。
- **发送路径改为泛洪。** `ChatService.SendPrivateMessageAsync` 保留解析与握手，然后 `GetOrCreateConnectionAsync(recipient)`（幂等；保证「已有会话密钥」分支也有直连）与 `FloodAsync`。`GroupChatService.SendGroupMessageAsync` 泛洪一次，不再逐成员扇出；成员过滤由接收端的群密钥解密承担。

## Alternatives considered

**限制度数 mesh + 邻居选择算法。** 标准的规模化答案。对本项目输在复杂度：邻居选择策略、加入/退出的再平衡，以及「送达依赖泛洪正确性」而非「依赖 mesh 连通性」。本项目明示的规模是小聊天群（几十个节点），O(N²) 连接可负担，全连接让每条消息对每个可达对端都一跳即达。

**仅群聊泛洪；私聊保持直连，多跳只作兜底。** 更接近现模型，且泄露更少的私聊元数据。它输在：它需要的兜底恰好是泛洪这个通用机制，却被实现两次 —— 一条直连路径加一条泛洪路径，再加一套「何时降级」的判定。而且它把私聊送达 —— 正是驱动本次改动的场景 —— 在直连失败且泛洪路径未维护时，留在了同样的无兜底缺口里。

**让入站连接进池，一条 TCP 连接双向复用。** 「TCP 是全双工」的诱人选项，也是第一版实现尝试的做法。它在打红双向测试后被否决：出站连接的发起方在握手后没有读循环，反向复用等于写给一个已停止读取的读端。保持池只含出站连接，代价是每对节点多一条连接，却保住了一条不变量 —— 池里每条连接的远端都有一个活着的读端。

## Consequences

- **赢得：为驱动本次改动的那个缺口提供了兜底。** 当 A 与 C 无法直连，A 的消息仍可经 B 到达 C。这**部分消解** —— 而非关闭 —— [协助节点穿透](../feature/2026-09-29-helper-assisted-traversal-and-ipv6.md) `## Risks` 里记录的张力；兜底在实践中是否存在，仍取决于是否**存在**一条经自愿对端的路径。
- **不是中继服务器。** [NAT 穿透](../bug-fix/2026-09-21-nat-traversal.md)否决的中继是特权、需部署基础设施的服务器。mesh 对端是平等的、不担任特殊角色，转发跑的是每个节点都跑的同一份代码。该否决仍然成立。
- **代价：私聊元数据全网可见。** 私聊的发送者身份与会话存在性，对每个转发的节点都可见，尽管内容仍是 AES-256-GCM 密文。这点已向用户挑明并被接受；这是泛洪私聊流量的价格。
- **代价：O(N²) 连接，每对两条。** 两侧各拨自己的出站连接。数量受小网络前提约束。
- **代价：每节点对每条消息最多一次转发跳。** 每节点对每条消息最多转发一次（重放防护封顶），回发给原始发送者由 `ForwardAsync` 的发送者键跳过所抑制。中间跳的回声（转发者在反向连接上再次收到自己的转发）无法仅凭信封抑制，由远端重放防护吸收 —— 在所明示的规模下可接受。
- **旧节点安全地打断泛洪链。** 旧版本节点不转发，混合网络中泛洪在旧节点边界停止。旧节点仍正常处理收到的内容；降级是「部分可达」，不是错误。

## Deferred

- **对端集合的维护目前只在启动期。** 维护器的种子列表取自启动时的静态对端；之后发现的对端通过按需拨号（私聊发送拨号、群邀请拨号）或主动连入进入 mesh，而不是由维护器接管。把维护器扩展到每轮都取实时路由表是一个小的后续项，本次未做。
- **无邻居度数上限。** 由用户选择全连接而接受；仅当部署超出几十个节点时才重新考虑。
- **`p2pc_peers` 的 `values` 路径裁剪与 O1 静态表遮蔽仍开放**，如 [开放缺陷](../bug-fix/2026-09-30-open-defects-and-pending-decisions.md)所记；mesh 不改变「握手仍需直连」这一事实。

## Related

- [NAT 穿透](../bug-fix/2026-09-21-nat-traversal.md) —— 其「中继否决」本 Note **不**推翻；其推迟的打洞由本 Note 的兜底部分补偿。
- [协助节点穿透与 IPv6](../feature/2026-09-29-helper-assisted-traversal-and-ipv6.md) —— 其 `## Risks` 里的无兜底张力由本 Note 部分消解。
- [开放缺陷与待定决策](../bug-fix/2026-09-30-open-defects-and-pending-decisions.md) —— O1（静态表遮蔽）与「打洞失败时的部署覆盖」仍开放。
- [消息签名](../bug-fix/2026-09-21-message-signing.md) —— 让转发可以复用信封而不重签的那套签名机制。
