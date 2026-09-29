# Agent Note: The automatic key-exchange response was never verified (silent session-key substitution on the first `/msg`)

Status: implemented

## Problem

`ChatService.ReadKeyExchangeResponseAsync` runs when a user sends their first `/msg` to a peer that has no session key yet — a peer obtained through DHT auto-discovery or `/add`, rather than one just hand-connected with `/connect`. `EnsureSessionKeyAsync` routes into `PerformKeyExchangeAsync`, which writes the `KeyExchangeMessage` onto one outbound connection and reads the answer back off that same connection.

The admission logic on that path is `MessageRouter.DeserializeEnvelope` plus payload deserialization, followed by `response.EphemeralPublicKey` fed straight into ECDH. There is no signature verification, no identity check, no freshness check, and no admission step of any kind: **the responding party alone dictated the session key.**

The consequences are silent rather than loud:

1. **Any host able to answer the endpoint names its own ephemeral key.** It supplies a public key it controls, ECDH completes against it, and the result becomes the session key for the conversation.
2. **The key is filed under the intended peer's NodeId.** `SetSessionKey(recipient.NodeId, sessionKey)` records it under the NodeId the local node resolved, never under the responder's. The session looks healthy, the log line says the key exchange completed, and the conversation works — only every private message in it is readable by the party that answered.
3. **Nothing observable separates the two cases.** No rejection, no warning, no second log line: the defect and a legitimate exchange produce the same observation, which is why it could sit unnoticed on the application's headline path.

A second defect occupies the same path. The method consumes the connection's byte stream directly and never reaches `MessageRouter.RouteIncomingAsync`, so the inbound replay guard that every other inbound frame passes does not run here either. The consequence is an **ownership inconsistency** rather than merely a missing check: the same protocol carried two different protection strengths, selected by which reader happened to consume the response.

## Decision

Admission of the key-exchange response is a chain of **four criteria, all enforced before the session key is written**, in `ReadKeyExchangeResponseAsync`:

- **① echo of a per-exchange one-time `ConversationId`.** The request carries `kx-{Guid.NewGuid():N}` and the response must return it verbatim (`StringComparison.Ordinal`). The correlation value it replaces was the constant `recipient.NodeId.ToHexString()`; a constant response value makes an echo check decoration, so **the value and the check had to change together**. A compliant peer already echoes it — `KeyExchangeHandler` sets `ConversationId = message.ConversationId` — so ① costs no false rejections.
- **② ECDSA verification** through `Core.Extensions.EnvelopeVerifier.Verify`, the single source of truth shared with the `/connect` path, which already enforces `NodeId.FromPublicKey(publicKey) == SenderId`.
- **③ the signer must be the expected peer.** `NodeId.FromPublicKey(envelope.SenderPublicKey!)` must equal `expectedPeerId`, which arrives from `FindNodeAsync` or the contact record. Identity is derived from the public key and never from the payload's `SenderId`.
- **④ `IReplayGuard.TryAccept`**, the same admission step every other inbound frame passes.

Failure at any criterion throws `KeyExchangeRejectedException` (`src/P2PChat.Core/Models/`) and logs at `LogError`. `Debug` is the wrong level: the outcome being removed is precisely the one that reported nothing.

`ChatService`'s constructor takes a **required** `IReplayGuard`, with no default and an `ArgumentNullException` on null. An optional parameter would make "forgot to inject" equal to "key exchange is unprotected", silently reproducing the inconsistency this decision removes; every construction site — DI wiring, test rigs — updates in step.

Frames that are not a `KeyExchangeMessage { IsResponse: true }`, including a key-exchange *request* the peer initiates on the same connection, are still handed back to `RouteIncomingAsync` and the read loop continues. One connection therefore keeps carrying normal inbound traffic while a handshake is outstanding.

### Why criterion ③ is a genuine rejection here and not on `/connect`

`/connect <ip:port>` connects to an **unknown endpoint**: the user supplies an address precisely in order to ask who is there, so its strongest available criterion is TOFU plus endpoint continuity, and a self-signed stranger is cryptographically indistinguishable from a legitimate node. Here the expected identity is **known in advance** — it is the NodeId the local node was about to address. An attacker holding a perfectly valid key pair produces a response that passes ① and ② in full, and ③ is what recognises it.

That is why the two paths' criteria must not be read as interchangeable, and why the two rejection types are not shared.

### `KeyExchangeRejectedException` is deliberately a separate type

`P2PChatTui`'s `HelloRejectedException` covers the `/connect` command — a local failure scoped to one command, whose user-facing meaning is "could not connect". This exception sits on `ChatService.SendPrivateMessageAsync`, the main path a user's `/msg` takes, where the meaning is "the message was not sent". The two need different wording, so the types stay separate.

The type carries a semantic guarantee the UI depends on: **throwing it means no session key was written and no plaintext went out.** Any failure occurring after the session key write must not use it, because a caller that reads this type as "safe to retry" would be told something untrue.

### Criterion ① is stateless by necessity

`IReplayGuard` is local state and cannot substitute for ①. A response an attacker recorded for one victim carries a `MessageId` that victim's node has never seen and that is absent from the dedup cache, so the guard lets it through. ① compares against the one-time value generated for this exchange and needs no history to be meaningful. This is the same argument that keeps the echo check on the `/connect` path independent, and it holds here for the same reason.

## The class, not the instance

`HANDOFF.md` §8 item 5 already records that this class of defect is a family rather than a series of unrelated bugs. `GroupChatService.ReadPeerPublicKeyFromResponseAsync` has the same shape — it reads a key-exchange response off the connection it holds itself — and it already verifies the signature and checks the responder against the expected member. **The correct pattern was known in this repository and simply was not applied uniformly**, which is the strongest available evidence that a fix scoped to the reported point leaves a false impression behind. After a fix on one reader, "the key-exchange response is verified now" reads as a property of the protocol rather than of one method; the property exists only where each reader enforces it.

Two judgement rules follow from the same audit:

- **Severity follows the impact ceiling, not the loudness of the amplifier.** This gap was carried in the repository's own inventories as lower impact on the grounds that it lacked an amplifier the `/connect` path has, namely deliberately reaching a stranger's host. The impact ceiling is identical — total loss of that conversation's confidentiality — and it sits on the path the main feature actually takes.
- **"It bypasses the shared guard" is a finding, not a nitpick.** A path that reads a connection's byte stream directly inherits none of `MessageRouter`'s protections, and it will never fail visibly when protection is absent: a missing guard here produces a working system, not an error. Two readers of one protocol carrying different protection strength is therefore something to search for, not something to note in passing.

## Alternatives considered

**Rely on the ECDH handshake itself (the response already proves what is needed).** Rejected: ECDH proves that both parties contributed to a shared secret. It says nothing about *who* the other party is. Substituting the responder's own ephemeral key yields a perfectly valid shared secret between the local node and whoever answered — the key agreement is real, the peer is not.

**Verify the signature and take the responder's identity from the verified envelope alone.** Rejected: verification establishes that the envelope is self-consistent with the public key it carries. It does not establish that the public key belongs to the node this conversation is with. An attacker holding a legitimate key pair produces a response that passes this test in full; it is stronger than nothing only because it rules out forgery and tampering.

**Keep the constant conversation id as the correlation value and add the echo check around it.** Rejected: with a value that never changes, the responder echoes it every time, including when replaying a response captured from another exchange. The check would read as a criterion while blocking nothing — worse than omitting it, because the criteria list would then be cited as a guarantee it is not.

**Compare the payload's `SenderId` against the expected peer instead of deriving identity from the public key.** Rejected: the payload is entirely peer-controlled input, and this defect family's history already contains one method taking identity from the envelope and its caller taking it from the payload, six lines apart. Deriving from the verified public key is the only form the verification result actually supports.

**Route the response through `RouteIncomingAsync` like every other inbound frame.** Rejected: the response arrives on a connection this process opened and is waiting on synchronously as a handshake continuation, not as an inbound dispatch. Routing it would hand a key-exchange response to handlers meant for chat traffic and return control to a handshake that still needs its result. The path keeps its own criteria; the correction is that it must now carry the same ones.

**Catch the rejection and continue without a session key.** Rejected: sending the message anyway would put plaintext on the wire with no key, and telling the user the send failed while still attempting it is the failure mode this decision exists to remove.

**One criterion fewer — drop ④ and keep ①, ②, ③.** Rejected, but for a weaker reason than it first appears: ① already blocks the recorded-response replay, because every new exchange carries a fresh correlation id that a recorded response cannot carry. What ④ adds is that this path then has **the same admission strength as every other inbound frame** rather than a hand-rolled subset, and it covers an envelope identity this node has already accepted being delivered again under a current correlation value. The stated goal is removing the two-strength inconsistency, and that is only true when both readers apply the same guard.

## Consequences

- The first `/msg` to a new peer becomes "**verify, then trust**": the response must echo this exchange's correlation id, pass `EnvelopeVerifier.Verify`, be signed by the expected peer's key, and pass the replay guard before a session key exists. On every rejection path, no session key is written and no plaintext is sent.
- **Cost: a wrong-endpoint peer now fails the send instead of silently accepting a key.** If discovery returns an endpoint belonging to a host that is not the expected node, the user gets an error naming the expected and actual short ids rather than a session that quietly works and leaks. That is the correct outcome, and it presents as "it used to work" to anyone who had such a peer.
- **This path is stronger than `/connect`, and the two must not be read as one mechanism.** `/connect` cannot reject a self-signed stranger on first contact; criterion ③ here is a real identity check because the expected identity is already known. Any future work that unifies the two has to preserve that difference rather than flattening it to the weaker of the two.
- **The correlation id on this path is a per-exchange nonce.** Anything reading or matching a `KeyExchangeMessage.ConversationId` on the `ChatService` path must treat it as an opaque nonce with a per-handshake lifetime, not as a conversation key — the private-chat conversation key is `ConversationId.ForPrivate(...)`, a different value with a different lifetime.
- **The required `IReplayGuard` parameter is a breaking constructor change** for every construction site, by design: a missing injection must fail loudly at construction rather than degrade into "the handshake is unprotected".
- **Three structural guards assert on source text** — a call to `EnvelopeVerifier.Verify`, a call to `_replayGuard.TryAccept`, and the absence of `new NodeId(response.SenderId` in comment-stripped source. They go red for cosmetic refactors that move a call across a line. The trade is "false red rather than let the check be deleted"; a maintainer seeing them fail should suspect a moved or reformatted call first.

## Testing

`tests/P2PChat.Chat.Tests/KeyExchangeResponseVerificationTests.cs` — 11 cases. `ChatService` is constructible in-process, so these are behaviour tests over real cryptography and a real `IReplayGuard`: they assert what was written and what went on the wire, not what the source contains.

- Criteria ② and the impersonation shape: `密钥交换应答未签名时拒绝发送_不写会话密钥也不把明文推上线`, and `密钥交换应答的签名与公钥不自洽时拒绝发送` — a valid P-256 public key carried by an envelope whose `SenderId` claims another node, signed with the attacker's own key, which is what defeats verification whenever identity is read from the payload.
- Criterion ③, the criterion the `/connect` path does not have: `密钥交换应答由非预期节点签名时拒绝发送_即使那条签名完全有效`, plus `攻击者的临时公钥不得被用来派生会话密钥`. The second is separate on purpose — it asserts the attacker's ephemeral key really was decryptable, so the first cannot pass merely because the attacker happened to build a broken key, and that no session key byte was written under the expected peer.
- Criterion ①: `密钥交换应答回显的不是本次关联标识时拒绝发送`, and `关联标识每次交换都必须重新生成_不得退回恒定的会话键`. The second runs two exchanges and asserts the two correlation ids differ, which is what pins a constant value out as a regression instead of leaving it to review.
- Criterion ④: `同一条密钥交换应答重复投递时第二次被重放防护拒绝` re-delivers a response that is correctly signed, correctly echoing, and signed by the expected peer's key, under a current correlation value but with a `MessageId` already accepted — the first three criteria pass both times, so only the guard can reject the second.
- Against a fix that rejects everything: `四道判据全部通过时正常建立会话并发出消息` asserts a 32-byte session key and a non-empty `TextMessage.Content`.
- Structural guards K1–K3: `ChatService必须调用Core的EnvelopeVerifier验签`, `ChatService的密钥交换应答必须经过重放防护`, `ChatService不得用载荷里的SenderId判定对端身份`. These close a gap the negative cases cannot: every one of those is a negative assertion, so all of them still go red if verification were softened into "warn and continue", but nothing would notice the *positive* wiring being absent. K3 reads the uncommented source, because what it guards against is misleading prose as much as code.

The naming convention matches the sibling `/connect` guards: the test name **is** the problem statement, so the list reads as a list of things that must never come back.

## Deferred

- **What the four criteria do not prove.** They establish that the response was signed by the holder of the private key for the identity this node intended to address, and that it answers this exchange. They do not establish that the peer's machine is uncompromised, and they do **not** establish that the endpoint `FindNodeAsync` returned belongs to that node — an impostor endpoint still cannot answer as the expected NodeId, but whether the endpoint is the expected node is a **discovery-layer trust question** and remains open. No user-facing wording may render the outcome as "the peer is verified".
- **`GroupChatService`'s probe path** (`ReadPeerPublicKeyFromResponseAsync`) has the same read-directly-from-the-connection shape. It verifies the signature and checks the responder against the expected member, so the two criteria that protect a session key are present; it carries no echo criterion and no `IReplayGuard` call, and its `ConversationId` is the member's NodeId. What it derives is a long-term public key for wrapping group keys rather than a private-chat session key, and that remaining asymmetry is unresolved.
- **The inventory entries are stale.** `Agent.md` — the `ReadKeyExchangeResponseAsync` row in the `ChatService` method table, and the residual-risk row describing this path as bypassing verification and replay protection — still describes the old contract. Both need the same-commit update this decision describes; that file sits outside the note tree.
- **No end-to-end assertion covers the automatic key-exchange path.** `scripts/e2e-verify.ps1` Phase 2 covers the `/msg` round trip, and the `/connect` round trip has no assertion either.
- The structural guards' sensitivity to cosmetic refactors is recorded under `## Consequences`.

## Related

- [/connect's hello response verification](./2026-09-28-connect-hello-response-verification.md) — the sibling path and the origin of `EnvelopeVerifier`; the criteria overlap but are not equivalent, and this note records where the two must not be flattened.
- [Inbound replay protection](../feature/2026-09-28-replay-protection.md) — `IReplayGuard`, the contract criterion ④ calls, and the local-state argument that makes criterion ① necessary rather than redundant.
- [Envelope wire codec converged onto a single source of truth in Core](./2026-09-28-envelope-codec-single-source.md) — the same convergence discipline, applied here to verification.
- [Message signing](./2026-09-21-message-signing.md) — phase 3.2 shipped self-signing; this path was a newly added reader that never took it.
