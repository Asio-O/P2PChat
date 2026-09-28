# Agent Note: Public DHT cannot discover peer nodes

Status: implemented

## Problem

The README lists "bootstrap and peer discovery over the public Mainline DHT" as a core feature of the program, but today **only bootstrap works — discovery does not**: unless one node is configured with the other as its bootstrap node, two nodes can never find each other, and `/msg` always fails with `目标节点未找到`.

Three gaps in the chain have to hold at once for this to happen:

1. **Nothing anywhere announces this node's `NodeId → endpoint` mapping.**
   `MainlineDhtService.StoreAsync` is an empty implementation that simply returns; `FindValueAsync` always returns `null`; an incoming `announce_peer` gets a success response and nothing else. No call site anywhere in the repository issues an `announce_peer` for this node.

2. **Lookup demands an exact `NodeId` match, which the public DHT cannot supply.**
   `FindNodeAsync` runs an iterative `find_node` and then filters on `c.NodeId.Equals(targetId)`. Public nodes return **BitTorrent nodes near that ID**; our node is registered nowhere, so an exact match can never hit. `ChatService.SendPrivateMessageAsync` is exactly this path.

3. **There is no escape hatch for supplying an endpoint by hand.**
   `Contact` carries only `NodeId` / `Alias` / status fields, so at send time the endpoint can **only** come from a DHT lookup.

The one path that works today is **treating the peer as a bootstrap node**: the two sides exchange IPs and PING each other during bootstrap. That path is confirmed to work by measurement, but it requires the two sides to exchange IPs beforehand — precisely what "no central server" set out to avoid. This change makes the discovery mechanism self-sufficient on the protocol itself (Mainline DHT KRPC) while preserving the manual `ip:port` fallback.

## Decision

Complete the `announce_peer` / `get_peers` halves of Mainline DHT so a node can announce itself and resolve peers through the public DHT, and decouple the TCP / DHT endpoint semantics so `UdpPort == TcpPort` is no longer an implicit precondition.

- **Announce (`MainlineDhtService.TryAnnounceAsync`)**: with `info_hash = this node's NodeId` (20 bytes), `get_peers` to the K=8 closest nodes in the routing table for a token, then `announce_peer`. The announced `port` is the **TCP listen port** (`LocalNode.EndPoint.Port`, set by `Program.cs` once `actualTcpPort` is known).
- **Resolve (`MainlineDhtService.FindNodeAsync` + `ResolveViaGetPeersAsync`)**: first issue `get_peers` with `info_hash = targetId`; on a `p2pc_peers` hit, return the `NodeInfo` built from the 26-byte entry. On a miss, fall back to the existing exact routing-table match.
- **Non-standard field**: `p2pc_peers` carries 26-byte entries (`[20B NodeId][4B IPv4][2B TCP Port BE]`), one more 20B NodeId than the BitTorrent-standard 6-byte `values` so the receiver can map IP:port back to a specific `NodeId`. Public bootstrap nodes do not parse this field and treat it as "nothing found".
- **Token**: per-remote random 8-byte token (`_issuedTokens` dictionary), long-lived; no expiry.
- **Local fallback**: `Contact.EndPoint` retains the `ip:port` path that triggers `IDhtService.RegisterStaticPeer` for `FindNodeAsync` direct hit (added in phase 0, unchanged here).
- **Endpoint decoupling**: `NodeInfo` gains an optional `DhtEndPoint` (the UDP source endpoint of KRPC traffic, written by `MainlineDhtService`); `EndPoint` keeps its role as the **TCP endpoint** and `MessageRouter.GetOrCreateConnectionAsync` continues to use it. `UdpPort != TcpPort` is no longer a precondition.
- **Bootstrap behaviour (`MainlineDhtService.BootstrapAsync`)**: remove the legacy "first success → `break`" so all configured bootstrap nodes are attempted; failure of any one is logged at Debug, not thrown.
- **Announcement state observable**: `IDhtService` gains `AnnouncedPeerCount` and `LastAnnounceUtc` (default impl `0` / `MinValue`); `P2PCHAT_SELFTEST` self-test prints a new `宣告状态:` line.

The announce / resolve cycle is fired once synchronously by `BootstrapAsync` (no 15-minute warm-up) and re-announced every cycle by the existing `RefreshLoopAsync`.

## Announced endpoint semantics

`NodeInfo.EndPoint` keeps its role as the TCP endpoint (`MessageRouter.GetOrCreateConnectionAsync` uses it to open the TCP connection). For the local node it is set by `Program.cs` after `actualTcpPort` is known; for a remote node it is filled from the TCP `port` field in that node's `announce_peer` request.

`NodeInfo.DhtEndPoint` (new, optional) is the source endpoint of the KRPC (UDP) packet, written by `MainlineDhtService.CreateRemoteNode` when it receives traffic from a peer. Behind NAT it equals the remote's public endpoint, which is the mechanism by which BitTorrent clients remain discoverable behind NAT; reserved for Phase 2 (task-4).

## P2PC protocol extensions

- **`p2pc_peers`** (in `get_peers` response): `bencode list<bytes>`, 26 bytes per entry `[20B NodeId][4B IPv4][2B TCP Port BE]`.
- **`p2pc_id`** (in `announce_peer` request, redundant with standard `id`): explicit marker that this node speaks the P2PC extension.

## Alternatives considered

**Keep the status quo and document "the peer is your bootstrap node".** Rejected: hardens a one-off manual IP exchange into product semantics. It leaves two users on different networks unable to find each other by node ID, directly contradicting the README's "no central server / peer discovery", and leaves "discovery" as a capability that does not in fact exist.

**Ship only the manual `ip:port` contact fallback, with no DHT announcement.** Rejected as the **sole** approach: it still requires the two sides to exchange addresses in advance and cannot answer "how do I reach someone when I only have their node ID". It is retained as a complementary path — cheap, reliable, and the only usable option when network policy blocks the public DHT.

**Stand up our own rendezvous node holding `NodeId → endpoint`.** Rejected: that is inviting the central server back in, in direct conflict with the project's "decentralized, no central server" premise.

**Encode the endpoint in the node ID, or derive it from the public key.** Rejected: `NodeId` is only 20 bytes and cannot hold an address; and the node ID is a long-term identity that must not change with network location.

**Require `UdpPort` to equal `TcpPort`, and write that into the docs.** Rejected: it promotes an implementation coupling into a constraint on the user. The right fix is to put the TCP port on the wire explicitly (see `## Announced endpoint semantics`) rather than make the user guess.

**Keep the `find_node` iteration but relax it to "take the nearest few nodes" instead of requiring an exact match.** Rejected: the nearest neighbours are a set of BitTorrent nodes with no relation to our peer; relaxing the match only returns wrong addresses.

**Use the BitTorrent-standard 6-byte `values` (IP+port only) as the peer identity.** Rejected: `values` does not carry `NodeId`, so IP:port cannot be associated with "the `NodeId` I am looking up". The 26-byte `p2pc_peers` extension is the chosen encoding for this reason.

## Consequences

**Won:** the `announce_peer` / `get_peers` closed loop covers `find_node → announce_peer → get_peers` end-to-end; announcement state is exposed on the `IDhtService` interface so `P2PCHAT_SELFTEST` can assert on it; TCP and UDP endpoints are decoupled and `UdpPort != TcpPort` no longer breaks chat; bootstrap no longer aborts after the first successful node.

**Paid:**
- The public DHT is best-effort storage. Nodes may rate-limit or drop `announce_peer`, and records can vanish across restarts; both announcing and resolving must treat "not found" as a normal outcome and pair announcement with periodic re-announcement.
- **Privacy cost:** announcing publishes this node's IP and port to the public DHT. That is the unavoidable price of moving from "does not announce" to "discoverable", and the README needs to say so.
- **Reachability behind NAT is still unsolved.** Announcement yields the public mapping, but if the NAT does not forward that port the peer still cannot connect; this Agent Note includes no UPnP / NAT-PMP or hole punching (task-4).
- **AOT constraints:** the new bencode parsing path (`ParseCompactPeers26`, `FlattenByteList`) stays reflection-free; the extension fields live in the KRPC bencode layer, not in MessagePack, so no formatter registration is required.
- **Sequencing risk:** announcing and resolving must land in the same change, or the result is a half-built state where one side announces and the other still cannot resolve. This change ships both at once.

## Verification

- `dotnet build P2PChat.slnx` — within the `discovery-engineer` write scopes (`src/P2PChat.Networking`, `src/P2PChat.Core`, `src/P2PChat.UI`, `src/P2PChat.App`): 0 errors, 0 warnings.
- `dotnet test P2PChat.slnx` — within the `discovery-engineer` write scopes, all green:
  - `P2PChat.Networking.Tests`: 12 pass (6 existing `BencodeTests` + 3 new `ParseCompactPeers26` boundary cases + 3 new `RealDiscoveryTests`).
  - `P2PChat.Core.Tests`: 37 pass (unchanged).
  - `P2PChat.Crypto.Tests`: 8 pass (unchanged).
- `RealDiscoveryTests` covers the `find_node → announce_peer → get_peers` closed loop:
  - `Bootstrap_单向依赖_B可发现A` — two real `MainlineDhtService` instances on ports 40001/40002 (loopback). B bootstraps from A. Asserts A's routing table contains B's real `NodeId` after B's bootstrap (the PING path is the test's only validation of the UDP round-trip).
  - `AnnounceNow_双向互宣告后_两端都能通过getPeers解析对端` — same loopback setup, both sides fire `AnnounceNowAsync`, assert both directions of `FindNodeAsync(peer NodeId)` return `NodeInfo` with the correct TCP port.
  - `Bootstrap_全部引导节点不可达时_不抛异常` — all bootstrap endpoints unreachable; `BootstrapAsync` does not throw, `GetAllKnownNodes` is empty, `FindNodeAsync(random)` returns null.
- All tests run on `IPAddress.Loopback` with fixed ports; **no public DHT** is required.

## Deferred

- **NAT forwarding / external reachability** — `DhtEndPoint` is in place but the actual UPnP mapping and external-endpoint backfill belong to Phase 2 (task-4).
- **`/msg` user-visible error wording** — `ChatService.SendPrivateMessageAsync` already throws `InvalidOperationException("目标节点未找到: ...")` on `FindNodeAsync` failure; TUI surfaces it via `AddSystemMessage($"发送失败: {ex.Message}")`. The wording was not changed in this Agent Note; the existing path is preserved.