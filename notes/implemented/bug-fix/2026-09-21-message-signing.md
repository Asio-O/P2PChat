# Agent Note: Outbound messages must be signed with the long-term ECDSA key; inbound messages must verify the signature

Status: implemented

## Problem

`MessageRouter.SendAsync` / `SendViaConnectionAsync` (in `src/P2PChat.Chat/Routing/MessageRouter.cs`) serialized a `Message` into a `MessageEnvelope` and called `connection.SendAsync(...)` directly; the receiver's `RouteIncomingAsync` deserialized the envelope and routed to the corresponding `IMessageHandler` without any signature verification step.

The consequence: `SenderId` was attacker-controlled input. `notes/implemented/bug-fix/2026-09-20-message-sender-identity.md` already unifies `SenderId` on `KeyPair.NodeId`, but the same note's "Not covered" section explicitly states: "this change only guarantees that an honest node announces its real identity; there is no signature, so an attacker can put any `SenderId` on the wire." This stage (REPAIR-PLAN §3.2) addresses that omission.

Measured:
- `IEncryptionService.Sign` / `Verify` (in `src/P2PChat.Crypto/Encryption/AesGcmEncryptionService.cs:104-128`) was already implemented (ECDSA P-256 + SHA-256) but had **zero call sites** anywhere in the repository.
- The receiver could be fed a forged-SenderId TextMessage / FileMetaMessage / etc. The current implementation only routed by `MessageType` to the corresponding handler, and handlers typically derive `NodeId` from `message.SenderId`, look up the session / group key, decrypt, and dispatch. Anyone who knows a group `GroupId` and the group key (which the `HandleInviteAsync` shortcut already bypasses to enroll) could inject messages that "look like they came from Alice / Bob".
- The same applied to private chat: an attacker who raced ahead of an ECDH handshake could forge a "KeyExchange response" with their own ephemeral public key and steer the session key.

## Decision

- Extended `Core/Models/MessageEnvelope.cs` with two **optional** fields:
  - `SenderPublicKey: byte[]?` — the sender's long-term identity public key (P-256 SubjectPublicKeyInfo, 91 bytes). The receiver must use this key to verify the signature; placing it on the envelope keeps it independent of any specific `Message` subclass's wire fields.
  - `Signature: byte[]?` — the ECDSA P-256 / SHA-256 signature, ~64 bytes.
- Upgraded `MessageRouter.SerializeEnvelope` / `DeserializeEnvelope`: after the fixed-size header, append **4B BE SenderPublicKey length + N public-key bytes + 4B BE Signature length + N signature bytes + N payload**. All length fields use `BinaryPrimitives.ReadUInt32BigEndian` / `WriteUInt32BigEndian`, matching the existing 4B frame-header style. The two static methods are shared with `ChatService.ReadKeyExchangeResponseAsync` to prevent deserialization drift.
- `MessageRouter` constructor gains two dependencies: `IKeyStore` (for the sender identity) and `IEncryptionService` (for Sign / Verify). `SendViaConnectionAsync` exit flow:
  ```csharp
  var identity = _keyStore.GetOrCreateIdentity();
  var unsigned = new MessageEnvelope {
      ...
      SenderId = identity.NodeId.ToByteArray(),          // force-overwrite
      SenderPublicKey = identity.PublicKey,
      Payload = _serializer.Serialize(message)
  };
  var envelope = SignEnvelope(unsigned, identity, _encryption);
  await connection.SendAsync(SerializeEnvelope(envelope), ct);
  ```
  `ToBeSignedBytes` is a fixed concatenation: `Version || MessageType || Seq || SenderId || MessageId || Timestamp || SenderPublicKey (length-prefixed) || Payload`; `Signature` itself is not part of the signed data.
- `RouteIncomingAsync` gains a verification gate at the entry, calling `VerifyEnvelope(envelope, _encryption, out failureReason)`:
  1. `Signature == null || Signature.Length == 0` → warning log + dropped (**never** dispatched to a handler).
  2. `SenderPublicKey == null || SenderPublicKey.Length == 0` → warning log + dropped.
  3. `NodeId.FromPublicKey(SenderPublicKey).ToByteArray()` ≠ `envelope.SenderId` → warning log + dropped (the core anti-forgery assertion: even if an attacker captures a valid signature off the wire, they cannot impersonate a different `SenderId`, because `SenderId` must equal the NodeId derived from the public key).
  4. ECDSA verification fails → warning log + dropped.
  5. All checks pass → normal handler dispatch.
- `KeyExchangeHandler.SendResponseAsync` now calls the static `MessageRouter.SignEnvelope` + `MessageRouter.SerializeEnvelope` methods so the response carries a signature too; its own length-prefixed encoder is gone.
- Production impact: `Program.cs`'s `AddSingleton<IMessageRouter, MessageRouter>()` registration is unchanged; the container already holds `IKeyStore` and `IEncryptionService` singletons and injects them automatically.
- Test impact: `NodeHarness.Start` takes the minimum-necessary extra arguments (`encryption, keyStore`); `PayloadCapturingMessageRouter` needs no change (it only delegates to `_inner.SendAsync`).

## Alternatives considered

**Put the signature on the `Message` base class.** Rejected: a `Signature` field on the base would change the schema of all 8 existing `[Union]` subclasses (TextMessage / FileMetaMessage / FileChunkMessage / FileAckMessage / KeyExchangeMessage / GroupInviteMessage / GroupNotifyMessage / DeliveryAckMessage); every source-generated formatter and every caller would need to be updated. `MessageEnvelope` is the smaller, single-place hook (`SerializeEnvelope` / `DeserializeEnvelope` are the only entry / exit points).

**Sign only `SenderId + MessageType + SequenceNumber`, omit the payload.** Rejected: the payload is the substantive content of the message; not signing it allows "keep the signature, replay a different payload" tampering (e.g., flip `TextMessage.Content`). The actual attack surface this stage addresses is "attacker controls both `SenderId` and payload," so the payload must be in the signed data.

**Don't sign `SenderPublicKey`; let the receiver look up the peer's public key in keyStore.** Rejected: `IKeyStore` does not currently store peer long-term public keys (only SessionKey / GroupKey / Identity). Adding a peer-key table means changing `contacts.json` / a new `node_keys.json` schema — orthogonal to this task. Putting `SenderPublicKey` on the envelope means the receiver verifies **independently** with no shared state; the cost is ~91B per message. We accept the cost.

**Use the receiver's session-key AES-GCM tag as a source-identification mechanism.** Rejected: AES-GCM is symmetric authentication (both sides hold the same key), which is the wrong semantic for "only the sender who holds the private key can sign." In group chats all members share the group key, so AES-GCM cannot distinguish "A sent this" from "B sent this." ECDSA's asymmetric semantics are what §3.2 needs.

**Wait for a separate sender-public-key handshake (KeyExchange carrying SenderPublicKey) before enabling signatures.** Rejected: that leaves the very first message (before the handshake completes) unsigned — and KeyExchange itself is the first unsigned message. Chicken-and-egg. Putting `SenderPublicKey` on the envelope means the first message can be signed.

**Derive keys from a field other than `SenderId` (to avoid repeating the public key).** Rejected: `NodeId.FromPublicKey` is the **single** identity-derivation point (see 2026-09-20 sender-identity §Decision); letting `SenderId` and the public key come from the same source is the cleanest way to eliminate the two-sources-of-identity problem. The 91B cost is small; the zero-state verification is worth it.

## Consequences

- Real ECDSA sign / verify round trip passes a test (`MessageSigningTests.出站消息_签名并附带SenderPublicKey_接收端验签通过明文还原`): after `alice.Chat.SendPrivateMessageAsync(bob.LocalNode.NodeId, "...")`, the captured outbound `MessageEnvelope` carries 91B public key + ~64B signature appended after the payload; Bob's `RouteIncomingAsync` verifies with the same `SenderPublicKey`, and `PrivateHandler.OnMessageReceived` yields the decrypted plaintext.
- Tampering with `SenderId` causes verification to fail → message is dropped (`MessageSigningTests.篡改SenderId_接收端验签失败丢弃且无ChatMessageEvent`, `MessageSigningTests.双节点_Alice篡改SenderId_Bob的路由拒绝并产生告警日志`, `MessageSigningTests.MessageRouter_SenderId与公钥派生NodeId不一致_验签失败_原因为不匹配`).
- Tampering with `Payload` causes verification to fail → same drop + warning (`MessageSigningTests.篡改Payload_接收端验签失败丢弃且无ChatMessageEvent`).
- Missing signature or missing public key is also rejected (`MessageRouter_缺签名_VerifyEnvelope返回false_原因为缺少签名`, `MessageRouter_缺SenderPublicKey_VerifyEnvelope返回false_原因为缺少发送方公钥`).
- Wire round-trip preserves verifiability: `Serialize → Deserialize` then `Verify` still passes (`MessageRouter_线缆_签名Envelope序列化与反序列化后验签仍通过`).
- Static round-trip (`MessageRouter静态SignEnvelope与VerifyEnvelope往返一致`) covers `SignEnvelope` / `VerifyEnvelope` without the network stack.
- README's group-chat security claim is unchanged (this stage adds the signing dimension; encryption is covered by 2026-09-21-group-message-encryption).
- `dotnet build` 0 errors 0 warnings; `dotnet test` all green (baseline 160 stays green; 9 new guards added).
- **Wire semantics changed; old and new versions are not interoperable.** Old clients send envelopes with no `Signature` / `SenderPublicKey`; new clients drop them at the `RouteIncomingAsync` gate and log a warning. Same batch as 2026-09-20 sender-identity; no migration provided.
- **Per-message envelope size grows by ~155B** (91B public key + 64B signature). TCP length-prefix framing already supports arbitrary sizes (100MB cap in `TcpConnection`); no real impact.
- **ECDSA P-256 + SHA-256 single-sign latency.** Measured < 1 ms; no benchmark needed for this task.
- **`MessageRouter` now depends on `IKeyStore` / `IEncryptionService`.** Both are already singletons in production DI. On the test side, `NodeHarness.Start` takes the same kind of constructor-argument extension as the `GroupChatService` change in `task-2`; `PayloadCapturingMessageRouter` (which only calls `_inner.SendAsync`) does not need changes.
- **`SenderPublicKey` and `Signature` are both optional** (`byte[]?`). `VerifyEnvelope` checks in the sequence "signature present → public key present → NodeId consistency → ECDSA verify"; any gate failure short-circuits to `false` plus a `failureReason` string for the log line.
- **The signature covers the payload**, so any tampering of a `Message` subclass's wire field (SenderId / ConversationId / Content / FileMeta.ChunkSize, etc.) trips the verification gate. That is what this stage wants — attackers tampering with any of these fields are caught.
- **Verification:** 9 new `MessageSigningTests` cover end-to-end round-trip, SenderId tamper, Payload tamper, missing signature, missing public key, NodeId inconsistency, wire serialize round-trip, static round-trip, and two-node integration. `Agent.md` §"群消息安全语义"段 stays as-is (this stage is signing, parallel to encryption, not a re-statement).
- **In lockstep with the existing note**, the §"Not covered" section in `notes/implemented/bug-fix/2026-09-20-message-sender-identity.md` is rewritten from "SenderId remains forgeable" to "fixed in 2026-09-21-message-signing" with a cross-link, per README §"Moving between lifecycles" (editing an implemented note to track where its existing decision lives is allowed; the decision itself is not rewritten).