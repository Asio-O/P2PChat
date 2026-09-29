# AGENTS.md — 仓库全局 standing orders

> **来源**：从 `deepseek-ai/deepseek-harness` 仓库的根 `AGENTS.md` 通用化而来。
> 所有"`<your ...>`"形式的占位符需要按本仓库的实际工具链替换。

这一组规则是**在仓库里做任何工作时都需要先知道的**。每条规则尽量做到**自包含**——它本身就能告诉你"应当怎么做"，并链接到详细文档（如果有）。

## 工作流与证据

1. **先验证再下结论**。每条判定都要有可执行的证据：跑过的命令、读到的代码、查到的字段默认值、引用过的决策记录。**只读不看就下结论**比**不读**更危险。
2. **推送前只跑相关检查**，不要无脑跑全套。完整套件和平台矩阵交给 CI；当 PR 的覆盖面真的"全仓库"或用户在诊断 CI 失败时再本地跑全套。
3. **变更与文档同步**。配置、默认值、错误、事件、公开行为变更时，相关 `README` 与 JSDoc/契约注释在同一次提交里更新。
4. **PR / Issue 用托管平台提供的官方 PR 堆栈**合并；不要手动重新 rebase + 重新设置 base 来伪造堆栈。
5. **GUI 改动必须附演示录像**。任何对终端用户可见的 GUI 改动都要附一段来自该 PR 真实构建的演示录像（参见 `skills/ui-demo-recording/`）。

## 决策与归档

6. **创建决策记录（ADR）只为持久决策的"为什么"**。机械性、局部、不跨范式的改动直接改即可。归档后的 ADR 是**冻结的历史快照**，不再修改、不再作为现行权威。
7. **写明"放弃的能力"**。每个决定都要在 ADR 里写明我们**主动放弃**了什么、未来什么条件下可以重新引入。
8. **写明替代方案**。决策记录必须有一节"**备选方案**"，写出真正被考虑过的备选和它输在哪里——否则下次同样的方案还会被重新提出。

## 散文与契约

9. **散文描述契约，不描述转录**。注释、JSDoc、文档、prompt、可见字符串描述**该处的契约**——行为、失败、归属、时序、副作用——不描述作者当时怎么得出这个结论的（详见 `skills/prose-standard/`、`skills/trim-cot-leakage/`）。
10. **散文写作前先辨认代词**：用 `contract`、`boundary`、`surface` 这类词前，先问是否有更精确的术语（`response fields`、`JSON validation`、`ESM exports`）。仅在确实描述一个被广泛承认的契约/边界时保留它们。
11. **空 `catch` 必须命名错误并说明原因**；`try` 保持单语句。
12. **公共导出必须有简洁的 JSDoc**，描述返回值区分、抛出、副作用、归属、时序、取消语义、持久化语义。

## 代码组织

13. **能力缝隙（capability seam）三角色**：Service Definition / Service Provider / Consumer 缺一不可；只有真正独立演化时才拆。
14. **优先选维护中的依赖而不是手写**。如果一个维护良好的外部库或当前引擎内置能删除你的实现 + 测试，那就用（详见 `skills/find-simplifications/`）。
15. **对称优于非对称**。平行的值如果出现非对称的写法，往往意味着漏了一个抽取。
16. **配置错误要在最早可解析点显式失败**。可自包含则在加载时失败；否则在最早能解析的步骤失败。永不静默跳过缺失的引用、缺失的文件、缺失的配置——"看起来能跑、运行时找不到"是排障最贵的失败模式。
17. **对不可见边界的 ID 要打品牌**（branded types）。同一进程静态类型信任；解析器 / 队列 / 模型与工具 JSON / 持久化与文件 / worker、进程 / 跨网络边界必须做运行时校验。
    不要在静态接口已经要求类型的位置加运行时校验、回退行为或敌对输入测试——那是把编译器的工件当运行时工件处理。品牌类型 + 显式 schema 校验才是跨界面的正确做法。
18. **配置即契约**：可部署性相关的选择必须是显式 `Config` 字段，能从配置文件改。固定的协议常量、外部规范、安全不变量保持常量。
19. **类型 vs 实现不要混在一起**：源码平面 / 构建产物平面不可混。TS 类项目里，路径别名指向 `src`，构建产物必须独立验证。
20. **禁止新增 `as unknown` / `<unknown>` 断言**。需要"穿过类型系统"就找出正确的类型、加真正的运行时校验、或引入受控 schema——而不是把 `T` 砸成 `unknown` 再 `as` 回去。已经存在的 baseline 只允许持平或减少，不允许扩张。
21. **TODO 标记按语义分级**：`FIXME`（需要尽快修）>`TODO`（应该修）>`XXX`（可疑 / 调研）。不要发明新的标记。
22. **文件末尾必须有且只有一个换行符**；用 `<your pre-commit whitespace gate>` 强制。

## 测试与可靠性

23. **测试描述行为，不描述正确性**。行为被废弃时，**连同测试一起改**，并在 PR 中说明为什么。
24. **资源、异步、跨平台测试要按可靠性原则写**。端口、临时目录、子进程、全局状态、定时器不能假定"单机跑就好"（详见 `skills/test-reliability/`）。
25. **别把 flake 掩耳盗铃**。加超时、加 retry、让全部串行、吃掉错误、弱化断言——这些都不是修复，只是把不确定性藏起来。

## 安全与隐私

26. **不要把凭证 / PII 提交进仓库**。不要把它们写进 benchmark、prompt、fixture、日志、截图、PR 描述。
27. **真实 API / 真实服务跑测试**时按"测试密钥管理策略"处理：CI 没密钥就跳过、有密钥的人类协作者本地跑；密钥不打印、不写到报告里。

## 编辑本规范

29. 编辑本规范时**让规则自包含**——能让 agent 在没有上下文的情况下执行；长篇细节放链接文档。
30. 修改本规范必须附带简短的"为什么这样改"的 commit 消息；
32. 本规范为仓库**唯一权威**；其他规范以本文件为唯一来源；如需在不同位置编辑，请保留软链接 / 副本并在原文件加 `This file is auto-generated; edit the real one at …` 的注释。

## 详细规范

- 文档结构 / Markdown 写作 → [`docs/AGENTS.md`](docs/AGENTS.md)
- 模块 / 子目录补充规则 → [`modules/AGENTS.md`](modules/AGENTS.md)
- 决策记录（ADR）格式与生命周期 → [`notes/README.md`](notes/README.md)
- 推送前只跑相关检查 → [`skills/quality-gate-pre-push/SKILL.md`](skills/quality-gate-pre-push/SKILL.md)
- 代码审查标准动作 → [`skills/code-review/SKILL.md`](skills/code-review/SKILL.md)
- 散文写作标准 → [`skills/prose-standard/SKILL.md`](skills/prose-standard/SKILL.md)
- 修剪思维链泄漏 → [`skills/trim-cot-leakage/SKILL.md`](skills/trim-cot-leakage/SKILL.md)
- 测试可靠性 → [`skills/test-reliability/SKILL.md`](skills/test-reliability/SKILL.md)
- 寻找可拆除的过时能力 → [`skills/find-simplifications/SKILL.md`](skills/find-simplifications/SKILL.md)
- 归档决策记录 → [`skills/archive-decisions/SKILL.md`](skills/archive-decisions/SKILL.md)
- UI 演示录制 → [`skills/ui-demo-recording/SKILL.md`](skills/ui-demo-recording/SKILL.md)
- Agent 工具 / 上下文设计 → [`skills/agent-experience/SKILL.md`](skills/agent-experience/SKILL.md)
- 性能优化的证据驱动流程 → [`skills/evidence-driven-perf/SKILL.md`](skills/evidence-driven-perf/SKILL.md)
