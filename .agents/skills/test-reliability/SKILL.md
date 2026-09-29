---
name: test-reliability
description: 设计、审查、诊断在 CI 并发、共享主机资源、时钟、进程全局状态、子进程、网络监听器或异步 teardown 下**可能**非确定性失败的测试与 fixture。**新增 / 修改**这类测试、调查 flaky CI、审查隔离性时用；挑"推送前跑哪些"则单独用 [`skills/quality-gate-pre-push/`](../quality-gate-pre-push/SKILL.md)。
---

> **来源**：从 `deepseek-ai/deepseek-harness` 的 `.agents/skills/dsh-ci-test-reliability/SKILL.md` 通用化而来。

# 可靠的 CI 测试

让测试在仓库真实 CI 拓扑下也正确，**不**只在安静单机单独跑。本 skill 拥有"隔离 + 可靠性"判断；它**不**替代测试层级策略，也不替你挑每一次推送的命令。

## 读归属规则

- 用仓库测试策略文件挑单元 / 覆盖 / 期望输出 / snapshot / 浏览器 / 真实 API 证据。
- 用仓库防御模式文件约束生命周期、子进程、取消、teardown 行为。
- 读生效的测试运行器配置与 CI workflow——它们的 worker / job 拓扑会影响测试。
- 已记录会话场景还要读仓库的 snapshot 指令。
- 测试设计**确定**后，用 [`skills/quality-gate-pre-push/`](../quality-gate-pre-push/SKILL.md) 挑外发的命令。

## 建模执行拓扑

除非当前活动配置证明可以，**默认假设**这些层会重叠：

1. 同一测试文件内的测试。
2. 不同测试文件或 worker 进程。
3. 同一 job 内独立的测试运行器或仓库门进程。
4. 共享同一主机的不同 CI job。

进程隔离**不**隔离主机端口、可预期文件系统路径、外部服务、数据库、socket、继承的子进程。每个获取的资源**显式识别**：所有者、原子分配机制、可观察就绪信号、注册清理、定时完成信号。

不要因为一个 fixture 没隔离就把整套序列化；先收紧互斥范围或换资源分配。一个串行测试块**保护不了**另一个文件 / 进程 / job / runner 抢占的主机资源。

## 原子分配资源

用资源所有者的分配器——不是先检查可用再 claim。

- 网络 fixture 用 `listen(0)` 绑 loopback，**仅在 server 报告 listening 后**读分配的地址。**永远不要**先扫空闲端口再绑。
- 每个测试用 `mkdtemp` 创建私有临时根；不要抢可预期的共享路径。
- 给共享数据库、socket、会话、输出位置**每个测试独立的命名空间**。
- 路径必须不存在时用独占创建。
- 把稳定记录的 id 与临时传输地址分开；fixture 内部翻译，**不要**让活的资源用记录值。

只作为解析输入 / 期望值的"字面路径 / URL"**不是**获取的资源——不要因为它们看起来固定就重写。

## 收拢进程全局状态

把 `process.env`、`cwd`、fake timer、locale / timezone、注册表、console hook、`globalThis`、全局 `fetch` 拦截当**独占可变资源**。

优先用注入依赖或实例本地 adapter。需要改时：

- 记录原始值是缺失还是**存在**；
- 还原那个**确切**状态；
- **立即**注册还原；
- 用 `try/finally` 包最小的 mutation 范围；
- 在本地 `finally` 之前失败可信时保留 `afterEach` fallback；
- 拦截 fixture 拥有的、最窄的精确请求。

## 尊重平台运行时语义

CI 在 Windows 与 POSIX 主机上跑同一套件；OS 拥有的值**不一定**按测试写入的样子回来。

- 只有当断言容忍回写失败时，"写回一个值"才安全。把文件 `mtime` 还原以证明"指纹仍然让它失效"——到处都对；为了证明"记录仍然有效"还原 `mtime`假定无损往返，NTFS 的 100ns tick 不会给亚毫秒。断言依赖还原时，从**新的读**拿期望值，而不是从记忆值。
- Windows 大小写不敏感地区分环境变量名，fixture 把 `http_proxy` 与 `HTTP_PROXY` 当两个 key 种在 Windows 上是一个。
- Windows 异步释放文件句柄；POSIX 主机"立等可取"的 rename / 删除在 Windows 上**要**有界 retry，次数要按观察到的争用设定。
- Windows 没有 POSIX 权限或信号语义。依赖它的用例要**显式**在该平台 skip 并说明理由，而不是让断言无谓地变弱。

优先选"每个平台都成立"的观察。真有不能跨平台的用例**显式**在该平台排除。

## 给超时编预算

`describe` / `case` 超时**覆盖**测试运行器的 `--testTimeout`——不向它让步。低于 lane 预算的值让 CI 已经给的额度被压低；同一个字面在 default 较小的主机上读作放宽。被进程创建锁住的 suite 占 lane 预算；一个更紧的值要说明它为什么更紧。

抬 hook 预算时跟测试预算一起抬。setup 与 teardown 同样吃争用，只抬 case 预算把争用失败挪进 `afterEach`。

超时本身是 subject 时，外等待要远比被测超时大。证明"20ms deadline 触发"的用例不能跟 harness 自己的等待赛跑，否则看哪个 deadline 先报。

## 按状态同步

固定 sleep **不是**"setup 完成"或"清理 settle"的证据。

- 等显式就绪事件、握手、状态迁移、归属 promise、外部可观察条件。
- 用 deferred promise 或 barrier 把 race 放到确定性点，再证明相关操作真的重叠。
- 超时只用来给等待设上限，**不是**让断言成立的条件。
- 不要断言调度器相关顺序，除非它是被测产品行为。
- 时间本身就是 subject 时，注入或伪造时钟并**总是**还原真 timer。

## 释放到定时停止

获取资源后**立即**注册清理，让断言失败也能释放。清理：阻止新回调 / 请求、脱 detach、恢复全局 hook、终止 owned 工作、await server close、close、worker 终止或等价完成信号。

调用 `abort()` / `close()` / `kill()` **不**等归属完成信号就是**不完整** teardown。late completion 可信时，证明处置确实能阻止它变更别的测试。

## 证明预期回归

- 可行时**先观察**一次普通回归在修复前失败。
- 新的静态 / 语料守卫，临时引入被拒案例并观察预期失败。
- race 用 barrier 证明重叠；单凭"跑多次"不是 race 测试。
- 端口 / socket / 共享路径 / 子进程 / 其他主机资源——当跨进程隔离是修复的一部分时，并发跑独立测试进程。
- fixture 自己的 deadline 起 spawn 时，先断言"没有信号 / 超时杀掉子进程"，再断言退出码——这样被杀子进程报"超时"而不是"状态不匹配"。
- 校验**外部**状态 / 事件 / 文件 / 日志 / 退出 / 处置，不相信组件自报告。

压力运行**补充**确定性回归；**不替代**。

## 拒绝"用噪声掩盖 flake"

下列不是"修复"确定性本地测试：

- 不找等待状态就加超时；
- 加 retry；
- 把全部文件串行；
- 吞错或 unhandled rejection；
- 弱化断言；
- 把不稳定行为规整掉；
- 在 cleanup / 断言前加 sleep。

文档化的"瞬态外部 provider 测试"按真实 API 策略仍允许 retry；把这个例外留在外部边界。

恢复预算**不**是掩盖。把 suite 抬回它已经有的 lane 预算，或把有界 retry 调成"实际在 runner 上测到的争用"——命名被等待的工作并把 lane 已经给的额度还回去；**既不**发明"空 headroom"也不留未检查的等待。

## 诊断现有 flake

现有的概率性 CI 失败读 [`references/ci-flake-diagnosis.md`](references/ci-flake-diagnosis.md)。**只**诊断的请求保持只读——用户没要 fix 时只报告原因 + 证据。

## 校验 + 报告

跑对受影响行为**最小聚焦**的回归。仅在改动**确实承担**该风险时加拓扑专属证据：

- 全局 mutation 要恢复证据；
- 生命周期 / 子进程工作要静默释放证据；
- 端口 / socket / 共享路径要并发独立进程证据；
- 新守卫要反例。

推送前用 [`skills/quality-gate-pre-push/`](../quality-gate-pre-push/SKILL.md)。报告**精确**命令与结果；**不要**把 retry / skip / pending CI 描述为通过。
