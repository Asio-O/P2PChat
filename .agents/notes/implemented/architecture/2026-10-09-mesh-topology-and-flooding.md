# Agent Note: The data plane becomes a full mesh: chat messages flood, point-to-point is no longer the only path

Status: implemented

## Problem

The data plane was **on-demand point-to-point**. Sending a message resolved the recipient's endpoint, dialed a fresh TCP connection, and unicast the frame (`MessageRouter.GetOrCreateConnectionAsync` + `SendAsync`). No node ever forwarded a message, so a message reached its recipient only if the sender could open a direct connection to it.

The gap was already on record: [NAT traversal](../bug-fix/2026-09-21-nat-traversal.md) rejected **relay servers** (privileged infrastructure, contradicts the no-central-server premise) and deferred hole punching, and [helper-assisted traversal](../feature/2026-09-29-helper-assisted-traversal-and-ipv6.md) explicitly bounded helping to "establishing the connection, not relaying traffic". The `## Risks` of that proposal names the tension the project has never escaped: when hole punching fails, there is **no fallback**, and the connection simply does not form.

This decision changes the data plane so that a message can travel through other peers instead of requiring a direct path to its recipient. It does not revive the rejected relay server — the distinction is in `## Decision`.

## Decision

**Every node maintains persistent TCP connections to its known peers (a full mesh), and chat messages flood across them.** Two choices were confirmed with the user before implementation: full-mesh connection strategy, and flooding for **all** chat messages (private and group), not group-only.

The mechanics, each chosen to reuse what already existed:

- **Forwarding reuses the original envelope, byte for byte.** `RouteIncomingAsync` forwards the already-verified envelope via `MessageRouter.ForwardAsync` without re-signing and without touching `SequenceNumber` / `Timestamp`. The receiver verifies and deduplicates against the **original** sender. No wire-format change: old nodes and new nodes interoperate.
- **Loop termination comes from the existing replay guard, not from a TTL.** `MessageReplayGuard` buckets by the original `SenderId` and records accepted `MessageId`s (`PeerKeyOf`), so a message that arrives over several paths is accepted once per node, and each node forwards each message at most once. The flood self-terminates on a mesh of any shape.
- **Only chat payloads flood.** `PrivateText` and `GroupText` forward. `KeyExchange` stays request-response (the response must return on the same connection the request left on), `FileMeta` / `FileChunk` / `FileAck` stay point-to-point (bulk traffic), and `GroupInvite` / `GroupNotify` / `DeliveryAck` stay directed. The whitelist lives in `MessageRouter.IsFloodable`.
- **The connection pool keeps its invariant: outbound connections only.** Inbound connections are **not** admitted to the pool, because an outbound connection's initiator has no persistent read loop on it — the key-exchange response is read directly by `ChatService`, and after the handshake nothing reads that direction. Admitted inbound connections would make flooding "reverse-reuse" a connection whose reader has stopped, silently dropping messages. Bidirectional reachability therefore uses two connections per peer pair, one dialed by each side; the mesh maintainer dials what is missing.
- **Private messages gain a recipient check.** Flooding routes a private message through unrelated nodes, some of which hold a session key with the sender and could decrypt it. `PrivateMessageHandler` now requires `message.ConversationId == ConversationId.ForPrivate(myNodeId, senderId)` before decrypting and publishing, so "can decrypt" no longer implies "is the recipient". Group messages need no such check — a non-member fails group-key decryption and the handler already discards it.
- **A maintainer keeps the mesh alive.** `MeshTopologyService` (new, `src/P2PChat.Chat/Mesh/`) ticks on `P2PChat:Mesh:MaintainIntervalSeconds` (default 30), unions the static peer seeds with `IDhtService.GetAllKnownNodes()`, and dials any known peer without an active connection, concurrency-capped at 4. It does not handshake — the mesh connection is only a TCP channel; session keys are still negotiated on demand by `ChatService.EnsureSessionKeyAsync`. It does not discover — the DHT plane is untouched.
- **Configuration is explicit.** `P2PChat:Mesh:FloodingEnabled` (default `true`) reaches the `MessageRouter` constructor and turns forwarding off when set to `false` — a behaviour-contrast / rollback valve, not a security boundary. `P2PChat:Mesh:MaintainIntervalSeconds` (default 30) drives the maintainer.
- **Sending paths now flood.** `ChatService.SendPrivateMessageAsync` keeps resolution and handshake, then `GetOrCreateConnectionAsync(recipient)` (idempotent; guarantees the direct path is present even on the already-has-session-key branch) and `FloodAsync`. `GroupChatService.SendGroupMessageAsync` floods once instead of fanning out per member; member filtering is the receiver's group-key decryption.

## Alternatives considered

**Constrained-degree mesh with a neighbour-selection algorithm.** The standard scale-out answer. It loses for this project on complexity: a neighbour-selection policy, a join/leave rebalance, and delivery that *depends* on flooding correctness instead of on the mesh being connected. The project's stated scale is a small chat group (tens of nodes), where O(N²) connections are affordable and the full mesh makes every message one hop away from every peer that is directly reachable.

**Group-only flooding; private messages stay direct, multi-hop only as a fallback.** Closer to the current model and leaks less private-message metadata. It loses because the fallback it needs is exactly the general mechanism, implemented twice: a direct path and a flood path, plus a decision procedure for when to degrade. It also leaves private delivery — the motivating scenario — with the same no-fallback gap whenever the direct path fails and the flood path is not maintained.

**Admit inbound connections to the pool so one TCP connection serves both directions.** The seductive "TCP is full-duplex" option, and the first implementation tried it. It was rejected after it broke the bidirectional tests: an outbound connection's initiator has no read loop after the handshake, so reverse-reusing the connection writes to a reader that has stopped. Keeping the pool outbound-only costs a second connection per pair but preserves an invariant that every pooled connection has a live reader at the far end.

## Consequences

- **Won: a fallback for the exact gap that motivated it.** When A and C cannot connect directly, A's message can still reach C through B. This partially relieves — it does not close — the tension recorded in [helper-assisted traversal](../feature/2026-09-29-helper-assisted-traversal-and-ipv6.md)'s `## Risks`; whether the fallback exists in practice still depends on whether *some* path through willing peers exists.
- **Not a relay server.** The relay rejected in [NAT traversal](../bug-fix/2026-09-21-nat-traversal.md) was a privileged, deployed-infrastructure server. A mesh peer is equal, holds no special role, and forwards by the same code every node runs. The rejection stands.
- **Cost: private-message metadata is network-visible.** The sender identity and conversation existence of a private message are visible to every node that relays it, even though the content stays AES-256-GCM encrypted. This was surfaced to the user and accepted; it is the price of flooding private traffic.
- **Cost: O(N²) connections, two per pair.** Each side dials its own outbound connection. The count is bounded by the small-network premise.
- **Cost: one relay hop per forwarding peer per message.** Every node forwards each message at most once (replay-guard bounded), and forwarding back toward the original sender is suppressed by `ForwardAsync`'s sender-key skip. Intermediate-hop echo (a relayer re-receiving its own forward on the reverse connection) is not suppressible from the envelope alone and is absorbed by the far end's replay guard — acceptable at the stated scale.
- **Old nodes break the flood chain, safely.** A node on an older build does not forward, so in a mixed network the flood stops at old-node boundaries. The old node still processes what it receives; degradation is partial reach, not an error.

## Deferred

- **Dynamic peer-set maintenance is seed-time only.** The maintainer's seed list is captured from static peers at startup; peers discovered later enter the mesh through on-demand dialing (private-send dial, group-invite dial) or by dialing in, not by the maintainer. Widening the maintainer to the live routing table every tick is a small follow-up, not done here.
- **No neighbour-degree limit.** Accepted by the user's choice of full mesh; reconsider only if the deployment outgrows tens of nodes.
- **`p2pc_peers` `values`-path trimming and the O1 static-table shadow remain open** as recorded in [open defects](../bug-fix/2026-09-30-open-defects-and-pending-decisions.md); the mesh does not change that a handshake still requires a direct connection.

## Related

- [NAT traversal](../bug-fix/2026-09-21-nat-traversal.md) — the decision whose relay rejection this note does **not** overturn, and whose deferred hole punching this note's fallback partially compensates for.
- [Helper-assisted traversal and IPv6](../feature/2026-09-29-helper-assisted-traversal-and-ipv6.md) — the proposal whose `## Risks` fallback gap this note partially relieves.
- [Open defects and pending decisions](../bug-fix/2026-09-30-open-defects-and-pending-decisions.md) — O1 (static-table shadow) and the traversal-failure deployment coverage remain open.
- [Message signing](../bug-fix/2026-09-21-message-signing.md) — the envelope signing that lets forwarding reuse an envelope without re-signing.
