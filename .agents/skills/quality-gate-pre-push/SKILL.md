---
name: quality-gate-pre-push
description: 在推送 / force-push / 标记 ready-for-review / 声称检查通过之前，挑出覆盖本次 diff 的最小一组本地检查——不要无脑跑全套仓库套件。本 skill 是"挑哪些命令"的判断依据，不替代 pre-commit / pre-push hook 与 CI 的覆盖。
---

> **来源**：从 `deepseek-ai/deepseek-harness` 的 `.agents/skills/dsh-pre-push-checks/SKILL.md` 通用化而来。
> 这份 skill 是**指导**而不是逐字脚本。每条原则都要求你按本仓库的实际工具链替换 `<your ...>` 占位。

# 推送前的相关检查

每次推送 / 强制推送 / 标记 ready / 声称"检查已过"之前，**只**跑覆盖本次 diff 的最小一组检查。CI 拥有穷尽覆盖与平台矩阵；本地不重复 CI 的职责。本地只在"真的没有更窄的覆盖"或"用户在诊断 CI 失败"时跑全套。

## 第一步：确认范围

1. 确认当前 checkout、当前分支、与待推送的代码组：

   ```sh
   git status --short --branch
   git rev-parse --show-toplevel
   ```

2. 确认 PR / 堆栈的 live base（或最近的稳定父提交），按 base 检查**全部**外发的改动：

   ```sh
   <your scope reporter> --base <verified-base-ref>
   ```

   这个命令**永远不要猜 base**。`<your scope reporter>` 的版本化 JSON 记录相对 merge-base 的提交路径，并标记当前 worktree 的脏文件与未追踪文件。

## 第二步：挑相关证据

"全局本地基线"除 hook 之外不存在。**每次行为改动**都需要一个**最窄的、本来就会在它回归时失败的**测试或专项检查；只对 diff **真的**触及的面加更宽的检查。

当外发的改动新增 / 修改了拥有资源或异步的测试、fixture、helper、CI 执行路径，先用 [`skills/test-reliability/`](../test-reliability/SKILL.md) 判断是否需要**资源恢复 / 反例 / 静默释放 / 并发进程**证据。本 skill 仍然负责"挑哪些命令"，并避免重复已经通过的检查。

按改动面选择：

- **模块 / 脚本行为**：跑该模块测试文件 + 聚焦测试名。共享契约变更时加相邻模块的测试；不要为了 CI 的全局覆盖把本地也跑全套，除非改动**真的**跨模块或用户明确要求。
- **远端 mock 类型**：把"未构建的 `any`"当作显式本地兜底——它不是严格证据。远端 / mock 改动前必须先跑 `<your typecheck>`；重建过时或缺失的生成声明后再诊断剩余错误。例外写在测试夹具文档里，不写在生产远端类型、环境标志或抄过来的签名里。
- **文档 / ADR / 目录 / 文档链注释**：跑 `<your doc sync gate>`；文档流程要求时跑完整 lint。
- **模型 / 编辑器 / CLI / 终端可见输出**：跑该输出归属的聚焦 keyless snapshot 或可执行示例。
- **预期输出放置**：测试选取的"已记录会话"既作为回放输入也是期望持久输出，要放在顶层 `<your snapshot root>/`，`snapshot.yml` 标明该会话使用的 profile 与组合 / 头部固定。规范命名：父文件 `session[.vN].jsonl`、子文件 `session.<ordinal>[.vN].jsonl`，工具选最高 generation per role。没有那段会话回放的 ARIA / 几何 / 生成器 / CLI / 单元期望要放在测试文件旁的 `tests/expected/`；不要把它们放进 snapshot 根或给它们一个 `*.snapshot.ts` 的归属。
- **profile 与配置放置**：跨模块的 `<your CLI profile>` 行为放在 `<your profile test root>/`；模块特定的 Loader 组合放在该模块的 `tests/fixtures/`。面向用户的可选 overlay 放在 `<your overlay root>/`，并配一份 `<your user doc>/` 下的指南。
- **包清单 / 公开导出 / 构建配置 / worker / bin 入口 / 构建产物路径**：跑 `<your build>`、相关 hygiene 检查、归属的"构建产物 smoke"。
- **真实 provider / agent 行为**：凭证可用时跑相关 `<your real API test>`；**绝不打印密钥**。

**不要**因为"commit / push 紧跟着"就**重复**已经通过的检查。**特别地**，不要为了重复 pre-push hook 而在推送前再跑 typecheck。

把 Vitest / jest / go test 的文件 + 名过滤直接放在 `<your package manager>` 后面；不要插一个孤立的 `--`，它会绕过测试运行器的 `-t` 过滤。例如：

```sh
<your package manager> run test:snapshot <path/to/scenario>.snapshot.ts -t '<name>'
```

跑完任何聚焦 run 之前**先核对**实际选中的测试数量；如果数量对不上"聚焦"的期望，先把覆盖面补齐再下结论。

### 把单元覆盖聚焦到"本次影响的源码"

测试选择与覆盖选择是**两件**事。一个文件过滤决定"跑哪些测试"，仓库配置在其它维度上度量每一个 `src/**/*.ts`。单元覆盖相关时，**同时**点名"该跑的测试"与"它必须证到的源文件 / 模块"：

```sh
<your test runner> run <module>/tests/<behavior>.spec.ts \
  --coverage \
  --coverage.include='<module>/src/**/*.ts'
```

行为真的只在一个模块里时给精确路径；多文件 / 多模块时多次 `--coverage.include`，并把每个归属测试文件都传上。每文件 100% 阈值在所选源里照样适用。

"该跑哪些测试"不清楚时，用测试运行器的**依赖图**找到一个候选集，**先看一遍**选中测试再把它当证据：

```sh
<your test runner> related <module>/src/<changed>.ts \
  --run \
  --coverage \
  --coverage.include='<module>/src/<changed>.ts'
```

`<your test runner> related` **不能**找到只通过配置 / 动态加载 / 子进程 / worker / 构建产物 / 外部 provider 触达的行为；这些面要显式点名。**不要**用 `--passWithNoTests`、降覆盖率阈值、单独收紧 `--coverage.include` 来隐藏未覆盖的影响文件。

## 第三步：完整的本地排练

只在用户**明确要求**、正在诊断 CI 失败、或改动真的"仓库级别无可信更窄集合"时才跑完整的本地近似。**不要**重新造一个被废弃的聚合门；用当前 workflow 与 package 脚本做清单。

## 第四步：保护改写历史的推送

独立 PR 与堆叠 PR 的分支都允许 rebase（含 review 后）。**改写独立分支历史前**：fetch 当前远端分支、记下它**精确**的 OID；用 `--force-with-lease=<branch>:<observed-oid>` 发布，使并发更新让 push 中止。托管平台的 stack 工具给它们管理的分支提供 lease 保护。**永远不要**用裸 `--force`。

改写推送后，重新 fetch 当前的 head 并重审未解决的 review 评论、批准、可合并性与检查结果。**改写前**的 commit 哈希与内联评论锚点**不再是**当前证据。

### 托管平台 stack 同步后的验证

托管平台的 stack 同步 fetch、cascade rebase、push 是一组动作，**不能**在"改写"与"发布"之间插入本地校验。运行前要求一个干净的 worktree、记录官方 stack 顺序与精确的远端 head。返回后：

1. 重新查每一个分支 head 与官方 stack 顺序。
2. 检查每个被改写层相对它 live PR base 的变化范围。
3. 跑本 skill 挑出的相关证据**覆盖每一层**。
4. 让每个 PR 都保持未合并状态；直到所有选中的检查通过**才**报告"待合并"。

post-sync 证据失败时，让 lease 保护的发布 head 留在原位，修失败、验证修复、发布修正。不要因为 sync 命令成功就声称 stack 已经 ready。

## 失败处理

相关检查失败时**停下**修复或解释阻塞——**不要 push 然后希望 CI 不同**。post-sync 例外按上面修复流程办。

失败看上去与平台有关时**证明它**：

- 记下精确命令、失败的测试与平台特定不符。
- 确认相关的**非平台**证据通过。
- 跨平台非确定性"真需要 fix"时就修。
- 只有用户明确同意才绕过本地 hook；报告到底什么失败、为什么 CI 估计会不同。

## 推送流程

普通推送与独立 rebase 推送：

1. 跑一次选中的相关检查。
2. 正常 commit；pre-commit fixer 修改的文件要先检查再继续。
3. 正常 push（或用上面精确 lease 重写授权分支）让 incremental typecheck hook 跑。
4. 验证远端 ref 与本地 `HEAD` 一致：

   ```sh
   git rev-parse HEAD origin/$(git branch --show-current)
   ```

5. PR 上传后看 CI：

   ```sh
   <your platform CLI> pr checks
   ```

pending 检查就是 pending。看到失败先**审**失败再归因到分支或环境。

托管平台 stack 同步走上面那段验证流程；不要假装"普通顺序"可用。
