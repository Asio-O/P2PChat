# Agent Note: 私聊会话键必须方向无关

Status: implemented

## Problem

私聊消息的 `ConversationId` 被设为 `recipientId.ToHexString()`，即**收件人**的节点 ID。这是一个**单向**的值：同一条消息在发送方与接收方得到两个不同的键。

- 发送方 A 发出时：`ConversationId` = B 的 NodeId，A 的 UI 也把这条消息记在「B」这个会话桶下 —— 一致。
- 接收方 B 收到时：`ConversationId` 仍是 B 自己的 NodeId，而 B 的 UI 会话桶以**对端（A）**的 NodeId 为键（`P2PChatTui.cs:134`、`:198`），取桶时也只查这个键（`CurrentMessages()`，`:412-419`）。

净效果：**消息到达 → 解密成功 → 投进「自己 NodeId」这个桶 → 该桶永远无法被选中 → 永不显示。** 发送端一切正常，所以现象是「我发了，对方说没收到」。

这个缺陷此前测不出来：测试只断言服务层事件的内容与密钥，没有任何测试覆盖 UI 的会话归桶。

## Decision

私聊会话键必须**方向无关**：双方必须算出同一个字符串。新增唯一定义点 `ConversationId.ForPrivate`。

```csharp
public static string ForPrivate(NodeId a, NodeId b)
{
    var x = a.ToHexString();
    var y = b.ToHexString();
    return string.CompareOrdinal(x, y) <= 0 ? x + y : y + x;
}
```

- `ChatService.SendPrivateMessageAsync` 用它生成 `ConversationId`；TUI 用同一个函数生成当前会话键。
- 群聊的会话键是 `GroupId`，本身就是方向无关的，不走这里。
- **配套新增 `P2PChatTui._currentPeerId`。** 会话键是 80 位拼接串，**不能反解出对端**，而发送时需要的正是对端 NodeId；此前 TUI 从 `_currentConversationId` 反解（`new NodeId(Convert.FromHexString(...))`），改成拼接串后必然抛「节点ID必须是20字节」。因此「当前会话键」与「当前对端」是两个独立字段，群聊会话下后者为 `null`。

## Alternatives considered

**保持 `ConversationId = 收件人 ID`，改让 TUI 按 `SenderId` 归桶。** 否决：`ChatMessageEvent` 没有携带「收件人」字段，因此 TUI 无法为自己发出的消息判定对端；而发送方的本地回声事件同样走这条归桶路径，会落进一个以自己为键的桶，变成发送方看不到自己的消息。

**把 `ConversationId` 设为发送方 ID。** 曾考虑：A→B 时键为 A，B→A 时键为 B，两侧都等于「对端」，看似成立。否决：发送方的本地回声事件携带的正是自己的 ID，于是**发送方**看不到自己刚发的消息 —— 只是把不可见从一侧搬到了另一侧。

**用随机 UUID 作会话 ID 并由双方协商。** 否决：需要额外的一次握手或消息字段来同步该 ID；而「两个已知 NodeId 的确定性拼接」无需任何协商即可在两侧得到同一个值，成本为零。

## Consequences

- 接收方能收到并显示私聊消息，且与发送方归入同一个会话。
- 会话键与传输方向无关，因此同一会话的双向消息落在同一个桶里，滚动历史与未读归并自然正确。
- **代价：`ConversationId` 不再等于任何单个节点的 ID**，长度为 80 个十六进制字符。任何「ConversationId 就是对端 NodeId」的假设都会失效；TUI 因此必须显式保存 `_currentPeerId`。
- **代价：线路语义变更，新旧版本不能互通**——接收方按新规则计算键，与旧版发出的键不一致，消息会显示在错误的会话下。本变更与 `SenderId` 的变更同批发布。
- 守卫测试 `IdentityAndEndpointTests.静态对端_仅凭显式端点即可完成双向加密私聊` 断言接收端事件的 `ConversationId` 等于 `ConversationId.ForPrivate(发送方, 接收方)`，并断言两个方向得到同一个键。
