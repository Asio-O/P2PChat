# Agent Note: The peer address table hardcodes a 4-byte address field, so IPv6 entries arrive as a second field

Status: implemented

## Problem

The `p2pc_peers` field of a `get_peers` response is a bencode list whose entries are fixed 26 bytes: `[20B NodeId][4B IPv4][2B Port BE]`. The address field width is hardcoded to 4.

`IPAddress.GetAddressBytes()` returns 16 bytes for an IPv6 address, so the fixed-width 4-byte `Buffer.BlockCopy` on that path read the first four bytes of the address and interpreted them as an IPv4 address. `2001:db8::1` became `32.1.13.184` — a syntactically valid address belonging to an entirely different host. Nothing was raised, nothing was logged, and there was no degraded mode: the address table began announcing an address that was not the peer's, and no downstream consumer could tell.

**This path is currently unreachable, and that is the part worth writing down.** `UdpTransport` binds `AddressFamily.InterNetwork`, so an IPv6 address cannot enter the table today. The reusable judgement is therefore not "the defect is harmless" but its inverse:

> A path that cannot currently be reached is not a safe path. It is a path that has not been walked yet.

The truncation lives in the encoder, so it activates on its own the moment the transport layer accepts IPv6 — with no change needed at the DHT layer to announce it, and with every existing log line still reporting success.

A second defect sits in the parser, in the same area.

`FlattenByteList(list, entrySize)` took an `entrySize` parameter and then called the 26-byte parser unconditionally: **the parameter was in the signature and absent from the behaviour.** A caller asking for a different entry width got 26-byte slicing regardless, so the standard 6-byte `values` list was cut into misaligned entries.

That one stayed hidden because the product of the `values` path is discarded downstream regardless — a standard `values` entry carries no NodeId, so it cannot satisfy the "entry NodeId must equal the query target" check and is dropped. **A misaligned parse that is masked is still a misaligned parse.** What the parameter promised and the body did not deliver was one edit away from producing wrong entries for any caller that *did* carry a NodeId to match.

## Decision

**Two fields, both present.** `p2pc_peers` keeps its 26-byte IPv4 layout **byte for byte**; `p2pc_peers6` is added alongside it for IPv6, at 38 bytes per entry: `[20B NodeId][16B IPv6][2B Port BE]`. A `get_peers` response splits cached entries across the two by address family, and each field is emitted only when it holds at least one entry.

**This shape was ruled by the project lead, not chosen by the user, and can be reversed.** The underlying question has been put to the user in two rounds without an answer. The repository's own discipline applies: the number of unanswered rounds is itself the signal and is not to be smoothed over, and a third identical question would not produce new information. The decision therefore proceeds on the lead's ruling rather than on a stated user preference. Anyone revisiting it should read the two-field shape as a proposal with a reason behind it, not as a requirement the user asked for.

Three properties land with the format:

- **Encoding selects the layout by address family and throws on an unknown one.** `Bencode.SerializeCompactPeer` sizes the address field from `AddressFamily` and raises `ArgumentException` for a family it does not know. A caller that cannot encode an entry skips it and logs a warning naming the peer and endpoint, so an unencodable address leaves a trace instead of leaving a wrong address. The choice is deliberate: failing here is preferable to emitting an address that is not the peer's.
- **Parsing dispatches on width and throws rather than guessing.** `FlattenByteList` switches on `entrySize` — 26 to the IPv4 parser, 38 to the IPv6 parser, anything else to an `ArgumentException` that names both known widths. The standard 6-byte `values` list moves to its own method, `FlattenValuesList`: its field layout differs in kind and not only in width, and the two no longer share a function. Sharing one is what produced the ignored parameter in the first place.
- **The NodeId on a parsed entry is nullable, and match sites skip `null` explicitly.** A standard `values` entry has no NodeId of its own, and the NodeId the code does hold in that case belongs to the **responder**, not to the entry. The `FindNodeAsync` hit check therefore drops `null` explicitly instead of treating an absent id as an identity.

## Alternatives considered

**Add a width marker byte to the existing 26-byte entry (a first byte identifying the entry layout).** Rejected, and the decisive reason is the old node's failure mode. A node that does not recognise a width does not stop — it slices at 26 bytes, which is exactly what its parser is written to do, and interprets the new layout as **wrong entries**. A dual-field design makes an old node fail *invisible*: finding no `p2pc_peers6` in the dictionary, it falls back to `values` and obtains nothing, which is a clean degradation. **Misparse is more dangerous than absence**, and this repository has paid for that lesson twice — the `EnvelopeCodec` divergence after [message signing](../bug-fix/2026-09-21-message-signing.md), and the false green in `HANDOFF` §8.1.2, where a number read as authoritative while the thing it described was not verified. See [Envelope wire codec converged onto a single source of truth in Core](../bug-fix/2026-09-28-envelope-codec-single-source.md).

**Accept the incompatibility and version the protocol.** The cleanest option on correctness: no dual layout, no field a reader may fail to notice, one format everywhere. It loses on **migration cost** — it requires two networks and two operational regimes during the transition, for a protocol whose deployed population is small and whose nodes are updated by hand. It is not wrong; it is the right answer at a different scale.

**Change the 26-byte entry to 38 bytes in place, with no new field.** Rejected: it breaks every existing node outright and leaves no backward-compatible path. The old field is the compatibility surface, and overwriting it destroys the property that made the two-field design cheap.

## Consequences

- **Old nodes degrade by absence, not by misreading.** Not finding `p2pc_peers6` means falling back to `values` and receiving no entries from that field. This is the decisive reason for the two-field shape, and it is a property of the design rather than an accident of the migration.
- **`p2pc_peers` is unchanged at the byte level, so this change required no backward-compatibility handling and rewrote no existing test's expected bytes.** A format change that costs nothing in compatibility handling is the practical argument for keeping the old field intact rather than versioning around it.
- **A `get_peers` response can now carry two peer-list fields**, and readers must handle a dictionary in which either, both, or neither key is present.
- **Cost: one more key in the protocol.** Every node that writes the response must keep the two fields consistent, and every reader must try both.
- **The unknown-address-family branch is deliberately loud.** It converts what was a silent wrong address into an exception plus a warning line naming the affected entry.

## Deferred

- **The IPv6 transport layer does not exist yet, so there is no data source.** Until it lands, `p2pc_peers6` is never non-empty. **This change therefore produces no behaviour change at all**: it does not repair the truncation described in `## Problem`, because that path still cannot be reached, and it introduces nothing new either. What it does is put the format capability in place and remove a silent error path that would have activated on its own when the IPv6 transport arrived. It is **not** IPv6 support, and nothing in this note may be read as claiming it.
- **Entry selection in mixed networks is undecided.** When one `info_hash` has both IPv4 and IPv6 peers, do both fields go out, or does the responder select according to the requester's capability? This was not chosen on the user's behalf and stays open.
- **The unknown-address-family branch is not reachable by a test on the current .NET.** `IPAddress` supports exactly two address families, so a third cannot be constructed. The branch is **not** claimed to be covered.
- **The address-family split inside the `get_peers` response handler has no direct unit test.** It sits inside a large method. What is covered is the codec layer it calls, not the splitting itself.
- The 26-byte `p2pc_peers` definition recorded in [Public DHT cannot discover peer nodes](../bug-fix/2026-09-20-public-dht-peer-discovery-gap.md) stays accurate and is not superseded. That note does not yet mention the sibling field, so this note is a partial extension of it rather than a replacement.

## Related

- [Public DHT cannot discover peer nodes](../bug-fix/2026-09-20-public-dht-peer-discovery-gap.md) — owns the original 26-byte `p2pc_peers` definition that this note extends.
- [Envelope wire codec converged onto a single source of truth in Core](../bug-fix/2026-09-28-envelope-codec-single-source.md) — the same "one layout, one implementation" discipline, and the misparse-is-worse-than-absence precedent.
- [Message signing](../bug-fix/2026-09-21-message-signing.md) — the phase whose codec divergence is what made a misparse dangerous rather than merely wrong.
- [The automatic key-exchange response was never verified](../bug-fix/2026-09-29-key-exchange-response-verification.md) — same round, same judgement: a currently unreachable gap is recorded together with its activation condition instead of being closed silently.
