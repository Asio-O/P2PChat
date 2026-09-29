# 决策记录（ADR）— Agent Notes

> **来源**：从 `deepseek-ai/deepseek-harness` 的 `.agents/notes/README.md` 通用化而来。
> ADR = Architecture Decision Record，记录"为什么"和"放弃了什么"。代码与现有文档之外**没说的不算持久决策**。

## 命名与目录

每个 ADR 文件名同时编码两个维度——**路径形式** `{lifecycle}/{class}/yyyy-mm-dd-topic-title.md`：

- **Lifecycle**（顶层目录）= ADR 的当前状态；状态改变时把文件移到对应目录：
  - **`proposed/`** —— 提案，已评审但未实施（可能部分实施）。
  - **`implemented/`** —— 决策已落地。文件记录"我们决定什么、否决了什么"；**与代码同步**：当代码后续移动文件、重命名包、改变默认值 / 机制时，ADR 在同一次提交里更新（只更新**事实**——路径、名字、结构——不更新**决策本身**）。
  - **`rejected/`** —— 提案被否决。**只在它的理由还能挡出某个诱人错误时保留**；否则三件套（en/zh/sidecar）一起删除。
- **Class**（嵌套目录）= 决策的**类别**，见下文 [Classification](#classification)。

文件名里的日期是话题**首次提出**的日期（按 git 历史）。ADR 之间的相互引用用相对 Markdown 链接（`[topic](../../implemented/architecture/2026-…-….md)`）——不是裸散文或编号——这样机器可校验，且在目录间移动时链接依然对。

活动 lifecycle 树就是工作清单：浏览 lifecycle/class 子目录或在仓库里搜即可。**不要**加中心化的 `INDEX.md`；"no-index ADR"规则拥有的就是这个理由。低未来价值的 `implemented/` 记录会转入 [`archived/`](archived/AGENTS.md) 树，作为冻结的历史快照保留。

## 分类

每个 ADR 属于一个由 `[scripts/your_lifecycle_classes.ts]`（或等价清单）定义的封闭集合中的某一类；分类门拒绝其他目录。要新增一类，必须同步更新清单与本节。

| Class | 覆盖范围 |
|---|---|
| `feature` | 新增用户或模型可见能力 |
| `bug-fix` | 修复缺陷，或填补 postmortem 暴露的缺口 |
| `simplification` | 删除代码、行为或表面，不新增能力 |
| `architecture` | 关于**已发布源码**的结构决策——模块如何关联、运行时词汇 |
| `process` | **围绕**代码的工具 / 策略 / 工作流——门、包管理器、版本锁定——不是运行时行为 |
| `testing` | 测试基础设施与策略 |

`architecture` / `process` 的边界：**架构**讲我们发布的源码；**process** 是围绕源码的工具与流程。`refactor` 被刻意省略——它与 `simplification` 重叠，后者的判别标准"可观行为是否改变？"已涵盖。

## 归档与删除

当已实施的 ADR 的落地决策已经**完整**且**理由不太可能再指导未来工作时**，归档它。**当它的备选、归属边界、负面保证、持久或网络语义、安全规则或重新引入条件仍然有用时**，让它继续留在 `implemented/`。**永不归档** `proposed/`：要拒绝就拒绝；被否决且不再防止诱人错误时，删除三件套（en/zh/sidecar）。用 [`skills/archive-decisions/`](../skills/archive-decisions/SKILL.md) 而不是字数 / 年龄 / 配额做判断。

归档路径编码为 `archived/{class}/yyyy-mm-dd-topic-title.md`；归档树刻意**不**含 `implemented/`，因为只有 `implemented/` 才能进入。归档提交只移动完整 en/zh/sidecar 三件套、保留 `Status: implemented`、在两侧 `Status` 行下面插入相同的 `Archived: YYYY-MM-DD`、重新记录 sidecar 哈希、修复或删除入站链接——这是**唯一允许的内容修改**。

一旦归档完成，**永久冻结**。不再编辑、翻译、重格式化、更新、移动或删除它，也不再把它当作当前行为的权威。文档门跳过归档源（含其出站链接）；活动散文仍可链接到归档源——当且仅当它**有意**引用历史。`<your archive verifier>` 强制封闭的类树、完整三件套、归档元数据、sidecar 哈希、append-only 冻结内容清单。

## 何时写一个

只在**同一次提交里**为**持久的决策理由**添加或更新 ADR——这些理由是代码、测试和现有文档都解释不了的。**对未来重大工作的提案**从 `proposed/` 开始；**已经做出的决定**从 `implemented/` 开始。挑匹配的 class（见 [Classification](#classification)）。

更新已经"拥有"该决策的 ADR 满足本规则；不要重复创建。机械性或局部改动（包括局部 UI 表现与交互改动）**免于本规则**。**永远不要把一个 ADR 改成"不同的决策"**——用一个新 ADR 替代它，并交叉链接二者，除非按下面规则把旧 ADR 完全吸收。

一个被完全替代的 `implemented/` ADR 可以被合并进当前主 ADR 并删除。删除前，**当前主**必须保留每一个**独特**的理由、备选、后果、需要的验证与命名的覆盖差距；修复每一个入站链接；**同一次提交**删除中文副本与一致性记录。**部分替代不算**：保留两份并交叉链接，并更新所有仍然成立的事实。合并不得把旧文件改写成相反方向，也不得只靠 git 历史当作理由的唯一副本。

一个"添加又删除"特性的 ADR 可以合并进后来的删除 ADR——**仅当**该特性已**不在**生产代码、配置、schema、持久或网络格式、迁移与兼容行为里；**没有任何**当前文档呈现它可用；**没有任何**测试把它当作受支持行为来跑。删除理由与验证"确实不在"的测试可以保留。删除方必须保留：原本为什么存在、现在为什么不再合理、备选的不完整删除路径、放弃的能力、重新引入的条件、完整不在性的证据。仅验证被删除行为的旧测试与实现机制**不是**当前验证证据。仅删一个传输 / 默认 / 实现 / 呈现是**部分**替代——任何存活的持久数据或兼容处理都使它成为部分替代。

## 文件格式

每个活动 ADR 遵循同一份内部格式，`<your ADR format gate>` 强制（`scripts/verify-adr-format.ts`，`<your doc sync gate>` 的一部分）；格式的理由与被否决的备选见 `implemented/process/<date>-uniform-format.md`。归档 ADR 保留归档时的格式 + 上面的归档日期行。

### 头部块

每个 ADR 的前三行**严格**是：

```markdown
# Agent Note: <title>

Status: <status>
```

后面跟一个空行。`Status:` 值是下面三种之一，且**必须**与文件所在的 lifecycle 目录一致——门会交叉校验：

- `Status: proposed`
- `Status: implemented`
- `Status: rejected — <reason, one line>`

`Status:` 行**不带日期、不带括号**——文件名带首次提出日期，其他都在 git 里。"以修正形式接受"是 body 内容（在决策被陈述的地方写明修正）。**否决**理由是 `Status:` 中唯一带内容的形式，因为读者读否决 ADR 就是来读那个 verdict。

### 正文骨架

每个 ADR 用 `## Problem` 开头——动机，**脱离解决方案也能读懂**。后续内容取决于 lifecycle：

#### `proposed/`

```markdown
## Problem
## Proposal
…bespoke sections…
## Alternatives considered
## Acceptance criteria
## Risks
```

`## Proposal` 描述"将要做什么"，可以合法地用将来时——计划、迁移步骤、未决问题在工作时归这里。`## Acceptance criteria` 说明"完成"的可观察状态。`## Risks` 既包括可能出错的事，也包括决定**主动放弃**的事。

#### `implemented/`

```markdown
## Problem
## Decision
…bespoke sections…
## Alternatives considered
## Consequences
```

`## Decision` 用**现在时**描述已落地现实；整份文件按 `implemented/AGENTS.md` 与之同步。`## Consequences` 记录 trade-off 的**代价与收益**。提案时代的标题在这里就是 spec-speak，门会拒绝——`## Proposal`、`## Plan`、`## Migration plan`、`## Acceptance criteria` 不得出现在已实施的 ADR 里。允许 `## Testing`、`## Deferred`、`## Related` 描述**现在时事实**。

#### `rejected/`

被否决的 ADR 就是冻结的提案：保留它在被否决时的所有小节（包括 `## Acceptance criteria` / `## Plan`），判决写在 `Status:` 行上。仅头部块、`## Problem` 开场、`## Proposal` 节与下面的 Alternatives considered 强制要求适用。

### 备选方案 —— 必填

每个 ADR 都有 `## Alternatives considered`：每一个**真正被考虑过的**备选与它输在哪——一段以加粗领头的段落，或一个 `### Why not <X>?` 子节。没有"打过谁"的决策会被重新讨论——这就是 ADR 要阻止的失败。

备选**被记录，不被编造**。格式之前的 ADR（在你的 ADR 格式统一日期之前）若无法从记录重建备选，**该节**用以下注释代替，门只为这种"前格式" ADR 接受：

```markdown
<!-- adr-format: alternatives-not-recorded (pre-format ADR) -->
```

### 在 lifecycle 之间移动

在 lifecycle 目录间移动 = **同一次**更新 `Status:` 行 + 重新满足目标目录的骨架——否则门失败。具体地，`proposed/` → `implemented/` 把 `## Proposal` 改写成现在时的 `## Decision`，把 `## Acceptance criteria` 与 `## Risks` 折进 `## Consequences`（或折进现在时的 `## Testing` / `## Verification` 节来描述现在钉死行为的东西），用"已落地"取代"将要做什么"——`implemented/AGENTS.md` 要求的重写被机械化。`proposed/` → `rejected/` 只在 `Status:` 行加理由，冻结文件。

### 双语副本（如果有）

`<filename>.zh.md` 镜像英文版的结构（一节对一节），遵循你的 i18n 契约；机器检查的头部标记（`# Agent Note: ` 与 `Status:` 行）保留英文原文。格式门跳过 `.zh.md` 文件——配对门检查一致性。
