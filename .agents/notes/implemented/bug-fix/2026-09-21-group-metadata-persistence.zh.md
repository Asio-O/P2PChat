# Agent Note: 群组元数据必须持久化

Status: implemented

## Problem

`GroupChatService` 在内存里维护 `ConcurrentDictionary<string, GroupInfo> _groups`，类内无任何文件 I/O。群密钥本身已经走 `FileBackedKeyStore` 的 `group_keys.json` 持久化（`SetGroupKey`/`RemoveGroupKey` → `_saveLock` → `File.WriteAllText`），但群组**元数据**——GroupId / GroupName / CreatorId / MemberIds / CreatedAt——不落盘。

实测复现：进程启动后执行 `/group create <name> <成员...>`，再 `/group list` 能看到该群；然后重启进程，再 `/group list` 为空，再 `/group send` 直接抛 `群组不存在: <id>`，即便群密钥本身仍在 `group_keys.json` 里完好保存。

净效果：
- 任何**已加入的**群聊在重启后立刻看不见、不能发——用户必须重新邀请。
- `group_keys.json` 与不存在的 `groups.json` 出现资源长期缺口：拥有解密能力却没有归属信息；或反过来，拥有归属信息却没有解密能力（虽然密钥已经在写，但元数据没在写）。

## Decision

- 新增 `Core/Abstractions/IGroupMetadataStore.cs`：最小契约 `LoadAll() : IReadOnlyList<GroupInfo>`、`Save(IEnumerable<GroupInfo> groups)`、`Remove(string groupId)`；理由——`GroupInfo` 含明文群密钥，应在「密钥所在的层」持久化，避免 `Chat` 反向依赖 `Crypto` 文件路径细节。
- 新增 `P2PChat.Crypto/Keys/FileBackedGroupMetadataStore.cs`：实现 `IGroupMetadataStore`，文件路径 `DataPath.GetPath("groups.json")`（即 `~/.p2pc/groups.json`），与 `FileBackedKeyStore` 同源同约定；序列化走 `JsonContext`（`System.Text.Json` 源生成器）。
- `StoredGroup`（在 `StoredModels.cs`）字段定型为 `GroupId` / `GroupName` / `CreatorIdHex` / `MemberIdsHexList` / `GroupKeyBase64` / `CreatedAt`；`byte[]` 一律走 hex 字符串（与 `NodeId.ToHexString` 风格一致），群密钥走 base64。
- `GroupChatService` 构造函数新增 `IGroupMetadataStore` 与可选 `ILogger<GroupChatService>`；启动时 `LoadPersistedGroups()` 从 store 加载所有群组到 `_groups`，仅当 `keyStore.GetGroupKey(...)` 缺失时才用元数据里的明文密钥回填（避免覆盖更新的会话密钥）。`CreateGroupAsync` 与 `HandleInviteAsync` 完成后调用 `PersistGroups()` 全量落盘；`HandleNotifyAsync` 处理 `dissolve` 时先 `_metadataStore.Remove(...)` 再 `_groups.TryRemove(...)`。
- DI：`ChatServiceCollectionExtensions.AddP2PChatChat` 与 `App/Program.cs` 都注册 `AddSingleton<IGroupMetadataStore, FileBackedGroupMetadataStore>()`。
- 加载失败与 JSON 字段异常走「跳过坏条目 + 警告日志」的容错约定，与 `FileBackedKeyStore.LoadAll` 一致。

## Alternatives considered

**在 `IKeyStore` 上加 `GetKnownGroups/SetGroup/RemoveGroup` 方法。** 否决：`IKeyStore` 的语义是「密钥」，`GroupInfo` 含群名、成员、创建时间等非密钥元数据；让它承担元数据持久化把抽象越界，未来「密钥轮换」「群元数据迁移」会纠缠。分两个接口，互不依赖，未来重构余地更大。
**用 `MessagePack` 而不是 `JsonContext` 持久化。** 否决：群密钥、群元数据在 `DataPath` 体系下其他文件（`identity.json`、`session_keys.json`、`group_keys.json`、`contacts.json`）全部走 `JsonContext`；混进 `MessagePack` 路径会破坏数据目录的格式一致性。
**不持久化群密钥，只持久化名 + 成员；启动后要求用户重新邀请。** 否决：用户体验严重倒退；密钥本身已经在落盘，丢弃这部分等于把已经走对的流程回退。
**把 `groups.json` 合并到 `group_keys.json`。** 否决：现有 `group_keys.json` 是一张「群组ID → base64 群密钥」扁平字典（`Dictionary<string,string>`）；合并需要重写该文件格式，让所有现存用户都要迁移。把元数据放进独立的 `groups.json` 文件，老数据原地不动。
**在 `GroupInfo` 加 `JsonPropertyName` 让 `System.Text.Json` 直接序列化 `byte[]`（base64）。** 否决：AOT 源生成器对 `byte[]` 二进制字段的可观测性更差（编进解析器会把全数组当成 base64，与 `NodeId` 已知大小/hex 字段风格冲突）；用 hex 字符串更可读、可调试、与 `NodeId.ToHexString` 风格一致。

## Consequences

- 进程创建群组后，`~/.p2pc/groups.json` 即存在且含 GroupId / GroupName / CreatorId / MemberIds / GroupKey；重启后 `GetKnownGroups()` 返回之前的群组，`/group send <id> <text>` 不再抛「群组不存在」。
- 处理 `dissolve` 通知时，`groups.json` 中该群组的条目被移除，不残留死记录；`keyStore` 中对应群密钥也同步清除。
- 持久化只在变更点落盘（`CreateGroupAsync` / `HandleInviteAsync` / `dissolve`），与 `FileBackedKeyStore` 同样的「变更触发」写文件模式。
- AOT 安全：`JsonContext` 源生成包含 `StoredGroup` 与 `List<StoredGroup>`；无反射。
- `dotnet build` 0 错 0 警；`dotnet test` 全绿（基线 + 新增 5 条守卫）：
  - `创建群组_必须把元数据写入groups_json磁盘文件`
  - `接收群邀请_必须把群组持久化`
  - `进程重启_新GroupChatService必须从磁盘恢复群组`（重启模拟 + SendGroupMessage 不抛）
  - `处理解散通知_必须从groups_json移除该群组`
  - `FileBackedGroupMetadataStore_往返持久化字段一致`（字段层回归）
- **新增接口与现有 `IKeyStore` 并列**——后续维护需要记住「群密钥走 `IKeyStore`、群元数据走 `IGroupMetadataStore`」。`Agent.md §3.3` 的 `GroupChatService` 行已补充说明；后续若新增相关持久化需求应继续走这两条独立路径。
- **JSON 文件损坏容忍度：** `FileBackedGroupMetadataStore` 加载失败时与 `FileBackedKeyStore.LoadAll` 保持一致——`catch` 静默吞，启动后群组为空（用户重新邀请恢复）。
- **群密钥明文存储：** 本阶段不解决；与现状 `group_keys.json` 一致。`ContactService.cs:13` 的「持久化 %APPDATA%」注释漂移留待其他阶段清理。
- **Verification：** 上述 5 条守卫钉死了「落盘 / 加载 / dissolve / 字段一致」四条路径；`Agent.md §3.3` 关于 `CreateGroupAsync` / `HandleInviteAsync` / `HandleNotifyAsync` 的描述同步改写。