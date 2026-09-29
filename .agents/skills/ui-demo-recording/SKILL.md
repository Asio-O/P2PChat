---
name: ui-demo-recording
description: 把浏览器 / Web UI 交互演示录成经过优化的截图。本文用现成的浏览器控制工作流、可选的高帧率录制 API、确定性编码，最后把截图附在 PR 里——并在没有 attach 通道时回退到独立的 assets 分支。被要求"做 / 录 / 生成 GIF"或每个改了终端用户可见 GUI 行为的 PR 都必须包含一段来自该 PR 真实服务器 + 真实模型流程的截图。
---

> **来源**：从 `deepseek-ai/deepseek-harness` 的 `.agents/skills/record-browser-gif/SKILL.md` 通用化而来。
> 通用化时移除了所有 DSH 私有的发布命令（`gh --attach`、assets branch 推送工作流）——本 skill 只剩**录制**原则；把截图放进 PR 是托管平台 / 发布工具的事。

# 录制 UI 演示

把一段短而真实的 UI 演示产出为本地截图（必要时附加 GIF / 视频）。**任务包含把它放进 PR** 时再走 PR 发布流程。**现成的浏览器控制工作流**优先；用平台的高帧率录制 API（带宽动态 / Playwright Video 等）当工作流支持；用附带的编码器做裁剪 / 播放速度 / 末尾停留 / 尺寸 / 大小。

![Presenter 在 PR 里讲什么](<路径/to/demo.gif>)

## 每个 GUI PR 都带截图

改了终端用户可见 GUI 行为的 PR **必须**包含一段用本 skill 录的演示截图，嵌进 PR 描述（PR 发布细节见你托管平台或发布工具的等价工作流）。

**录制本身是证据的一部分**：用**该 PR 分支构建出的真实服务器**、真实 API key、真实模型回合跑出来的。**永远不要**用 fixture query、mock transport、合成事件注入或测试专用 hook 顶替——除非用户明确要求 fixture 录制。截图旁边陈述：被演示的精确 commit SHA、跑它的 tree 与 origin、任何 mode flag / 浏览器状态例外、是否跑了真实模型 round——让 reviewer 一眼看清"这张录像到底证明了什么"。

## 录制与发布分开

- **录制**产出本地视频 / 截图与**一份** `.gif` 制品——**永远不改远程状态**。
- **发布**（把截图放进 PR 描述，或在没 attach 通道时推到独立 assets 分支嵌入 URL）是单独的末步，**只在任务包含发布**时执行。它**永不**触碰 PR 自己的分支。
- 保留请求的录制条件。真实服务器 / 真实 API 演示**不要**用 fixture query、mock transport、合成事件注入或测试 hook。凭证或 server 不可用时**报告限制**而不是给替代 input。
- **永远不要**读 / 暴露凭证值。用应用的常规配置路径 + 一段良性演示 prompt。

## 准备应用

针对每个 PR 的截图演示**该 PR 的 tree**：

1. 要求干净 worktree，记下它的精确 commit (`git rev-parse HEAD`)，再 build 这个被记录的 tree（如 `<your build> && <your web build>`）。对另一个 commit 的 build 录的图把证据挂错了地方。
2. 从那个 tree 用**新的** scratch home / workspace / 会话状态启一个 server 给每个端口；给浏览器一个**新的隔离 context 或 profile**；浏览器工作流不能创建时，在导航前清掉该 origin 的 cookie 与 site storage，避免持久客户端状态污染证据。凭证走应用的常规路径读根 .env，绝不回显 key。
3. 一个 storyboard = 一次证据 run：每帧都来自同一个 server + 同一组 state root、workspace、session 与模型支持场景。capture 自动化失败时**丢掉该次帧并从新 root 重跑**——**永远不要**把不同 run 的帧拼起来。
4. 切换 PR 时按 PID 或精确的命令行匹配停旧 server；宽泛的 `pkill -f` 模式可能误杀启动它的 shell——包括你自己的。

## 录流程

跟"可用的浏览器控制工作流"的 setup / interaction / cleanup 走。它暴露 `recordVideo` 时，在同一个 controlled context 上启用 video 以捕获更多中间帧。否则把浏览器自带的截图 API 接进同一脚本，按"截图捕获"一节的目录 / 时长约定走（见下）；video 可用与否**不**决定用哪个浏览器控制工作流。已有用户浏览器状态仍是显式隔离例外。

浏览器控制不可用**才**用仓库声明的浏览器依赖（Playwright / Cypress / Selenium 等）跑隔离的 headless 浏览器，并在 capture notes 里声明该 fallback。在你的仓库里它在 `<your web app package>/package.json` 解析；**不要**安装额外的 driver 或开用户浏览器。

录制前识别 origin、build 还是 dev server、transport、任何 mode override。production default 开了一个 automation 驱动不了的 native surface 时，选一个官方"浏览器可操作 production backend"通过常规应用配置切换，**披露**该 override。

把脚本、原始视频、时序 notes、QA 帧、GIF 放在仓库 `.gitignore` 的录制目录（如 `.playwright-mcp/`）。**先建**该 run 目录。

### 录制视频

`viewport` 与 `recordVideo.size` **显式**匹配：否则录制库会把视频缩到 800×800，UI 文本可能不可读。

视频通过所选浏览器控制工作流配置。独立的 Playwright fallback：

```js
const { chromium } = createRequire(join(repo, '<your web app package>/package.json'))('<your browser automation>')
const browser = await chromium.launch()
const size = { width: 1440, height: 900 }
const context = await browser.newContext({
  viewport: size,
  recordVideo: { dir: join(runDir, 'videos'), size },
})
try {
  const page = await context.newPage()
  const video = page.video()
  // 在这里 navigate 并触发真实应用。
  await context.close()
  await video.saveAs(join(runDir, 'demo.webm'))
} finally {
  await context.close()
  await browser.close()
}
```

`createRequire` 来自 `node:module`，`join` 来自 `node:path`；`repo` 与新的 `runDir` 在录制脚本里设绝对路径。close page 之前**保留** page 的 video handle。先 `await context.close()` 再 `video.saveAs()` 或编码；只 close browser 不保证 video flush。每个 page 有自己的 video：显式选被演示的 page，**不要**拼无关 page 或 run。**失败**的 run 只用作诊断。

选一个 3–6 个有意义状态的短故事。状态稳定前等**唯一**语义 locator；accessible-name 相等用 `exact: true`，不能匹配 prompt echo 的精确文本完成 predicate。固定 wait 可用作"状态已验证"后的"阅读停留"，**绝不**作为就绪证据。录视频时保留动画与滚动。

演示一个 tool call / rejection / recovery 时，把它的 detail 或 trajectory 展开让 video 显示 tool 身份、状态或稳定错误码、下游结果。瞬态运行态要紧时，prompt 一段慢前台操作并观察其具体 DOM marker；连续视频捕获它的中间帧。给模型一个短最终 sentinel 锚定完成。被演示状态已可见后停止不必要的长真实 API run。

**不要**捕获密钥、个人数据、无关 tab 或通知。浏览器视频含 page 内容不含浏览器 chrome；避免在 app 里渲染带凭证的 URL。复盘整段被选区间，包括中间状态。**整个过程保持同一 viewport**。

## 编码 GIF

需要 `python3`、`ffmpeg`、`ffprobe`。缺一个媒体 binary 时**报告**该依赖，不要未经授权装软件。`GIF_SKILL_DIR` 单设一行导出；行内赋值在同条命令里**影响不到**参数展开。

```sh
export GIF_SKILL_DIR=/absolute/path/to/this/skill
python3 "$GIF_SKILL_DIR/scripts/encode_gif.py" \
  /absolute/path/to/demo.webm \
  /absolute/path/to/demo.gif \
  --start 2 --end 32 --speed 2 --final-hold 3 \
  --fps 10 --max-width 1200 --colors 128
```

`--start` / `--end` 选一个**连续**的源区间（秒）。默认保留全视频 1× 速度并加 2 秒末尾停留。`--speed` 改播放速度；速度 + 所选区间在 GIF 旁披露——demo 不能暗示测得的反应时长。用**观察到**的视频时间，不是猜的墙钟偏移，并保留被演示行为的完整 cause 与 outcome。最终停留重复最后一帧。`--fps` 设编码 GIF 帧率；提它恢复不了源录制没拍到的动作。保留原始 WebM 用作 QA；不要拼不同 run 或合成缺失状态。

编码器探 WebM container 时长，在 palette 转换前应用 trim + speed，并校验编码后时长、动画、宽度、字节大小。它**拒绝**空或越界区间、不到两帧的选择、模式不适用的 flag、误覆盖。GIF 太大先降 `--max-width`，再降 `--colors` 或 `--fps`；保留可读文本。**确认**精确输出路径后用 `--force`。

### 截图捕获

连续视频不可用或用户要 storyboard 时，按可用的浏览器控制工作流。**一次**隔离 run 捕获 3–6 个验证过的状态（用浏览器的 screenshot API）。把返回的字节**直接**写到一个 run 目录里作 `00-initial.png`、`01-typed.png` 等；用相同的尺寸与裁剪。瞬态状态在同一个 browser-script 调用里 poll 它的 DOM marker 再 capture。

```sh
python3 "$GIF_SKILL_DIR/scripts/encode_gif.py" \
  /absolute/path/to/frames /absolute/path/to/demo.gif \
  --durations 1.5,1.5,1.5,3.5 --fps 10 --max-width 1200 --colors 128
```

一份时长对所有截图生效；否则每张截图给一个正时长，把 settled state 留最久。目录输入拒绝少于两帧、尺寸不一致、时长数不匹配。视频时长 flag **仅对视频**生效；`--durations` 与 `--pattern` **仅对截图目录**生效。

## 验证制品

1. 读编码器的 JSON 摘要——确认输出路径、源区间 + 速度（或截图数）、编码帧数、尺寸、时长、字节大小。
2. **看**编码后 GIF 本身，不仅看源帧。确认转场可读、终态停留够长、无敏感内容。若 viewer 只渲染第一帧，用 `ffmpeg` 从编码后 GIF 解出代表帧来审；预编码截图证明不了编码后顺序、调色板或末停留。
3. `git status --short` 确认原始视频、QA 帧、制品**只**落在 `.gitignore` 路径下。
4. 返回 GIF 绝对路径，当客户端支持本地媒体时**渲染**它，并说明录制用了真实 API / fixture / 其他 transport。**任务不包含把 GIF 放进 PR 时停在这里**。

编码器维护：装好媒体前置后跑 `python3 -m unittest discover -s "$GIF_SKILL_DIR/scripts" -p 'test_*.py' -v`。这些本地媒体测试**不**进仓库 CI。

## 发布截图（PR 流程）

**任务包含把 GIF 放进 PR 时**做这步。

> 通用化原则：发布细节（commit-attached 视频 vs assets branch vs PR description attach）**由你的托管平台 / 发布工具决定**。本节给出通用原则；具体命令请参照你的工具。

**通用原则**：

1. **永不**把媒体 commit 进 PR 的 branch 或任何合到长期 branch 的 branch——binary media commit 进那里会让未来每次 clone 都胖。优先用托管平台提供的 PR 描述 attach 通道（不安装播放器、不污染任何分支）一次上传 + 一次改写引用。
2. attach 之前**重读**被演示 PR 的 live head（对一个新演示 PR 是 pushed branch tip），对照记录在 GIF 旁的 commit。它**移动过**就**重录**。
3. attach 后**重读** live head，要求它**仍是**记录的 commit。**重读** live body 确认引用**现在**指向上传的 URL；通过托管平台的 Markdown API 渲染 body，确认期望的 `<img>`；上传 URL 抓一次确认 `200` 与正确 `image/gif`。
4. attach **不可用**时（媒体超限、CLI 版本不够、仓库不在支持的托管平台），回退到独立的 assets branch：媒体只在那条 orphan branch 上、append-only、绝不 force-push 或删除——merged PR body 永远引用它的 URL。

attach 通道与 assets-branch 工作流的精确命令请按你的托管平台 / 工具文档操作。
