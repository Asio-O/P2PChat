# AGENTS.md — 决策记录（ADR）总览

> **来源**：从 `deepseek-ai/deepseek-harness` 的 `.agents/notes/AGENTS.md`、`.agents/notes/implemented/AGENTS.md`、`.agents/notes/archived/AGENTS.md` 通用化合并而来。

ADR（Agent Note / Architecture Decision Record）是 agent 写的 RFC：持久的提案与决策记录，保留理由、备选、后果与需要持续验证的契约。遵循仓库 [`docs/AGENTS.md`](../docs/AGENTS.md) 的写作标准与 [`notes/README.md`](README.md) 的 ADR 规则。

## 添加新 ADR 时触发替代检查

每新增一个 ADR 都要在**活动树里**搜覆盖同一个决策或机制的老化记录，按 [`skills/archive-decisions/`](../skills/archive-decisions/SKILL.md) 分类**完整或部分替代**，并把合格的 `implemented/` 三件套在同一次 PR 里归档。**部分替代**保留活动并交叉链接。

## 三种 lifecycle 目录

- **`proposed/`** —— 未落地的提案。规格文档形态，可以是将来时。
- **`implemented/`** —— 已落地的决策。**现在时**描述已发布现实——与代码同步，按下面规则。
- **`rejected/`** —— 提案被否决。只在它能挡出诱人错误时保留，否则三件套删除。
- **`archived/`** —— 已落地但低未来价值的 ADR；冻结历史快照，**永不**作为现行权威（详见 [`archived/AGENTS.md`](archived/AGENTS.md)）。

## 保持 `implemented/` ADR 与代码同步

路径、符号、默认值与机制在改变它们的同一次提交里同步更新；**就地**重写过期事实，**不要**追加变更历史。

当已落地的 ADR 不太可能再指导未来工作时，把它完整三件套归档到 [`archived/`](archived/AGENTS.md)（走 [`skills/archive-decisions/`](../skills/archive-decisions/SKILL.md)），而不是继续维护。

### 这不是"重写决策本身"的许可

就地更新**事实实现**。**翻转决策或理由**要求新 ADR 并交叉链接；旧 ADR 仅当按 ADR 规则里的"合并规则"被完整吸收时才可删除。

## 归档是冻结

[`archived/`](archived/AGENTS.md) 里的三件套是冻结的历史快照。**永不**编辑、重格式化、翻译、修复、删除或移动密封的制品；新决策与新事实用活动 ADR 或当前文档。

归档提交只允许：移动完整 en/zh/sidecar 三件套；在两份语言文件的 `Status: implemented` 行下面插入相同的 `Archived: YYYY-MM-DD` 行；重新记录 sidecar；修复或删除入站链接。**不要**检查、验证或修复从归档 ADR 出站的链接。

跑 [`skills/archive-decisions/`](../skills/archive-decisions/SKILL.md) 工作流，用 `<your archive verifier>` 写入新制品哈希。常规验证器拒绝改动 / 缺失的密封制品、不完整三件套、未知类目录、invalid 的归档元数据。
