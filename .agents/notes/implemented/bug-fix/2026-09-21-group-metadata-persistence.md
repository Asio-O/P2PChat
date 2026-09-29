# Agent Note: Group metadata must be persisted

Status: implemented

## Problem

`GroupChatService` keeps `ConcurrentDictionary<string, GroupInfo> _groups` in memory only — there is no file I/O inside the class. The group **key** already goes through `FileBackedKeyStore` and lands in `group_keys.json` (via `SetGroupKey` / `RemoveGroupKey` → `_saveLock` → `File.WriteAllText`), but the group **metadata** — GroupId, GroupName, CreatorId, MemberIds, CreatedAt — is never persisted.

Reproduced: start the process, run `/group create <name> <members...>`, then `/group list` shows the group. Restart the process. `/group list` is empty, and `/group send` throws `群组不存在: <id>` — even though `group_keys.json` still holds the key.

Net effect:
- Every joined group disappears across a restart; the user has to be re-invited.
- A persistent resource gap appears between `group_keys.json` (which is written) and `groups.json` (which doesn't exist): the system either has the decryption capability with no ownership info, or ownership with no decryption.

## Decision

- New `Core/Abstractions/IGroupMetadataStore.cs`: minimal contract `LoadAll() : IReadOnlyList<GroupInfo>`, `Save(IEnumerable<GroupInfo> groups)`, `Remove(string groupId)`. Rationale — `GroupInfo` carries the plaintext group key; persistence belongs in the same layer that owns the key (`Crypto`), so `Chat` doesn't have to reach into `Crypto` path details.
- New `P2PChat.Crypto/Keys/FileBackedGroupMetadataStore.cs`: implements `IGroupMetadataStore` against `DataPath.GetPath("groups.json")` (= `~/.p2pc/groups.json`), matching `FileBackedKeyStore`'s location convention. Serialization goes through `JsonContext` (source-generated, no reflection).
- `StoredGroup` (`StoredModels.cs`) is now `{ GroupId, GroupName, CreatorIdHex, MemberIdsHexList, GroupKeyBase64, CreatedAt }`; all `byte[]` fields are encoded as hex strings (consistent with `NodeId.ToHexString`); the group key is encoded as base64.
- `GroupChatService` constructor takes `IGroupMetadataStore` and an optional `ILogger<GroupChatService>`; on startup, `LoadPersistedGroups()` reads every persisted group into the dictionary and back-fills the plaintext key into `keyStore` **only when the key is missing** (to avoid clobbering a newer session key). `CreateGroupAsync` and `HandleInviteAsync` finish by calling `PersistGroups()`, which writes the full set. `HandleNotifyAsync` on `dissolve` removes the entry from `groups.json` before removing it from the in-memory dictionary.
- DI registration: `ChatServiceCollectionExtensions.AddP2PChatChat` and `App/Program.cs` both register `AddSingleton<IGroupMetadataStore, FileBackedGroupMetadataStore>()`.
- Corrupt or partial entries (`StoredGroup` fields that don't decode) are dropped with a warning log, matching the `FileBackedKeyStore.LoadAll` tolerance convention.

## Alternatives considered

**Add `GetKnownGroups` / `SetGroup` / `RemoveGroup` to `IKeyStore`.** Rejected: `IKeyStore`'s contract is "keys"; `GroupInfo` carries name, members, timestamps, etc. Mixing metadata persistence into the key store crosses an abstraction boundary and entangles future work like key rotation with metadata migration. Two interfaces, neither depending on the other, gives the cleanest seam.
**Persist via MessagePack instead of `JsonContext`.** Rejected: every other file under `DataPath` (`identity.json`, `session_keys.json`, `group_keys.json`, `contacts.json`) uses `JsonContext`. Mixing in MessagePack would break the data-directory format consistency.
**Persist only name + members, drop the key, force re-invite on restart.** Rejected: this is a serious UX regression — the key is already on disk, throwing it away means walking back a working flow.
**Merge `groups.json` into `group_keys.json`.** Rejected: the current `group_keys.json` is a flat `Dictionary<string,string>` (groupId → base64 key). Merging forces a format change for that file and breaks every existing user. Keeping metadata in its own file leaves existing data untouched.
**Add `JsonPropertyName` to `byte[]` fields on `GroupInfo` so `System.Text.Json` round-trips them as base64.** Rejected: AOT source-gen handles `byte[]` less observably (it'd route the whole array through base64, conflicting with the "fixed-size / hex-encoded" `NodeId` style already in the codebase). Hex strings are readable, debuggable, and consistent with `NodeId.ToHexString`.

## Consequences

- After creating a group, `~/.p2pc/groups.json` exists and contains the group's GroupId, GroupName, CreatorId, MemberIds, and GroupKey. After process restart, `GetKnownGroups()` returns the previously-created groups and `/group send <id> <text>` no longer throws "群组不存在".
- On `dissolve`, both the in-memory dictionary and the on-disk entry are cleared; the corresponding `keyStore` entry is removed too.
- Persistence writes only on change (`CreateGroupAsync`, `HandleInviteAsync`, `dissolve`), mirroring the change-triggered write pattern in `FileBackedKeyStore`.
- AOT safe: `JsonContext` source generation covers `StoredGroup` and `List<StoredGroup>`; no reflection.
- `dotnet build` 0 errors 0 warnings; `dotnet test` all green (baseline + 5 new guards):
  - `创建群组_必须把元数据写入groups_json磁盘文件`
  - `接收群邀请_必须把群组持久化`
  - `进程重启_新GroupChatService必须从磁盘恢复群组` (restart simulation + `SendGroupMessageAsync` does not throw)
  - `处理解散通知_必须从groups_json移除该群组`
  - `FileBackedGroupMetadataStore_往返持久化字段一致` (field-level round-trip)
- **New interface alongside `IKeyStore`.** Future maintainers must remember "group keys live in `IKeyStore`, group metadata lives in `IGroupMetadataStore`". `Agent.md §3.3` `GroupChatService` row already mentions both; any future group-related persistence should keep both paths separate.
- **Tolerance for a corrupt `groups.json`.** `FileBackedGroupMetadataStore` swallows load failures the same way `FileBackedKeyStore.LoadAll` does — `catch` empty, leaving the in-memory dictionary empty on startup. Users recover by being re-invited. Matches existing project convention.
- **Plaintext group key on disk.** Out of scope for this stage; mirrors the existing `group_keys.json`. The `ContactService.cs:13` "stores under %APPDATA%" doc-drift comment is left to a later cleanup.
- **Verification:** the five new guards above pin the four paths (write / load / dissolve / field round-trip); `Agent.md §3.3` `CreateGroupAsync` / `HandleInviteAsync` / `HandleNotifyAsync` descriptions are updated in lockstep.