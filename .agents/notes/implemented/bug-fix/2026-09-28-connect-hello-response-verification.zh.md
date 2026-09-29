# Agent Note: /connect 的 hello 响应曾经完全不验签（未经认证的对端引入）

Status: implemented

## Problem

`/connect <ip:port>` 是「**我只知道端点、不知道对端是谁**，连上去等对方先应答」的盲连接。而修复前的 `P2PChatTui.ReadHelloResponseAsync` 实现是：

```csharp
var raw = await connection.ReceiveMessageAsync(ct);
var envelope = EnvelopeCodec.Deserialize(raw);
if (envelope.MessageType != MessageType.KeyExchange) continue;
var msg = serializer.Deserialize<Message>(envelope.Payload);
if (msg is KeyExchangeMessage { IsResponse: true } response)
    return (response, envelope.SenderPublicKey);
```

**从头到尾没有任何验签调用。** 任何抢在真节点之前应答的主机都会被无条件信任。后果沿调用链放大：

1. **身份取自攻击者可控的载荷。** 调用方随即 `new NodeId(response.SenderId)` —— 而 `response` 是从 `envelope.Payload` 反序列化出来的，载荷里的 `SenderId` 由对端自行填写。
2. **会话密钥派生到攻击者选定的身份名下。** ECDH 用的是攻击者的临时公钥，派生结果随后 `SetSessionKey(peerId, sessionKey)` 挂在攻击者自选的 `peerId` 下。
3. **攻击者被登记为静态对端并落盘为联系人。** `RegisterStaticPeer` 把 `PublicKey = peerPublicKey` 一并写进路由表。

第 3 条最危险：`PublicKey` 取自那条**未经验证**的信封，而 `NodeId.FromPublicKey(PublicKey)` 正是路由表 / 静态对端层做防冒名判定的依据。攻击者因此可以登记一个**公钥与自己声明的 NodeId 对不上**的静态对端 —— 那一层守卫在 `/connect` 路径上不是「变弱」，而是被**绕过**。

这与[入站重放防护](../feature/2026-09-28-replay-protection.md)性质不同：重放防护处理「**已认证**对端的重复投递」，这里是「**身份本身从未被认证**」，两者不可互相顶替。

## Decision

**验签收敛到 Core 依赖图的根**（`Core/Extensions/EnvelopeVerifier.cs`，与 [EnvelopeCodec 收敛](./2026-09-28-envelope-codec-single-source.md) 同一个模式）：`EnvelopeVerifier.Verify` 保留四道检查（签名非空、公钥非空、`NodeId.FromPublicKey(公钥) == SenderId`、对 `EnvelopeCodec.ComputeSignedBytes` 的结果做 ECDSA 验签），**无状态、可重复调用**。`MessageRouter.VerifyEnvelope` 改为委托且**签名不变**（`VerifyEnvelopeCore` 同样保留为委托壳），既有调用方与测试零改动。

在此之上，`/connect` 的应答准入由**三条判据**串联而成，各自挡不同的东西：

- **判据 ①：ConversationId 回显校验。** 本次 hello 带一个一次性关联标识，应答必须原样回显。
- **判据 ②：ECDSA 验签。** 通过之后，对端身份**全部由公钥派生**（`PeerNodeId = NodeId.FromPublicKey(peerPublicKey)`），不再取载荷里的 `SenderId`。
- **判据 ③：端点→身份连续性。** `EnvelopeVerifier.EvaluatePeerIdentity` 给出三种裁决：`FirstContact`（首次接触）、`MatchesExistingPin`（连续性成立）、`ConflictsWithExistingPin`（**拒绝**）。

### 为什么判据 ① 不能交给 `IReplayGuard`

`/connect` 这条路径**走不到路由器** —— 它在自己持有的 TCP 连接上直接读应答，从不经过 `RouteIncomingAsync`，因此 `IReplayGuard` 根本不在这条路径上运行。而重放防护是**本地状态**：受害者节点从未见过那条被重放的 `MessageId`，去重缓存里没有它，会**放行**。攻击者知道自己的临时私钥，于是能解出受害者派生的会话密钥。判据 ① 因此是**无状态**的：它比对的是本次调用刚生成的一次性标识，而不是任何本地历史。

由此得到一条必须记住的推论：**将来若有人把 `/connect` 接上路由器，重放防护并不会自动覆盖它 —— 判据 ① 仍然必需。** 它挡的是「跨受害者重放」，与「同一受害者被重复投递」是两种不同的攻击。

### pin 的来源纪律

`KnownEndpointIdentities()` 只取 `dhtService.GetAllKnownNodes()` 里 **`PublicKey` 非空**的条目作为「端点 → 身份」绑定，**刻意不读 `contacts.json`**。理由是：联系人里存的是**用户自己写下的** NodeId 与 ip:port，那是用户的**意图**，而不是「我们亲眼验过的身份」；把它当 pin，等于把用户的笔误升级成安全断言。只取公钥非空的条目，是因为只有真正完成过一次 `/connect`（或经 DHT 拿到公钥）的对端才会带着公钥登记。

这条纪律防的不是攻击，而是**把一个弱断言当成强断言** —— 与本会话反复出现的「范围更窄的证据被写成更宽的结论」属于同一族错误，只不过这一次发生在**数据来源的选择**上。

### 冲突优先于匹配

`EvaluatePeerIdentity` **不得在首个匹配处 early-return**：它扫完全部绑定，任一冲突即判冲突。一般的去重逻辑是「匹配了就放过」，这里必须反过来 —— 本地记录自相矛盾（同一端点既有匹配又有不匹配）本身就说明绑定数据已损坏，此时「有一条能对上」不足以放行。**宁可拒绝**。这是一个刻意的不对称。

### TOFU 与降级行为

首次接触在信息论上就是 TOFU：攻击者用自己私钥签出的信封与合法节点的在密码学上**不可区分**。因此首次接触时 UI 必须**如实告知**「该端点为首次接触，其身份未经带外验证（TOFU）」并提示通过可信渠道核对 NodeId，**绝不能显示「已验证」「身份可信」**。冲突时默认拒绝，用户可加 `--force` 放行**本次** —— `--force` **不永久改写**已登记的身份。

判据任一失败，抛 `HelloRejectedException`，UI 单独 catch 并明确输出「未做任何登记：会话密钥与静态对端都未写入」，**绝不静默回退到不验证**。静默回退等于把漏洞原样留在原地，而用户还以为自己连上了目标节点。

另加**结构守卫测试**：`P2PChatTui` 中不得再出现任何自行实现的信封解析 / 验签逻辑。缺陷的成因正是「在 UI 层另抄一份」，而这类改动不写测试几乎必然复发。

## Alternatives considered

以下是本轮**实际否决**的方案。前几条是常见直觉，否掉它们的机制都不复杂，但每一条都会把一个具体的安全属性悄悄丢掉。

**只用验签，不做端点→身份连续性（认为签名通过就够了）。** 否决，且这是最容易被误以为足够的一条。验签只证明「应答方持有它自己那把私钥，且其声明的身份与公钥自洽」——**攻击者用自己的私钥自签的信封在密码学上完全有效**，没有任何字段能把它与合法节点区分开。因此首次接触没有任何拒绝依据，只有「我们自己此前为这个端点记下的身份」是可用信息。判据 ③ 是这条路径上**唯一真正能拒自签攻击者的手段**。

**用 `contacts.json` 里的联系人做 pin（联系人表最方便，反正已经有 NodeId 与 ip:port）。** 否决：那是**用户的意图**而非我们亲眼验过的身份。把它当 pin 等于把「用户说这人就是它」这条带外人工断言，升级成「系统验证过此人绑定此端点」—— 两者强度完全不同，而一旦混淆，日志与告警都会开始说没有依据的话。

**冲突时「匹配了就放过」（first-match-wins）。** 否决：本地绑定数据自相矛盾本身即是异常信号，此时放行等于把「数据已损坏」当成「验证通过」。冲突优先于匹配是刻意的**不对称**。

**`--force` 直接改写已登记的身份（用户一次确认就把新身份固定下来）。** 否决：`--force` 只豁免**本次会话**的冲突判定。一次性豁免与永久改写是两件事 —— 后者会把一次误判固化成此后所有连接的基线。

**让判据 ① 依赖 `IReplayGuard`（既然已经有重放防护，不必再加回显校验）。** 否决：`/connect` 不经过路由器，`IReplayGuard` 压根不在这条路径上执行；即便经过，重放防护是本地状态，对**跨受害者**重放同样无效（受害者从没见过那条 `MessageId`）。回显比对是无状态的，判据 ① 因此独立存在。

**「用 TCP 连接本身作为信任凭据」。** 否决：TCP 只保证字节流**到达了同 IP 同端口上的那个进程**，不保证**那个进程是我们的节点**。NAT 之后同出口 IP 的其他主机、同网段内的不同容器 / VM、以及本机上的另一个进程都可能抢先应答。

**「沿用 `/add <nodeId> <ip:port>` 的显式信任模型」。** 否决：两条路径的信任来源不同。`/add` 里用户显式提供了 NodeId，信任的是「**用户断言此人就是它**」；`/connect` 的全部价值恰恰在于**用户不知道对端是谁才去问**。用户给的是**地址**，不是**身份**。

**「直接在 UI 层调 `MessageRouter.VerifyEnvelope`」。** 否决：`MessageRouter` 属于 Chat 层，而 `P2PChat.UI` 只引用 `Core`，引用不到。

**「在 UI 里自己实现一份验签」。** 否决，而且这是本缺陷最该被记住的错误选项：成因恰恰是「在 UI 层另抄一份、结果没跟上签名上线」，再抄一次就是**同构重演**。正解是收敛到 Core。

**「给 `P2PChat.UI` 加 ProjectReference 引用 `P2PChat.Chat`」。** 否决：把视图层绑到会话编排层（连带 Crypto / Networking 传递依赖），且只解决「UI 能不能看见」，没解决「两份消费者共用一份实现」这个根问题。

## Consequences

- `/connect` 从「**先信后验**」变成「**先验后信**」：应答必须回显本次关联标识、通过验签、且不与本机已登记的端点身份冲突，才允许写会话密钥、登记静态对端、落盘联系人。
- **代价：离线 / 旧版本对端连不上。** 验签是阶段 3.2 才上线的，早于它构建的节点发来的响应没有签名，会被直接拒绝；回显校验也会拒掉不实现该约定的旧实现。这是**正确**的失败（无法证明身份就不该被信任），但对用户表现为「以前能连现在连不上」，因此错误信息必须说清原因，而不是让用户对着「连接超时」猜测。
- **首次接触仍然是 TOFU。** 判据 ③ 只在「本机此前为该端点记过身份」时才有判据；**没有历史就没有判据**，此时只能信任并如实告知。任何把 TOFU 结果写成「已验证对端身份」的文案都是错的。
- **`--force` 是用户的显式判断，不是系统的结论。** 它一次性豁免冲突判定，因此一次社会工程式的「确认无误」可以放行一次错误身份；日志里会留有 `LogWarning`，但系统无法把它与真正的拒绝区分开。
- **`PublicKey` 从「未经核实的对端自述」变成「已验签信封中的、与 NodeId 强绑定的长期公钥」**，路由表 / 静态对端层的防冒名守卫在这条路径上恢复有效。
- **`MessageRouter.VerifyEnvelope` 的 `public static` 签名被刻意保留为委托外观**，代价是验签实现在 Core、入口在 Chat，两处都在；好处是既有调用方与测试零改动。
- **「不证明什么」被写了三处，且这个冗余是刻意的。** `EnvelopeVerifier` 的类注释（✅ / ❌ 清单）是真正定义语义的地方；`ReadHelloResponseAsync` 与 `ConnectCommandAsync` 的安全模型段，是给「只看那一段代码」的读者准备的现场提示。删掉任何一处，都会让某个只读局部的维护者重新踩同一个坑 —— 参见 `## Comment discipline`。

## Comment discipline

本缺陷的核心不是「忘了加验签」，而是**一句主动误导的注释**。修复前 `ReadHelloResponseAsync` 的 XML 注释写着：

> 取自该条**已验签**响应信封的 `MessageEnvelope.SenderPublicKey`。……且 `MessageRouter.VerifyEnvelopeCore` 已强制 `NodeId.FromPublicKey(公钥) == SenderId`，因此「公钥 ↔ 身份」的绑定是可信的。

而这条路径**从未调用过** `VerifyEnvelope` / `VerifyEnvelopeCore`。同一次编辑里，调用处的同一断言又出现了一遍。

由此得到的规矩：**注释声称的保证，必须在当前这条代码路径上真实存在。** 引用他处的不变量（「`VerifyEnvelopeCore` 已经保证了 X」）只有在**本路径确实调用了它**时才成立。

它比没有注释更危险，因为**它改变读者的行为**：没有注释时，读代码的人会自己去看这条路径到底做了什么；有了这句注释，他会**跳过检查**，带着「这里已经验过了」的先验继续往下读 —— 而下游的一切（会话密钥写入、静态对端登记、公钥落盘、联系人持久化）都建立在这个先验上。它让缺陷**更难被发现**，而不只是更难被理解。

同一份注释体系里还有第二处同族问题：注释写着「载荷里的 `message.SenderId` 是对端可控输入，**不能**用来推导身份」，而它的调用方做的正是 `new NodeId(response.SenderId)`。两处都发生在同一个文件、同一个方法上。

修复不只是改注释：这两条现在分别是**代码里的显式不变量**（顺序不变量注释写明「绝不能把验签下移到使用点之后」「身份必须从公钥派生」）和**会以问题本身命名的守卫测试**。

## Testing

- `tests/P2PChat.Chat.Tests/EnvelopeVerifierTests.cs` — **11 条**（244 行）。覆盖伪造者用自己的合法私钥自签但冒名第三方被拒、伪造响应被拒时从公钥派生的身份绝不是第三方（会话密钥不会记在好友名下）、**自洽的陌生主机可以通过验签（这是 TOFU 的合法起点）**、端点身份裁决的各分支（首次接触 / 连续性成立 / 换身份判冲突 / 别的端点不影响本端点 / 本地记录自相矛盾按冲突处理 / 参数非法抛异常），以及委托一致性（`MessageRouter.VerifyEnvelope` 必须与 `EnvelopeVerifier` 判定完全一致、缺签名或缺公钥时给出与既有测试相同的中文明因）。
- `tests/P2PChat.Integration.Tests/HelloResponseVerificationTests.cs` — **8 条**（272 行）。含 `TUI的hello响应必须经过Core的EnvelopeVerifier验签`、`TUI的hello路径必须校验应答回显了本次的关联标识`、`TUI必须按端点身份裁决分流_冲突时默认拒绝`、`首次接触的提示必须写明未经带外验证_不得声称已验证`，以及两条防抄实现的结构守卫。

**命名惯例是刻意的**：`TUI必须从公钥派生对端身份_不得使用载荷里的SenderId`、`TUI不得再声称VerifyEnvelopeCore替它守过身份绑定`、`自洽的陌生主机可以通过验签_这是TOFU的合法起点` —— 这些用例的**名字就是问题描述**，在测试列表里一眼就能看出它防的是什么。这比 `TestVerifyX` 有用得多：结构守卫的价值恰恰在于「下一个人改坏时它会红」，而如果红的原因藏在方法体里，红灯就只是一次需要考古的噪音。

全量门禁（由 Lead 复核运行）：`dotnet build` 0 错 0 警，`dotnet test` **356 通过 / 0 失败**，AOT publish 我方代码 0 条 IL 警告，e2e `PASS=41 / FAIL=0`。

## Deferred

- `ChatService.ReadKeyExchangeResponseAsync` 同样在连接上直接读取密钥交换响应并返回，**不经过 `RouteIncomingAsync`**，因此也不经过重放防护。影响较低（该响应只用于本地派生会话密钥），但它与本缺陷同属「绕开统一入站管线」这一族，应与本变更一并复核，而不是留成第二处独立例外。
- 结构守卫测试断言的是**源码文本 / 结构**，对纯排版性重构敏感。本会话已出现过一次实例：某次无害重构使两条守卫因文本不再匹配而变红。这类守卫的取舍是「宁可假红也不放过同构复发」，但维护者需要知道红起来时该先怀疑什么。
- `/connect` 的端到端往返仍无自动化 e2e 断言（`scripts/e2e-verify.ps1` 的 Phase 2 覆盖的是 `/msg` 私聊往返）。

## Related

- [Envelope wire codec converged onto a single source of truth in Core](./2026-09-28-envelope-codec-single-source.md) —— 同一个模式：UI 层另抄一份导致 `/connect` 在签名上线后必然失败；验签的收敛与它完全同构。
- [Inbound replay protection](../feature/2026-09-28-replay-protection.md) —— 处理「已认证对端的重复投递」；本条处理「身份本身未经认证」。判据 ① 的存在理由也源于它：那条路径走不到路由器。
- [/connect <ip:port> — blind connect](../feature/2026-09-21-blind-connect.md) —— 被本缺陷击中的路径，其「安全模型」注释现已补齐。
- [Message signing](../bug-fix/2026-09-21-message-signing.md) —— 阶段 3.2 自签名上线；本缺陷不是签名机制的错，而是新能力没有被接进新出现的调用路径。
