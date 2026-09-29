# Agent Note: plain 模式没有 stdin 输入通道

Status: implemented

## Problem

`P2PChatTui` 的线性（plain）模式只有输出侧、**没有输入侧**。`ReadKeyOrNull()` 的第一句就是 `if (_plainMode) return null;`，而主循环只在拿到按键时才把输入交给命令分派，于是 `_plainMode` 为真时**一切 stdin 都被静默丢弃**：程序照常启动、照常打印收到的消息、照常维持 DHT，但敲进去的每个字节都不产生任何动作 —— `/add`、`/connect`、`/msg`、`/group` 等全部命令都不可达。

这**不是脚本专用的问题**。`_plainMode` 有三条进入路径：

- `P2PCHAT_PLAIN=1` 环境变量（脚本与 CI 显式开启）；
- `RunAsync` 中设置 `Console.Title` / `Console.Clear()` 失败时的 `catch`；
- `ReadKeyOrNull` 的 `catch`，以及 `Render()` 捕获 `InvalidOperationException` 时的降级。

后两类是**自动降级**：任何控制台初始化或全屏渲染失败的用户都会掉进 plain 模式。因此**凡是 stdout 被重定向、在 CI 里跑、走管道的用户都中招** —— 管道只解决了「输出怎么取」，输入侧仍然是空的，节点变成只能收不能发的哑巴。这也是 `scripts/e2e-verify.ps1` Phase 2 的 A30/A31 必然失败的原因：脚本把 `/msg nodeB <明文>` 写进 nodeA2 的 stdin，nodeA2 从不读取，自然从未发出任何消息。

## Decision

plain 模式的输入改为**按行读取 stdin**，与它既有的逐行输出语义（`DrainInbox` 中的 `Console.WriteLine`）对称。具体分七步，全部落在 `src/P2PChat.UI/Views/P2PChatTui.cs`：

1. 新增 `using System.Threading.Channels;`，并新增字段 `private readonly Channel<string> _plainInput = Channel.CreateUnbounded<string>();`（附完整 XML 注释，说明为什么 plain 模式必须按行读、为什么放后台）。
2. `RunAsync` 中在 `_plainMode` 为真时启动后台循环 4：`_ = ReadPlainInputAsync(ct)`。
3. `ReadPlainInputAsync(CancellationToken)` 是这条通道的全部 IO：阻塞的 `Console.ReadLine()` 被包进 `Task.Run(...)`，因此不会占住主循环；读到的非空行 `WriteAsync` 进 `_plainInput`；读到 `null`（stdin 已 EOF，例如脚本注入完毕后关闭管道）即跳出循环，`finally` 中 `_plainInput.Writer.TryComplete()` 结束通道，让主循环的 `TryRead` 恒返回 `false` 而不空转。
4. 主循环的 plain 分支消费后台读到的整行：先幂等补启动（见第 7 步），再 `_plainInput.Reader.TryRead(out var line)`，取到即 `SubmitLine(line); continue;`。位置在按键分支之后、50ms 轮询之前。
5. 抽出 `SubmitLine(string)` 作为两条输入路径共用的分派：先 `Trim()`、空串直接返回；`/` 开头交给 `ProcessCommandAsync`，否则在已选中会话时走 `SendMessageAsync`、未选中则回一条系统消息。`SubmitInput()`（交互模式的 `Enter` 键）改为调用它，只保留 `_input` 的清空与重绘。命令语义因此在两条路径上完全一致。
6. **交互模式的 `ReadKey` 路径完全未改动**：`ReadKeyOrNull` 的 `Console.KeyAvailable` + `Console.ReadKey(intercept: true)`、`HandleKey` 的全部按键分支、`_input` 的逐字符编辑与 `Render` 的 `_dirty` 重绘，全部保持原样。
7. stdin 读取**惰性且幂等**地启动：新增 `EnsurePlainInputStarted(CancellationToken)`，内部以 `Interlocked.Exchange(ref _plainInputStarted, 1)` 做原子的「检查并置位」，`RunAsync` 开头与主循环 plain 分支各调一次，主循环里必须**排在 `TryRead` 之前**（先 `TryRead` 再补启动会漏掉启动瞬间已到达的行）。之所以必须惰性：`_plainMode` 的两条自动降级路径（`ReadKeyOrNull` 的 `catch`、`Render()` 的 `InvalidOperationException`）可能在**运行中途**才把它翻成 `true`；只在 `RunAsync` 开头判断一次，程序就会退回到「永远读不到按键、stdin 读取任务又没启动」的静默无输入，而且此时通道永不完成、主循环只会空转、连 EOF 都不会退出。**第一版修复正是这样写的，这道口子由本次修复自己暴露，并在同一次修复内补上** —— 只在启动时判断一次是不够的。

## Alternatives considered

**把 stdin 抽成 `IPlainInputSource` 抽象再测。** 否决：这是为可测性预付的税。修复真正需要被锁住的是「一行文本如何分派成命令或消息」，而这段逻辑已经通过抽出 `SubmitLine(string)` 变成了可测的；通道本身只有一句 `Console.ReadLine`，为它引入接口 + 一份必须同步维护的假实现，换回来的可测性对不上改动面。

**让 plain 模式改用 `Console.KeyAvailable` / `Console.ReadKey`（即不提前 `return null`）。** 否决：stdin 被重定向或处于非交互控制台时，`Console.ReadKey` 要么抛异常、要么阻塞等待一个永远不会到来的按键 —— 等于把「丢弃输入」换成「挂死整个进程」。而且逐字符按键依赖 `Enter`，脚本注入时需要额外约定按键编码。

**保持现状，只改 e2e 脚本**（让脚本绕过 TUI 直接调服务，或改用 `P2PCHAT_SELFTEST` 那种无输入用法）。否决：这是把产品缺陷留给用户。控制台初始化 / 渲染失败时的自动降级意味着真实用户同样无法发消息，脚本能绕过，用户不能。

**在主循环里同步调用 `Console.ReadLine()`。** 否决：`ReadLine` 是阻塞调用；只要 stdin 暂时不给换行符，整个循环就会连 `DrainInbox` 和 `Render` 一起停摆，对端消息不再显示。必须放到后台任务上，主循环只做非阻塞 `TryRead`。

**把整行 stdin 拆成模拟按键喂给 `HandleKey`。** 否决：会让输入语义分裂成「逐字符 + 特殊键」和「整行」两套，而 `HandleKey` 是交互模式的 UI 层；plain 模式能拿到的只有整行，没有 `ConsoleKeyInfo` 可构造。

## Consequences

- plain 模式下 `/add`、`/connect`、`/msg`、`/group` 等命令与交互模式等价可用，共用同一条 `SubmitLine` → `ProcessCommandAsync` / `SendMessageAsync` 分派。
- 空行被丢弃（投递前判断 `line.Length > 0`），不会误发空消息。
- stdin 关闭（EOF）后通道被完成，主循环只是回到 50ms 轮询，节点继续收消息直到被关停：既不自旋，也不自行退出。
- 输入侧与输出侧现在成对：plain 模式逐行 `Console.WriteLine` 出去、逐行 `Console.ReadLine` 进来。
- 交互模式行为不变，按键路径与 `_input` 编辑路径逐字未动；`SubmitInput` 改为委托给 `SubmitLine` 后，分派语义与改动前等价。
- **中途降级不再是死胡同。** `_plainMode` 在运行中途被 `ReadKeyOrNull` 的 `catch` 或 `Render()` 的降级翻成 `true` 时，主循环的 plain 分支会幂等地补启动 stdin 读取，因此不会退回修复前的静默无输入。这道缺口是本次修复自身暴露并在同一次修复内补上的，记录在此以免有人把启动判断「简化」回 `RunAsync` 开头的一次性判断。
- **代价：plain 模式是「行」语义。** 逐字符编辑、光标移动、`Tab` 切换会话只在交互模式可用；plain 模式下切换会话要靠 `/msg <节点ID>` 之类的命令。
- 新增依赖只有 BCL 的 `System.Threading.Channels`，不触及 AOT 信条：无反射、无 `MakeGenericType`、无新序列化类型、无需注册 formatter。

## Testing

- 端到端明文往返由 `scripts/e2e-verify.ps1` 的 **A30 / A31** 覆盖：A30 断言 nodeA2 经 stdin 注入的明文出现在对端 nodeB2 的日志中，A31 断言它出现在 nodeB2 的 StdOut + Log 合计输出里；A32 反向断言发送端自身不回显该明文。
- 进程内回归守卫由 `tests/P2PChat.Integration.Tests/PlainModeInputTests.cs` 补齐，覆盖本次修复真正改动的分派逻辑。它的形态需要说明：`P2PChat.UI` 没有测试工程引用，`SubmitLine` / `ReadPlainInputAsync` 又都是 `private`，要在进程内直接调用就得改产品代码或引入反射（两者都被明确排除），因此这条守卫采用的是**对产品源码做结构契约断言**，而不是在进程内跑真实 stdin。
- 脚本 Phase 2 目前仍以逐字符 + 30ms 间隔写 stdin。输入现已按行读取，逐字符写入依然正确（`Console.ReadLine` 会一直等到换行符才返回），只是这个节拍已无必要。

## Deferred

- 没有引入 `IPlainInputSource` 抽象；只有当需要注入假 stdin 做更细粒度的进程内测试时才值得补。
- e2e 脚本 Phase 2 的逐字符写入节拍可以简化成一次整行写入（由脚本改动单独跟进）。

## Related

- [Direct connect via an explicit endpoint](../feature/2026-09-20-static-peer-explicit-endpoint.md) —— 在 plain 模式下，`/add <nodeId> <ip:port>` 才是非交互搭线的入口。
- [/connect <ip:port> — blind connect](../feature/2026-09-21-blind-connect.md) —— 同一条「命令由分派执行」的路径，只是入口不同。
