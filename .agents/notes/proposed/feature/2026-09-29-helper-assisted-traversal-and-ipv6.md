# Agent Note: Helper-assisted traversal from stranger nodes, and IPv6 in the transport layer

Status: proposed

## Problem

Four deployment situations have to work, and none of them is the one the current design was shaped around: the same LAN; separate home broadband where the router permits port mapping; networks that yield no public address at all (CGNAT, corporate, campus); and phone hotspots. Two address families ride along with them — public IPv4 and public IPv6.

Two of those situations have no way out under the current design. A node behind CGNAT, a corporate network, or a campus network gets no port mapping, and whatever a peer records for it is a private address that cannot be dialled. Separately, the whole stack is IPv4-only: `GetLocalIPAddress` is hard-wired to `AddressFamily.InterNetwork` and reverse-resolves the local address by connecting to the IPv4 literal `8.8.8.8`; `UdpTransport`, `TcpTransport` (listener and `ConnectAsync` both) and `UpnpClient`'s SSDP probe each construct an `InterNetwork` socket of their own; and UPnP is IPv4-only by specification, the IGD mechanism on IPv6 being a different one. A node reachable only over IPv6 is not reachable at all, and IPv6 is not an extension of the UPnP work — it is separate work.

The address table that traversal would depend on is not missing, and neither is the mechanism that fills it. `MainlineDhtService._peerCache` maps an info_hash to `List<AnnouncedPeer(NodeId, tcpEp, dhtEp, externalEp)>`, and `RecordAnnounce` runs when this node receives an `announce_peer` whose info_hash matches — a node that has received an announcement has already built the table. The two non-standard extensions that carry it, `p2pc_peers` in `get_peers` replies and `p2pc_id` in `announce_peer` requests, are in place as well. What is absent is anything that makes a stranger reachable, and anything that fills the table between two nodes that have never met.

## Proposal

Traversal becomes assistance that a stranger node can give, bounded to establishing the connection. Four user requirements fix the shape:

- **1** — "1、2、3、4 、公网ipv4、ipv6 的情况都要考虑到，能被公开访问的节点可为其他节点提供NET穿透协商能力"
- **2** — "陌生节点之间也可以提供穿透协商，并且保存节点地址表"
- **3** — "随客户端分发几个已知地址 这点建议很好，而且要加上可配置公共协助节点；节点的协助能力默认开启；节点的协助能力只限于协助建立 p2p 连接"
- **4** — "协助节点要不要向协助对象披露身份也不需要可感知性，这是p2pc 我为人人、人人为我的基本准则"

Requirement 4 gives the reason the rest of the design rests on, in the user's own words: "这是 p2pc 我为人人、人人为我的基本准则" — under a reciprocal model, this node acting as a helper is the network's normal condition rather than an exposure its owner has to be shown.

### The parts already in place

This is an extension of existing machinery, not a new subsystem:

| Already present | Where | What it means for this proposal |
|---|---|---|
| The address table | `MainlineDhtService._peerCache` — `ConcurrentDictionary<string, List<AnnouncedPeer>>`, keyed by info_hash, values carrying `NodeId`, TCP / DHT and external endpoints | The table this proposal relies on already exists and is already written to |
| The write path | `RecordAnnounce`, invoked when an `announce_peer` arrives locally with a matching info_hash | Receiving an announcement is what builds the table |
| The wire extensions | `p2pc_peers` (in `get_peers` replies, 26 bytes per entry) and `p2pc_id` (in `announce_peer` requests, carrying the announcing node's own NodeId) | A node that is not a P2PChat node passes both through untouched; the class comment states this |

Because the table and its write path exist, the work is what reaches them, not what holds them.

### Bounded to establishing the connection

Assistance stops at the connection. This is equivalent in meaning to hole punching, or to coordinating a simultaneous connection attempt, and it does not relay traffic: a third party cannot see conversation content and cannot see transport traffic, and the "who talked to whom" association is exposed only during connection setup. Payload stays end-to-end encrypted, so a helper cannot read plaintext even during that phase.

This bound is what keeps [the 2026-09-21 rejection of a relay node](../../implemented/bug-fix/2026-09-21-nat-traversal.md) standing as written. That note rejected deploying a privileged relay server as conflicting with the decentralisation premise; a peer that helps a stranger establish a connection, chosen locally, with no designated server, is a different act and the rejection does not reach it.

### Defaults and visibility

Assistance is on by default. Because there is no opt-in, every node ships as one that stores addresses for others and takes part in assisting connections.

Visibility was raised and refused. A UI affordance in the self-check block showing that this node is acting as a helper, with the ability to inspect and disable it, was proposed against the always-on default and is declined under requirement 4. The cost of that refusal is diagnosability rather than privacy, and it is recorded under `## Risks`.

### Identity disclosure

Two statements, kept separate because they are not the same statement. The user answered that disclosure is not to be considered. Independently, disclosure is what lets the two assisted parties verify each other: a helper that does not disclose leaves both of them able only to take the address it hands them on trust. The cryptographic gate already bounds that: a malicious helper that lies about an address is rejected outright by the signer-must-be-the-expected-peer check in the automatic key-exchange path, so it can cause a denial of service but cannot decrypt silently. Disclosure therefore costs nothing and buys mutual verification — **this note's conclusion is to disclose, and the user is not recorded as having asked for it.**

### The three gaps

| Gap | State in the code | Scope of the work |
|---|---|---|
| **How a helper is found** | A new node's only initial contacts are four public BitTorrent bootstrap nodes, and the class comment states they parse neither `p2pc_peers` nor `p2pc_id` — they relay the reply and nothing more. `peers.txt`, read by `LoadKnownPeers`, is a local file that needs out-of-band seeding. | Two layers, per requirement 3: a set of known helper addresses shipped with the client, plus a configurable set of public helper nodes the user can add to, remove from, or override. **These four are separate things** — the two shipped/configured layers, the hard-coded BitTorrent bootstrap nodes, and `peers.txt` must not be written up as one "bootstrap mechanism"; the new layers do not replace the existing ones |
| **Whether the table is non-empty** | `TryAnnounceAsync` announces to the 8 nodes closest in its own routing table (`AnnounceK = 8`). Two fresh nodes' closest 8 almost never overlap, so strangers have most likely never announced to each other and the table is empty | Getting announcements to reach a stranger at all, and making that observable |
| **Whether an address is diallable** | With both sides behind CGNAT, the recorded address is a private one | Hard. This gap is what `## Risks` is about |

### IPv6

The model layer is already address-agnostic: `Core.Extensions.EndpointText` parses `[::1]:5000`-form literals, and `IPEndPoint` is used throughout. The bottleneck is the transport layer, so `NodeId`, `MessageEnvelope` and `ConversationId` are not in scope — only sockets, the local-address probe, and the SSDP probe.

The wire format is already prepared ahead of the transport: the dual-field layout keeps `p2pc_peers` byte-for-byte unchanged and adds `p2pc_peers6` for IPv6 entries (see [the address-table IPv6 format note](../../implemented/architecture/2026-09-29-peer-address-table-ipv6-format.md)). That decision keeps old nodes' failure mode *invisible* rather than *misread* — a node that does not know the key falls back to `values` and gets nothing, which is a clean degradation, whereas widening an entry in place would make old nodes split the stream on the wrong stride and parse new bytes as wrong entries. The field exists with no data source: until the transport layer carries IPv6, `p2pc_peers6` is never non-empty, so the format change produces no behaviour on its own.

### Open questions

These are not decided here, and the user has not been asked:

- Which entries to send when one info_hash has both IPv4 and IPv6 peers — both fields, or chosen by the requester's capability.
- How a helper is selected: by reachability, by the user, or preferring existing contacts.
- The order of the IPv6 work relative to the traversal work.
- Whether v6-only and v4-only nodes must interoperate.
- What "saving the address table" persists, where, and for how long. Forwarding is per-session and transient; saving is long-lived and survives restart, and `_peerCache` is currently in-memory only. Where that data would live is a separate open item.
- Whether the mixed-family entry rule and the traversal selection strategy are user decisions or implementation choices; nothing in this note presumes either.

## Alternatives considered

**Deploy a relay node or a TURN-style server.** Rejected: it conflicts with the decentralisation premise, and it is the design [the 2026-09-21 note](../../implemented/bug-fix/2026-09-21-nat-traversal.md) already turned down. Requirement 3 does not ask for it — it asks that already-reachable nodes assist.

**Let helper nodes relay the traffic.** Rejected by requirement 3's bound to establishing the connection. This is the single decision that keeps the privacy cost of assisting to a build-time connection, and it is also what makes the traversal gap a real gap rather than a solved one; see `## Risks`.

**Rely on the public bootstrap nodes to surface P2PChat nodes.** Rejected: the class comment states they parse neither extension. Any design that needs a public node to understand P2PChat's protocol contradicts the format they implement.

**Ship `peers.txt` alone as the way strangers find each other.** Rejected: it needs out-of-band seeding, so it cannot be the answer to "two nodes that have never met". Requirement 3 adds two layers alongside it rather than replacing it.

**Widen the `p2pc_peers` entry in place, with a marker for the width.** Rejected: entries are split on a fixed 26-byte stride, so a node that does not recognise the new width splits the stream on the wrong boundary and produces wrong entries. Missing is a clean degradation and recoverable; misparse is neither, and it is the same lesson as the EnvelopeCodec incident and the false green in the e2e assertions.

**Make assistance opt-in.** Rejected by requirement 3. The consequence is that the cost lands on every install at once rather than on the users who choose it.

**Surface the helper role in the self-check block with an on/off control.** Proposed, then rejected under requirement 4. Retained here because a rejected option is not the same as one that never existed.

**Leave identity undisclosed.** Rejected: undisclosed, the two assisted parties can only trust the helper's address. With disclosure costing nothing and the signer-must-be-the-expected-peer check already rejecting a helper that lies, mutual verification is available for free.

## Acceptance criteria

Each is observable. None carries a work estimate.

- **N1 — IPv6 transport.** Two local instances complete one bidirectional encrypted private chat over `[::1]`, and `EndpointText` covers IPv6 literals.
- **N2a — finding a helper.** A bootstrap entry point that brings a stranger helper into range is exercised end to end.
- **N2b — a non-empty table.** The address table is observed going from empty to non-empty, its entry count is readable, and the announcement that filled it is attributable to a specific peer.
- **N2c — diallable or not.** "The table is non-empty" and "the connection actually establishes" are two independently observable facts, and the first must not be recorded as the second.
- **N3 — the `/connect` reverse-registration poison entry.** Fixed, and the end-to-end symptom that has so far only been reasoned about — reachable from one side, unable to answer back, recoverable by restarting — is put under test rather than left inferred.

> **N1, N2a, N2b, N2c and N3 all green does not mean the four deployment situations are covered.** The condition N2c names is the one that decides it, and it is unmeasured; see `## Risks`. This note is written so that a later handoff cannot read a fully green milestone list as coverage.

## Risks

**The commitment and the no-relay bound are in tension for the two hardest situations.** The requirement is that all four deployment situations work; the bound is that no node relays. When both sides sit behind CGNAT, a corporate network, a campus network, or a mobile network, hole punching may fail — and because relaying is not allowed, there is no fallback. The connection does not come up. "All four situations work" is therefore conditional for the third and the fourth, and the condition is whether punching succeeds.

**Punching success has never been measured in this project.** This note states no estimate, range or order of magnitude, because a number that cannot be attributed to a measurement is worse than a blank. What can be stated is that the success rate being unknown is itself a fact that has to be obtained before deployment, and it belongs on a pre-deployment list rather than under a reassuring assumption.

**First contact is not solved by peer assistance.** Two nodes that have never spoken cannot find each other through the public DHT, and there is no authenticated way to ask a stranger "find someone for me". Requirement 2 answers this constraint rather than leaving it open: a node the user has never met can assist, and it does keep an address table. The remaining consequence is that the helper still has to be reachable by some other means, which is what N2a covers.

**Saving an address table is heavier than forwarding.** Forwarding is per-session and transient. Saving is long-lived and survives restart, so a node holds third-party addresses for as long as it runs; `_peerCache` is in-memory today, so "save" is not yet defined. Combined with assistance being on by default, the exposure is not one a user opted into.

**Declining UI visibility costs diagnosability, not privacy.** A user whose node is being contacted heavily has no interface-level clue, because there is no control, no count, and no displayed state. Existing logs carry the events — `登记静态对端` and `已登记反向可达的对端` at information level, `宣告完成` at information level, and `宣告本轮全部失败` at debug level, which is emitted because the minimum level is Debug. Diagnosing therefore depends on logs rather than on the interface; the interface has nothing to consult. This is a cost, not a resolution.

## Related

- [Public DHT node discovery gap](../../implemented/bug-fix/2026-09-20-public-dht-peer-discovery-gap.md) — the `p2pc_peers` / `p2pc_id` extensions and the announcement path this proposal extends.
- [NAT traversal](../../implemented/bug-fix/2026-09-21-nat-traversal.md) — the UPnP work, the rejected relay, and the port semantics this proposal inherits.
- [Key exchange response verification](../../implemented/bug-fix/2026-09-29-key-exchange-response-verification.md) — the signer-must-be-the-expected-peer check that bounds what a malicious helper can do.
- [Address-table IPv6 format](../../implemented/architecture/2026-09-29-peer-address-table-ipv6-format.md) — the dual-field `p2pc_peers` / `p2pc_peers6` layout.
