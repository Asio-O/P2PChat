# Agent Note: Open items in NAT reverse registration, IPv6 transport, and the group key-probe path

Status: proposed

## Problem

Six things are known and unresolved. Three are defects with a reachable symptom, one is a defect that is not yet reachable, one is a consistency narrowing with no exploitable consequence today, and two are product decisions that belong to the user rather than to an implementer. None of them has an owner today.

They used to live together in a single state document, which no longer exists. Under the no-index ADR rule the active lifecycle tree **is** the work list: there is no central index to add a row to, and there is no summary file whose numbers have to be re-synchronised by hand each round. An item that has not landed therefore belongs in `proposed/`, carrying its motivation, its acceptance criterion, and what it costs — not in a checklist.

The residual-risk statements and the comment-discipline rules that sat alongside these items are already fully covered by the standing orders in `Agent.md` and by existing bug-fix notes, and they are not restated here: one fact, one home.

## Proposal

### O1 — the reverse-registration poison entry behind `/connect` across NAT

**State.** The responder side of `/connect` writes the peer's listening endpoint into the static peer table, and that endpoint can only be **self-reported** — the three-way handshake does not carry it. The self-reported value is the peer's `LocalNode.EndPoint`, which is built once at startup from a LAN address and, being an `init` property on a record, never changes afterwards. A node that has just obtained a public mapping does not update it: `ApplyMapping` writes `_localExternalEndPoint` and leaves `LocalNode.EndPoint` alone, so a node behind NAT goes on announcing its private address. Across NAT that address is not diallable.

The record is also consulted first and returned unconditionally. `FindNodeAsync` checks the static peer table before anything else, with no expiry and no fallback on a connection failure, so one unusable entry does not merely fail to connect — it shadows every other discovery route for that peer.

**Partially mitigated, not fixed.** `RegisterStaticPeer` now keeps the registered endpoint when a new one conflicts with it and logs a warning, so a bad self-reported value can no longer displace a good hand-registered one. The rule that follows from it is narrow:

> **Not overwriting on conflict is not the same as refusing to write a bad value.**

A **first** reverse registration still writes the private address, and the symptom is unchanged.

**Proposed.** A self-reported endpoint that the local node knows to be private must not be written to the static peer table, and the shadowing must end — either the entry is not created, or the static table stops answering unconditionally. The end-to-end symptom that has so far only been reasoned about — one side reachable, the other unable to answer back, recovery by restarting — belongs under test as part of this, not as a note about it.

**Cost.** Reverse registration stops working in exactly the situation it was built for, so the affected peer becomes reachable only after the user re-adds it with an explicit endpoint. That is a visible, actionable state rather than a silent one, and it is the price of removing an entry that silently disables discovery.

### O2 — IPv6 in the transport layer

**State.** The stack is IPv4-only throughout. `GetLocalIPAddress` is hard-wired to `AddressFamily.InterNetwork` and reverse-resolves the local address by connecting to the IPv4 literal `8.8.8.8`, falling back to `IPAddress.Loopback`; `UdpTransport`, `TcpTransport` (the listener and `ConnectAsync` both) and `UpnpClient`'s SSDP probe each construct their own `InterNetwork` socket.

The **model layer is already address-agnostic**: `Core.Extensions.EndpointText` parses `[::1]:5000`-form literals and `IPEndPoint` is used throughout. The bottleneck is the transport, not the data model, which is what keeps the scope to sockets, the local-address probe, and the SSDP probe.

UPnP is IPv4-only by specification and the IGD mechanism on IPv6 is a different one, so this is **not** an extension of the existing port-mapping work; the two cannot be finished as one change.

**Proposed.** The socket constructions, the local-address probe, and the SSDP probe become address-family aware, and a node reachable only over IPv6 becomes reachable at all.

**Cost.** A dual-stack listener changes what "reachable" means on a host that has both families, and the announcement and registration paths then have to decide which family to publish — which is O5, and is not decided here.

### O3 — the silent IPv6 truncation in `p2pc_peers`

**State.** The address field of a `p2pc_peers` entry was a fixed 4 bytes, so an IPv6 address was truncated into a semantically unrelated IPv4 address, with no exception, no log, and no degraded mode. The two-field `p2pc_peers` / `p2pc_peers6` layout has been decided and landed, and the encoder now selects the layout by address family and raises on a family it does not know.

**What that does and does not mean, stated precisely:** the wrong-output path is eliminated, and **the defect itself is not repaired.** The transport layer does not carry IPv6, so `p2pc_peers6` is never non-empty, so the code that would have been wrong is still never reached. This item is open until it is exercised, and a landed encoder change is not its closure.

**Proposed.** Once O2 exists, an instance that actually holds an IPv6 address publishes its IPv6 entries byte for byte intact.

**Cost.** None today, and that is also the hazard: an item that cannot be closed before its predecessor can quietly age, and it is the kind that reads as finished because something about it visibly changed.

### O4 — narrowing the group key-probe path

**State.** `GroupChatService`'s key-probe path reads its response straight off the connection it holds, and it does two things its sibling in `ChatService` no longer does: it still uses a **constant** `ConversationId` of `memberNodeId.ToHexString()`, and it does not call `IReplayGuard`.

Re-reviewed, and this matters for how the item is prioritised: the only output of that path, the peer's long-term public key, is bound to `expectedPeer` three times over — signature verification derives the identity, the responder is checked against the expected member, and `PeerPublicKeyRegistry.Record` re-checks that the key derives that NodeId. Any frame passing all three carries the peer's currently correct public key, so **replay protection buys no security here today.** No consequence is claimed for this item beyond that, and a consequence should not be added without a verifiable chain first.

**Proposed.** Align the path with its sibling: a per-exchange correlation id and a replay-guard call. The two reasons are unrelated to an attack — the path is weaker than the one beside it, and a constant `ConversationId` will silently defeat a freshness semantic the day someone adds one to this path.

**Cost.** The value is wire-visible: the responder echoes it verbatim, so this changes what that message type carries rather than staying local.

### O5 — which entries to publish in a mixed network

When one info_hash has both IPv4 and IPv6 peers, do both fields go out, or does the responder select by the requester's capability? **Undecided, and not decided here.** The format already emits two fields, so the question is live in the code today; it is a product decision and it is the user's.

### O6 — what persisting the address table means

Forwarding is per-session and transient; **saving** is long-lived and survives restart, so a node holds third-party addresses for as long as it runs. `_peerCache` is in-memory only today, and where that data would live is itself undecided. Whether a user must be told, can inspect it, can delete it, and how long it is kept — all open. This note does not choose any of them.

## Alternatives considered

**Keep a live handover document as the work list.** Rejected: gate numbers have a very short shelf life, and a status file has to be synchronised by hand every round. The failures this repository has actually paid for — a figure that disagreed between a summary line and the authoritative table, an older figure copied forward as though it were this round's result — all originate in exactly that arrangement. Those numbers now live only in commit messages, which are timestamped and immutable, and the item each one describes is a decision record with its own lifecycle.

**Put the open items into an existing `implemented/` note.** Rejected: `implemented/` means the shipped present, in the present tense. Filing something unimplemented there makes "implemented" readable as "planned", which is the one thing that directory's status value exists to prevent. What has not landed belongs in `proposed/`.

## Acceptance criteria

Each is observable. None carries a work estimate.

- **O1 — no private self-report in the static table.** A reverse registration across NAT does not place a private address into the static peer table, and the shadowing ends: a peer whose static entry is unusable is still resolvable by the other routes. The end-to-end symptom that has so far only been reasoned about — one side reachable, the other unable to answer back, recovery by restarting — is under test.
- **O2 — IPv6 transport.** Two local instances complete one bidirectional encrypted private chat over `[::1]`, and `EndpointText` covers IPv6 literals.
- **O3 — IPv6 entries intact.** With both instances holding an IPv6 address, the IPv6 entries the address table publishes survive byte for byte.
- **O4 — the probe path matches its sibling.** The group key-probe path carries a per-exchange correlation id and passes the replay guard, and a repeated delivery of the same frame is answered by a rejection rather than by a second acceptance.

> **O1 to O4 all green does not mean the four deployment situations are covered.** The tension between "no relaying" and "all four situations work" is set out in `## Risks` of [Helper-assisted traversal and IPv6](../feature/2026-09-29-helper-assisted-traversal-and-ipv6.md), and whether it resolves in our favour depends on hole-punching succeeding — which has never been measured in this project. This note is written so that a later handoff cannot read a fully green milestone list as deployment coverage.

## Risks

**O1's fix removes a feature in the case it was written for.** Refusing to register a self-reported private endpoint means reverse registration cannot help a node behind NAT — the one case it exists for. The user-visible result is a peer that must be re-added with an explicit endpoint, which is honest but is not the seamless behaviour the feature promised.

**O2 is not "add IPv6 support" in the abstract.** It changes what a node publishes about itself, and once both families exist the choice of which to announce becomes a product question (O5) rather than an implementation detail. Getting that wrong does not fail loudly: it produces nodes that are reachable on a family nobody looked up.

**O3 can be read as closed.** The encoder visibly changed, the truncation code is gone, and the defect is not fixed. The reason is in the item itself; the reason it is worth stating anyway is that this is the shape a later reader is most likely to compress into "done".

**O4 has no threat behind it, and adding one would be a mistake.** The alignment is worth making because two readers of one protocol should not differ in strength, and because the constant correlation id is a trap rather than a hole. Describing it as a vulnerability would overstate what the code does and would make the item harder to justify on the merits.

**O5 and O6 have no owner, and this note does not become one.** The mixed-family rule is already implied by a format that emits two fields, and the persistence question has no answer at all. Both are recorded as open; neither is pre-empted, and nothing in this note should be read as a recommendation on either.

**Collecting items here does not sequence them.** Nothing in this note states an order, an estimate, or an approval, and no item is claimed to be imminent.

## Related

- [Helper-assisted traversal and IPv6](../feature/2026-09-29-helper-assisted-traversal-and-ipv6.md) — the proposal this shares O2 with, and whose `## Risks` sets the deployment-coverage condition restated in `## Acceptance criteria`.
- [Address-table IPv6 format](../../implemented/architecture/2026-09-29-peer-address-table-ipv6-format.md) — O3's landed half, and the record of why `p2pc_peers6` is never non-empty until O2 exists.
- [Making `/connect` bidirectional](../../implemented/bug-fix/2026-09-28-connect-bidirectional.md) — where reverse registration and the static-table shadowing were introduced; O1 is the follow-on defect of that decision.
- [NAT traversal](../../implemented/bug-fix/2026-09-21-nat-traversal.md) — the UPnP work that O1 and O2 sit on.
- [The automatic key-exchange response was never verified](../../implemented/bug-fix/2026-09-29-key-exchange-response-verification.md) — the sibling path whose admission strength O4 aligns to.
