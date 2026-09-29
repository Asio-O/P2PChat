# Agent Note: Group messages must travel AES-256-GCM encrypted

Status: implemented

## Problem

`GroupChatService.SendGroupMessageAsync` (`src/P2PChat.Chat/Services/GroupChatService.cs:140-178`) wrote plaintext straight into `Content` when constructing a `TextMessage`:

```csharp
var message = new TextMessage
{
    SenderId = senderId,
    ConversationId = groupId,
    Content = text,    // plaintext
    IsGroup = true
};
```

The receive-side `GroupMessageHandler.HandleAsync` (`src/P2PChat.Chat/Handlers/GroupMessageHandler.cs`) treated it as plaintext the same way:

```csharp
var chatEvent = new ChatMessageEvent
{
    Content = message.Content,    // treated as plaintext
    ...
};
```

The group key (`keyStore.GetGroupKey(message.ConversationId)`) was only used for a presence check; it never participated in encryption or decryption. The consequences:

- The group-key distribution path (`EncryptGroupKeyForMember` → `GroupInviteMessage.EncryptedGroupKey`) is correctly AES-256-GCM.
- The group messages themselves are **plaintext end-to-end**. The README's "end-to-end encryption" claim is **false for group chats**, and that is a security defect (B5).
- Group messages are not signed; anyone who knows a GroupId can inject forgeries into the group.

## Decision

- **Sender** `GroupChatService.SendGroupMessageAsync`:
  ```csharp
  var groupKey = _keyStore.GetGroupKey(groupId)
      ?? throw new InvalidOperationException("群组密钥缺失: " + groupId);
  var ciphertext = _encryption.Encrypt(System.Text.Encoding.UTF8.GetBytes(text), groupKey);

  var message = new TextMessage {
      SenderId = senderId,
      ConversationId = groupId,
      Content = Convert.ToBase64String(ciphertext),   // 12B nonce + ct + 16B tag
      IsGroup = true
  };
  ```
  Reuses the existing `IEncryptionService.Encrypt` (AES-256-GCM, output layout `[nonce 12B][ct][tag 16B]`), matching the same wire contract the private chat already uses (`ChatService.SendPrivateMessageAsync:71`).
- **Receiver** `GroupMessageHandler.HandleAsync`: Base64-decode → `_encryption.Decrypt(..., groupKey)` → UTF-8 → write `ChatMessageEvent.Content`. Mirrors `PrivateMessageHandler`: missing group key, decryption failure, or under-length `Content` all drop the message with a warning log, never publish a `ChatMessageEvent`.
- Group-message **signing** is out of scope for this stage. `SenderId` is still forgeable; that work belongs to phase 3.2 (the `task-3` work item, which depends on `task-1`).
- The README's "end-to-end encryption" claim is corrected from "advertised contract does not hold" to "holds". `Agent.md §3.3` (`GroupMessageHandler` row + `GroupChatService.SendGroupMessageAsync` row) is updated in lockstep.

## Alternatives considered

**Reuse the split-layout from `GroupInviteMessage` (separate 12B nonce + 16B tag fields).** Rejected: that layout was a one-off accommodation to fit extra bytes onto an existing `Message` subclass. Both private chat and this stage use the merged `[nonce|ct|tag]` layout from `IEncryptionService.Encrypt` (28B overhead). One layout across the codebase is simpler than two.
**Encrypt each group message separately to each member.** Rejected: the semantic of a group message is "one ciphertext decryptable by all members"; that's exactly what the group key exists for. Per-member encryption would explode each message into N ciphertexts on the fan-out path, with no security gain and a clean inconsistency versus the symmetric-key private chat.
**Use a different encryption scheme for groups.** Rejected: the project's `Message` / `Encryption` stack is unified around `IEncryptionService.Encrypt` (AES-256-GCM). Adding a second scheme fragments the codebase.
**Bundle this with phase 3.2 (signing + encryption).** Rejected: the two changes don't depend on each other (3.1 touches `GroupChatService` + `GroupMessageHandler`; 3.2 touches `MessageRouter` + `Message`/Envelope), so they can land in parallel. Serializing them only widens the window of exposure.

## Consequences

- Sender-side `Content` (captured) is Base64 of `[nonce 12B][ct][tag 16B]`; Base64-decode → AES-GCM-decrypt with the group key → UTF-8 byte-equal to the original text. Pinned by the new guards `群消息加密_端到端_两节点明文还原一致` (round-trip across two real-TCP nodes) and `群消息加密_发送端TextMessageContent是AES_GCM密文Base64_不含明文片段` (substring search of plaintext against captured payload returns zero hits).
- Receiver-side `GroupMessageHandler` drops (with a warning log) every message that lacks a group key, fails decryption (see `群消息加密_解密失败_必须丢弃且不上报事件`, where flipping one byte of the GCM tag forces the failure path), or has an under-length `Content` (see `群消息加密_Content长度不足时_必须丢弃`); no `ChatMessageEvent` is published in any of those cases.
- The pre-existing test `发送群消息_向所有其他成员扇出_且不发送给自己` is rewritten: instead of asserting `t.Content == "群消息正文"` (which now no longer holds), it asserts `t.Content` contains no plaintext substring AND `Convert.FromBase64String → Decrypt → UTF-8` recovers the original text.
- The README's group-chat security claim is updated from "advertised contract does not hold" to "holds"; `Agent.md §3.3` `GroupMessageHandler` row and `GroupChatService.SendGroupMessageAsync` row are updated in lockstep.
- `dotnet build` 0 errors 0 warnings; `dotnet test` all green (baseline + 4 new guards).
- **Wire semantics changed; old and new versions are not interoperable.** Old clients put plaintext into `Content`; new clients try to Base64-decode and AES-GCM-decrypt it and fail. There is no usable version to stay compatible with, so no migration is provided.
- **Plaintext group key on disk.** Continues to use the current `group_keys.json` representation; out of scope for this stage.
- **Group-message forgery.** Anyone holding a GroupId can inject. This stage does not address *signature*; that work belongs to 3.2. Until 3.2 ships, the existing `.agents/notes/implemented/bug-fix/2026-09-20-message-sender-identity.md` §"Not covered" already states this risk; when 3.2 lands, that note is updated under the README §"Moving between lifecycles" rule that allows editing an implemented note to track where its existing decision lives.
- **Verification:** the four new `群消息加密_*` guards plus the rewritten `发送群消息_*` guard cover end-to-end round-trip, no-plaintext-on-wire, decryption-failure-drop, length-too-short-drop, and fan-out paths.