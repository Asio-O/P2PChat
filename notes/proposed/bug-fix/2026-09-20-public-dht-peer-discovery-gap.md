# Agent Note: Public DHT cannot discover peer nodes

Status: proposed

## Problem

The README lists "bootstrap and peer discovery over the public Mainline DHT" as a core feature of the program, but today **only bootstrap works — discovery does not**: unless one node is configured with the other as its bootstrap node, two nodes can never find each other, and `/msg` always fails with `目标节点未找到`.

Three gaps in the chain have to hold at once for this to happen:

1. **Nothing anywhere announces this node's `NodeId → endpoint` mapping.**
   `MainlineDhtService.StoreAsync` is an empty implementation that simply `return Task.CompletedTask` (`src/P2PChat.Networking/Dht/MainlineDhtService.cs:94-98`); `FindValueAsync` always returns `null` (`:101-105`); an incoming `announce_peer` gets a success response and nothing else, with the comment "not implemented, but return success to avoid being blacklisted" (`:314-316`). No call site anywhere in the repository issues an `announce_peer` for this node.

2. **Lookup demands an exact `NodeId` match, which the public DHT cannot supply.**
   `FindNodeAsync` runs an iterative `find_node` and then filters on `c.NodeId.Equals(targetId)` (`:87-91`). Public nodes return **BitTorrent nodes near that ID**; our node is registered nowhere, so an exact match can never hit. `ChatService.SendPrivateMessageAsync` is exactly this path (`src/P2PChat.Chat/Services/ChatService.cs:50`).

3. **There is no escape hatch for supplying an endpoint by hand.**
   `Contact` carries only `NodeId` / `Alias` / status fields and **no endpoint field** (`src/P2PChat.Core/Models/Contact.cs`); `/add` accepts only "node ID + alias" (`src/P2PChat.UI/Views/P2PChatTui.cs:251-261`), so at send time the endpoint can **only** come from a DHT lookup.

The one path that works today is **treating the peer as a bootstrap node**: `HandleQueryAsync` adds the node that sent a query to the routing table (`:268-272`), and the `KrpcPingAsync` response gives the initiator the peer's `NodeId` and endpoint too, so the two become mutually visible. That path is confirmed to work by measurement, but it requires the two sides to exchange IPs beforehand — precisely what "no central server" set out to avoid.

Measured evidence (2026-09-20, two instances on one machine, running the published Native-AOT artifact): node A used `UdpPort = TcpPort = 20081` with the public bootstrap nodes; node B used `UdpPort = TcpPort = 20092` with `BootstrapNodes = [127.0.0.1:20081]`. B's log read `引导节点 127.0.0.1:20081 响应成功` → `路由表: 1节点`, and on exit the two `peers.txt` files cross-confirmed A = `127.0.0.1:20092` and B = `127.0.0.1:20081`. That is, mutual visibility held only because B bootstrapped from A. The same run confirmed two incidental facts: `BootstrapAsync` `break`s after the first bootstrap node succeeds (`:73`), so a custom bootstrap node suppresses public DHT bootstrap entirely; and messages do not arrive when `UdpPort != TcpPort` (see `## Announced endpoint semantics`).

## Proposal

Complete the `announce_peer` / `get_peers` halves of Mainline DHT so a node can announce itself and resolve peers through the public DHT.

- **Announce**: after bootstrap completes, and thereafter alongside the existing refresh loop (`RefreshLoopAsync`, 15-minute period, `:428-440`), use `info_hash = this node's NodeId` (20 bytes) to `get_peers` from the K closest nodes in the routing table for a token, then `announce_peer`. The announced `port` is the **TCP listen port**.
- **Resolve**: `FindNodeAsync` first issues `get_peers` with `info_hash = targetId`; on a `values` hit (BitTorrent compact node format, 6 bytes of IP + port) it builds a `NodeInfo` from it and returns. On a miss it falls back to the existing exact routing-table match.
- **Local fallback**: add an optional `EndPoint` to `Contact` and let `/add <nodeID> <ip:port> [alias]` set it explicitly. Resolution order is "explicit endpoint > DHT `get_peers` > exact routing-table match". This path also frees LAN debugging and the two-instance test from the "bootstrap node is the peer" trick.

## Announced endpoint semantics

`NodeInfo.EndPoint` currently **carries two meanings at once**, a coupling that has to be fixed in the same change:

- it is filled by `CreateRemoteNode` from the **UDP packet source endpoint** (`:394-411`);
- yet it is used by `MessageRouter.GetOrCreateConnectionAsync` to **open the TCP connection** (`src/P2PChat.Chat/Routing/MessageRouter.cs:112`).

So the peer's UDP port gets used as its TCP port. The only workaround today is configuring `UdpPort` and `TcpPort` to the same value; otherwise TCP lands on the peer's UDP port and is refused — a configuration landmine.

This proposal makes "the TCP endpoint announced to the world" an explicit field on the wire (the `port` of `announce_peer`), has `NodeInfo` distinguish the DHT endpoint from the TCP endpoint, and removes `UdpPort == TcpPort` as an implicit precondition.

One announcement subtlety deserves spelling out: a Mainline DHT receiver records the peer by the **source address of the announce packet**, so a NATed node announces its public mapping — the same mechanism that keeps BitTorrent clients findable behind NAT. This proposal does **not** solve NAT forwarding itself: the announced port still needs a mapping on the NAT (UPnP / NAT-PMP or manual forwarding) to be dialable.

## Alternatives considered

**Keep the status quo and document "the peer is your bootstrap node".** Rejected: that hardens a one-off manual IP exchange into product semantics. It leaves two users on different networks unable to find each other by node ID, directly contradicting the README's "no central server / peer discovery", and leaves "discovery" as a capability that does not in fact exist.

**Ship only the manual `ip:port` contact fallback, with no DHT announcement.** Rejected as the **sole** approach: it still requires the two sides to exchange addresses in advance and cannot answer "how do I reach someone when I only have their node ID". It is retained as a complementary path — cheap, reliable, and the only usable option when network policy blocks the public DHT.

**Stand up our own rendezvous node holding `NodeId → endpoint`.** Rejected: that is inviting the central server back in, in direct conflict with the project's "decentralized, no central server" premise.

**Encode the endpoint in the node ID, or derive it from the public key.** Rejected: `NodeId` is only 20 bytes and cannot hold an address; and the node ID is a long-term identity that must not change with network location.

**Require `UdpPort` to equal `TcpPort`, and write that into the docs.** Rejected: it promotes an implementation coupling into a constraint on the user. The right fix is to put the TCP port on the wire explicitly (see `## Announced endpoint semantics`) rather than make the user guess.

**Keep the `find_node` iteration but relax it to "take the nearest few nodes" instead of requiring an exact match.** Rejected: the nearest neighbours are a set of BitTorrent nodes with no relation to our peer; relaxing the match only returns wrong addresses.

## Acceptance criteria

- Two fresh identities, **having exchanged no IPs**, started only against the public bootstrap nodes, complete a private-message round trip after each runs `/add <peer node ID>`; the verdict is decrypted plaintext appearing on the receiving side.
- That round trip still succeeds when `UdpPort` and `TcpPort` are set to **different** values, proving the port coupling is gone.
- When the public DHT is unreachable, `/msg` returns an explicit error rather than hanging silently, and the log shows why announcement failed.
- `P2PCHAT_SELFTEST` output gains this node's announcement state (peers announced to / time of the last announcement) so unattended verification can assert on it.
- The existing "peer as bootstrap node" path keeps working; `scripts/e2e-verify.ps1` must not regress.

## Risks

- **The public DHT is best-effort storage.** Nodes may rate-limit or drop `announce_peer`, and records can vanish across restarts; both announcing and resolving must treat "not found" as a normal outcome and pair announcement with periodic re-announcement.
- **Privacy cost:** announcing publishes this node's IP and port to the public DHT. That is the unavoidable price of moving from "does not announce" to "discoverable", and the README needs to say so.
- **Reachability behind NAT is still unsolved.** Announcement yields the public mapping, but if the NAT does not forward that port the peer still cannot connect; this proposal includes no UPnP / NAT-PMP or hole punching.
- **AOT constraints:** the new bencode parsing path must stay reflection-free; parsing `values` and `token` means extending the existing hand-written `Bencode` decoder.
- **Sequencing risk:** announcing and resolving must land in the same batch, or the result is a half-built state where one side announces and the other still cannot resolve.
- **Acceptance-environment dependency:** the first acceptance criterion depends on the public DHT being reachable; on restricted networks it must degrade to an equivalent check over two LAN instances with an explicit endpoint.
