# Agent Note: /connect <ip:port> — direct connect without a known peer NodeId

Status: implemented

## Problem

`/add <nodeId> <ip:port>` (see [Direct connect via an explicit endpoint](./2026-09-20-static-peer-explicit-endpoint.md)) already lets two nodes connect directly when **both** facts (NodeId, ip:port) are known. The remaining gap was a more natural user story: **the user only has the peer's TCP endpoint** (learned it from a shared note, an out-of-band message, or a log), and does not know the peer's 40-hex-digit NodeId. No command covered that path.

The wire had everything needed to recover the identity: `KeyExchangeMessage.SenderId` on a response is filled in by `KeyExchangeHandler` as `identity.NodeId.ToByteArray()` (the peer's real NodeId). What was missing was a command that does one round-trip of `KeyExchange`, reads back the identity, registers the peer under that real NodeId, and adds them to the contact book.

## Decision

`/connect <ip:port>` is implemented in `P2PChatTui` and follows these steps in order:

1. The TUI parses `ip:port` via `EndpointText` (literal IPs only, no DNS).
2. It builds a temporary `NodeInfo` with `NodeId.CreateRandom()` and the parsed `EndPoint`, opens a TCP connection through `IMessageRouter.GetOrCreateConnectionAsync`, and sends a `KeyExchangeMessage` (`IsResponse=false`) carrying a fresh ECDH ephemeral public key.
3. It reads frames from the same connection until it sees a `KeyExchangeMessage` with `IsResponse=true`. The `envelope.SenderId` on that response is the peer's real `NodeId`.
4. It derives the shared secret locally with the response's ephemeral public key and stores the session key in `IKeyStore` under the peer's real NodeId.
5. It calls `IDhtService.RegisterStaticPeer` with the real NodeId and the known endpoint, so `FindNodeAsync` (and therefore `/msg`) routes straight to the peer on the next send.
6. It calls `IContactService.AddContactAsync` to persist the contact with a default alias `peer-<8-hex>`.
7. It closes the temporary connection. The connection pool is keyed by NodeId, so the next `/msg` opens a fresh one under the real NodeId.

A 10-second timeout on the whole hello prevents the TUI from hanging on a dead endpoint. Failure paths ("endpoint unparseable", "connection refused", "timeout") all surface in the system message line.

`P2PChatTui` gains two constructor parameters: `IMessageRouter` (for the temporary connection) and `IEncryptionService` (for the ECDH ephemeral and shared-secret derivation). Both are already part of `P2PChat.Core.Abstractions`, so no new project dependency is introduced.

## Alternatives considered

**Use `ChatService.SendPrivateMessageAsync` after manually inserting a random NodeId into the routing table.** Rejected: that command requires a target NodeId, which the user does not have. It also tries to find the peer via `FindNodeAsync` first, so it would have to be preceded by the very routing-table workaround `/connect` already exists to provide.

**Make `/connect` hello-only — learn the NodeId, do not derive the session key, let `ChatService.PerformKeyExchangeAsync` redo the handshake on first `/msg`.** Rejected: throws away the work already done. Both sides already have the response with the peer's ephemeral public key and have generated their own ephemeral private key; deriving the shared secret and storing the session key is a local-only operation that costs nothing and saves one round trip on the next `/msg`.

**Add a new wire type `HelloMessage` instead of reusing `KeyExchangeMessage`.** Rejected: the wire already supports identity announcement via `SenderId` on `KeyExchange`, and adding a parallel type means another `MessageType` enum value, another `[Union]` registration, another `IMessageHandler`, and another registration site in `Program.cs` — for a single round trip with no new semantics.

**Look up the peer's identity by sending a UDP DHT query or by inspecting KRPC traffic.** Rejected: DHT discovery cannot resolve an arbitrary NodeId (see [Public DHT cannot discover peer nodes](../bug-fix/2026-09-20-public-dht-peer-discovery-gap.md)), and listening for KRPC for a single connect is unrelated work.

## Consequences

- After `/connect <ip:port>`, the TUI prints the discovered peer NodeId (40 hex) and a `peer-xxxxxxxx` alias.
- `/msg peer-xxxxxxxx <text>` immediately after `/connect` does NOT trigger a second `KeyExchange` round trip on the wire: `ChatService.HasSessionKey(peerId)` is already true.
- A second `/connect <same ip:port>` is idempotent: the existing contact is reused (its alias is preserved), and the static peer is re-registered.
- An unreachable endpoint produces a clear timeout / connection-refused message in the TUI; no unhandled exception escapes to the UI.
- **Cost: the peer's `KeyExchangeHandler` overwrites any pre-existing session key under the connector's NodeId.** In practice `/connect` is invoked precisely because the user does not yet know the peer's NodeId, so no prior session key with that peer can exist; the cost only shows up in pathological re-runs, which `/msg` will then repair on the next round trip.
- **Cost: `SenderId` is still forgeable.** `/connect` learns whatever `SenderId` the peer's `KeyExchangeHandler` writes, so a malicious peer can advertise any NodeId here. The follow-up ECDSA-signing work (`Phase 3.2`, separately tracked) will close that hole; `/connect` does not worsen the situation, because every other `SenderId` field on the wire has the same property today.
- **Cost: the peer's long-term public key remains empty in the resulting `NodeInfo`.** `/connect` does not return the peer's identity public key in the hello (the wire has no field for it on `KeyExchangeMessage`), so the static peer is registered with `PublicKey = identity.PublicKey` (our own key, used as a placeholder). Group invites to a `/connect`-discovered contact still fail for the same reason a `/add`-registered one does, and the same long-term fix (returning the long-term public key in `KeyExchangeMessage`) applies to both.
- `dotnet build` is 0 errors / 0 warnings; `dotnet test` remains green (139 passing in the test set this change touched; the two `FileTransferIntegrityTests` that fail under the combined run fail because of an unrelated in-progress change by another teammate on `FileTransferService.SendFileChunksAsync`).

## Deferred

Integration-test coverage of the `/connect` hello path is intentionally **not** added in this change. Per the Lead's coordination note, writes to `tests/P2PChat.Integration.Tests/` are deferred until `task-1`/`task-2` complete, because both tasks restructure parts of the same test surface (NodeHarness changes for task-1, file-transfer service changes for task-2). Once those are committed, a focused test like `BlindConnectTests./connect_通过hello_学到对端NodeId并登记为静态对端_随后_/msg_不触发第二轮握手` can be added against `NodeHarness` without overlap risk.
