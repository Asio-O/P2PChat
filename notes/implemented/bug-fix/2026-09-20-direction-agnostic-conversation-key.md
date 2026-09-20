# Agent Note: Private conversation key must be direction-agnostic

Status: implemented

## Problem

A private message's `ConversationId` was set to `recipientId.ToHexString()` — the **recipient's** node ID. That is a **one-directional** value: the same message gets two different keys on the sending and receiving sides.

- On the sender A: `ConversationId` = B's NodeId, and A's UI files the message under the "B" conversation bucket — consistent.
- On the receiver B: `ConversationId` is still B's own NodeId, while B's UI buckets conversations by the **peer's** (A's) NodeId (`P2PChatTui.cs:134`, `:198`) and only looks up that key (`CurrentMessages()`, `:412-419`).

Net effect: **the message arrives → decrypts successfully → lands in the "my own NodeId" bucket → that bucket can never be selected → it never renders.** The sending side looks perfectly healthy, so the symptom is "I sent it, the other side says nothing arrived".

This defect was previously untestable here: the tests assert only service-layer event contents and keys, and nothing covered the UI's conversation bucketing.

## Decision

A private conversation key must be **direction-agnostic**: both sides must compute the same string. A single definition point, `ConversationId.ForPrivate`, was added.

```csharp
public static string ForPrivate(NodeId a, NodeId b)
{
    var x = a.ToHexString();
    var y = b.ToHexString();
    return string.CompareOrdinal(x, y) <= 0 ? x + y : y + x;
}
```

- `ChatService.SendPrivateMessageAsync` uses it to build `ConversationId`; the TUI uses the same function for the current conversation key.
- A group conversation key is the `GroupId`, already direction-agnostic, and does not go through this.
- **`P2PChatTui._currentPeerId` was added alongside it.** The conversation key is an 80-hex-character concatenation and **cannot be inverted back to the peer**, yet sending needs the peer's NodeId itself. The TUI previously derived it from `_currentConversationId` (`new NodeId(Convert.FromHexString(...))`), which necessarily throws "节点ID必须是20字节" once the key becomes a concatenation. So "current conversation key" and "current peer" are two independent fields, with the latter `null` for group conversations.

## Alternatives considered

**Keep `ConversationId = recipient ID` and make the TUI bucket by `SenderId` instead.** Rejected: `ChatMessageEvent` carries no recipient field, so the TUI cannot determine the peer for messages it sent; and the sender's own local echo takes the same bucketing path, landing in a bucket keyed by itself — the sender would stop seeing its own messages.

**Set `ConversationId` to the sender's ID.** Considered: on A→B the key is A, on B→A it is B, so each side sees "the peer", which looks workable. Rejected: the sender's local echo carries its own ID, so the **sender** cannot see the message it just sent — that only moves the invisibility from one side to the other.

**Use a random UUID as the conversation ID, negotiated by both sides.** Rejected: it needs an extra handshake or message field to synchronise the ID, whereas a deterministic concatenation of two known NodeIds yields the same value on both sides with no negotiation at all.

## Consequences

- The receiver can see private messages, in the same conversation as the sender.
- The conversation key is independent of direction, so both directions of one conversation land in one bucket and scrolling history plus unread merging behave correctly.
- **Cost: `ConversationId` no longer equals any single node's ID** and is 80 hex characters long. Any assumption that "the ConversationId is the peer's NodeId" breaks; the TUI therefore has to hold `_currentPeerId` explicitly.
- **Cost: the wire semantics changed, so old and new versions cannot interoperate** — the receiver computes keys by the new rule, which does not match keys sent by an old version, so messages appear under the wrong conversation. This ships in the same batch as the `SenderId` change.
- The guard test `IdentityAndEndpointTests.静态对端_仅凭显式端点即可完成双向加密私聊` asserts that the received event's `ConversationId` equals `ConversationId.ForPrivate(sender, receiver)`, and that both directions produce the same key.
