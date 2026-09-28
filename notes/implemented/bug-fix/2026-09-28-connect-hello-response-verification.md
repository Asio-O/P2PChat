# Agent Note: /connect's hello response was never verified (unauthenticated peer introduction)

Status: implemented

## Problem

`/connect <ip:port>` is a blind connect — "**I only know the endpoint, not who the peer is**; connect and wait for it to answer first". The implementation of `P2PChatTui.ReadHelloResponseAsync` (`P2PChatTui.cs:660-673`) is:

```csharp
var raw = await connection.ReceiveMessageAsync(ct);
var envelope = EnvelopeCodec.Deserialize(raw);
if (envelope.MessageType != MessageType.KeyExchange) continue;
var msg = serializer.Deserialize<Message>(envelope.Payload);
if (msg is KeyExchangeMessage { IsResponse: true } response)
    return (response, envelope.SenderPublicKey);
```

**There is no verification call anywhere in it.** On this path, any host that answers before the real node is trusted unconditionally.

The consequences amplify down the call chain (every item below is a fact about the current code):

1. **Identity comes from attacker-controlled payload.** The caller then does `var peerId = new NodeId(response.SenderId);` (`:582`) — `response` was deserialized from `envelope.Payload`, and `SenderId` inside the payload is filled in by the peer. The irony is that this very method's comment at `:657` states that "the `message.SenderId` in the payload is peer-controlled input and **must not** be used to derive identity" — and its caller does exactly that.
2. **The session key is derived under an identity the attacker chose.** `DeriveSharedSecret(ephemeral.PrivateKey, response.EphemeralPublicKey)` (`:586`) uses the attacker's ephemeral public key, and the resulting session key is written with `SetSessionKey(peerId, sessionKey)` — filed under the attacker-chosen `peerId`.
3. **The attacker is registered as a static peer and persisted as a contact.** `RegisterStaticPeer` (`:591-603`) writes `PeerId`, `EndPoint` and `PublicKey = peerPublicKey` into the routing table, and `AddContactAsync` then persists it.

The third one deserves singling out: `PublicKey` is taken from that **unverified** envelope, and `NodeId.FromPublicKey(PublicKey)` is exactly what the routing-table / static-peer layer uses for its anti-impersonation check. An attacker can therefore register a static peer carrying a public key that **does not match its own claimed `NodeId`**, rendering that layer's identity-binding guard useless on this path.

This is a **different kind of problem** from [inbound replay protection](../feature/2026-09-28-replay-protection.md), and neither substitutes for the other: replay protection handles "**repeated delivery by an already-authenticated** peer", while this is "**the identity itself was never authenticated**". Replay protection, however strict, can say nothing about an envelope that was never verified — it answers "is this message arriving for the first time", not "is this person who they claim to be".

## Decision

Envelope verification converges onto the root of the dependency graph, following **exactly the same pattern** as [the EnvelopeCodec convergence](./2026-09-28-envelope-codec-single-source.md):

1. A new `src/P2PChat.Core/Extensions/EnvelopeVerifier.cs` puts the verification logic in Core. Core references no project and both Chat and UI reference it — the only place where two consumers can share one implementation.
2. `MessageRouter.VerifyEnvelope` delegates to it while **keeping its existing `public static` signature unchanged**. The signature must not move: `MessageSigningTests` has 5 direct call sites of `MessageRouter.VerifyEnvelope(`, and changing it would blow up a batch of cryptographically correct tests.
3. `P2PChatTui.ReadHelloResponseAsync` references the Core implementation instead, and **returns only after verification passes**; a failure means no return and no adoption.
4. **Any downgrade must be explicit**: when verification fails, `/connect` clearly reports that the peer's identity could not be verified and fails — it **never silently falls back to not verifying**. A silent fallback leaves the hole exactly where it was while the user remains unaware, and a "looks like it connected" outcome is far more dangerous than a clear failure.
5. The XML comment claiming this path is already verified is corrected (see `## Comment discipline`).
6. A **structural guard test** is added: no self-implemented envelope parsing or verification logic may reappear in `P2PChatTui`. The point of that guard is to prevent a isomorphic recurrence — the defect was caused by "copying another implementation into the UI", and without a test that kind of change nearly always happens again.

Once verification actually runs, the `NodeId.FromPublicKey(publicKey) == SenderId` binding check takes effect automatically (it was already part of the verification flow), so an attacker can no longer register a static peer whose public key disagrees with its NodeId.

## Alternatives considered

**Use the TCP connection itself as the credential** ("I reached this IP:port, so it must be them"). Rejected, and the reason this intuition is wrong is worth stating plainly: TCP only guarantees that a byte stream reached *the process listening on that IP and port*; it does not guarantee that *process is our node*. At least three scenarios let someone else answer first: other hosts behind the same NAT (the peer sees one shared public source address), different containers / VMs on the same subnet, and another process on the user's own machine — compromised or simply another instance. And `/connect`'s argument is precisely a **bare endpoint**: the user may have just copied an IP:port from chat, an announcement or a log, and that carries no anchor for cross-checking identity. Treating transport reachability as identity evidence conflates "reachable on the network" with "identity confirmed".

**Reuse the explicit trust model of `/add <nodeId> <ip:port>`** (the user names the node, so we trust the user). Rejected: the two paths draw trust from **entirely different sources**. In `/add` the user supplies the NodeId explicitly, so what is trusted is "**the user asserts this person is it**" — an out-of-band human assertion. The entire value of `/connect` lies in the fact that **the user does not know who the peer is and is asking precisely in order to find out**. Wrapping `/add`'s model around it yields "the user gave an endpoint, therefore we may trust the peer's identity" — but what the user gave is an **address**, not an **identity**. The two cannot be mixed.

**Call `MessageRouter.VerifyEnvelope` directly from the UI layer.** Rejected, and the mechanism is the dependency graph: `MessageRouter` belongs to the Chat layer, while `P2PChat.UI` references only `P2PChat.Core` and **does not reference `P2PChat.Chat`** (`Agent.md` §2.2 dependency matrix). The UI simply cannot reach it.

**Implement verification inside the UI ourselves.** Rejected — and this is the option this defect most needs recorded. The cause of this defect is precisely that "envelope codec was copied into the UI layer and then failed to follow the signing rollout"; writing another copy of verification into the UI now would be an **isomorphic recurrence**: the same layering mistake, the same future drift point, and nothing at all preventing it from happening again. The correct move is to **converge** the implementation into Core, not to copy it once more into a consumer.

**Add a ProjectReference from `P2PChat.UI` to `P2PChat.Chat`.** Rejected: technically it would let the UI reach `VerifyEnvelope`, but the price is that the view layer would depend on the entire session-orchestration layer (plus the transitive Crypto / Networking dependencies), and it solves "can the UI see it" without solving the root problem of "two consumers sharing one implementation". Converging into Core solves both at the cost of one static class.

## Consequences

- `/connect` goes from "**trust first, verify later**" to "**verify first, then trust**": the peer's identity must be proven with a private key it actually holds, and registering the static peer plus persisting the contact both happen after verification.
- A forged hello response is rejected. Note that this attack case is harder to write than one might expect: an envelope self-signed by the attacker with **their own** private key is cryptographically **perfectly valid** — it simply should not be accepted for this endpoint. "The verification function returned true" and "this peer is trustworthy" are therefore two different things, and a test must construct the latter.
- **Cost: offline / older peers stop connecting.** Verification only shipped with phase 3.2; a hello response from a node built before it carries no signature and is rejected outright. This is the **correct** failure (an identity that cannot be proven should not be trusted), but it presents to the user as "it used to connect and now it doesn't", so the error message has to say why rather than leaving them guessing at "connection timeout".
- The `PublicKey` field changes from "an unverified self-description by the peer" to "the long-term public key from a verified envelope, strongly bound to the NodeId", restoring the anti-impersonation guard at the routing-table / static-peer layer on this path.
- `MessageRouter.VerifyEnvelope` deliberately keeps its `public static` signature as a forwarding facade, at the cost of the verification implementation living in Core while its entry point stays in Chat. The benefit is that existing callers and tests change nothing.

## Comment discipline

What this defect exposes is not merely "verification was forgotten"; it is an **actively misleading comment**. The XML comment on `ReadHelloResponseAsync` itself (`:653-657`) says:

> Taken from the `MessageEnvelope.SenderPublicKey` of that **already-verified** response envelope. […] and `MessageRouter.VerifyEnvelopeCore` already enforces `NodeId.FromPublicKey(publicKey) == SenderId`, so the "public key ↔ identity" binding is trustworthy.

But this path **never calls `VerifyEnvelope` / `VerifyEnvelopeCore` at all**. The same claim appears once more at the call site (`:595-597`). Whoever wrote those comments cited an invariant that genuinely holds **elsewhere**, but that invariant was **never cashed in on this path**.

The rule that follows, and that should hold from here on: **a guarantee claimed by a comment must actually exist on the current code path.** Referencing an invariant elsewhere ("`VerifyEnvelopeCore` already guarantees X") is legitimate only if **this path really calls it**; otherwise it is not a reference but a well-sounded-sounding untruth.

That makes it more dangerous than having no comment at all, because **it changes what the reader does**: with no comment, a reader will go look at what this path actually does; with that comment, the reader **skips the check** and continues downstream carrying the prior belief that "this has already been verified" — and everything downstream (static peer registration, public key persistence, contact persistence) rests on that prior belief. It makes the defect **harder to discover**, not merely harder to understand.

## Deferred

- `ChatService.ReadKeyExchangeResponseAsync` likewise reads the key-exchange response directly off the connection and returns, **without passing through `RouteIncomingAsync`**, and therefore also without replay protection. The impact is lower (that response is only used to derive a local session key and produces no business side effect), but it shares this defect's root and belongs to the "bypasses the unified inbound pipeline" family of problems. It should be reviewed together with this change rather than left as a second, separate exception.
- The `/connect` round trip still has no automated e2e assertion (Phase 2 of `scripts/e2e-verify.ps1` covers the `/msg` private-chat round trip).

## Related

- [Envelope wire codec converged onto a single source of truth in Core](./2026-09-28-envelope-codec-single-source.md) — the same pattern and the same root cause: a codec copied into the UI layer failed to follow the signing rollout, so `/connect` could not work once signing shipped. This defect is its isomorphic recurrence, so the solution is the same.
- [Inbound replay protection](../feature/2026-09-28-replay-protection.md) — handles "repeated delivery by an authenticated peer", a different problem from "the identity itself was never authenticated"; neither substitutes for the other.
- [/connect <ip:port> — blind connect](../feature/2026-09-21-blind-connect.md) — the path this defect broke.
- [Message signing](../bug-fix/2026-09-21-message-signing.md) — phase 3.2 self-signing shipped; this defect is not the signing mechanism's fault, but a new capability that was never wired into a newly added call path.
