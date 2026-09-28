# Agent Note: 数据目录迁移至用户主目录

Status: implemented

## Problem

程序把全部运行时数据写在 `<程序目录>/data/` 下，这带来两个已发生的故障：

- **只读目录下无法启动。** 程序若位于只读位置或需要管理员权限的路径（`Program Files`、受控共享目录），首次写入 `data/` 即失败，身份密钥无法生成。
- **覆盖发布即丢失身份。** 重新发布覆盖程序目录时，连带删除 `identity.json`、`session_keys.json`、`group_keys.json`、`contacts.json`。用户升级一次程序就换了一个节点身份，历史会话密钥与联系人一并消失。

同时 `P2PCHAT_DATA_DIR` 原本是**唯一**的目录定位手段：默认路径自身不可用，用户必须显式设置该变量才能正常使用。

## Decision

运行时数据统一存放在**用户主目录下的 `.p2pc/`**，与程序安装位置解耦。

- 默认路径：`%USERPROFILE%\.p2pc`（Windows）/ `$HOME/.p2pc`（Unix）。
- 主目录解析逐级回退：`USERPROFILE` → `HOME` → `LocalApplicationData`；三者皆空时抛出 `InvalidOperationException`，并在消息中提示改用 `P2PCHAT_DATA_DIR`。
- `P2PCHAT_DATA_DIR` 保留，但从「必需」降级为**可选覆盖**，用途收窄为同机多实例隔离。
- `DataPath.Root` 与 `SetRoot` 加锁（`_initLock` + 双重检查），消除并发首次访问的竞态；`SetRoot` 增加空值校验并对路径取全路径。
- 新增 `DataPath.GetDirectory(name)`（取子目录并按需创建）与 `DataPath.Reset()`（清空缓存，测试用）。
- 日志目录改用 `GetDirectory("logs")`，不再手工 `Path.Combine(Root, "logs")`。
- 启动日志与 `P2PCHAT_SELFTEST` 自检输出均打印数据目录，便于定位「数据究竟写到了哪里」。

实现位置：`src/P2PChat.Core/Extensions/DataPath.cs`。

## Data directory layout

| 文件 | 内容 |
|---|---|
| `identity.json` | 长期身份密钥对（ECDH P-256） |
| `session_keys.json` | 各对端会话密钥 |
| `group_keys.json` | 各群组密钥 |
| `contacts.json` | 联系人列表 |
| `peers.txt` | 已知节点（启动加载、退出保存） |
| `logs/p2pchat-YYYYMMDD.log` | 按日滚动的运行日志 |

配套改动：`.gitignore` 增加 `.p2pc/` 与 `contacts.json`；根 `README.md` 增补数据目录章节与文件清单；`scripts/config-probe.ps1`、`scripts/e2e-verify.ps1` 的说明与断言文案同步更新。

## Alternatives considered

**继续使用 `<程序目录>/data/`，把写入失败暴露给用户。** 否决：只读目录是常见部署形态，要求用户先把程序放到可写位置才能启动，等于把部署约束转嫁给使用者；且覆盖发布丢失身份密钥的问题依旧存在。

**以 `LocalApplicationData`（`%LOCALAPPDATA%`）作为默认位置。** 否决：路径更深、平台差异更大，且它绑定「应用」语义而非「用户」语义。`.p2pc` 放在主目录下更直观，用户手工备份与迁移更容易。该目录仍保留为最后一级兜底，而非默认值。

**保留 `P2PCHAT_DATA_DIR` 作为必需项。** 否决：这是「默认不可用」的延续——不设置变量就无法正常使用。降级为可选覆盖后，默认路径自身即可工作。

**不提供多实例隔离手段，让同机实例共享一份身份。** 否决：端到端测试需要同机拉起两个互为对等体的节点，共用 `identity.json` 会使两个节点 ID 相同、无法互相发现。`P2PCHAT_DATA_DIR` 因此保留。

## Consequences

- 程序可位于任意位置（含只读目录），重新发布不再影响用户身份与聊天数据。
- 同一用户的多份程序副本天然共享同一身份，符合「一个用户一个身份」的语义。
- **代价：既有数据不会自动迁移。** 旧位置 `<程序目录>/data/` 中的身份密钥、会话密钥、联系人不会被读取，用户升级后表现为「新身份 + 空联系人列表」。本变更未提供迁移路径；需保留旧身份时必须手工把旧 `data/` 内容复制到 `~/.p2pc/`。
- **代价：多实例必须显式设置 `P2PCHAT_DATA_DIR`。** 忘记设置的两个实例会共用同一份 `identity.json`，表现为节点 ID 相同、互不可见。该失败模式已写入 `scripts/e2e-verify.ps1` 的说明，并由断言 A24 覆盖。
- 数据目录由三级回退解析得出，「数据在哪」不再由单一环境变量决定，排查问题时以启动日志打印的路径为准。
