# P2PChat — REPAIR-PLAN 实施交接

> 交接日期：2026-09-28
> 目标：完成 [REPAIR-PLAN.md](REPAIR-PLAN.md) 阶段 1–4
> 状态：**阶段 0–4 全部完成。`/connect` 已变双向（自报监听端点），场景三成立并由 A35 端到端证明。**
> 门禁：`dotnet build -t:Rebuild --no-incremental` **0 错 0 警** ✅ + `dotnet test` **382 通过 / 0 失败** ✅ + `dotnet publish` AOT **我方 0 条** ✅（第三方 4 条，见 §3.1）+ `e2e` **PASS=42 / FAIL=0 / SKIP=0，退出码 0** ✅
> **⚠️ 上述为 2026-09-28 收口实测，逐行测量者见 §3.1。门禁数字有保质期 —— 下次改动后引用前请重新跑一遍。** 正式门禁判据见 **§7.4**。
> 阶段落地明细：[REPAIR-PLAN.md「实施进度」与「阶段 1/2/3 实施记录」](REPAIR-PLAN.md)（`MainlineDhtService.cs` 采用方法名而非行号，原因见该节顶部警告）
> 仍开放项：见 §8。其中 🔴 需**用户确认**的「同局域网 vs 跨网络」**已询问 5 次仍未答复**

---

## 1. 一句话现状

**REPAIR-PLAN 阶段 0–4 全部落地并通过全部门禁**（`dotnet build` 0 错 0 警、`dotnet test` 340 全绿、
AOT 我方 0 条、e2e `PASS=41 / FAIL=0`），文档与代码已对齐。
本轮修掉两处重的东西：**「收到的消息从不显示」**（B3 同类缺陷复发 —— 用户看得到自己发的，
永远看不到任何人发来的，私聊 + 群聊同时中招）与**入站重放防护**（关闭前请先读 §8.1.1 的五条残余风险）。

**唯一需要人回答、且代码无法代替的问题是「两台目标设备是同局域网还是跨网络」** ——
已询问 **3 次**（2026-09-28）仍未答复。它决定阶段 2 是硬需求还是可降级项，代码走的是尽力而为路线，
**不能据「已实现」反推「用户场景已覆盖」**。

---

## 2. 任务板状态

### 2.1 上一轮（收口轮，2026-09-28）—— 9 项任务全部 completed

| 任务 | 主题 | 负责人 | 状态 |
|---|---|---|---|
| task-1 | 修 `scripts/e2e-verify.ps1` 正则缺空格（A14/A15/A17）+ 全脚本正则自查 | e2e-fixer | ✅ completed |
| task-2 | plain 模式 stdin 输入通道补回归测试 | plainmode-tester | ✅ completed |
| task-3 | 填充空壳测试项目 `tests/P2PChat.Chat.Tests`（0 → 80 条） | chattests-filler | ✅ completed |
| task-4 | `KeyExchange` 回送长期公钥 + 修群邀请 `catch` 静默吞掉 | keyexchange-fixer | ✅ completed |
| task-5 | 优化 DHT 引导耗时（40.02s → 3.01s），消除阶段 1.5 副作用 | bootstrap-perf | ✅ completed |
| task-6 | 补写 plain 模式 stdin 修复的 Agent Note | note-writer | ✅ completed |
| task-7 | 同步 `Agent.md` 与 `README.md` 到已落地状态 | doc-writer | ✅ completed |
| task-8 | 更新 `REPAIR-PLAN.md` 进度表 + 本文档 | plan-scribe | ✅ completed |
| task-9 | AOT 门禁 + 最终 e2e 验证 + 未提交 diff 安全性审查 | **lead 本人认领** | ✅ completed |

### 2.2 本轮（2026-09-28 起）—— 进行中

| 负责人 | 主题 | 状态 |
|---|---|---|
| keyexchange-fixer | 入站**重放防护**实现（`IReplayGuard` 已有接口，实现在途） | 🟡 in_progress |
| chattests-filler | 重放防护测试（串行 / 并发 / 过期 / 未来 / 不误杀） | 🟡 in_progress |
| plainmode-tester | 重放防护**配置接线** | 🟡 in_progress |
| lead | 若干与重放无关的独立修复 + 本轮门禁实跑 | 🟡 in_progress |
| plan-scribe | REPAIR-PLAN / HANDOFF 跟进（task-16） | 🟡 in_progress |

> ⚠️ **在 Lead 说出「可以收口了」之前**，本节的 4.4–4.6 状态与 §3 的任何门禁数字都不得改成新值。

> ⚠️ **编号提醒**：更早版本 §2 用过的 `task-1`…`task-6` 是**旧板**编号，与 §2.1 **不是同一批任务**，
> 按主题对照会串。阶段 0–4 的成果已改按阶段收进 §2.3 / §9。

### 2.3 阶段 0–4 交付归档（按阶段，不再占用 task 编号）

| 阶段 | 主题 | 负责人 | 状态 |
|---|---|---|---|
| 阶段 0 | 显式端点 + 真实 `SenderId` + 方向无关会话键 | discovery-engineer | ✅ completed |
| 阶段 1 | DHT `announce_peer`/`get_peers` 闭环 + 端口语义解耦 | discovery-engineer | ✅ completed |
| 阶段 2 | NAT 穿透（UPnP / `ExternalEndPoint`） | discovery-engineer | ✅ completed（**2.4 除外**，见 §8 #7） |
| 阶段 3.1 / 3.3 / 3.4 | 群消息加密 / 群元数据持久化 / 文件 ChunkSize | security-engineer | ✅ completed |
| 阶段 3.2 | 消息签名（长期 ECDSA） | security-engineer | ✅ completed（**重放防护本轮已关闭，残余风险见 §8.1.1**） |
| 阶段 4.1–4.3 | `NodeHarness.SenderId` 回归 + 防复发守卫断言 | test-engineer | ✅ completed |
| 阶段 4.4 | 真实发现闭环测试 `RealDiscoveryTests` | test-engineer | ✅ completed |
| 阶段 4.5 | `KnownDefectsTests` 清理（死 skip 分支已删） | test-engineer | ✅ completed |
| 阶段 4.6 | e2e 明文往返断言 A29–A32 | test-engineer | ✅ **e2e 实测 `PASS=39 / FAIL=0`** |
| 零散 | `ContactService` 文档漂移 + `/connect <ip:port>` | test-engineer | ✅ completed |

---

## 3. 门禁指标

> ✅ **本轮（2026-09-28，双向 `/connect` + 三场景 + A35 轮）。**
> **逐条标注测量者** —— 队员自报的数字**不算门禁证据**，这是本项目一直执行的纪律。
> **引用前请重新跑一遍** —— 见 §3.1 的「上一轮」列作为对照。

```
dotnet build P2PChat.slnx -t:Rebuild   →  0 个警告 / 0 个错误   ✅
dotnet test  P2PChat.slnx              →  见 §3.1（该格存在分歧，未采信）
dotnet publish -c Release              →  我方代码 0 条 IL/AOT 警告   ✅（第三方 4 条，见 §3.1）
scripts/e2e-verify.ps1                 →  PASS=42 / FAIL=0 / SKIP=0，退出码 0   ✅
```

> ⚠️⚠️ **正式门禁必须用 `-t:Rebuild`。** 增量构建 + `--no-build` 可以同时失效并给出完美假绿灯 ——
> **判据见 §7.4，这是本项目最容易被自己骗过去的一处。**

> 📌 **测试数演进轨迹**：121 → 146 → 179 → **297** → **340** → **本轮见 §3.1**。

> ⚠️ `dotnet test --no-build` 会读到过期 DLL 产生假失败；**且它与增量 build 叠加时可能给出「全绿」**。

### 3.1 门禁回填表

| 门禁 | 上一轮收口值 | **本轮实测值** | 测量者 | 通过标准 |
|---|---|---|---|---|
| `dotnet build -t:Rebuild --no-incremental` 警告 / 错误 | 0 / 0 | **0 / 0** | Lead 亲自实跑 | 0 / 0 ✅ |
| `dotnet test` 通过 / 失败 | 340 / 0 | **382 / 0** | Lead 亲自实跑 | 全绿 ✅ |
| `dotnet publish` IL/AOT 警告 | 我方 0 条 / 第三方 4 条 | **我方 0 条 / 第三方 4 条** | Lead 亲自实跑 | 见下方说明 ✅ |
| e2e `PASS` / `FAIL` / `SKIP` | 41 / 0 / 0，退出码 0 | **42 / 0 / 0，退出码 0** | Lead 亲自实跑 | FAIL=0 ✅ |
| AOT 产物 exe SHA256 | `F65AF0C1…` | **`D9DCB925BA346E64463BD488A6BCD0E0C3C18E42F453F08C493AEE428789F292`** | Lead 亲自实跑 | 与实际产物一致 ✅ |

**分项目（Lead 实测，与总数 382 自洽）**：

| 项目 | 上一轮 | 本轮 |
|---|---|---|
| Crypto.Tests | 8 | 8 |
| Core.Tests | 52 | 52 |
| Chat.Tests | 110 | **137** |
| Integration.Tests | 147 | **158** |
| Networking.Tests | 23 | **27** |
| **合计** | **340** | **382** |

> 📌 **本表两格曾一度留空，理由已写在下 —— 保留那段文字，因为它本身是判据的一部分。**
> 当时 `dotnet test` 总数有 375 / 376 两个互斥值且无人实测，exe SHA 因树未静止无法取得。
>
> **判据：填一个指认不了来源的数字，比空着更有害。**
> 不要为了让表格好看而去选一个 —— 这与 §7.3(b) / §7.4 是同一条纪律。

### 3.2 AOT 门禁为什么**不是**一个干净的「0」

`dotnet publish src/P2PChat.App/P2PChat.App.csproj -c Release` 的结果拆开看是：

| 来源 | 条数 | 明细 |
|---|---|---|
| **我方代码** | **0** | —— |
| 第三方程序集 | 4 | `MessagePack.dll` 的 `IL3053` + `IL2104`；`Serilog.dll` 的 `IL2104` |

**请勿把这一格简写成「0」。** 它是「**第三方库自身的代码路径不 trim 友好，而我们不走那些路径**」
这一**人工判断**的结果，**不是工具自动判定的门禁达成**。把两件事混为一谈会让后来者误以为
AOT 安全是自动保证的 —— 而 `Agent.md` §4.3 的核心信条恰恰要求它被持续显式核对。
**警告数一旦变化必须重新人工判断，不存在可依赖的「阈值」。**

#### AOT 产物（禁止复制 exe —— 会触发 Windows 防火墙重复弹窗）

```
路径   src\P2PChat.App\bin\Release\net11.0\win-x64\publish\P2PChat.App.exe
SHA256 D9DCB925BA346E64463BD488A6BCD0E0C3C18E42F453F08C493AEE428789F292
```

> ⚠️ **SHA256 每轮都会变**（中途源码修复触发重新发布）。校验产物请**只比对本节**。
>
> **exe SHA256 演进链条**（用于识别「我手上这份是不是旧的」）：
> `B8F023…`（最早）→ `DC7C33…` → `F65AF0…` → **`D9DCB9…`（当前）**。
>
> ⚠️ `tests/P2PChat.Chat.Tests` 原为空壳（"没有可用测试"），已填充至 137 条 —— 已关闭。

### 3.3 已完成的静态核对

| 项 | 结果 | 证据 |
|---|---|---|
| `scripts/config-probe.ps1` 正则与源码对齐 | ✅ **已修** | Lead 2026-09-28 修 `'PING引导节点: (\S+)'` → `'PING 引导节点: (\S+)'`，对照 `MainlineDhtService.cs` 的 `"PING 引导节点: {Ep}"` |
| `config-probe.ps1` 其余模式 | ✅ 正确 | `引导节点: (.+)` ↔ `Program.cs`；`UDP端口/TCP端口 (\d+) 被占用` ↔ `UdpTransport` / `TcpTransport` |
| `config-probe.ps1` PowerShell 语法自检 | ✅ **0 错误** | `Parser::ParseFile` → tokens=2005, errors=0 |

> 该项原先被我记为 §8 开放项 #10，**现已关闭**，不再占用遗留清单。

---

## 4. e2e 门禁 —— ✅ **已闭合**（`FAIL=0`）

> **✅ 本节已闭环。** 最终实测：**`PASS=39 / FAIL=0 / SKIP=0`，退出码 0**（起始基线 32/5）。
> 下文「起始基线」是修复前的数字，保留作对照。

起始基线（Lead 亲自跑的，AOT 已重新发布）—— **这是修复前的数字**：

```
[A14] FAIL — nodeA DHT 引导发出 PING            ||  PING 行数=0
[A15] FAIL — nodeA PING 到对端 127.0.0.1:20082  ||  匹配=False
[A17] FAIL — nodeA 引导完成且路由表节点数 >= 1   ||  引导完成时路由表节点数=（空）（同批 FIND_NODE 返回：发现 1 个节点）
[A30] FAIL — Phase 2：明文出现在 nodeB2 日志     ||  明文='HELLO-E2E-93104ab4'
[A31] FAIL — Phase 2：nodeB2 StdOut+Log 含明文   ||  no match in 4309B combined
```

### 4.1 根因一（产品缺陷）—— plain 模式无 stdin 输入通道 —— ✅ **已修，输入通路经 e2e 实测有效**

`src/P2PChat.UI/Views/P2PChatTui.cs` 原先：

```csharp
private ConsoleKeyInfo? ReadKeyOrNull()
{
    if (_plainMode) return null;   // ← plain 模式永不读 stdin
    ...
}
```

主循环只在拿到按键时才分派命令，所以 `P2PCHAT_PLAIN=1` 模式下**一切 stdin 输入被丢弃**。e2e 把 `/msg nodeB <明文>` 写进 nodeA2 的 stdin，nodeA2 从不读取 → 从未发消息 → A30/A31 必然失败。

**这不只是脚本问题**：`P2PChatTui.cs` 里控制台初始化失败（`catch`）同样降级到 `_plainMode`。因此**任何 stdout 被重定向 / CI / 管道的用户都落入无输入模式**，是真实可用性缺陷。

**修复内容**（`P2PChatTui.cs`）：

1. 新增 `using System.Threading.Channels;`
2. 新增字段 `_plainInput = Channel.CreateUnbounded<string>()`（含完整 XML 说明）
3. `RunAsync` 中：`_plainMode` 时启动后台任务 `ReadPlainInputAsync(ct)`（后台循环 4）
4. `ReadPlainInputAsync`：阻塞 `Console.ReadLine()`，逐行投递进 `_plainInput`；读到 `null`（EOF）即 `TryComplete()`，避免主循环自旋
5. 主循环新增：`_plainMode && _plainInput.Reader.TryRead(out var line)` → `SubmitLine(line)`
6. 抽出 `SubmitLine(string)`，`SubmitInput()` 与 plain 模式共用同一分派路径
7. `!_plainMode` 的 `ReadKey` 交互路径**完全未改动**
8. **后续加固**：`_plainMode` 原本只在启动时判断一次，运行中途降级会导致**静默无输入**；
   已改为**幂等惰性启动**

**✅ e2e 实测证据 —— 但它证明的范围比看起来窄**：

```
plain 模式：stdin 读到 false (长度 29)
```

29 正是 `/msg nodeB HELLO-E2E-xxxxxxxx` 的长度 —— **说明 29 个字符的 stdin 已被完整读入并分派**，
随后 TCP 连接、密钥交换、双向明文全部打通，往返耗时 0.5s。

> ⚠️ **这条证据只证明「输入通路」修好了，不证明「消息被显示了」。**
> 本轮才发现「收到的消息从不显示」这个阻断级缺陷（详见 §8.1.2）——
> e2e A30/A31 断言「明文出现在 nodeB2 输出」，而 Serilog 的
> `私聊消息已处理: {Sender} -> {Text}` **那一行本身就含明文**，
> 断言被日志行满足即可通过。**上一轮记录的 `PASS=39 / FAIL=0` 对「显示」而言是假绿灯。**
>
> 教训见 **§7.3** —— 那里把本条（测试侧）与「把范围更窄的证据写成了更宽的结论」（文档侧）
> 并置成**同一个陷阱的两个面**：这里这句话本身就是 (b) 的现场。
> 修复 `IChatEventPublisher` 唯一事件源后，e2e 本轮 `PASS=41 / FAIL=0` 才同时覆盖了显示通路。

- ✅ **Agent Note 已补**（task-6 / note-writer）：
  [`notes/implemented/bug-fix/2026-09-28-plain-mode-stdin-input.zh.md`](notes/implemented/bug-fix/2026-09-28-plain-mode-stdin-input.zh.md) 三件套齐全。
- ✅ **回归测试已补**（task-2 / plainmode-tester）：`tests/P2PChat.Integration.Tests/PlainModeInputTests.cs`
  测 `SubmitLine` 的分派行为（`/xxx` 命令 vs 普通文本 vs 空行）。
  设计取舍：不抽 `IPlainInputSource` 新抽象（那会改动产品代码面），只测本修复真正改动的
  `SubmitInput → SubmitLine` 抽取，端到端由 e2e A30/A31 兜底。

### 4.2 根因二（脚本正则漏空格）—— A14 / A15 / A17 —— **已修 + 已全脚本自查**

> **注意**：我最初误判为「阶段 1.5 产生了两条日志行导致先命中错误那行」。**这是错的。**真正原因是正则漏掉了日志里真实存在的空格。

实际日志（摘自 e2e 实跑）：

```
[DBG] PING 引导节点: 127.0.0.1:20082
[INF] 引导节点 127.0.0.1:20082 响应成功
[INF] 引导完成, 发现 1 个节点
[INF] DHT 引导完成, 路由表: 2 节点
```

`scripts/e2e-verify.ps1` 中的模式（**缺空格**）：

| 行 | 现模式 | 应为 |
|---|---|---|
| 342 | `'PING引导节点: \S+'` | `'PING 引导节点: \S+'` |
| 343 | `"PING引导节点: 127\.0\.0\.1:$NodeBUdpPort"` | `"PING 引导节点: 127\.0\.0\.1:$NodeBUdpPort"` |
| 348 | `'DHT引导完成, 路由表: (\d+)节点'` | `'DHT 引导完成, 路由表: (\d+) 节点'` |

**旁证**：A16 的模式 `"引导节点 127\.0\.0\.1:$NodeBUdpPort 响应成功"` 恰好带了空格，所以它**通过了**。

> 顺带说明：阶段 1.5 确实让 nodeA 打了 `引导完成, 发现 N 个节点` 和 `DHT 引导完成, 路由表: N 节点` 两条日志，但**这不是失败原因**，两条都能被修正后的正则正确处理，无需删改任何断言语义。

**本轮已修（task-1 / e2e-fixer）**：

- 上表 3 行按「应为」列改正，**断言语义未放宽**；
- 第 536 行 `keyPattern` 里的 `PING引导节点` 同样补空格（只影响摘要显示）；
- **全脚本正则自查**：把 `scripts/e2e-verify.ps1` 中所有正则与 `src` 里
  `LogDebug` / `LogInformation` / `LogWarning` 的格式串逐一比对，修掉同类缺空格 / 字面量不符之处；
  Phase 2（A29–A32）断言逻辑未动；
- PowerShell 语法自检 0 错误。

> ⚠️ 后续 `scripts/config-probe.ps1:210` 有一处同样的 `'PING引导节点: (\S+)'` 缺空格，
> 已由 Lead 于收口时修复（§3.2）。

> ✅ **task-5（bootstrap-perf）改 `BootstrapAsync` 后，这四条日志已逐字保留**，
> A14/A15/A16/A17 最终全部通过 —— 收口实测 `PASS=39 / FAIL=0` 即为证据。

### 4.3 根因三（脚本连续踩三个坑）—— A30 / A31 —— ✅ **已修，且每个都是静默失败**

修完 4.1 与 4.2 后 A30/A31 仍然 FAIL。**根因不是产品缺陷，是脚本自己连续踩了三个坑** ——
而三个坑**全部是静默失败**：不抛异常、不打日志，只是让中间数据悄悄变成错的，
最终表现为「明文没到」，把排查方向误导到网络上。

| # | 坑 | 后果 | 修法 |
|---|---|---|---|
| 1 | `Start-P2PNode` 开头的 `Remove-Item -Recurse -Force` 把 Phase 2 刚预置的 `contacts.json` **删掉了** | nodeA2 **以零联系人启动** | 新增 `-PreserveDataDir` 开关，Phase 2 显式开启 |
| 2 | `@(...) \| ConvertTo-Json` 在**单元素时塌缩**成裸对象 | 与 `List<StoredContact>` 契约不符 → `JsonException` → 联系人全丢 | 改用 `-InputObject @(...)` 且**不加** `-AsArray` |
| 3 | 加了 `-AsArray` 之后变成 `-InputObject @(...) -AsArray` | 产生**嵌套数组** `[[{}]]`，一样解析不出联系人 | 同上 —— 这正是坑 2 的错误修法 |

> 📌 **坑 3 值得单独记住**：它是「修坑 2 时选的错修法」。`-InputObject @(...) -AsArray`
> 比坑 2 的塌缩更隐蔽 —— 它**确实产出了数组**，只是多包了一层，解析器同样拿不到联系人。
> **两个坑的表现完全一样（联系人丢失），但修法必须不同**，只看「是不是数组」会踩第二次。

### 4.4 修正了一条**错误**断言（不是放宽断言）—— A32

A32 原先断言「nodeA2 看不到明文」。**这个前提本身是错的**：
`DrainInbox` 在 plain 模式下**必然**回显本地出站事件（`[cid] [HH:mm] 我: 正文`），
所以 nodeA2 **本来就应该**看到自己发出去的那条明文。

**已改为**：「nodeA2 的**聊天事件行**若含明文，**必须**标为『我』」，并显式排除 Serilog 日志行。

> ⚠️ **请注意这是「修正错误断言」，语义变严格了，不是放宽。**
> 原断言在功能正常时也会 FAIL（它把正确的回显当成 bug）；
> 新断言验证的是「回显的身份标注正确」—— 这是**更强的约束**。

### 4.5 新增 A29a / A29b 两条前置断言

让 §4.3 的坑 1 与坑 3 这类**静默失败当场变红**，而不是拖到最后退化成「明文没到」这种
指向错误方向的症状：

| 断言 | 守的是什么 |
|---|---|
| **A29a** | 预置的 `contacts.json` 在 nodeA2 启动后**仍然存在**（静态对端前提） |
| **A29b** | `contacts.json` 是「**单层数组 + StoredContact 对象**」（与 `LoadContacts` 契约一致） |

> 这两条是本轮最有价值的沉淀：它们把「静默失败」变成了「显式失败」。
> 下一个人再改脚本或 `LoadContacts` 契约时，会在 A29 就看到红，而不是在 A30 猜网络。

### 4.6 A35 —— 场景三 `/connect` 双向的端到端证明

`/connect` 变双向后，**第一次有了跨进程的行为证明**：发起方自报监听端点 → 应答侧登记反向可达
的静态对端 → **两侧都能互发**。这是场景三（LAN 内一台能连公网、一台不能）成立的前提。

> ⚠️ **在 A35 之前，这个功能只有集成测试证明，没有 e2e 证明。**
> 集成测试走真实 `MessageRouter`，但不跨进程 —— 「反向登记后能双向发话」这一**用户可见行为**
> 当时没有任何端到端证据。这是 B7 的第三次命中，详见 [REPAIR-PLAN B7 教训段](REPAIR-PLAN.md) 与 §7.3。

---

## 5. 复现手册：怎么重新验证本项目的门禁

> 🎯 本节不是「交接清单」，是**可重复执行的验证手册**。
> **不要只信本文档记录的数字** —— 权威性来自你能自己跑出来一遍。
> 步骤 1–4 复现上一轮门禁；步骤 5 验证**重放防护真的生效**（不只是「测试跑过了」）。

> 原「步骤 1–4」是本轮的执行清单，**已全部完成**（见 §3.1）。下面按**复现顺序**重排，
> 供下一个团队**独立重跑一遍**以验证结论可重复 —— **不要只信本文档的记录。**

### 步骤 1：确认前置条件已满足

§2.1 的 9 项任务全部 `completed`。**本轮全部改动尚未提交（`git status` 90 条）** ——
接手第一件事是确认这些改动已在工作区可见。

### 步骤 2：build + test

```bash
# ⚠️ 必须带 -t:Rebuild —— 增量 build 在编译错误存在时仍可能报 0 错 0 警。判据见 §7.4。
dotnet build P2PChat.slnx -t:Rebuild   # 必须 0 错 0 警
dotnet test  P2PChat.slnx              # 必须全绿；不要加 --no-build（会读过期产物）
```

> 📌 **数字请以自己实跑为准，不要抄 §3.1 的** —— §3.1 本就有两格因无人实测而留空，
> 这正是本项目对待数字的态度：**能指认来源才写，指认不了就留空并说明为什么。**

### 步骤 3：AOT 发布（必须先做，否则测的是旧 exe）

```bash
dotnet publish src/P2PChat.App/P2PChat.App.csproj -c Release
```

> ⚠️ 逐条检查 **IL2026 / IL2070 / IL2072 / IL2075 / IL3050 / IL3053**：
> 上一轮实测**我方代码 0 条**，第三方 4 条（`MessagePack.dll` IL3053+IL2104、`Serilog.dll` IL2104）。
> ⚠️ 判读方式见 §3.1 —— 这是**人工判断**，不存在可依赖的阈值。
>
> ⚠️ 产物 exe 路径是 `src\P2PChat.App\bin\Release\net11.0\win-x64\publish\P2PChat.App.exe`。
> **不重新 publish 就会测到旧代码** —— 仓库里那个 exe 的 mtime 曾长期停在 2026-09-16。
> 校验产物请比对 §3.2 的 SHA256（中途修复触发过重新发布，SHA256 已变过一次）。
>
> ⚠️ 绝不要复制 exe（会触发 Windows 防火墙重复弹窗，是历史用户投诉根因）。实例隔离只靠
> `P2PCHAT_DATA_DIR`（漏了会让两个实例共用 `identity.json` → NodeId 相同 → 无法互认，见断言 A24）。

### 步骤 4：复跑 e2e

```bash
pwsh -NoProfile -File scripts\e2e-verify.ps1 -SelfTestWaitMs 20000
```

目标：**FAIL=0**。上一轮实测 `PASS=39 / FAIL=0 / SKIP=0`，退出码 0（起始基线 32/5）。

> 📌 DHT 引导已从 **40.02s 优化到 3.01s**（13.3×，优化前为实跑基线非估算），e2e 单次耗时相应下降。


### 步骤 5：验证**重放防护真的生效**（不只是「测试跑过了」）

> 🎯 **这一节回答的不是「测试绿了吗」，而是「重放防护真的挡住了吗」。**
> 测试全绿只证明**被写下来的那些用例**成立，它证明不了**攻击者会怎么打**。
> 下面四条按攻击者能力从弱到强排列，**每条都要自己动手打一遍**，不要只读测试名。

#### 5.1 复现一条**完全合法**的重放

ECDSA 签名能证明「来自持私钥的一方」，**不能**证明「这是一条新消息」。
攻击者录下一条**合法**信封（原封不动、签名有效、验签全过）反复重放 —— 这是最基础的攻击。

```
1) 正常发一条消息，从 nodeB 侧取到那一条【完整信封的原始字节】
2) 把这串字节原封不动再喂给 nodeB 一次
```

- **要看的证据**：不是「没报错」，而是**第二次明确被拒 + 只投递了一次事件**。
- ⚠️ **最常见的假阳性**：拒绝发生在验签**之前**（长度/公钥检查），那证明不了重放防护起作用。
  **务必确认拒绝原因来自重放判定本身**，而不是被别的关卡顺带挡下。

#### 5.2 验证**并发重放**（契约要求原子性）

`IReplayGuard.TryAccept` 的契约明写：**「两条完全相同的信封并发到达时，有且只有一条能返回 true」**。
用 `Task.WhenAll` 同时灌 2 条、再灌 50 条同一信封：

- **期望**：恰好 1 条被接受，其余全部被拒。
- ⚠️ **这是最容易漏掉的一条**：只测串行重放会通过，但 `Check` 与 `Add` 之间的竞态**只在并发下暴露**。
  实现若用「先查后加」而非原子操作，串行测试**全绿**而并发直接放行 N-1 条。

#### 5.3 验证**过期**与**未来**时间戳

`Timestamp` 此前「签了但不校验新鲜度」。现在确认两端都被拒：

| 输入 | 期望 | 攻击含义 |
|---|---|---|
| `Timestamp` 早于 `ReplayMaxAgeSeconds` | 拒（原因：过旧） | 重放数小时前的包 |
| `Timestamp` 明显在**未来** | 拒（原因：来自未来） | 时钟回拨 / 预生成包 |

> ⚠️ **`ReplayMaxAgeSeconds <= 0` 时是完全无防护的**（见 §8.1 残余风险）。
> 验证时**必须显式确认当前配置值 > 0**，否则你测的其实是「关闭状态」。
> 该值从 `P2PChat:ReplayMaxAgeSeconds` 读取（`Program.cs`，默认 3600 秒）；
> `<= 0` 时 TUI 自检块会显式提示「重放防护已关闭 —— 逃生阀已打开」。

#### 5.4 验证**不误杀正常流量**（反向检查，最容易漏）

重放防护是**有状态**的，最常见的失败不是「挡不住」，而是**「挡住了正常消息」**：

- 正常连续发 N 条**不同**消息 → **一条都不能被拒**；
- 同一对端**高频**互发（压一下 `MessageId` 缓存与环形容量）→ 仍不能误杀；
- 重启后立即互发 → 仍正常（跨重启**不应**因缓存清空而误杀**新**消息）。

> 📌 **判据**：装上防护后，`dotnet test` 的**既有签名/收发测试必须仍然全绿**。
> 若加防护炸红了一堆语义正确的测试，多半是把有状态策略塞进了 `MessageRouter.VerifyEnvelope` ——
> 那是**纯密码学、无状态、可重复调用**的函数，同一信封校验两次第二次就会失败。
> `IReplayGuard` 的设计注释正是为此把它与验签**刻意分离**，不要合并。

#### 5.5 复核「重放键选对了」——别把它"优化"成 `SequenceNumber`

重放键必须是**已被签名覆盖的 `MessageId`**（16 字节 Guid，天然唯一）。
若有人日后想改成 `SequenceNumber`，**以下事实足以否掉该改动**：

| 事实 | 后果 |
|---|---|
| `SequenceNumber` 由**进程内** `Interlocked.Increment` 生成 | **进程重启即归零**，不能作跨重启的单调水位 |
| **同一节点存在两个互相独立的序号计数器**：`MessageRouter._seqCounter` 与 `KeyExchangeHandler._responseSequence` | 两者都从 1 开始，**同一对端的不同消息可能合法携带相同的 `SequenceNumber`** |

> 📌 **结论：在本协议里 `SequenceNumber` 根本不是唯一键。** 第二条比「重启归零」更硬 ——
> 它说明即使不重启，序号也可能**合法重复**，拿它做去重键会直接误杀正常流量（与 5.4 冲突）。
> 这是 note-writer 在实现过程中独立发现的，不是事后补的理由。

---

## 6. 关键约束（务必遵守）

**AOT 安全是本项目的核心信条**：无反射、无 `MakeGenericType`、MessagePack 用源生成 formatter、JSON 必须 `JsonContext` 源生成且 `JsonSerializerIsReflectionEnabledByDefault=false`。新增类型务必同步注册 formatter。

**Agent Note 规范**（`notes/README.md`）：
- 路径 `notes/{lifecycle}/{class}/yyyy-mm-dd-topic-title.{md,zh.md,i18n.yaml}`
- 首三行必须恰好是 `# Agent Note: <title>` / 空行 / `Status: <status>`；lifecycle 目录必须与 Status 一致
- `## Alternatives considered` **必填**
- `implemented/` 下**不得**出现 `## Proposal` / `## Plan` / `## Migration plan` / `## Acceptance criteria`
- 允许 `## Testing` / `## Deferred` / `## Related`
- 交叉引用用相对 markdown 链接
- 中文是 body of record，英文承载 base filename

**REPAIR-PLAN 阶段 0–3 产出的 Agent Note（共 11 组，三件套齐全，均为 `Status: implemented`）**：

| 阶段 | 主题 | 路径 |
|---|---|---|
| 0 | 静态对端 + 显式端点 | [`notes/implemented/feature/2026-09-20-static-peer-explicit-endpoint.zh.md`](notes/implemented/feature/2026-09-20-static-peer-explicit-endpoint.zh.md) |
| 0 | `SenderId` 真实身份 | [`notes/implemented/bug-fix/2026-09-20-message-sender-identity.zh.md`](notes/implemented/bug-fix/2026-09-20-message-sender-identity.zh.md) |
| 0 | 方向无关会话键 | [`notes/implemented/bug-fix/2026-09-20-direction-agnostic-conversation-key.zh.md`](notes/implemented/bug-fix/2026-09-20-direction-agnostic-conversation-key.zh.md) |
| 1 | 公网 DHT 节点发现缺陷 | [`notes/implemented/bug-fix/2026-09-20-public-dht-peer-discovery-gap.zh.md`](notes/implemented/bug-fix/2026-09-20-public-dht-peer-discovery-gap.zh.md) |
| 2 | NAT 穿透 | [`notes/implemented/bug-fix/2026-09-21-nat-traversal.zh.md`](notes/implemented/bug-fix/2026-09-21-nat-traversal.zh.md) |
| 3.1 | 群消息加密 | [`notes/implemented/bug-fix/2026-09-21-group-message-encryption.zh.md`](notes/implemented/bug-fix/2026-09-21-group-message-encryption.zh.md) |
| 3.2 | 消息签名 | [`notes/implemented/bug-fix/2026-09-21-message-signing.zh.md`](notes/implemented/bug-fix/2026-09-21-message-signing.zh.md) |
| 3.3 | 群元数据持久化 | [`notes/implemented/bug-fix/2026-09-21-group-metadata-persistence.zh.md`](notes/implemented/bug-fix/2026-09-21-group-metadata-persistence.zh.md) |
| 3.4 | 文件分块大小 | [`notes/implemented/bug-fix/2026-09-21-filetransfer-chunksize.zh.md`](notes/implemented/bug-fix/2026-09-21-filetransfer-chunksize.zh.md) |
| 零散 | `/connect` 盲连接 | [`notes/implemented/feature/2026-09-21-blind-connect.zh.md`](notes/implemented/feature/2026-09-21-blind-connect.zh.md) |
| 本轮 | plain 模式 stdin 输入通道 | [`notes/implemented/bug-fix/2026-09-28-plain-mode-stdin-input.zh.md`](notes/implemented/bug-fix/2026-09-28-plain-mode-stdin-input.zh.md) |

> ✅ `notes/` 下已无 `Status: proposed` 的笔记。
> plain 模式 stdin 输入修复的 Agent Note 已由 task-6 / note-writer 补齐（原 §6 此处为「待补」）。
> ⚠️ **task-4 若落地 `KeyExchangeMessage` 回送长期公钥这一线路变更，必须另补一篇 Agent Note**
> （当前 task-4 的 write scope 不含 `notes/`，需要 Lead 另行指派）。

---

## 7. 团队交接注意

### 7.1 上一轮（阶段 1–3 主体）

- `discovery-engineer`（阶段 1 + 2）与 `security-engineer`（阶段 3）均已正常收工，产出完整、报告详实。
- `test-engineer` 在当时的 task-5 上**连续失败两次**（两次都停在「跑 e2e 脚本」环节，无收尾消息）。
  它此前完成的其它任务质量没问题，所以更可能是 e2e 运行耗时超出其执行窗口，而非能力问题。
  - 它在失败前**已落地**：`KnownDefectsTests.cs` 重写（4.5）、`e2e-verify.ps1` Phase 2 A29–A32（4.6）、
    `Agent.md` §3.3 同步。这三份经 grep 核对**确认仍在且有效**，不要重做。
  - 派它做长时 e2e 验证时，务必让 Lead 亲自跑最后一遍。

### 7.2 收口轮（2026-09-28）

- 本轮 8 名成员各守一个 write scope，**互不重叠**。改 `scripts/` 的不知道 `src/` 改了什么，
  所以**跨 agent 的假设必须由 Lead 统一核对**（本轮最容易翻车的三处已在文档里点名：
  §4.2 末尾的引导日志字面量、§3.1 的 AOT 判读方式、§3 的测试数基线）。
- **e2e 只能由 Lead 亲自跑**（单次约 10 分钟，超出队员执行窗口 —— 上一轮就是这么翻车的）。
  其它 agent 只做静态核对。
- **行号纪律**：文档里写的落地位置必须 grep 源码核对，不能照抄上一份文档的行号 ——
  DEFECTS.md 记的是「缺陷发现时」的行号，HANDOFF §4 记的是「当时」的行号。**写错行号比不写更糟。**
- **并发编辑期间写进文档的行号是有保质期的资产**：本轮 `MainlineDhtService.cs` 因 task-5 并发修改，
  当日全部行号漂移（`BootstrapAsync` 99→124、`ApplyMapping` 888→970 等 13 处）。
  处理方式是**改用方法名**而非行号 —— 方法名不受漂移影响。给易变文件写行号前先想清楚这一点。
- **核实了局部事实 ≠ 可以外推出全局结论**（本人反面教材）：曾从「`ExternalEndPoint` 零赋值点」
  这一**正确**事实，推出「跨网络建连不闭合」这一**错误**结论，被复核推翻。
  错在默认了「公网入口只能经由该字段抵达建连路径」，没检查 `announce_peer` 是否已把公网 IP
  **直接烘进** `EndPoint`。**把「死字段」当成「能力缺失」** 是这类外推的典型形态。

### 7.3 本轮最贵的一课：**范围更窄的证据被写成了更宽的结论**

> 📌 **这是一个跨「测试」与「文档」两条线的同一个陷阱，不是两条独立的教训。**
> 下面 (a) 在测试代码里，(b) 在文档里 —— 请当成一件事来读。

**(a) 在测试里：断言的强度 ≠ 它指向了正确的对象**（详见 §8.1.2）

「消息从不显示」这个**阻断级**缺陷能长期存在且全绿，是因为
① 全部 `OnMessageReceived` 测试读的都是 **handler 自己的私有通道**，没有一处跨组件；
② e2e「明文出现在输出」的断言被 Serilog 的 `私聊消息已处理: … -> 明文` **一条日志行**满足。

> **一条被日志行满足的断言，比没有断言更危险 —— 它提供虚假的安全感。**
> 提问方式：**「这条断言会变红吗」→「它变红时，故障真的存在吗」。**

**(b) 在文档里：把一个范围更窄的证据写成了更宽的结论**（本人反面教材）

上一轮曾写下这样一句：

```
✅ e2e 实测证据（这是本修复有效的直接证明）
```

**这句话在语法上完全正确** —— e2e 确实跑了、确实 PASS、确实证明了「**输入通路**」修好了。
它错在**主语**：它把这句话挂在了「消息能被显示」这个修复上，而当时「显示」根本还没被验证过。

**这句话如果不被改，会一直绿着躺在 HANDOFF 里**，被下一个读到它的人当成「显示问题已验证」——
因为它测的是输入通路，所以它会持续为真。**一个自信地陈述了错误范围的绿灯，
比一句「我不知道」危险得多。**

> 🎯 **收口时最容易出错的不是「忘了写」，而是「把一个范围更窄的证据写成了更宽的结论」。**
> 在写下 **「✅ 已验证」** 之前要问的不是「它绿了吗」，而是 ——
> **「它绿的那条断言 / 那次运行，指向的是不是我要证明的那件事？」**
>
> 与 (a) 完全同构：(a) 是断言绿了但**对象**错了；(b) 是证据绿了但**范围**错了。
> **一个自信的、范围错误的陈述，比诚实的不确定更贵** —— 因为后者会让人去查，前者会让人停下。

**为什么值得回头改**：那句话是**两个轮次前**我自己写的。留着它不订正，等于把成本转嫁给
下一个人去发现；而**主动推翻自己下过的结论**，是本团队要维持的东西。


### 7.4 正式门禁一律 `-t:Rebuild` —— 这是一条**判据**，不是「已修复」

> 🎯 **读法：这不是「build 的问题已经修好了」的记录，而是一条「什么样的绿灯才配被采信」的判据。**
> CS0165 已经修掉，**但那件事本身不会保护下一个人** —— 只有这条判据会。

### 发生了什么

task-23 收口时，**编译错误 CS0165 真实存在于工作区**，但当时看到的是：

```
dotnet build          →  0 错 0 警        ← 增量构建
dotnet test --no-build →  375 通过 / 0 失败  ← 读的是上一次的产物
```

**两道路具同时失效，给出了一个完美的假绿灯。** 全量门禁已经绿到 375 个，
**差点就据此收工** —— 收工之后这个问题会进主干。

`dotnet build -t:Rebuild` 立刻把 CS0165 挖了出来。

### 为什么两道路具会**同时**失效

| 道具 | 单独用时的失效模式 | 为什么这次叠在一起 |
|---|---|---|
| 增量 `dotnet build` | 编译错误存在时仍可能报 0 错 0 警（时间戳/增量判定认为无需重编） | —— |
| `dotnet test --no-build` | 读到**上一次**的过期产物，照样全绿 | —— |
| **组合** | 第一个让「编译这一步」看起来成功，第二个让「测试这一步」用旧产物证明成功 | **没有任何一步真正编译过当前代码** |

> ⚠️ **关键不是「某个工具坏了」** —— 两个工具各自都在做它们被设计去做的事。
> **关键是没有任何一步真正编译过当前这份代码，而所有输出看起来都是绿的。**
> 这与 §7.3 是同一个家族：一个自信的绿灯，指向的不是你以为它指向的东西。

### 判据

> 📌 **未来看到「0 错 0 警 + 全绿」，只信 `-t:Rebuild` 那一次。**
>
> 具体的门禁命令（§5 步骤 2/3 已按此写）：
> ```bash
> dotnet build P2PChat.slnx -t:Rebuild   # 正式门禁必须带 -t:Rebuild
> dotnet test  P2PChat.slnx              # 允许带 -c Release，但不要加 --no-build
> ```
>
> **增量 build 可以用来「快速看有没有崩」，不能用来证明「没有编译错误」。**

### 附带：MSB3026 文件锁警告

同一轮还出现过 **101 个 MSB3026**。这是**测试进程持有 DLL** 的环境问题，**不是代码问题**。

> 判读顺序：**先查是不是有 `testhost` / 测试进程残留**（环境竞争），再怀疑代码。
> 我当时的第一反应是先怀疑代码 —— **顺序反了**。

### 第二个实证：编译错误藏在测试项目里

**同一轮稍后**，task-32 新增的 `tests/P2PChat.Networking.Tests/PublicDhtDiscoveryEmpiricsTests.cs`
**编译不过 —— 4 个 error**（队员在飞，代码未完成）。这与 task-23 收口时的 **CS0165** 是**同族**：

| | 绿灯实际指向了什么 |
|---|---|
| task-23 收口时 | 「0 错 0 警 + 全绿」指向的是**一段没有被真正编译过的代码** |
| task-32 在飞时 | `增量 build + --no-build` 同样会把「测试项目根本编译不过」这件事**一并掩盖** |

> 🎯 **两条判据同源，差别只在被掩盖的东西：**
> 一个是「绿灯指向了**没编译过的代码**」，另一个是「绿灯指向了一个**空跑通过的程序集**」。
>
> **两次的共同点是：没有任何一步真正编译过当前这份代码，而所有输出看起来都是绿的。**
>
> 实操含义：**只看主项目 build 是不够的** —— 编译错误可能住在 `tests/**` 里。
> `-t:Rebuild` 必须覆盖**整个解决方案**，或至少让测试项目也参与一次真实编译。

### 7.5 `/connect` 从单向变双向 —— **以及 Lead 给的方案是错的**

> 📌 **如实记录，不美化。** 这条里错误方案是我（Lead）给的，队员独立诊断出根因并推翻了我。

**背景**：`/connect` 原本是**单向**的 —— A 连上 B、拿到 B 的 NodeId + 端点，但 B 知道 A 是谁、
**却不知道 A 在哪**。后果是 task-24 核实三场景时的**场景三**（LAN 内一台能连公网、一台不能）
直接不可用：不能连公网那台永远查不到能连的那台。

**我最初给的方案：用 `senderConnection.RemoteEndPoint` 登记静态对端。**

**这个方案是错的，而且错得很根本。** `RemoteEndPoint` 是 **ephemeral 临时端口**，
不是监听端口 —— 对端拿这个端口去连，会被拒。后果不止于此：
`FindNodeAsync` **优先查静态对端表**，一条写错的记录会**永久遮蔽**该对端的 DHT 解析路径，
让一个本来能工作的对端变得再也连不上。

**plainmode-tester 独立诊断出同一根因，并说出了那句本该由我先想到的话**：

> **「协议层根本拿不到，不是实现疏忽。」**

他还给出了更好的退路（**宁可不写**），而不是硬凑一个值。**一个正确的判断 + 一个诚实的退路，
比一个自信的错方案有价值得多。**

**修正后的解法**：由**发起方**在 `KeyExchange` 请求里**自报监听端点**
（`KeyExchangeMessage.SenderListenEndPoint`，载荷字段，**被签名覆盖** ⇒ 可归因）；
应答侧优先用它登记反向可达的静态对端。缺失或不可解析时**降级为只记身份与公钥，不写静态对端表**。

**附带一处同族缺陷**：`KeyExchangeHandler` 同一方法内，一处用**载荷**的 `SenderId`、
另一处用**信封**的 —— 相隔六行。两条真相源。已随本次一并统一为信封。

> 🎯 **这条与 §7.3、§7.4 是同一个家族**：
> §7.3(b) 是**证据**范围写宽了；§7.4 是**绿灯**指向了没编译过的代码；
> 这里是我**给的方案**指向了一个协议层不提供的值。
> **三者都不是「忘了检查」，而是「检查时确信自己要的东西就在那儿」。**
>
> **给后来者的一句实操建议**：当一个方案需要「拿一个我们手上没有的值」时，
> 先假设**协议层根本拿不到**，再去设计怎么让它被**显式传过来**（而不是去猜/去填）。

### 7.6 结构守卫的**自指陷阱**，以及它在本仓库的既定解法

> 🎯 **一句话**：结构守卫断言的字面量，**不要出现在被守卫的代码里，也不要出现在解释这段代码的注释里** ——
> 否则「代码里**不该有** X」的断言会被注释里的 X 满足，**永远绿**。
>
> **本仓库的现状：这一条已经由守卫自身解决，因此它对注释不适用。** 下面第 2 点给出为什么。

**本会话已发生三次**，前两次：chattests-filler 的守卫指控了 keyexchange-fixer 那条
「绝不能出现『身份可信』」的纪律注释；第三次：`P2PChatTui` 的身份真相源注释里写着
`dhtService.LocalNode.NodeId`，若守卫不剥注释就会被它满足。

**✅ 本仓库已有既定解法，且已经写进守卫自己的纪律注释里** ——
`ConversationIdentitySourceTests` 的类注释明确写着：

> **纪律**：断言代码内容必须**先去注释**再匹配，否则注释里出现目标字样会让
> 「代码里该有 X」的空断言假通过（本文件一律走 `StripComments`）。

该守卫的 `StripComments` 先去掉 `/* */` 再去掉 `//`，**然后**才跑正则。因此
`P2PChatTui` 里那段解释性注释（仍字面提及 `dhtService.LocalNode.NodeId`）**不会**让 G2 假通过。

**三个必须记住的事实：**

1. **`//` 正则会吞掉 `///`。** `StripComments` 用的是 `Regex.Replace(source, @"//[^\n]*", " ")`，
   而 `///` 在字面上**以 `//` 开头**，所以 **XML 文档注释也被剥掉了**。
   这不是「碰巧能跑」，是 `//[^\n]*` 的必然结果 —— 但**很容易被误以为只匹配普通行注释**。
2. **因此：注释里含目标字样，在当前守卫集合里不是问题。** 不需要为它做任何改写。
3. **上一轮那次「据自指风险改写注释」是基于错误前提做的工作 —— 应当避免。**
   本会话已经有人为了「防自指陷阱」而去改写一段**根本不会被守卫读到的注释**。
   **被一个不成立的威胁吓到而做无用功，比不做更贵**：它制造了「已经处理过了」的错觉，
   并让真正的解法（剥注释再匹配）继续隐身。

> 📌 **所以：那段注释改不改都不影响门禁。**
>
> **可复用的判据**：写结构守卫时，**先剥注释再匹配**；并在守卫自己的类注释里写明这条纪律。
> 这样「注释里提到被禁的字样」就永远不会成为一个需要人判断的例外 ——
> **例外是要人来判断的，而人会被不成立的威胁吓去做无用功。**

> 📌 **下一次收口的自查清单**（贴在这里以免忘记）：
> - [ ] 每一条「✅ 已验证」后面，能说出**具体是哪次运行 / 哪条断言**支撑的吗？
> - [ ] 那次运行 / 那条断言测的**对象**，和我想证明的**事情**是同一个吗？
> - [ ] 剩下的开放项，**询问次数**更新了吗？（次数本身是诚实信号，不该修饰）
> - [ ] 文档里有没有「上一轮的数字」被当成本轮结论继续用？
> - [ ] **build 用的是 `-t:Rebuild` 吗？覆盖整个解决方案（含 `tests/**`）了吗？**（见 §7.4）
> - [ ] 数字指认不了来源时，**留空并写明为什么**了吗？（不要为了让表格好看而选一个）

---
## 8. 已知遗留

> 状态图例：🔴 阻塞验收 / 🟠 本轮处理中 / ⚪ 后续处理

1. 🔴 **部署场景未确认（必须先问用户）**：REPAIR-PLAN §四问的是「两台目标设备是**同一局域网**
   还是**各自家宽 / 移动网络**」。**截至 2026-09-28 已向用户询问 5 次以上，仍未获答复。**
   > ⚠️ **次数本身是诚实的信号，不修饰。** 每多问一次而答案仍空缺，就更说明它不是
   > 「问一句就能定」的问题 —— 请勿在文档里把它淡化成「已确认」或「按最坏情况处理」。
   目前实现走「尽力而为」策略：UPnP 成功则映射，失败则 TUI 显式提示
   「仅同网段/公网可达方可主动连入」，并保留 `/add <ip:port>` 与 `/connect <ip:port>` 手工接入。
   **三场景的逐条部署指引见 task-24（README 对照表）**，其中场景三的前提就是这个问题。
   **注意：阶段 2 代码落地 ≠ 用户场景已覆盖。** 不得据「已实现」反推。
   同一提示已写入 [REPAIR-PLAN.md「实施进度」顶部与 §四](REPAIR-PLAN.md)。
2. ⚪ **静态/盲连接对端的 `PublicKey` 为空** → 群邀请需要公钥包装密钥。
   ✅ **task-4 已处理**：`KeyExchangeMessage` 回送长期公钥（线路变更）+ `CreateGroupAsync` 的
   `catch {}` 已改为带日志告警，静默吞掉这一独立缺陷已消除。详见 §8.1。
3. 🔴 **`/connect` 的 hello 响应全程不验签 —— 未经认证的对端引入**（2026-09-28 浮现，Lead 亲自 build 后确认）。
   `ChatService.ReadKeyExchangeResponseAsync` 只做 `MessageRouter.DeserializeEnvelope` +
   载荷反序列化就取用，**任何抢在真节点前应答的主机都会被无条件信任并登记为静态对端**。
   严重度高于「响应不过重放检查」—— 后者是在**已认证**对端的前提下重复投递，
   而这里是**对端身份本身未经认证**。
   → **修法与验收标准固化在共享任务板 `task-17`；攻击面刻画见 `task-33`。此处不重复以免漂移。**
4. 🔴 **同一路径同时绕过重放防护**：`ReadKeyExchangeResponseAsync` 取信封后**未过 `IReplayGuard`**，
   即 §8.1.1 已建立的入站重放防护**不覆盖 `/connect` 握手响应**。
   也就是说，**`/connect` 通道的防护强度低于已建立的消息通道** —— 这是一个「两套强度」的不一致，
   而非「全面开启但有残余风险」。修法同上（task-17 / task-33）。
5. ⚪ **身份真相源同族缺陷 —— 两处实例均已修复，但「家族」教训保留**。
   根因是**同一份身份有两个来源**：`MessageRouter` 写入信封用 `_keyStore.GetOrCreateIdentity().NodeId`
   （**发送时现读**），而 `NodeInfo` 是 record + init、`LocalNode.NodeId` 是**启动时派生后冻结**的值。
   两者今天相等**只是因为** `Program.cs` 装配时共用同一 `IKeyStore` 单例；一旦分叉：

   | 实例 | 分叉后的失败模式 | 状态 |
   |---|---|---|
   | `FileTransferService.GetLocalNodeId()` | 载荷 `SenderId` 与信封不自洽 → 被「载荷/信封一致」检查**整条拒掉**，**有一条与文件毫无关系的告警** | ✅ task-28 已修（与 `MessageRouter` 同源、发送时现读） |
   | `P2PChatTui.PrivateConversationKey` | 消息**照常收发**，只是落进 UI **永远选不中的会话桶** —— **无拒绝日志、无异常**，用户只看到「消息发不出去」 | ✅ 已修（改用 `keyStore.GetOrCreateIdentity().NodeId`） |

   > ⚠️ **第二个比第一个更隐蔽**：第一个至少有拒绝日志（只是指向错了地方），第二个**什么都不说**。
   >
   > **这不是两个孤立的 bug，是一类**。已落地结构守卫
   > `ConversationIdentitySourceTests`（G1 锁 `PrivateConversationKey`、G2 锁全文件
   > 「除展示用途外不得把 `dhtService.LocalNode` 当身份真相源」）。
   > **建议：新增会算身份的地方时，让 G2 的白名单显式列出「展示/自述」用途，而不是靠人记得豁免。**
   → **这是「家族」而非单点**：`KeyExchangeHandler` 曾在同一方法内六行之差用了两个真相源，
   `EvaluatePeerIdentity` 的「宁可拒绝」一度是死代码。**建议按家族排查，不要只补已报出的点。**
6. ⚪ **`P2PChatTui` 仍无行为级测试**。`Chat.Tests` 虽已从 0 填到 110+ 条，但 TUI 主循环本身
   仍无行为级测试覆盖 —— plain 模式靠**结构契约**测试 + e2e 兜底。
7. ⚪ **本轮文档改动尚未提交**（本文件 + `REPAIR-PLAN.md`）。产品代码已随 `287efab` 推送。
8. ⚪ **`NodeInfo.ExternalEndPoint` 是死字段**（零赋值点）。
   阶段 2.2 的回填实际落在 `AnnouncedPeer.ExternalEndPoint`（私有 record struct，经 `ListAnnouncedPeers()` 暴露），
   **而非** `NodeInfo.ExternalEndPoint`。
   **但经核实这不影响建连** —— `announce_peer` 分支已用「**UDP 包源 IP + 宣告的 TCP 端口**」拼出
   公网可达的 `NodeInfo.EndPoint`（见 `MainlineDhtService` 的 `announce_peer` case）：
   NAT 后主机的出站 UDP 包，其源地址在公网节点侧就是**公网 IP**；而宣告的端口取自
   `TryAnnounceAsync` 的 `LocalNode.EndPoint.Port`（= `actualTcpPort`），UPnP 又是**同号映射**，
   所以「宣告的端口」就是公网端口。两条合起来 `NodeInfo.EndPoint` = 公网IP + 公网TCP端口，
   `MessageRouter` 可直接连入 —— **链路是闭合的**。
   这同时说明**阶段 1.4 的端口语义解耦实现是正确的**：`EndPoint`(TCP) 与 `DhtEndPoint`(UDP 包源)
   确实是两个字段、两种语义，正是为了消掉 `UdpPort == TcpPort` 的前提。
   `MessageRouter` 只读 `NodeInfo.EndPoint` 是**正确的** —— 那个字段在 NAT 场景下已是公网可达地址。
   **性质是接口冗余 / 文档误导，不是功能缺陷。** 建议后续**删除该死字段或补齐其语义**，
   而**不是**去补「让 `MessageRouter` 改读 `ExternalEndPoint`」的连连逻辑。
   - 附带澄清（同属本项）：`Program.cs` 的 `TryMapAsync(actualTcpPort)` 与 DHT 的
     `UdpTransport(actualUdpPort)` 两者默认不等（都是 0 → 各自自动选），导致路由器上映射的
     **公网 UDP 端口**与我们的 **UDP 监听端口**不一致。
     影响**仅限于「其它 P2PChat 节点无法主动向我们发 DHT 查询」**，
     **不影响建连** —— 我们是通过 `announce_peer` 主动把自己的记录推进对端缓存的，
     对端拿到的 `EndPoint` 已经是可直连的公网地址。
9. ⚪ **阶段 2.4（中继 / 打洞）未做** —— REPAIR-PLAN 标注为可选。
10. ⚪ **`<program dir>/data/` 下的既有数据永不迁移**，仅新路径 `~/.p2pc/` 生效。
11. ⚪ **发布配置与 `Directory.Build.props` 重复声明 TFM**，可能漂移。

### 8.1 已关闭的项（不再占用上面的清单）

| 项 | 结论 | 证据 |
|---|---|---|
| **e2e 门禁未闭合**（原 🔴 阻塞） | ✅ **已闭合** | 最终实测 `PASS=39 / FAIL=0 / SKIP=0`，退出码 0（起始基线 32/5）。见 §3.1、§4 |
| **AOT 发布从未验证**（原 🔴 阻塞） | ✅ **已解除** | `dotnet publish -c Release` 成功；**我方代码 0 条** IL/AOT 警告，第三方 4 条，详见 §3.1 的重要说明 |
| **`tests/P2PChat.Chat.Tests` 是空壳**（0 个测试） | ✅ **已填成 80 条** | task-3 / chattests-filler。这是阶段 4.1–4.6 的测试策略第一次真正落到底层单元 |
| **群邀请公钥缺失 + `catch {}` 静默吞掉** | ✅ **已修** | task-4：`KeyExchangeMessage` 回送长期公钥（线路变更）；`CreateGroupAsync` 静默 catch 改为带告警 |
| **阶段 1.5 引导耗时**（本机实测单节点约 30s） | ✅ **40.02s → 3.01s（13.3×）** | task-5 / bootstrap-perf：并发探测 + 引导期短超时，**未退回「首个成功即 break」**；D7 修复（引导成功节点入路由表）保留 |
| **`AnnouncedPeerCount` 恒为 0**，TUI 自检「已宣告节点数」不可观测 | ✅ **已修** | `MainlineDhtService` 显式覆写 `AnnouncedPeerCount => AnnouncedNodeCount`。根因是接口上是默认实现 `int AnnouncedPeerCount => 0;`，而类里只暴露了另一个名字 `AnnouncedNodeCount` —— **名字对不上** |
| **`ContactService.LoadContacts` 三个静默丢数据缺陷** | ✅ **已修 + 3 条回归测试** | ① 用磁盘原始 hex 当键 → 大写 hex 的 contacts.json 加载后**联系人全部静默丢失**；② 单条非法 NodeId 让**全部**联系人清空；③ 顶层写成裸对象时抛 `JsonException` 并清空全部。现改为规范化小写 hex 作键、单条坏数据不连累其余、兼容裸对象顶层 |
| **`UpnpClient` 在用户路由器上留洞** | ✅ **已修** | 映射已建立但 `GetExternalIPAddress` 失败时返回 null，而 `Program.cs` 只在非 null 时 `RemoveAsync` → **每次这种运行都留下一个最长 1 小时、退出时不撤销、UI 还显示「无 UPnP」的映射**。已改为主动回滚（`DeletePortMappingAsync` 撤销 TCP+UDP） |
| **`/connect` 自阶段 3.2 签名上线后必然失败** | ✅ **已修** | TUI 里有一份没跟上的旧格式解析器副本。已把编解码收敛为 `Core/Extensions/EnvelopeCodec.cs` **唯一真相源** + 15 条边界测试 |
| **`_plainMode` 只在启动时判断一次** | ✅ **已修** | 运行中途降级会导致**静默无输入**。已改为幂等惰性启动 |
| **5 处外部数据的 `PublicKey` 显式置 `null`** | ✅ 已修 | 由 `Array.Empty<byte>()` 改为显式 `null`；`NodeInfoDto.PublicKey` 同步可空化。**注意：此项不含 `NodeInfo.ExternalEndPoint`**，那条仍是死字段，见上面 #6 |
| **`scripts/config-probe.ps1:210` 缺空格** | ✅ 已修 | 见 §3.2 |
| **🔴 收到的消息从不显示**（B3 同类缺陷**复发**） | ✅ **已修 + e2e 验证** | `PrivateMessageHandler` / `GroupMessageHandler` 各自持有**私有** `Channel<ChatMessageEvent>`，而 TUI 读的是 `ChatService._messageChannel` —— **三者互不相通**，且 `PublishMessageAsync` 在 `src/**` **零调用者**。净效果：**用户能看到自己发的消息，永远看不到任何人发来的**（私聊 + 群聊同时中招）。修法：新增 `IChatEventPublisher` 作为**唯一事件源**（`ChatService` 是唯一实现），`src` 内该 Channel 由 3 处降为 1 处 |

#### 8.1.1 「无重放防护」的残余风险 —— **关闭了，但读这段再部署**

✅ 本轮已关闭。四条关闭判据逐条满足（实现接入 / 34 条测试含 14 处变异验证 / 队员用 256 线程
Barrier 与真实 TCP 重放实测 / 本节风险已原样保留）。

**实现**：`MessageReplayGuard`（`IReplayGuard` 的唯一实现），接入 `MessageRouter` 入站路径
（**验签之后、投递事件之前**）。重放键是**已被签名覆盖的 `MessageId`**（16B Guid）。
配置项 `P2PChat:ReplayMaxAgeSeconds`，默认 3600。

> ⚠️ **这不是一个「装上就安全」的东西。下面五条残余风险必须一起读：**
>
> 1. **环形淘汰窗口**：`MessageId` 去重表是每桶 1024 条环形缓冲。被淘汰后、
>    **且仍在时间窗内**的重放**可能通过**。
> 2. **对端桶 LRU 淘汰**：「对端 → 桶」字典封顶 `MaxTrackedPeers = 512` + LRU。
>    某对端被淘汰后，**该对端的近期消息失去去重保护**（仅剩时间窗一道）。
> 3. **`ReplayMaxAgeSeconds <= 0` 关掉的是**整个时间窗**（不是只关时间窗的一半）——
>    此时只剩去重一层，**超前时间戳也不再被拒**。
> 4. **去重状态纯内存，进程重启即清空**：跨重启的重放不受防护。
> 5. **挡不住流量重定向与 DoS**：重放防护只管「同一条合法信封被重复投递」，
>    对流量重放/放大/耗尽类攻击**无效** —— 那属于传输层与连接层的职责。
>
> 两个有界维度缺一不可：只有环形缓冲则对端数无界，只有 LRU 则单桶内 `MessageId` 无界。

> 📌 **为什么重放键不能用 `SequenceNumber`**（防止日后被"优化"掉，详见 §5.5）：
> 除「进程重启即归零」外，更硬的一条是**同一节点有两个独立序号计数器**
> （`MessageRouter._seqCounter` 与 `KeyExchangeHandler._responseSequence`，都从 1 开始），
> 同一对端的不同消息**可能合法携带相同序号** —— 它在本协议里根本不是唯一键。


#### 8.1.2 上一条缺陷的**潜伏机制** —— 比缺陷本身更值得记

它是 REPAIR-PLAN **B7**（「测试与真实链路脱节」）最完整的一次实证：**不是「测试不够多」，是「测试读错了对象还全绿」**。

| 机制 | 后果 |
|---|---|
| 全部 `OnMessageReceived` 测试读的都是 **handler 自己的通道**，**没有一处跨组件** | 集成测试**全绿** —— 它们验证的是 handler 写进了自己的私有通道，而这**从未**是 UI 读的那个 |
| e2e A30/A31 断言「明文出现在 nodeB2 输出」，而 Serilog 的 `私聊消息已处理: … -> 明文` **那行本身就含明文** | 断言被**一条日志行**满足 —— **上一轮给的是假绿灯** |

> ⚠️ **两处绿灯都是假的，但都不是因为「没测」，而是因为「测的是另一个对象」。**
> 详见 §7.3 的不署名教训 —— **(a)**「断言的强度 ≠ 它指向了正确的对象」、
> **(b)**「把范围更窄的证据写成了更宽的结论」；本条是 (a)。
>
> 这也意味着：上一轮 §4.1 用「stdin 读到 false (长度 29)」论证 plain 模式修复有效**仍然成立**
> （那测的是**输入**通路，不是**显示**通路），但用它论证「消息真的显示了」**不成立**。

---

## 9. 阶段交付内容速览

> 📌 本节是**不含行号的速览**。需要精确到落地位置、验证结果与 Agent Note 链接，
> 见 [REPAIR-PLAN.md「阶段 1/2/3 实施记录」](REPAIR-PLAN.md)。
>
> ⚠️ **该节对 `MainlineDhtService.cs` 采用方法名而非行号**：task-5 正在并发改这个文件，
> 2026-09-28 已核出该文件的行号全部漂移（如 `BootstrapAsync` 99→124、`FindNodeAsync` 149→214、
> `ApplyMapping` 888→970）。其余文件行号当轮无并发修改，已逐条 grep 核对。

- **阶段 1（DHT 发现）**：`announce_peer`（info_hash = 本节点 NodeId，先 `get_peers` 取 token）；`Bencode.ParseCompactPeers26`；`FindNodeAsync` 改 `get_peers` 优先；`NodeInfo` 拆分 `EndPoint`(TCP) 与 `DhtEndPoint`(KRPC 源)；`BootstrapAsync` 改为遍历全部引导节点；`AnnouncedPeerCount` / `LastAnnounceUtc` / `AnnounceNowAsync()`。
- **阶段 2（NAT 穿透）**：`IUpnpClient` + `UpnpClient`（**裸 HTTP/1.1 over SSDP + SOAP，刻意不用 COM 以保 AOT 兼容**）；TCP/UDP 同端口映射、退出时 `RemoveAsync` 释放；`ApplyMapping` / `NatMappingState` / `LocalExternalEndPoint`；announce 报文源地址回填对端 `ExternalEndPoint`；TUI 自检块显式展示 UPnP 状态与降级提示。
- **阶段 3.1（群消息加密）**：`GroupChatService.SendGroupMessageAsync` 出口 AES-256-GCM，`GroupMessageHandler` 解密还原，失败丢弃并告警。
- **阶段 3.2（消息签名）**：`MessageEnvelope` 加 `SenderPublicKey` + `Signature`；线路格式升级为「固定头 + 4B 大端公钥长度 + N + 4B 大端签名长度 + N + Payload」；`SendViaConnectionAsync` 出口强制以真实 NodeId 签名；`RouteIncomingAsync` 入口四道关卡（签名存在 / 公钥存在 / `NodeId.FromPublicKey(公钥) == SenderId` / ECDSA 验签），任一失败即丢弃并告警。
- **阶段 3.3（群元数据持久化）**：`IGroupMetadataStore` + `FileBackedGroupMetadataStore`（`~/.p2pc/groups.json`），启动加载 + CreateGroup/HandleInvite/dissolve 三处落盘。
- **阶段 3.4（文件分块）**：`SendFileChunksAsync` 改用 `state.ChunkSize`，不再硬编码 `DefaultChunkSize`。
- **阶段 4.4（真实发现测试）**：新增 `tests/P2PChat.Networking.Tests/RealDiscoveryTests.cs`，拉起两个**真实** `MainlineDhtService` 跑通 `find_node → announce_peer → get_peers`，**不预置任何发现路径**。
- **阶段 4.5（KnownDefects 清理）**：6 个 `SkipException` 动态跳过分支删除，测试重写为真实断言的回归守卫。
- **阶段 4.6（e2e 明文断言）**：`e2e-verify.ps1` Phase 2 新增 A29–A32（两实例就绪 / 接收端日志含明文 / 接收端 StdOut+Log 含明文 / 发送端自身不含明文）。**代码已落地，未复跑**。






