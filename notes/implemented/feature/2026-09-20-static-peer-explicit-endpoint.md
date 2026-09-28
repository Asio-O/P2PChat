# Agent Note: Direct connect via an explicit endpoint (static peers)

Status: implemented

## Problem

The program had no way for two nodes to connect **without relying on DHT discovery**. The only path was `ChatService` → `IDhtService.FindNodeAsync(peer NodeId)`, and that path necessarily fails in the current implementation (see [Public DHT cannot discover peer nodes](../bug-fix/2026-09-20-public-dht-peer-discovery-gap.md)):

- nothing anywhere announces a `NodeId → endpoint` mapping for a node;
- `FindNodeAsync` demands an exact NodeId match, while the public DHT returns BitTorrent nodes near that ID;
- the `Contact` model has **no endpoint field** and `/add` accepts only "node ID + alias", so even the escape hatch of supplying an address by hand did not exist.

The result was that private chat, group fan-out, and file transfer were all unusable, and the user had no way to work around it — the direct cause of "basically unusable on two devices".

## Decision

Introduce **explicit endpoints**: a user can register the peer's `ip:port` on the contact, bypassing DHT lookup entirely.

- `Contact.EndPoint` (optional `ip:port` text) plus the persisted field `StoredContact.EndPoint`.
- `IContactService.AddContactAsync(nodeId, alias, endPoint)`; the TUI command is
  `/add <nodeID(hex,40)> [ip:port] [alias]` — if the second token parses as `ip:port` it is the endpoint, otherwise it is the alias.
- New `EndpointText` (`ip:port` ⇄ `IPEndPoint`): accepts **literal IPs only** (including IPv6 as `[::1]:5000`), requires a port in 1..65535, and performs **no DNS resolution**.
- `IDhtService.RegisterStaticPeer(NodeInfo)` registers a peer whose endpoint is known. `FindNodeAsync` gives **static peers priority** and falls back to the iterative lookup on a miss; static registrations live in their own dictionary and are **immune to `KBucket` eviction**.
- At startup `Program.RegisterStaticPeersFromContacts` re-registers every persisted contact that carries an endpoint, so a restart does not require running `/add` again.

The priority lives at the `IDhtService.FindNodeAsync` layer because that is the **single shared entry point** for `ChatService`, `GroupChatService` fan-out, and `FileTransferService`; resolving there benefits all three business paths at once instead of making each depend on the contact service.

## Alternatives considered

**Resolve the endpoint inside `ChatService` (injecting `IContactService`).** Rejected: a larger blast radius and a misplaced layer. It forces a change to `ChatService`'s constructor (and thus the test harness), and `GroupChatService` and `FileTransferService` still cannot see the explicit endpoint through `FindNodeAsync`, so two more places would need editing.

**Write static endpoints into the routing table only, without a separate registry.** Rejected: a full `KBucket` evicts its oldest entry, so a statically registered peer could silently disappear at runtime. A separate dictionary is immune, and the routing table stays as a supplementary source for `find_node` replies.

**Stand up a rendezvous node holding `NodeId → endpoint`.** Rejected: it invites the central server back in, in direct conflict with the project's "decentralized, no central server" premise; the same reasoning that rejected this option in [Public DHT cannot discover peer nodes](../bug-fix/2026-09-20-public-dht-peer-discovery-gap.md).

## Consequences

- Two nodes can connect directly as long as they know each other's node ID and TCP listen port — **no discovery mechanism required**.
- The endpoint is persisted and re-registered automatically at startup, so one `/add` lasts.
- A static peer also enters the routing table, so `find_node` replies advertise it to other nodes, a mild positive for overall discoverability.
- **Cost: information must be exchanged out of band.** The user has to obtain both facts (node ID and `ip:port`) through some other channel. This does not answer "how do I reach someone when I only have their node ID" — that is owned by [Public DHT cannot discover peer nodes](../bug-fix/2026-09-20-public-dht-peer-discovery-gap.md).
- **Cost: a hand-specified endpoint never updates itself.** If the peer changes address or port, the user must run `/add` again.
- **Known gap: group invites to manually added contacts still fail.** A static peer's `PublicKey` is empty, while `GroupChatService.EncryptGroupKeyForMember` needs the long-term public key to wrap the group key; that failure is **silently swallowed** by `CreateGroupAsync`'s `catch {}`. The fix is to return the long-term public key in `KeyExchangeMessage` (a wire change), which is out of scope here.
- Guard tests: `IdentityAndEndpointTests.静态对端_登记后无需DHT发现即可被FindNodeAsync命中`, `IdentityAndEndpointTests.静态对端_仅凭显式端点即可完成双向加密私聊` (which never calls the harness's `Discover`), and the 19 parsing cases in `EndpointTextTests`.

## Deferred

`/connect <ip:port>` — connecting without knowing the peer's node ID in advance. It needs an extra "hello" exchange to learn the peer's identity (for example via the `SenderId` of `KeyExchange`); shipped separately as [Direct connect via an endpoint without a known peer NodeId](./2026-09-21-blind-connect.md).
