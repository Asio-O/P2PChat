# Agent Note: /connect's hello response was never verified at all (unauthenticated peer introduction)

Status: implemented

## Problem

`/connect <ip:port>` is a blind connect — "**I only know the endpoint, not who the peer is**; connect and wait for it to answer first". The implementation of `P2PChatTui.ReadHelloResponseAsync` before the fix was:

```csharp
var raw = await connection.ReceiveMessageAsync(ct);
var envelope = EnvelopeCodec.Deserialize(raw);
if (envelope.MessageType != MessageType.KeyExchange) continue;
var msg = serializer.Deserialize<Message>(envelope.Payload);
if (msg is KeyExchangeMessage { IsResponse: true } response)
    return (response, envelope.SenderPublicKey);
```

**There was no verification call anywhere in it.** Any host that answered before the real node was trusted unconditionally. The consequences amplify down the call chain:

1. **Identity was taken from attacker-controlled payload.** The caller then did `new NodeId(response.SenderId)` — while `response` was deserialized from `envelope.Payload`, and `SenderId` inside the payload is filled in by the peer.
2. **The session key was derived under an identity the attacker chose.** ECDH used the attacker's ephemeral public key, and the result was filed with `SetSessionKey(peerId, sessionKey)` under the attacker-chosen `peerId`.
3. **The attacker was registered as a static peer and persisted as a contact.** `RegisterStaticPeer` wrote `PublicKey = peerPublicKey` into the routing table along with it.

The third is the most dangerous: `PublicKey` came from that **unverified** envelope, and `NodeId.FromPublicKey(PublicKey)` is exactly what the routing-table / static-peer layer uses for its anti-impersonation check. An attacker could therefore register a static peer carrying a public key that **does not match its own claimed NodeId** — that layer's guard is not "weakened" on the `/connect` path, it is **bypassed**.

This is a different kind of problem from [inbound replay protection](../feature/2026-09-28-replay-protection.md): replay protection handles "**repeated delivery by an already-authenticated** peer", while this is "**the identity itself was never authenticated**". Neither substitutes for the other.

## Decision

**Verification converges onto the root of the dependency graph** (`Core/Extensions/EnvelopeVerifier.cs`, following the same pattern as [the EnvelopeCodec convergence](./2026-09-28-envelope-codec-single-source.md)): `EnvelopeVerifier.Verify` keeps four checks (signature non-empty, public key non-empty, `NodeId.FromPublicKey(publicKey) == SenderId`, and ECDSA verification over `EnvelopeCodec.ComputeSignedBytes`), and is **stateless and re-callable**. `MessageRouter.VerifyEnvelope` now delegates to it with its **signature unchanged** (`VerifyEnvelopeCore` is likewise kept as a delegating shell), so existing callers and tests change nothing.

On top of that, admission of the `/connect` response is a chain of **three criteria**, each blocking something different:

- **Criterion ①: `ConversationId` echo check.** The hello carries a one-time correlation identifier, and the response must echo it verbatim.
- **Criterion ②: ECDSA verification.** Once it passes, the peer's identity is derived **entirely from the public key** (`PeerNodeId = NodeId.FromPublicKey(peerPublicKey)`), never from the payload's `SenderId`.
- **Criterion ③: endpoint→identity continuity.** `EnvelopeVerifier.EvaluatePeerIdentity` returns one of three verdicts: `FirstContact`, `MatchesExistingPin`, and `ConflictsWithExistingPin` (**reject**).

### Why criterion ① cannot be left to `IReplayGuard`

This path **never reaches the router** — it reads the response off the TCP connection it holds itself and never passes through `RouteIncomingAsync`, so `IReplayGuard` does not run here at all. And replay protection is **local state**: the victim node has never seen the replayed `MessageId`, it is absent from the dedup cache, so it is **let through**. The attacker knows their own ephemeral private key and can therefore derive the victim's session key. Criterion ① is consequently **stateless**: it compares against the one-time identifier just generated for this call, not against any local history.

That yields a conclusion worth remembering: **if someone later wires `/connect` through the router, replay protection will not automatically cover it — criterion ① remains necessary.** It blocks cross-victim replay, which is a different attack from repeated delivery to the same victim.

### Pin-source discipline

`KnownEndpointIdentities()` takes as its "endpoint → identity" bindings **only entries from `dhtService.GetAllKnownNodes()` whose `PublicKey` is non-empty**, and **deliberately does not read `contacts.json`**. The reason: a contact stores a NodeId and ip:port that **the user themselves wrote down** — that is the user's *intent*, not "an identity we verified with our own eyes"; treating it as a pin would promote a user's typo into a security assertion. Only peers that have actually completed a `/connect` (or obtained a public key via DHT) are registered carrying a public key, which is why the non-empty filter is the right one.

What this discipline guards against is not an attacker; it is **treating a weaker assertion as a stronger one** — the same family of error as "evidence with a narrower scope written up as a broader conclusion", except that here it shows up in the **choice of data source**.

### Conflict beats match

`EvaluatePeerIdentity` **must not early-return at the first match**: it scans every binding, and any conflict decides the outcome. Ordinary dedup logic lets a match through; here it has to be the other way round — a self-contradictory local record (same endpoint with both a matching and a non-matching identity) already means the binding data is corrupt, and "one of them lined up" is not enough to let it through. **Prefer rejection.** This is a deliberate asymmetry.

### TOFU and downgrade behaviour

First contact is TOFU in the information-theoretic sense: an envelope signed with the attacker's own private key is cryptographically **indistinguishable** from a legitimate node's. So on first contact the UI must **tell the truth** — that the endpoint is first contact and its identity has not been verified out of band — and prompt the user to check the NodeId through a trusted channel. It **must never display "verified" or "identity is trustworthy"**. On conflict the default is to reject; the user may add `--force` to let **this one** through, and `--force` **does not permanently rewrite** the registered identity.

If any criterion fails, a `HelloRejectedException` is thrown; the UI catches it separately and states explicitly that nothing was registered — no session key and no static peer — and **never silently falls back to not verifying**. A silent fallback leaves the hole exactly where it was while the user still believes they connected to the intended node.

A **structural guard test** is added as well: no self-implemented envelope parsing or verification logic may reappear in `P2PChatTui`. The defect was caused precisely by "copying another implementation into the UI", and without a test that kind of change nearly always happens again.

## Alternatives considered

These are the options actually rejected in this round. The first few are common intuitions; the mechanisms that rule them out are not complicated, but each one would quietly cost a specific security property.

**Verify only, with no endpoint→identity continuity (believing a passing signature is enough).** Rejected — and this is the one most likely to be mistaken for sufficient. Verification only proves "the responder holds the private key for the public key it presented, and the identity it claims is self-consistent with that key": **an envelope the attacker self-signed with their own key is cryptographically perfectly valid**, and no field distinguishes it from a legitimate node. First contact therefore has no basis for rejection at all; the only usable information is "the identity we ourselves recorded for this endpoint earlier". Criterion ③ is **the only thing on this path that can actually reject a self-signed attacker**.

**Use contacts from `contacts.json` as pins** (the contact table is the most convenient source, it already has NodeId and ip:port). Rejected: that is the user's *intent*, not an identity we verified ourselves. Using it as a pin promotes the out-of-band human assertion "the user says this is it" into "the system has verified that this identity is bound to this endpoint" — the two are very different in strength, and once they are conflated, logs and warnings start saying things they have no basis for.

**"If it matches, let it through" on conflict (first-match-wins).** Rejected: a self-contradictory local binding record is itself an anomaly signal, and letting it through treats "the data is corrupt" as "verification passed". Conflict beating match is a deliberate **asymmetry**.

**Have `--force` rewrite the registered identity** (the user confirms once, so fix the new identity). Rejected: `--force` only exempts **this session's** conflict check. A one-time exemption and a permanent rewrite are different things — the latter turns a single misjudgement into the baseline for every later connection to that endpoint.

**Let criterion ① lean on `IReplayGuard`** (replay protection already exists, no need for an echo check). Rejected: `/connect` does not go through the router, so `IReplayGuard` never runs on this path in the first place; and even if it did, replay protection is local state and is equally ineffective against **cross-victim** replay (the victim has never seen that `MessageId`). The echo comparison is stateless, which is why criterion ① exists independently.

**"Use the TCP connection itself as the credential."** Rejected: TCP only guarantees that a byte stream reached *the process on that IP and port*; it does not guarantee that *process is our node*. Other hosts behind the same NAT, different containers / VMs on the same subnet, and another process on the user's own machine can all answer first.

**"Reuse the explicit trust model of `/add <nodeId> <ip:port>`."** Rejected: the two paths draw trust from different sources. In `/add` the user supplies the NodeId, so what is trusted is "**the user asserts this is it**"; the entire value of `/connect` lies in the fact that **the user does not know who the peer is and is asking precisely in order to find out**. What the user gives is an **address**, not an **identity**.

**"Call `MessageRouter.VerifyEnvelope` directly from the UI layer."** Rejected: `MessageRouter` belongs to the Chat layer, while `P2PChat.UI` references only `Core` and cannot reach it.

**"Implement verification inside the UI ourselves."** Rejected — and this is the wrong option this defect most needs recorded: the cause was precisely that a copy was made in the UI and then failed to follow the signing rollout, so copying once more would be an **isomorphic recurrence**. The correct move is to converge into Core.

**"Add a ProjectReference from `P2PChat.UI` to `P2PChat.Chat`."** Rejected: it binds the view layer to the session-orchestration layer (plus the transitive Crypto / Networking dependencies), and it only solves "can the UI see it", not the root problem of "two consumers sharing one implementation".

## Consequences

- `/connect` goes from "**trust first, verify later**" to "**verify first, then trust**": the response must echo this call's correlation identifier, pass verification, and not conflict with the endpoint identity this machine already has on record before a session key may be written, a static peer registered, or a contact persisted.
- **Cost: offline / older peers stop connecting.** Verification only shipped with phase 3.2, so a response from a node built before it carries no signature and is rejected outright; the echo check also rejects older implementations that do not implement that convention. This is the **correct** failure (an identity that cannot be proven should not be trusted), but it presents as "it used to connect and now it doesn't", so the error message has to say why rather than leaving the user guessing at "connection timeout".
- **First contact is still TOFU.** Criterion ③ only has a basis when "this machine has recorded an identity for this endpoint before"; **without history there is no basis**, and all that is left is to trust and say so honestly. Any wording that renders a TOFU result as "peer identity verified" is wrong.
- **`--force` is the user's explicit judgement, not the system's conclusion.** It exempts the conflict check once, so a socially engineered "I'm sure it's fine" can let one wrong identity through; a `LogWarning` remains in the log, but the system cannot distinguish it from a genuine rejection.
- **`PublicKey` changes from "an unverified self-description by the peer" to "the long-term public key from a verified envelope, strongly bound to the NodeId"**, restoring the anti-impersonation guard at the routing-table / static-peer layer on this path.
- **`MessageRouter.VerifyEnvelope`'s `public static` signature is deliberately kept as a forwarding facade**, at the cost of the verification implementation living in Core while its entry point stays in Chat; the benefit is that existing callers and tests change nothing.
- **"What it does not prove" is written in three places, and that redundancy is deliberate.** The class comment on `EnvelopeVerifier` (the ✅ / ❌ list) is where the semantics are actually defined; the notes on `ReadHelloResponseAsync` and `ConnectCommandAsync` are on-site reminders for readers who only ever see that one piece. Deleting any one of them would let some maintainer who reads only a fragment fall into the same hole again — see `## Comment discipline`.

## Comment discipline

The core of this defect is not "verification was forgotten"; it is an **actively misleading comment**. Before the fix, the XML comment on `ReadHelloResponseAsync` said:

> Taken from the `MessageEnvelope.SenderPublicKey` of that **already-verified** response envelope. […] and `MessageRouter.VerifyEnvelopeCore` already enforces `NodeId.FromPublicKey(publicKey) == SenderId`, so the "public key ↔ identity" binding is trustworthy.

Yet this path **never called** `VerifyEnvelope` / `VerifyEnvelopeCore` at all. The same edit added the identical claim once more at the call site.

The rule that follows: **a guarantee claimed by a comment must actually exist on the current code path.** Referencing an invariant elsewhere ("`VerifyEnvelopeCore` already guarantees X") is legitimate only if **this path really calls it**.

That is more dangerous than having no comment, because **it changes what the reader does**: with no comment, a reader goes and looks at what the path actually does; with that comment, the reader **skips the check** and carries on downstream holding the prior belief that "this has already been verified" — and everything downstream (writing the session key, registering the static peer, persisting the public key and the contact) rests on that prior belief. It makes the defect **harder to discover**, not merely harder to understand.

There was a second, same-family problem in the same body of comments: a comment stated that "the `message.SenderId` in the payload is peer-controlled input and **must not** be used to derive identity", while its caller did exactly `new NodeId(response.SenderId)`. Both sat in the same file, in the same method.

The fix was more than editing the comment: those two are now respectively an **explicit invariant in the code** (an order-invariant comment stating "never move verification below the point of use" and "identity must be derived from the public key") and a **guard test named after the problem itself**.

## Testing

- `tests/P2PChat.Chat.Tests/EnvelopeVerifierTests.cs` — **11 cases** (244 lines). Covers a forger who self-signs with a perfectly valid private key but impersonates a third party being rejected; that when such a response is rejected the identity derived from the public key is never the third party (so the session key is not filed under a friend's name); **that a self-consistent stranger passes verification — the legitimate starting point of TOFU**; the branches of the endpoint identity verdict (first contact / continuity holds / identity change is a conflict / another endpoint's binding does not affect this one / self-contradictory local records are treated as a conflict / invalid arguments throw), plus delegation consistency (`MessageRouter.VerifyEnvelope` must agree exactly with `EnvelopeVerifier`, and missing signature or public key must produce the same Chinese failure reasons as the existing tests).
- `tests/P2PChat.Integration.Tests/HelloResponseVerificationTests.cs` — **8 cases** (272 lines). Including `TUI的hello响应必须经过Core的EnvelopeVerifier验签`, `TUI的hello路径必须校验应答回显了本次的关联标识`, `TUI必须按端点身份裁决分流_冲突时默认拒绝`, `首次接触的提示必须写明未经带外验证_不得声称已验证`, plus two structural guards against re-copying an implementation.

**The naming convention is deliberate**: `TUI必须从公钥派生对端身份_不得使用载荷里的SenderId`, `TUI不得再声称VerifyEnvelopeCore替它守过身份绑定`, `自洽的陌生主机可以通过验签_这是TOFU的合法起点` — these test names **are** the problem statement, so scanning the test list immediately shows what each one prevents. That is far more useful than `TestVerifyX`: the whole value of a structural guard is that "it goes red when the next person breaks it", and if the reason for the red light is buried in the method body, the red light is just noise requiring archaeological work.

Full gates (re-run and confirmed by the Lead): `dotnet build` 0 errors / 0 warnings, `dotnet test` **356 passing / 0 failing**, AOT publish 0 IL warnings in our own code, e2e `PASS=41 / FAIL=0`.

## Deferred

- `ChatService.ReadKeyExchangeResponseAsync` likewise reads the key-exchange response directly off the connection and returns, **without passing through `RouteIncomingAsync`**, and therefore also without replay protection. The impact is lower (that response is only used to derive a local session key), but it belongs to the same "bypasses the unified inbound pipeline" family as this defect and should be reviewed together with this change rather than left as a second, separate exception.
- The structural guard tests assert **source text / structure**, so they are sensitive to purely cosmetic refactors. This session produced one live instance: a harmless refactor turned two guards red because the text no longer matched. The trade is "false red rather than let an isomorphic recurrence through", but a maintainer needs to know what to suspect first when they go red.
- The `/connect` round trip still has no automated e2e assertion (Phase 2 of `scripts/e2e-verify.ps1` covers the `/msg` private-chat round trip).

## Related

- [Envelope wire codec converged onto a single source of truth in Core](./2026-09-28-envelope-codec-single-source.md) — the same pattern: a codec copied into the UI layer failed to follow the signing rollout, so `/connect` could not work once signing shipped; the verification convergence is structurally identical to it.
- [Inbound replay protection](../feature/2026-09-28-replay-protection.md) — handles "repeated delivery by an authenticated peer", while this handles "the identity itself was never authenticated". Criterion ① also exists because of it: that path never reaches the router.
- [/connect <ip:port> — blind connect](../feature/2026-09-21-blind-connect.md) — the path this defect broke; its "security model" comment has since been filled in.
- [Message signing](../bug-fix/2026-09-21-message-signing.md) — phase 3.2 self-signing shipped; this defect is not the signing mechanism's fault, but a new capability that was never wired into a newly added call path.
