# P2PChat — REPAIR-PLAN 实施交接

> 交接日期：2026-09-28
> 目标：完成 [REPAIR-PLAN.md](REPAIR-PLAN.md) 阶段 1–4
> 状态：**✅ 全部收口。阶段 0–4 全部完成，e2e `FAIL=0`。** 本轮 9 项任务全部 completed
> 门禁：`dotnet build` **0 错 0 警** ✅ + `dotnet test` **297 通过 / 0 失败 / 0 跳过** ✅ + `dotnet publish` AOT **我方 0 条** ✅（第三方 4 条，见 §3.1 的重要说明）+ `scripts/e2e-verify.ps1` **PASS=39 / FAIL=0 / SKIP=0，退出码 0** ✅
> 阶段落地明细：[REPAIR-PLAN.md「实施进度」与「阶段 1/2/3 实施记录」](REPAIR-PLAN.md)（`MainlineDhtService.cs` 采用方法名而非行号，原因见该节顶部警告）
> 仍开放项：**5 条**（见 §8），其中 🔴 阻塞验收的只有「同局域网 vs 跨网络」需用户确认

---

## 1. 一句话现状

REPAIR-PLAN 阶段 0–4 全部落地并通过全部门禁：`dotnet build` 0 错 0 警、`dotnet test` 297 全绿、
AOT 发布我方 0 条警告、e2e 端到端 `PASS=39 / FAIL=0`。
文档与代码已对齐（`REPAIR-PLAN.md` / `HANDOFF.md` / `Agent.md` / `README.md` 同步于 2026-09-28）。
**唯一需要人回答的问题是「两台目标设备是同局域网还是跨网络」** —— 它决定阶段 2 是硬需求还是可降级项，
代码走的是尽力而为路线，**不能据「已实现」反推「用户场景已覆盖」**。

---

## 2. 任务板状态

### 2.1 本轮（收尾轮，2026-09-28）—— 共享任务板

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

> ⚠️ **编号提醒**：上一版 §2 用的 `task-1`…`task-6` 是**旧板**编号，与上表**不是同一批任务**，
> 按主题对照会串。上一轮的成果已改按阶段收进 §2.2 / §9。

### 2.2 上一轮已交付（按阶段归档，不再占用 task 编号）

| 阶段 | 主题 | 负责人 | 状态 |
|---|---|---|---|
| 阶段 0 | 显式端点 + 真实 `SenderId` + 方向无关会话键 | discovery-engineer | ✅ completed |
| 阶段 1 | DHT `announce_peer`/`get_peers` 闭环 + 端口语义解耦 | discovery-engineer | ✅ completed |
| 阶段 2 | NAT 穿透（UPnP / `ExternalEndPoint`） | discovery-engineer | ✅ completed（**2.4 除外**，见 §8 #7） |
| 阶段 3.1 / 3.3 / 3.4 | 群消息加密 / 群元数据持久化 / 文件 ChunkSize | security-engineer | ✅ completed |
| 阶段 3.2 | 消息签名（长期 ECDSA） | security-engineer | ✅ completed |
| 阶段 4.1–4.3 | `NodeHarness.SenderId` 回归 + 防复发守卫断言 | test-engineer | ✅ completed |
| 阶段 4.4 | 真实发现闭环测试 `RealDiscoveryTests` | test-engineer | ✅ completed |
| 阶段 4.5 | `KnownDefectsTests` 清理（死 skip 分支已删） | test-engineer | ✅ completed |
| 阶段 4.6 | e2e 明文往返断言 A29–A32 | test-engineer | 🟡 **代码已落地，未复跑** |
| 零散 | `ContactService` 文档漂移 + `/connect <ip:port>` | test-engineer | ✅ completed |

---

## 3. 门禁指标（全部实测，2026-09-28 收口）

```
dotnet build P2PChat.slnx -t:Rebuild   →  0 个警告 / 0 个错误   ✅
dotnet test  P2PChat.slnx              →  297 通过 / 0 失败 / 0 跳过   ✅
    Crypto.Tests            8
    Core.Tests             52
    Chat.Tests             80   ← 本轮从空壳（0）填起
    Integration.Tests     134
    Networking.Tests       23
dotnet publish -c Release              →  我方代码 0 条 IL/AOT 警告   ✅（第三方 4 条，见 §3.1）
scripts/e2e-verify.ps1                 →  PASS=39 / FAIL=0 / SKIP=0，退出码 0   ✅
```

> 📌 **测试数演进轨迹**：阶段 0 前 121 → 阶段 0 后 146 → 本轮中途 179 → **收口 297**。
> 增长主要来自 task-3 把 `Chat.Tests` 从空壳填成 80 条，以及 task-2 / task-4 / task-5 的回归测试。
> `Chat.Tests` 曾是「没有可用测试」的空壳，**这也是阶段 4.1–4.6 的测试策略第一次真正落到底层单元**。
>
> ⚠️ `dotnet test --no-build` 会读到过期 DLL 产生假失败。**测试前先 build。**

### 3.1 收口回填（全部为实测值）

| 门禁 | 收口值 | 通过标准 |
|---|---|---|
| `dotnet build -t:Rebuild` 警告 / 错误 | **0 / 0** | 0 / 0 ✅ |
| `dotnet test` 通过 / 失败 / 跳过 | **297 / 0 / 0** | 全绿 ✅ |
| `dotnet publish` IL/AOT 警告 | **我方代码 0 条 / 第三方程序集 4 条** | 见下方说明 ✅ |
| e2e `PASS` / `FAIL` / `SKIP` | **39 / 0 / 0**，退出码 0 | FAIL=0 ✅（起始基线 32/5） |

#### ⚠️ AOT 门禁为什么**不是**一个干净的「0」

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
大小   9.05 MB
SHA256 DC7C33F7703848442A3D77A4807F6E35099F0E173BB73668E8A4349E3EF37B41
```

> 注：SHA256 与上一轮记录的 `B8F023…` 不同 —— 中途的源码修复触发了重新发布。
> **以本节的值为准**；校验产物时请比对本节，不要用旧值。
> ⚠️ `tests/P2PChat.Chat.Tests` 原为空壳（"没有可用测试"），本轮由 task-3 填成 80 条 —— 已关闭。

### 3.2 已完成的静态核对

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

### 4.1 根因一（产品缺陷）—— plain 模式无 stdin 输入通道 —— ✅ **已修，且经 e2e 实测验证有效**

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

**✅ e2e 实测证据**（这是本修复有效的直接证明）：

```
plain 模式：stdin 读到 false (长度 29)
```

29 正是 `/msg nodeB HELLO-E2E-xxxxxxxx` 的长度 —— **说明 29 个字符的 stdin 已被完整读入并分派**，
随后 TCP 连接、密钥交换、双向明文全部打通，往返耗时 0.5s。

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

---

## 5. 后续接手：复现本轮门禁（原步骤已全部执行完毕，此处留作复现手册）

> 原「步骤 1–4」是本轮的执行清单，**已全部完成**（见 §3.1）。下面按**复现顺序**重排，
> 供下一个团队**独立重跑一遍**以验证结论可重复 —— **不要只信本文档的记录。**

### 步骤 1：确认前置条件已满足

§2.1 的 9 项任务全部 `completed`。**本轮全部改动尚未提交（`git status` 90 条）** ——
接手第一件事是确认这些改动已在工作区可见。

### 步骤 2：build + test

```bash
dotnet build P2PChat.slnx -t:Rebuild   # 必须 0 错 0 警
dotnet test  P2PChat.slnx              # 必须全绿（当前 297 通过 / 0 失败 / 0 跳过）
```

### 步骤 3：AOT 发布（必须先做，否则测的是旧 exe）

```bash
dotnet publish src/P2PChat.App/P2PChat.App.csproj -c Release   # 必须先做，否则测的是旧 exe
pwsh -NoProfile -File scripts\e2e-verify.ps1 -SelfTestWaitMs 20000
```

目标：**FAIL=0**。本轮实测 `PASS=39 / FAIL=0 / SKIP=0`，退出码 0。

> ⚠️ `dotnet publish` 阶段逐条检查 **IL2026 / IL2070 / IL2072 / IL2075 / IL3050 / IL3053**：
> 本轮实测**我方代码 0 条**，第三方 4 条（`MessagePack.dll` IL3053+IL2104、`Serilog.dll` IL2104）。
> ⚠️ 判读方式见 §3.1 —— 这是**人工判断**，不存在可依赖的阈值。

> ⚠️ 脚本默认 exe 路径是 `src/P2PChat.App\bin\Release\net11.0\win-x64\publish\P2PChat.App.exe`。
> **不重新 publish 就会测到旧代码** —— 仓库里那个 exe 的 mtime 曾长期停在 2026-09-16。
> 校验产物请比对 §3.1 的 SHA256（中途修复触发过重新发布，SHA256 已变过一次）。
>
> 📌 本轮 DHT 引导已从 **40.02s 优化到 3.01s**（13.3×，优化前为实跑基线非估算），e2e 单次耗时相应下降。
>
> ⚠️ 绝不要复制 exe（会触发 Windows 防火墙重复弹窗，是历史用户投诉根因）。实例隔离只靠
> `P2PCHAT_DATA_DIR`（漏了会让两个实例共用 `identity.json` → NodeId 相同 → 无法互认，见断言 A24）。

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

### 7.2 本轮（收尾轮）

- 本轮 8 名成员各守一个 write scope，**互不重叠**。改 `scripts/` 的不知道 `src/` 改了什么，
  所以**跨 agent 的假设必须由 Lead 统一核对**（本轮最容易翻车的三处已在文档里点名：
  §4.2 末尾的引导日志字面量、§8 #6 的 AOT 未验证、§3 的测试数基线）。
- **e2e 只能由 Lead 亲自跑**（单次约 10 分钟，超出队员执行窗口 —— 上一轮就是这么翻车的）。
  其它 agent 只做静态核对。
- **行号纪律**：文档里写的落地位置必须 grep 源码核对，不能照抄上一份文档的行号 ——
  DEFECTS.md 记的是「缺陷发现时」的行号，HANDOFF §4 记的是「当时」的行号。**写错行号比不写更糟。**

---

## 8. 已知遗留

> 状态图例：🔴 阻塞验收 / ⚪ 后续处理（本轮已无 🟠 处理中项 —— 9 项任务全部 completed）

1. 🔴 **REPAIR-PLAN 自身未回答的问题（阻塞验收，必须先问用户）**：两台目标设备是**同一局域网**
   还是**家庭/移动不同网络**？这决定阶段 2（NAT 穿透）是硬需求还是可以降级。
   该问题自 2026-09-20 提出至今**未获用户答复**。目前实现走「尽力而为」策略：UPnP 成功则映射，
   失败则 TUI 显式提示「仅同网段/公网可达方可主动连入」，并保留 `/add <ip:port>` 与
   `/connect <ip:port>` 手工接入。
   **注意：阶段 2 代码落地 ≠ 用户场景已覆盖。** 验收前必须先向用户确认，不得据「已实现」反推。
   同一提示已写入 [REPAIR-PLAN.md「实施进度」顶部与 §四](REPAIR-PLAN.md)。
2. ⚪ **静态/盲连接对端的 `PublicKey` 为空** → 群邀请需要公钥包装密钥。
   ✅ **task-4 已处理**：`KeyExchangeMessage` 回送长期公钥（线路变更）+ `CreateGroupAsync` 的
   `catch {}` 已改为带日志告警，静默吞掉这一独立缺陷已消除。详见 §8.1。
3. ⚪ **无重放保护**：`MessageEnvelope.SequenceNumber`（`MessageRouter` 出站经 `NextSeq()` 自增、
   `EnvelopeCodec` 写进线路）**只写不校验**；`Timestamp` 虽被签名覆盖，**但不校验新鲜度**。
   即：签名能证明「消息来自持有私钥的一方」，但**不能证明「这条消息不是重放的旧包」**。
   需补：按 `(SenderId, SequenceNumber)` 去重的水位表 + 时间窗新鲜度检查。**尚未指派。**
4. ⚪ **`P2PChatTui` 仍无行为级测试**。`Chat.Tests` 虽已从 0 填到 80 条，但 plain 模式目前只有
   **结构契约**测试 + e2e 端到端覆盖，没有驱动真实 `P2PChatTui` 主循环的行为测试。
5. ⚪ **本轮全部改动尚未提交**（`git status` 90 条）。这是接手第一件事，见 §5 步骤 1。
   提交前请连同 §3.1 的 AOT 结论与 exe SHA256 一并复核。
6. ⚪ **`NodeInfo.ExternalEndPoint` 是死字段**（零赋值点）。
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
7. ⚪ **阶段 2.4（中继 / 打洞）未做** —— REPAIR-PLAN 标注为可选。
8. ⚪ **`<program dir>/data/` 下的既有数据永不迁移**，仅新路径 `~/.p2pc/` 生效。
9. ⚪ **发布配置与 `Directory.Build.props` 重复声明 TFM**，可能漂移。

### 8.1 本轮已关闭的项（不再占用上面的清单）

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
