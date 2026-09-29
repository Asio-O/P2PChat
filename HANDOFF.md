# HANDOFF.md — P2PChat 交接

> **日期**：2026-09-29
> **HEAD**：`876c268` — `feat: p2pc_peers6 —— 地址表线路格式支持 IPv6，并堵住静默截断`
>
> **现状（一句话）**：信封族缺陷（读线上信封即信任）已收口 —— 四条「读线上信封」的路径全部有验签，
> 自动密钥交换的应答读取路径本轮补齐了四道判据；门禁全绿，剩余开放项**全部已知且已定性**。

⚠️ **门禁数字全文只出现在 §1 这一处。** 别处不要复制门禁值 —— 复制出来的第二处必然与真实值漂移。
本文件只放**数字**与三条最紧要的纪律；门禁的**完整规则**已单独成篇
[`implemented/process/2026-09-29-gate-discipline-and-measured-numbers`](.agents/notes/implemented/process/2026-09-29-gate-discipline-and-measured-numbers.zh.md)。

> **前一份 HANDOFF 已归档**为
> [`archived/process/2026-09-28-p2pchat-handoff`](.agents/notes/archived/process/2026-09-28-p2pchat-handoff.zh.md)，
> 配套的 REPAIR-PLAN 归档为
> [`archived/process/2026-09-20-p2pchat-repair-plan`](.agents/notes/archived/process/2026-09-20-p2pchat-repair-plan.zh.md)。
> ⚠️ **这两份是冻结历史快照，明永不修改，其中的门禁数字与开放项会永远过期。**
> 现行权威是本文件。
>
> 旧文档里绝大多数篇幅属于冻结件或已被 Agent Note 取代，**不要搬回本文件**：
> 逐轮任务板、每轮门禁回填表、历史行号快照、已关闭项的完整台账、
> `PASS=39/41/42` 那几处并存的旧数字、询问次数 3/5/6 的互斥表述。
> ⚠️ **全文只允许有一个「当前门禁」表**，不要再造第二处。

---

## §1 当前门禁（唯一权威位置）

下表由 **Lead 亲自实跑**。「上一轮」列是 2026-09-28 的收口值，仅供对照。

| 门禁 | 上一轮（2026-09-28）收口值 | **当前（2026-09-29）实测值** | 测量者 | 通过标准 |
|---|---|---|---|---|
| `dotnet build -t:Rebuild --no-incremental` 警告 / 错误 | 0 / 0 | **0 / 0** | Lead 亲自实跑 | 0 / 0 ✅ |
| `dotnet test --no-build` 通过 / 失败 / 跳过 | 382 / 0 / 0 | **418 / 0 / 0** | Lead 亲自实跑 | 全绿 ✅ |
| `dotnet publish -c Release` IL/AOT 警告 | 我方 0 条 / 第三方 ~~4~~ → **3** 条 | **我方 0 条 / 第三方 3 条** | Lead 亲自实跑 | 见下方说明（**已重新人工判断**）✅ |
| `scripts/e2e-verify.ps1` `PASS`/`FAIL`/`SKIP` | 42 / 0 / 0，退出码 0 | **43 / 0 / 0，退出码 0** | Lead 亲自实跑 | `FAIL=0` 且退出码 0 ✅ |
| AOT 产物 exe SHA256 | `D9DCB925…` | **`EDD2C5E2FAE61FA50DA2D584E27EF43218B194530B6BDDD0C57495486584ECD3`** | Lead 亲自实跑 | 与实际产物一致 ✅ |

分项（与总数自洽）：Crypto **8** · Core **64** · Chat **149** · Integration **158** · Networking **39**。

⚠️ **正式门禁必须带 `-t:Rebuild --no-incremental`。** 增量构建叠加 `--no-build` 可以**同时失效**，
并给出一个**完美的假绿灯** —— 编译没真做、测试跑的是上一轮的旧产物，而两边都不报错。

⚠️ **队员自报的数字不算门禁证据。** 门禁值必须由 Lead 亲自实跑并署名。
「我这边是绿的」是证词，不是证据；上表的「测量者」一列就是这个意思。

⚠️ **AOT 那一格永远不要简写成「0」。** 它是**人工判断**的结果 —— 第三方库自身的代码路径
不 trim / AOT 友好，而我们**不走那些路径** —— 不是工具自动判定的门禁达成。
并且：**「第三方 4 条」这个历史数字本轮已被证明是错的**（Serilog 只有 `IL2104`、没有 `IL3053`，实为 3 条）。
⚠️ **条数变化必须重新人工判断，不能套用旧结论** —— 上一轮的条数与理由都不构成这一轮的依据。

---

## §2 开放项（只列仍需行动的）

### 2.1 🔴 需要修

**§8 #12 `/connect` 跨 NAT 反向登记毒丸** —— A 连得上 B、B 回不了话、重启才好。
成因是反向登记所依赖的监听端点只能由对端自报，跨 NAT 时该自报值不可达；
而静态对端表**优先**于 DHT 解析命中，于是一条废记录能把发现能力一并遮蔽。
⚠️ 条件已变：用户要求**四种部署场景全支持** ⇒ 这条**在范围内**，不再是「已知取舍」。
细节见归档件 §8。

**§8 #13 `/msg <短hex>` 前缀歧义** —— **本轮已修并有测试**，已从开放项移走；
指向 [`implemented/bug-fix/2026-09-29-contact-lookup-ambiguity`](.agents/notes/implemented/bug-fix/2026-09-29-contact-lookup-ambiguity.zh.md)。

**部署场景问题** —— 🔴 问了 **6 次**后于 2026-09-29 获答，**不再是开放项**。
（保留这个事实：本仓库看重「问过几次」，它记录了当时的信息缺口有多深。）
协助节点 / 陌生节点协助 / 地址表 / IPv6 的范围见
[`proposed/feature/2026-09-29-helper-assisted-traversal-and-ipv6`](.agents/notes/proposed/feature/2026-09-29-helper-assisted-traversal-and-ipv6.zh.md)。

### 2.2 ⚪ 待做

**§8 #14 IPv6 缺口** —— 全栈 IPv4-only。模型层（`NodeInfo` / bencode / 线路格式）**已地址无关**，
瓶颈在**传输层**。详见 Note。

**§8 #15 `p2pc_peers` 的 IPv6 静默截断** —— 方案已由 Lead 裁决（双字段并存），
**代码已落地、格式能力已备好**。⚠️ 但 **IPv6 传输层未做 ⇒ 该路径仍打不到**。
⚠️ **如实写成：已消除会随 #14（N1）自动激活的错误路径，但缺陷本身因不可达而未实际修复。**
不要把它记成「已修」—— 一旦传输层补上 IPv6，截断就会重新变成活路径。

**其余仍开放**（一行一条，细节指向归档件对应小节）：

- 旧 `data/` 目录不自动迁移
- `TargetFramework` 在 `Directory.Build.props` 之外**每个 csproj 都各自重复声明**一次
- TUI 层没有行为级测试（`Chat.Tests` 覆盖 `ChatService` 一侧，UI 基本是空的）
- `GroupChatService` 成员公钥探测路径的**硬化项**：不过 `IReplayGuard`、`ConversationId` 仍恒定。
  ⚠️ **经复核未发现可利用后果** —— 其唯一输出（对端公钥）被验签的派生检查、预期对端核对、
  `Record` 的 `DerivesNodeId` 复核**三重绑定**，任何能通过三者的帧携带的都是该 NodeId **当前**的正确公钥
  （`NodeId = SHA1(公钥)`，换密钥必同时换 NodeId，故「旧公钥写回」不可达）。
  保留为待办的理由只有两条：与兄弟路径强度不一致；**恒定 `ConversationId` 会在将来有人给本路径
  加「新鲜度」语义时静默把它废掉**。

### 2.3 ⚪ 未决（需要人拍板，不要当已决定）

混合网络下 `p2pc_peers` / `p2pc_peers6` 两个字段的**条目取舍规则**尚未裁决。
本文件不代做这个决定。

---

## §3 残余风险指针

**不要复制正文。** 需要细节时链到对应 Note 或归档件小节。

- 重放防护的五条残余 → 归档件 §8.1.1
- 密钥交换四道判据的五条残余 → 归档件 §8.1.3

⚠️ **关闭 ≠ 没有残余，也 ≠ 对端身份已被验证。**

「本轮已修 / 门禁全绿」说的是**这条代码路径不再缺少它应有的判据**，
**不是**「对端是谁已被证明」。首次接触仍是 TOFU：攻击者用自己的私钥自签的信封在密码学上**完全有效**。
**「验签通过」永远不能表述成「对端可信」。**

### 3.1 判据 ① 那条最容易复发的推理

四道判据的顺序固定，第一道尤其反直觉：

> **回显校验的边界不是「哪条路径」，而是「该值是否每次交换重新随机」。**

`ConversationId` 恒定时，攻击者录下的旧应答天然对得上，**回显校验形同虚设**。
`ChatService` 此前正是恒定的 `recipient.NodeId.ToHexString()` —— 所以当初那句
「不要给 `PerformKeyExchangeAsync` 加回显校验」在**当时是对的**：它只在恒定值上做，验它等于没验。
该值现已改为每次新生成的 `kx-{Guid:N}`，同一条校验随之成立且必要。

⚠️ 有测试钉住这条（`关联标识每次交换都必须重新生成_不得退回恒定的会话键`，
`tests/P2PChat.Chat.Tests/KeyExchangeResponseVerificationTests.cs`）。
**四道判据在运行时不自检** —— 退回恒定值时代码不会报错、不会拒、不会留痕；
这类退化对生产代码是**隐形**的，只有外部断言看得见。

### 3.2 一条结构事实，不是待办项

`ChatService.ReadKeyExchangeResponseAsync` **结构上就**走不到 `RouteIncomingAsync` ——
它读的是本进程主动发起、且没有入站消费循环的出站连接。
所以它必须**自己**答全四道判据，**不因「它不经过路由器」而豁免**。
把它当成「漏走了路由器所以该修」是读错了。

---

## §4 本轮已关闭 + 指向

| 项 | Agent Note |
|---|---|
| 密钥交换应答四道判据 | [`implemented/bug-fix/2026-09-29-key-exchange-response-verification`](.agents/notes/implemented/bug-fix/2026-09-29-key-exchange-response-verification.zh.md) |
| `/msg` 前缀歧义 + 解析逻辑下沉 Core | [`implemented/bug-fix/2026-09-29-contact-lookup-ambiguity`](.agents/notes/implemented/bug-fix/2026-09-29-contact-lookup-ambiguity.zh.md) |
| `p2pc_peers6` 双字段 + 堵住静默截断 | [`implemented/architecture/2026-09-29-peer-address-table-ipv6-format`](.agents/notes/implemented/architecture/2026-09-29-peer-address-table-ipv6-format.zh.md) |
| 协助节点 / 陌生节点协助 / 地址表 / IPv6 范围 | [`proposed/feature/2026-09-29-helper-assisted-traversal-and-ipv6`](.agents/notes/proposed/feature/2026-09-29-helper-assisted-traversal-and-ipv6.zh.md) |
| 门禁纪律与实测数字 | [`implemented/process/2026-09-29-gate-discipline-and-measured-numbers`](.agents/notes/implemented/process/2026-09-29-gate-discipline-and-measured-numbers.zh.md) |

**冻结工作文档**（历史快照，**不是**现行权威）：

- [`archived/process/2026-09-20-p2pchat-repair-plan`](.agents/notes/archived/process/2026-09-20-p2pchat-repair-plan.zh.md)
- [`archived/process/2026-09-28-p2pchat-handoff`](.agents/notes/archived/process/2026-09-28-p2pchat-handoff.zh.md)

---

## §5 写这份文件时的纪律

⚠️ **代码注释里的「已保证 / 已强制 / 可信」必须能指到一处可验证的调用点。**
指不到的，宁可写「⚠️ 此处**未**验签」。

**缺注释只是让人知道「这里没做」；假注释让人主动放弃检查** —— 危害大得多。
本仓库已为这类形态付出过代价：编解码与验签都曾各被复制多份，每次都漏改一处。
正确的修法是**收敛**（把实现下沉到 `Core`），不是在第二个地方再抄一份。
