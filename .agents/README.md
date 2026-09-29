# 通用 Agent 约束与技能集（.agents）

这一组文件是从 [deepseek-ai/deepseek-harness](https://github.com/deepseek-ai/deepseek-harness) 仓库的 `.agents/` 目录抽取出来的"约束性"内容，经过**通用化**之后适用于任何类型的项目。

它要解决的不是"如何写代码"——它是给 agent（人类或 AI 助手）用的**写作 / 修改 / 审查 / 测试 / 归档 / 演示**规范。本质上是一套"在仓库里工作时应当遵守的工程守则"。

## 适用场景

- 你希望 AI agent 在你的仓库里工作时遵循一致、可被验证的标准。
- 你希望"决策记录（ADR/RFC）"、"散文质量"、"测试隔离"、"代码审查"、"推送前证据"等高频但容易走样的环节被显式约束。
- 你愿意把它当作**起点**而不是**教条**——根据你的语言、框架、CI 实际可执行性再做裁剪。

## 目录结构

```
.agents/
├── README.md                       # 本文件
├── AGENTS.md                       # 顶层 standing orders：仓库全局规范
├── docs/
│   └── AGENTS.md                   # 文档结构 / Markdown 规范 / 字数预算
├── modules/
│   └── AGENTS.md                   # 模块 / 子目录级别的补充规则（默认未启用时启动，从其他位置覆盖）
├── notes/
│   ├── AGENTS.md                   # 决策记录生命周期总览
│   └── README.md                   # 决策记录（ADR）的格式、分类、归档规则
└── skills/
    ├── quality-gate-pre-push/      # 推送前只跑相关检查（不无脑跑全套）
    ├── code-review/                # 审查 PR 时的标准动作
    ├── prose-standard/             # 散文（注释 / 文档 / JSDoc）写作标准
    ├── trim-cot-leakage/           # 修剪"思维链泄漏"（站在 commit 时刻之外才看得懂的引用）
    ├── test-reliability/           # 测试的并发、平台、资源隔离可靠性
    ├── find-simplifications/       # 寻找可以拆除 / 合并 / 降级的过时能力
    ├── archive-decisions/          # 把低未来价值的 ADR 转入历史归档
    └── ui-demo-recording/          # GUI 改动必须附演示录像
```

## 通用化原则

从 DSH 抽取时做了如下改造：

1. **删除专有词**：所有 `dsh`、`@deepseek-ai/dsh-*`、`Cordis`、`Session`、`SCHEMA_VERSION`、`packages/<group>/<pkg>` 等 DSH 内部概念改为通用占位（如 `<your binary>`、`<your plugin framework>`、`<your modules root>`）。
2. **删除具体脚本**：把 `pnpm run change-scope`、`pnpm run verify-doc-budgets`、`pnpm run verify-agent-note-format` 等具体命令替换成 `<your scope reporter>`、`<your budget gate>`、`<your format gate>` 等占位，由你按本仓库的工具链映射。
3. **删除平台耦合**：`gh stack` 这种 GitHub-only 的能力在文档中改为"使用托管平台提供的官方 PR stack"这一原则性表述，而不是逐字复刻 `gh` 命令。
4. **保留全部行为约束**：所有"应当如何写"、"如何审查"、"如何归档"、"如何隔离测试"、"如何修剪思维链泄漏"的**判断标准**与**取舍哲学**完整保留——这些是真正的工程约束。
5. **保留门槛与反例**：每条规则都尽量配上"什么不算这条规则的兑现"的反例，便于日后自检。

## 怎么接入到自己的仓库

1. 把整个 `.agents/` 目录复制到你的仓库根目录（或者你已经使用 AGENTS.md 体系的工具期望的位置）。
2. 让你的 agent 工作流（Claude Code / Codex / DSH / Cursor 等）能够发现它——多数 agent 会自动加载 `AGENTS.md` 或 `.agents/`。
3. 按以下顺序**最小化裁剪**：
   1. 读 `AGENTS.md`，按你的语言/框架/构建工具替换 `<your ...>` 占位。
   2. 如果你**不**需要决策记录（ADR）系统，删掉 `notes/` 和 `skills/archive-decisions/`、`skills/find-simplifications/`，但**保留**它们可以让你受益——ADR 是这套规范里性价比最高的部分。
   3. 如果你不做 GUI，删掉 `skills/ui-demo-recording/`。
   4. 如果你只跑单元测试，删掉 `skills/test-reliability/` 中关于子进程 / 端口 / 平台差异的小节，注明"工作流"的删除。
4. 在 `AGENTS.md` 里**保留显式允许的例外**：每条规则都附"什么情况下可以不遵守"，比"绝对命令"更可执行。

## 与 CLAUDE.md / AGENTS.md 体系的兼容性

很多 agent 工具同时识别以下入口：

- `AGENTS.md`（仓库根或子目录）
- `CLAUDE.md`（Claude Code）
- `.cursor/rules`
- `.cursorrules`
- `.github/copilot-instructions.md`

本目录默认走 `AGENTS.md` 体系；如果你使用的是其他体系，可以**软链接**或**复制**对应文件：

```sh
# 让 Claude Code 也读到根 AGENTS.md
ln -s AGENTS.md CLAUDE.md
```

## 与 deepseek-harness 原文的对应

| 通用化文件 | 原文位置 |
|---|---|
| `AGENTS.md` | `AGENTS.md` + `docs/AGENTS.md` + `packages/AGENTS.md` 合并精简；按上游新增规则择要补全（misconfig 显式失败 / 跨界校验 / 禁 `as unknown`） |
| `docs/AGENTS.md` | `docs/AGENTS.md` |
| `modules/AGENTS.md` | `packages/AGENTS.md` |
| `notes/README.md` + `notes/AGENTS.md` | `.agents/notes/README.md` + `.agents/notes/{implemented,archived}/AGENTS.md` |
| `skills/quality-gate-pre-push` | `.agents/skills/dsh-pre-push-checks` |
| `skills/code-review` | `.agents/skills/dsh-code-review` |
| `skills/prose-standard` | `.agents/skills/dsh-prose-standard` |
| `skills/trim-cot-leakage` | `.agents/skills/dsh-trim-cot-leakage`（清掉上游 `references/` 链接，原则就地内化） |
| `skills/test-reliability` | `.agents/skills/dsh-ci-test-reliability`（自带通用化的 `references/ci-flake-diagnosis.md`） |
| `skills/find-simplifications` | `.agents/skills/dsh-find-simplifications`（保留长篇版；结构差异留待单独一轮整） |
| `skills/archive-decisions` | `.agents/skills/dsh-archive-agent-notes`（新增 `Implemented — delete` 分类） |
| `skills/ui-demo-recording` | `.agents/skills/record-browser-gif`（去掉 GH attach、PR 流程部分） |
| `skills/agent-experience` | `.agents/skills/agent-experience`（纯原则版，无 DSH 耦合） |
| `skills/evidence-driven-perf` | `.agents/skills/dsh-speed-up-perf`（仅原则骨架；具体 benchmarks/、Node/browser lane 由下游细则化） |

上游有但本交付件**不**携带的：

- `skills/dsh-doc`：与 README `kind` 系统、`docs/i18n/` 强耦合，超出通用化范围。
- `skills/dsh-merging-stacked-prs`：GitHub-only `gh stack`；原则已在 `AGENTS.md` 规则 4 表达。
- `skills/dsh-translate-docs`：`disable-model-invocation: true`，依赖 DSH i18n 管线。
- 所有 `references/`、`templates/`、`scripts/` 附件——下游仓库按各自文档工具链引入。

每个文件的开头都会注明"源自 `…AGENTS.md`/`<…SKILL.md>`，由 `…` 通用化"。修改前请阅读原文以核对是否漏掉了有用的细节。

## 许可证

本目录是从 [deepseek-harness](https://github.com/deepseek-ai/deepseek-harness) 抽取并通用化的内容。请遵循上游仓库的许可证（见原仓库 `LICENSE`）。
