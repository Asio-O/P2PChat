# Agent Note: Message SenderId must derive from the node identity

Status: implemented

## Problem

Every message's `SenderId` was written as `identity.PublicKey.Take(20)`, with the original comment "simplified: use the first 20 bytes of the public key as the ID". That is **not** an identity — it is a constant.

The identity public key is produced by `ECDiffieHellman.ExportSubjectPublicKeyInfo()`, i.e. a P-256 **SubjectPublicKeyInfo (DER, 91 bytes)**. Its first 27 bytes are a **fixed algorithm header** (SEQUENCE + algorithm OID + BIT STRING header) independent of the key material, so `Take(20)` yields the same bytes for **every node**. Two nodes' persisted public keys, measured:

```
nodeA: MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE UQ8VvJm...
nodeB: MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE l5Xxzmn...
```

The first 36 base64 characters (= 27 bytes) are byte-for-byte identical.

The real definition of node identity is `NodeId.FromPublicKey` = **SHA-1(public key)** (`NodeId.cs:27-31`), used for `localNode.NodeId`, the routing table, contacts, `/add`, and `/id`. So the **identity announced on the wire** and the **identity known locally** became two unrelated values.

Three consequences:

- **Session-key slot collision.** `KeyExchangeHandler` keys `keyStore` by `new NodeId(message.SenderId)`, so every peer shares one slot. With two nodes it works by luck; **the moment a third node joins it overwrites the earlier peer's session key**, whose messages then fail to decrypt.
- **Contact aliases can never resolve.** The UI looks up aliases with `contactService.FindByNodeId(chatEvent.SenderId)`, while contacts are stored under the real NodeId → always `null`, so the sender renders as a hex blob.
- **The group creator identity becomes meaningless.** `GroupInviteMessage.CreatorId` is the same for every node.

It also contradicted an existing documented contract: `Agent.md:595` declares the field as `SenderId (NodeId raw bytes)`.

## Decision

`SenderId` always carries **this node's identity**, and that identity has exactly **one definition point**: `KeyPair.NodeId`.

```csharp
public record KeyPair
{
    public Models.NodeId NodeId => Models.NodeId.FromPublicKey(PublicKey);
}
```

- Private messages, key-exchange requests and responses, group messages, and the group creator ID all use `identity.NodeId` (`ChatService` ×2, `KeyExchangeHandler` ×1, `GroupChatService` ×3).
- Nothing slices bytes out of `PublicKey` by hand any more.
- The test harness follows suit: `NodeHarness.SenderId` changed from "mirroring the implementation" to reusing `KeyPair.NodeId`.

Because both sides now announce their real identity, session keys land naturally under the **peer's real NodeId**: the sender files it under `recipient.NodeId`, the receiver under `new NodeId(message.SenderId)`, and the two agree.

## Alternatives considered

**Keep slicing bytes out of the public key as the SenderId (changing only the offset).** Rejected: slicing is not derivation. Standing alongside `NodeId.FromPublicKey` it creates two sources of identity, and the moment they disagree this defect returns; the sliced bytes also depend on DER encoding details and break if the structure changes.

**Keep `PublicKey.Take(20)` and key `keyStore` by the full public key instead.** Rejected: it treats only the session-key slot symptom. Contact aliases and the group creator stay wrong, and the wire still carries no verifiable identity at all.

**Introduce a separate "message identity" type alongside `NodeId`.** Rejected: that adds a second notion of identity. What was missing was one authoritative derivation point, not a type.

## Consequences

- The identity announced on the wire and the identity known locally are unified; the `Agent.md:595` contract holds again.
- Session keys are slotted by the peer's real identity, so **any number of peers** is supported; a third node joining no longer breaks existing sessions.
- Contact aliases, the group creator, and sender display all work again.
- Key-exchange caching becomes effective. Previously, because the keys disagreed, each side filed its key in a slot the other could not see, forcing a directional re-handshake.
- **Cost: the wire semantics changed, so old and new versions cannot interoperate.** The old version's `SenderId` is a constant; a new node receives it, finds no session key under the real NodeId, drops the message, and logs `私聊消息缺少会话密钥`. No usable version exists to stay compatible with, so no migration is provided.
- The guard test `IdentityAndEndpointTests.身份_SenderId不得取公钥前20字节` first asserts that two nodes' public keys really do share their first 20 bytes, then asserts `SenderId` is not that value — pinning the defect mechanism itself into the test so reverting goes red immediately.
- **Not covered: `SenderId` forgeability — fixed in [2026-09-21-message-signing](../../implemented/bug-fix/2026-09-21-message-signing.md).** The original "Not covered" entry said the only guarantee was that an honest node announces its real identity, since no signature meant an attacker could put any `SenderId` on the wire; that follow-up has now landed. `MessageEnvelope` carries `SenderPublicKey` + `Signature`; `MessageRouter.RouteIncomingAsync` drops any envelope that fails the four-gate check (signature present, public key present, `NodeId.FromPublicKey(SenderPublicKey) == SenderId`, ECDSA verify). The decision taken in this note (one authoritative identity-derivation point, `KeyPair.NodeId`) is unchanged.
