# Agent Note: 群消息必须走 AES-256-GCM 加密

Status: implemented

## Problem

`GroupChatService.SendGroupMessageAsync`（`src/P2PChat.Chat/Services/GroupChatService.cs:140-178`）构造 `TextMessage` 时直接把明文写入 `Content`：

```csharp
var message = new TextMessage
{
    SenderId = senderId,
    ConversationId = groupId,
    Content = text,    // 明文
    IsGroup = true
};
```

接收端 `GroupMessageHandler.HandleAsync`（`src/P2PChat.Chat/Handlers/GroupMessageHandler.cs`）原样当作明文：

```csharp
var chatEvent = new ChatMessageEvent
{
    Content = message.Content,    // 原样当明文
    ...
};
```

群密钥（`keyStore.GetGroupKey(message.ConversationId)`）仅用于「是否拥有」的检查，不参与加解密。结果：

- 群密钥的分发链路（`EncryptGroupKeyForMember` → `GroupInviteMessage.EncryptedGroupKey`）走 AES-256-GCM，正确。
- 群消息本身**全程明文**。README 宣称的端到端加密对群聊**不成立**，构成安全缺陷（B5）。
- 群消息无签名；任何知道 GroupId 的人都可向群里注入假消息。

## Decision

- **发送端** `GroupChatService.SendGroupMessageAsync`：
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
  加密复用现有 `IEncryptionService.Encrypt`（AES-256-GCM，输出 `[nonce 12B][ct][tag 16B]`），与私聊完全同一线路契约（`ChatService.SendPrivateMessageAsync:71` 已经在用同样模式）。
- **接收端** `GroupMessageHandler.HandleAsync`：Base64 解码 → `_encryption.Decrypt(..., groupKey)` → UTF-8 → 写入 `ChatMessageEvent.Content`。与 `PrivateMessageHandler` 对称：缺群密钥、解密失败、Content 长度不足时一律丢弃并写告警日志，不上报 `ChatMessageEvent`。
- 群消息**签名**不在本阶段。`SenderId` 仍可伪造由阶段 3.2（任务 `task-3`，依赖 `task-1` 完成）处理。
- README 的「端到端加密」语义改写为「成立」，`Agent.md §3.3` 关于群聊的安全语义同步改写。

## Alternatives considered

**复用现有 `GroupInviteMessage` 的密文布局（12B nonce 与 16B tag 分开存放）。** 否决：那是为兼容「在已有 Message 子类字段上塞额外 28B」的临时安排；私聊与本阶段都直接用 `IEncryptionService.Encrypt` 自带的 `[nonce|ct|tag]` 合并布局（28B），简单统一。
**每条群消息用发送方的 ECDH 临时密钥加密给每个成员。** 否决：群消息的语义是「一次加密、所有成员解密」；群密钥就是为此而生的设计。引入「每成员独立加密」会让扇出路径上的每条消息爆炸为 N 个不同密文，且与私聊用的「对称会话密钥」不统一。
**群消息也走 `char[]` 加密（与 `Contact` 字段一样）。** 否决：项目 `Message`/`Encryption` 体系已统一为 `IEncryptionService.Encrypt` AES-256-GCM，`ContactService` 是另一回事。引入第二种加密方式只会让代码分裂。
**等阶段 3.2 一起做（签名 + 加密）。** 否决：3.1 与 3.2 互不依赖（3.1 改 `GroupChatService` + `GroupMessageHandler`；3.2 改 `MessageRouter` + `Message`/Envelope），并行无问题；顺序串行做只会拉长窗口期风险暴露。

## Consequences

- 发送端走 AES-256-GCM：`TextMessage.Content` 经 Base64 解码后长度 = 12 + 明文 UTF-8 字节数 + 16；解密还原明文与原文逐字节一致（新增 `群消息加密_端到端_两节点明文还原一致`、`群消息加密_发送端TextMessageContent是AES_GCM密文Base64_不含明文片段` 两条守卫验证）。
- 接收端 `GroupMessageHandler` 在缺群密钥、解密失败（`群消息加密_解密失败_必须丢弃且不上报事件`，手工翻转 tag 中间字节验证）、Content 长度不足（`群消息加密_Content长度不足时_必须丢弃`）时均丢弃并写告警日志，不上报 `ChatMessageEvent`。
- 既有测试 `发送群消息_向所有其他成员扇出_且不发送给自己` 已改写为「明文不上线 + 密文可解密还原」双向断言（`t.Content` 不含明文片段 + `Convert.FromBase64String → Decrypt → UTF-8` 与原文一致）。
- README 群聊端到端加密语义从「广告契约不成立」改为「成立」；`Agent.md §3.3` 的 `GroupMessageHandler` 行 + `GroupChatService.SendGroupMessageAsync` 行同步改写。
- `dotnet build` 0 错 0 警；`dotnet test` 全绿（基线 + 新增 4 条守卫）。
- **线路语义变更，新旧版本不可互通。** 旧版本客户端 `Content` 是明文，新版本会把它当作 Base64 密文去解密并失败丢弃。当前阶段无可用版本，无需提供迁移。
- **群密钥本身的明文存储。** 仍沿用现状 `group_keys.json` 平文本；本阶段不动。
- **群消息源伪造。** 攻击者持有 GroupId 即可注入假消息。本阶段只做加密、不做签名（签名由 3.2 处理）；此风险在 3.2 落定前持续存在——`.agents/notes/implemented/bug-fix/2026-09-20-message-sender-identity.md` §"Not covered" 部分已在该 note 注明，3.2 完成时按 README §"Moving between lifecycles" 允许 editing implemented note to track where its existing decision lives 的规定同步改写。
- **Verification：** 4 条新增 `群消息加密_*` 守卫 + 1 条改写的 `发送群消息_向所有其他成员扇出_*` 守卫覆盖「端到端还原 / 明文不上线 / 解密失败丢弃 / 长度不足丢弃 / 扇出」全部路径。