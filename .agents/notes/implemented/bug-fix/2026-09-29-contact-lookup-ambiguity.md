# Agent Note: Ambiguous short NodeId prefixes resolved to the first match (silent misdelivery in `/msg` and `/file send`)

Status: implemented

## Problem

`P2PChatTui` resolves the `<联系人|节点ID>` argument of `/msg` and `/file send` against the local contact list — exact alias match first, otherwise NodeId hex prefix — and it took the **first** match. Two distinct NodeIds are 20 bytes each, so 40 hex characters of equal length, and therefore never a prefix of one another: a **full** 40-character NodeId resolves to at most one contact. Ambiguity is reachable only from a **shorter** input, and when it is reached the resolution is silent — no rejection, no warning, no log line, and nothing at the call site that distinguishes a unique match from a collision.

The harm is not simply "the message went to the wrong person". The message is encrypted with the **wrong** contact's session key and delivered to the wrong peer, and for that peer it is **readable**, because that peer holds the key. The intended recipient never receives it, and the user believes it was sent. What does **not** follow is plaintext on the wire: an on-path observer still sees ciphertext under a session key they do not hold. The break is confidentiality **towards the peer that was silently substituted**, not the secrecy of the link.

Two further facts make it hard to notice and hard to reproduce:

- The local echo line `我 -> <别名>: <文本>` is emitted **after** the send and independently of whether the send succeeded, so it is not a signal. It prints the substituted peer's alias in the same shape as a correct send, with nothing marking it as a defect.
- The ambiguity surface **grows over time**. A successful `/connect` persists the peer into the contact list automatically, with an alias of the form `peer-{hex[..8]}`, and that contact list is the input set for this resolution. Every automatic registration adds another entry against which a short prefix can collide.

## Decision

**One invariant: ambiguity must refuse to send.** The resolution lives at `Core.Extensions.ContactResolver` as the single implementation; the TUI calls it and does not re-implement it. Alias-exact first, then NodeId prefix:

- an alias matching **exactly one** contact resolves to it;
- otherwise the NodeId prefix must match **exactly one** contact;
- **more than one match is ambiguity, and the caller must not send.** The resolution carries the complete candidate list, and `P2PChatTui.ReportContactLookupFailure` refuses, names the count, lists candidates with their short id, alias and endpoint, and states that a longer NodeId or an exact alias removes the ambiguity.

Two distinct NodeIds are equal-length 40-hex strings and neither can be a prefix of the other, so **only inputs shorter than 40 characters can be ambiguous** — exactly the set this refusal covers. Duplicate aliases are refused the same way: `/add` does not enforce alias uniqueness, so "several contacts share this alias" is a reachable state, and choosing among them on the user's behalf is the same defect.

Alias matching is tried before prefix matching because an alias is something the user named, while a short hex prefix is a substring of an identifier that a growing list of automatically registered peers keeps extending.

### Making the sort deterministic is not a fix

`ContactService.GetAllContactsAsync` orders by online state then alias, and the alias ordering uses the default string comparer. Adding an explicit comparer there is the change a maintainer is most likely to reach for after reading this, and it is not a fix: it makes the choice **reproducible** rather than correct, and a party able to influence which contact sorts first keeps the whole chain. The reusable rule:

> **A deterministic sort is not a correct selection.**

This is also why the resolver asserts nothing about ordering: the only question it answers is whether the input denotes a **unique** contact among the ones supplied.

### Why the resolution sinks to Core

The reason is the one that put `EnvelopeCodec` and `EnvelopeVerifier` in Core, and it is structural rather than stylistic. `P2PChatTui` cannot be constructed in-process — it depends on real console state — and **no project under `tests/` references `P2PChat.UI`**. The resolution was therefore a private method with no behavioural test and, short of adding an assembly dependency, no way to get one: under that shape only a deliberately constructed ambiguous input exposes the defect, and there was nowhere to construct one. The logic is a pure function with no UI dependency, so placing it in a layer that can be referenced is the only way to give it tests at all.

### What resolution does not prove

Resolving to a contact does **not** mean that contact is the one the user had in mind. The contact list is local storage whose integrity is a separate question — entries typed in by hand through `/add`, and entries a `/connect` TOFU acceptance persisted automatically. `ContactResolver` guarantees one thing: **among the contacts supplied, the input denotes at most one.** No user-facing wording may render the outcome as an identity verification.

## Alternatives considered

**Add an explicit `StringComparer.Ordinal` to `ContactService`'s ordering.** Rejected: it makes the outcome deterministic, not correct. The defect is that a choice was made at all; a stable order still makes one, and whoever influences which contact lands first keeps the result. A deterministic sort is not a correct selection.

**Require a minimum prefix length, refusing anything shorter than some number of hex characters.** Rejected: the collision cost does fall off steeply as the prefix shortens, but that is an order-of-magnitude judgement with no measurement behind it, and a hard-coded digit count becomes an assertion that reads as authoritative and expires silently as the contact list grows. It substitutes a false-precision rule for the real invariant, and it also refuses inputs that are genuinely unambiguous.

**Keep the first match and warn in the UI.** Rejected: a warning does not change behaviour, and the damage is done before any warning could be shown — the message has been encrypted under the substituted peer's key and sent. Making the send conditional on the resolution is the entire decision.

**Give the UI project a test `ProjectReference` and cover the private method where it lives.** Rejected: it works, and it buys testability with a product-assembly dependency edge plus a permanently awkward placement. The rule from the convergence decisions applies: the fix for "one consumer cannot reach a shared implementation" is to move the implementation, not to widen the dependency graph. The edge also leaves the logic somewhere no other caller can reach.

**Ask the user to choose among ambiguous candidates, defaulting to the first if they decline.** Rejected: it keeps "pick one" as a reachable outcome. Refusal is the only state that cannot be reached by inattention, mis-click, or pressure to just send it.

## Consequences

- A user who types an ambiguous short prefix is **refused and shown the candidates**. This is an intentional behaviour change: input that previously went to one arbitrary contact now sends nothing. A few more characters, the full 40-character NodeId, or the exact alias removes the ambiguity.
- **Cost: an ambiguous input is a hard failure with no automatic resolution.** There is deliberately no "did you mean" picker, so a user who does not recognise the candidates has to look the id up.
- **`ContactService`'s ordering comparer is deliberately untouched.** Sorting still decides what the contact list and the candidate display show, which is a presentation concern; it no longer has any say in which contact receives a message. Changing it will not change send behaviour, and a future change that touches it must not be described as a fix for this.
- **The logic is testable for the first time.** With the resolution in Core, its invariants are asserted directly rather than inferred from a UI that cannot be constructed.
- **The displayed candidate list is capped** at eight entries, with the remainder reported as a count, so a large collision set stays readable. The resolver still returns the complete set.

## Testing

`tests/P2PChat.Core.Tests/ContactResolverTests.cs` — 12 cases. Mutation: reverting the prefix branch to "take the first" turns **exactly three** of them red, and the set is the informative part:

- `短前缀_命中多个联系人时判为歧义_且不返回任何一个` — two contacts sharing a four-character prefix;
- `短前缀_命中三个联系人时同样判为歧义` — the same at three candidates, which also pins that the candidate list comes back in full;
- `歧义时即使其中一条恰好排在前面的联系人存在_也绝不选它` — a trap set specifically against the sort-based pseudo-fix: the contact that sorts first exists, and it is still not selected.

The third is what the file's own class comment means when it says the file asserts nothing about ordering. Without it, an implementation that resolves ambiguity by "whichever contact the list happens to put first" would pass a suite checking only the two-candidate case.

The remaining cases cover what the refusal must not break: unique alias, case-insensitive alias, full 40-character NodeId, duplicate aliases refused the same way, alias precedence over a prefix that would also match, zero candidates and an empty list both reported as not-found rather than ambiguous (the UI words those two differently), `ArgumentNullException` on a null collection or target, and the pure-function contract that the caller's collection is not reordered.

## Deferred

- **The ambiguity surface grows as the contact list grows**, and this decision does not shrink it. A hybrid network, or a set of assisting nodes registering more peers automatically, increases the number of entries a short prefix can collide against. Refusing stays correct as the list grows; what remains unresolved is anything that makes the surface smaller.
- **`ContactService`'s ordering comparer stays as it is, on purpose.** It orders what the UI lists; it must not become the thing that decides who receives a message, and adding a comparer there is not a fix for this decision.
- **The contact list's own integrity is untouched**: entries typed in through `/add` and entries persisted by a `/connect` TOFU acceptance remain whatever local storage holds. Resolution is not verification.
- **`P2PChatTui` still has no test project reference**, so the refusal path and the candidate display in the UI have no unit coverage; every rule this decision states is covered in `ContactResolverTests` instead. `scripts/e2e-verify.ps1` works around alias collisions by using a distinct alias per run, so the end-to-end script does not exercise the ambiguous path either.
- **The e2e script's own comment on that workaround** still describes alias-collision resolution as an open defect and refers the reader to a handover document for the details. That wording is now out of date; updating it belongs with the script, not with this note.

## Related

- [Envelope wire codec converged onto a single source of truth in Core](./2026-09-28-envelope-codec-single-source.md) — the same sink-to-Core argument, and the same "move the implementation rather than widen the dependency graph" rule.
- [/connect's hello response verification](./2026-09-28-connect-hello-response-verification.md) — the path whose successful handshakes populate the contact list, and therefore widen this one's ambiguity surface; its `Pin-source discipline` section is the standing statement that a contact is the user's intent rather than a verified identity.
- [Inbound replay protection](../feature/2026-09-28-replay-protection.md) — the other half of inbound admission: a replay guard decides whether a frame may enter, this decides whether a name may be resolved to a peer.
- [Message signing](./2026-09-21-message-signing.md) — the reason a session key binds to one peer's identity at all, and therefore what a wrong-peer send actually exposes.
