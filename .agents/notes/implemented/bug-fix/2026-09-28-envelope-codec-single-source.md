# Agent Note: Envelope wire codec converged onto a single source of truth in Core

Status: implemented

## Problem

The envelope's wire encode/decode logic **had three hand-written implementations alive at the same time**:

1. one embedded in `MessageRouter` (`SerializeEnvelope` / `DeserializeEnvelope`);
2. one private copy in `ChatService`, later turned into a forwarder to `MessageRouter.DeserializeEnvelope` so that two deserialization paths could not drift apart;
3. one more copied into `P2PChatTui.ReadHelloResponseAsync` — the code that reads the hello response for `/connect <ip:port>`.

Phase 3.2 introduced ECDSA self-signing, which added two fields between the fixed header and the real payload: a **4+N sender-public-key length prefix** and a **4+M signature length prefix**. Copy 3 **was not updated** — it still treated everything after the 50-byte fixed header as `Payload`. So when `/connect <ip:port>` parsed the hello response, MessagePack was handed "two length prefixes + the real payload" instead of the payload, and deserialization necessarily failed: **since self-signing shipped, blind connect could not possibly succeed**.

The crux is **why neither the compiler nor the existing tests caught it**:

- **Nothing at compile time.** All three implementations had identical signatures, none referenced the others, and each was a self-consistent private method. Missing two length prefixes is not a type error; it is a logic error.
- **Zero runtime coverage.** `ReadHelloResponseAsync` only runs on the `/connect` path, while every existing integration test goes through the `ChatService` / `MessageRouter` path — which used the copy that *had* been updated. In other words: the one path that reads the wrong format was exactly the one path with no tests. `scripts/e2e-verify.ps1` has no `/connect` assertion either (its Phase 2 covers the `/msg` private-chat round trip).

There is a deeper cause, and it dictates the fix: **the third copy was forced by the dependency graph.** `P2PChat.UI` references only `P2PChat.Core` and **does not reference `P2PChat.Chat`**, so the TUI architecturally could not call `MessageRouter.DeserializeEnvelope`. Whoever copied it was not careless — copying was the only route available at the time.

## Decision

The codec converges onto **the root of the dependency graph**, `P2PChat.Core.Extensions.EnvelopeCodec`, as the single source of truth for the envelope wire format. Core references no project, while Chat, UI, Networking, Crypto and FileTransfer all reference Core — the only place where both the sending side and the receiving side can reference the same code.

- `EnvelopeCodec` provides `Serialize` / `Deserialize` / `ComputeSignedBytes`, plus `FixedHeaderSize` (50) and `MaxFieldLength` (100 MB).
- `MessageRouter.SerializeEnvelope` / `DeserializeEnvelope` are kept but demoted to **public thin forwarders** onto `EnvelopeCodec`; existing callers (`ChatService`, `GroupChatService`, tests) need no change. `ChatService`'s private forwarder is kept too.
- `P2PChatTui.ReadHelloResponseAsync` now calls `EnvelopeCodec.Deserialize(raw)` directly.
- The two hand-written implementations are deleted. The wire format (after phase 3.2) is:

```text
[1B Version][1B MessageType][4B Seq(BE)][20B SenderId][16B MessageId]
[8B Timestamp(BE)][4B PublicKeyLen(BE)][N PublicKey][4B SignatureLen(BE)][M Signature][Payload]
```

The same place settles what malformed input means: deserialization throws `InvalidDataException` when the envelope is too short, a length prefix is truncated, a declared length exceeds `MaxFieldLength`, or a declared length exceeds the bytes actually remaining. Preferring to drop the whole message beats letting an out-of-range `Slice` throw a semantically meaningless `ArgumentOutOfRangeException`, and beats letting one malformed length prefix drive a huge allocation. All length comparisons run in `long` arithmetic, so a `uint` → `int` truncation cannot turn a length above `int.MaxValue` negative and slip past the check.

The AOT constraint is untouched: plain `BinaryPrimitives` reads and writes, no reflection, no dynamic code, and no new serialized type needing registration.

**Why not "just sync it once more"**: syncing once zeroes only this incident; the next format change still needs someone to remember three sites by hand. And the copy is structural — as long as "the implementation lives in Chat" remains true, the temptation to copy one into UI remains too, so the incident will recur. Convergence changes the structure, not people's memory.

## Alternatives considered

**Leave the codec in the Chat layer (keep it in `MessageRouter`, promote `DeserializeEnvelope` from private to public).** Rejected: that only answers "can UI see it", not "does UI want to depend on Chat". To get a byte-level parser, the whole Chat layer — session orchestration plus encryption plus routing — becomes a UI dependency, and the coupling cost is wildly out of proportion to the benefit. UI (views) and Chat (session orchestration) should not reference each other anyway.

**Add a ProjectReference from `P2PChat.UI` to `P2PChat.Chat`.** Rejected: same problem, one step worse — it trades the technical problem of "two consumers sharing one implementation" for the architectural problem of "UI depends on the whole Chat layer", dragging in the Crypto / Networking transitive dependencies as well.

**Keep maintaining multiple copies and just re-sync the UI one.** Rejected: see "why not just sync once more" above. That copy was not produced by carelessness; it was forced by the dependency graph. After one re-sync, the next format change drifts it again — and the cost of that drift (a user-visible feature that cannot work) far exceeds the cost of syncing.

**Create a neutral project (e.g. `P2PChat.Wire`) dedicated to the wire format.** Rejected: cleaner layering in principle, but Core already references nothing and already owns `Models` / `Extensions`. Adding a project-reference tier for one static class only lengthens the dependency graph and slows the build, without buying any extra isolation.

## Consequences

- The hello response for `/connect <ip:port>` now goes through the **same** parser as every other send and receive path; the next wire-format change needs one edit.
- `MessageRouter.SerializeEnvelope` / `DeserializeEnvelope` drop from "the only implementation" to "public thin forwarder": existing callers and tests change nothing and behave equivalently.
- **Cost: one malformed frame ends that connection's receive loop.** The catch around `DeserializeEnvelope` in `ProcessIncomingConnectionAsync` logs an error and `break`s. This is the direct consequence of the "fail loudly" contract — a clear failure beats silently parsing garbage, but it is not "drop one frame and keep receiving".
- **Cost: Core now holds a type that knows byte layout** (endianness, length prefixes). Core was previously more about models and abstractions. This is a deliberate trade: the wire contract is knowledge shared by every layer, and only at the root of the graph can it avoid being copied.
- **Cost: sender and receiver now share one code path.** That is the point (otherwise this incident is what happens), but it also means every change to `EnvelopeCodec` must be reasoned about on both sides.

## Testing

- `tests/P2PChat.Core.Tests/EnvelopeCodecTests.cs`: 13 test methods (12 `[Fact]` plus one `[Theory]` with three `[InlineData]` rows), 15 cases in total, covering the fixed header size, a field-by-field round trip with public key and signature, the payload start when there is neither (exactly the shape of the TUI's old bug), an empty payload, absent fields deserializing to `null`, the coverage span of `ComputeSignedBytes`, and five classes of malformed length prefix that must throw `InvalidDataException`.
- Structural protection: the implementation lives in Core, so Chat and UI **must** reference the same code. The XML comment at both consumption points (`MessageRouter` and `P2PChatTui.ReadHelloResponseAsync`) states that there is only one implementation of the envelope format and that a parser must never be copied into that file again, and records this incident.

## Deferred

- **Replay protection is still missing.** `MessageRouter.VerifyEnvelope` does exactly four things: require a signature, require the sender public key, require `NodeId.FromPublicKey(SenderPublicKey) == SenderId` (blocking `SenderId` impersonation), and verify the ECDSA signature. `SequenceNumber` **is written but never validated**, and `Timestamp` **is signed but never checked for freshness** — so a captured, valid frame can be replayed indefinitely. This is **not fixed now**: a sequence window has to handle out-of-order arrival and the sequence baseline after a restart, and a time window has to handle clock skew. Both change existing protocol semantics and belong in a standalone wire change rather than being slipped into a refactor.
- The `/connect` hello round trip still has no automated end-to-end coverage: `P2PChatTui` has no test project reference and the e2e script has no `/connect` assertion. Today it rests on the unit coverage in `EnvelopeCodecTests` plus manual verification.

## Related

- [Message signing](./2026-09-21-message-signing.md) — phase 3.2 self-signing shipped, and it is the change that triggered this incident.
- [Message SenderId must derive from the node identity](./2026-09-20-message-sender-identity.md) — where the "derived NodeId must equal SenderId" anti-impersonation guard in `VerifyEnvelope` comes from.
- [/connect <ip:port> — blind connect](../feature/2026-09-21-blind-connect.md) — this defect broke exactly that path.
