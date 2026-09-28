# Agent Note: NAT traversal (UPnP / external endpoint)

Status: implemented

## Problem

REPAIR-PLAN §Phase 0 left reachability up to the user — either the same LAN, or manual `/add <ip:port>`. When devices are on different networks (e.g. each on its own home broadband or mobile carrier), there is no automatic mechanism for a peer to discover our inbound IP and port. `MessageRouter.GetOrCreateConnectionAsync` uses `node.EndPoint` to open the TCP connection; if that endpoint came from the KRPC packet source, it is the peer's view of "us" (our NAT public IP); if from `GetLocalIPAddress()`, it is our own LAN IP (impossible for a peer to reach). `NodeInfo.ExternalEndPoint` was declared in Phase 0 with a comment "for NAT traversal" but is never written or read anywhere in the repository.

Phase 1 implements DHT announcement — `announce_peer.port` carries our `actualTcpPort`. If we are behind NAT and the NAT does not forward that port, peers that resolve us via the DHT and try to connect will be silently refused by the NAT.

## Decision

Implement a best-effort NAT traversal layer:

- **2.1 UPnP IGD port mapping (`P2PChat.Networking.Transport.UpnpClient`)**: HTTP/1.1 over SSDP + SOAP, mapping the same port for both TCP and UDP (`actualTcpPort`) on the router. **No COM (NATUPnP)** — incompatible with Native-AOT. This change uses raw SSDP (UDP multicast) + raw TCP socket + manual HTTP/1.1 + SOAP framing, all AOT-friendly.
- **2.2 DHT source-address backfill of `ExternalEndPoint`**:
  - The local node's `ExternalEndPoint` is filled by UPnP's `GetExternalIPAddress` SOAP call (`MainlineDhtService.ApplyMapping(mapping)`).
  - A remote node's `ExternalEndPoint` is captured at `announce_peer` receive time into `_peerCache[info_hash][i].ExternalEndPoint` (= the KRPC packet source UDP address) — the same mechanism by which BitTorrent clients remain discoverable behind NAT.
- **2.3 Graceful degradation**: when UPnP is unavailable or mapping fails, the TUI self-test prints a new `UPnP 状态:` line explicitly stating "无 UPnP —— 仅同网段/公网可达方可主动连入", never silently fails.
- **2.4 Relay / hole punching**: out of scope.

## Protocol & interfaces

- **`IUpnpClient` interface** (`P2PChat.Core.Abstractions`):
  - `Task<UpnpMapping?> TryMapAsync(int port, int leaseDurationSeconds = 3600, CancellationToken ct = default)`
  - `Task RemoveAsync(int port, CancellationToken ct = default)`
  - Production implementation: `UpnpClient` in Networking/Transport; tests use a private `FakeUpnpClient`.
- **`UpnpMapping`** record `(ExternalEndPoint, Gateway, LeaseDuration)`.
- **`NatMappingState` enum** in Core/Abstractions/IDhtService: `NotAttempted` / `Mapped` / `Unavailable`. `IDhtService` exposes `NatMappingState` and `LocalExternalEndPoint` (default `NotAttempted` / `null`), so the TUI reads through the interface — no cast to `MainlineDhtService` required.

## P2PC protocol extensions (none new)

`announce_peer` / `get_peers` carry no new P2PC fields for this change. Phase 1's 26-byte `p2pc_peers` already conveys the announced peer's IP+port; our own `ExternalEndPoint` comes from UPnP and does not go over KRPC.

## Alternatives considered

**Use COM (NATUPnP).** Rejected: COM interop is incompatible with Native-AOT (IL warnings, single-file publish failure). Use raw SSDP / SOAP instead.

**Use NAT-PMP (Apple routers) / PCP (RFC 6887).** Deferred: parallel support would increase complexity for limited gain; current implementation covers UPnP IGD only. Revisit if needed.

**Relay nodes.** Rejected: directly conflicts with the "decentralized, no central server" premise.

**Hole punching (UDP/TCP).** Rejected: implementation complexity, NAT-behaviour-dependent, not portable across routers; out of scope.

**Auto-retry / back-off.** Kept simple: failure is logged; `RefreshLoopAsync` re-arms on its 15-minute cycle. No exponential back-off.

## Consequences

**Won:**
- When the router supports UPnP, our TCP+UDP ports are mapped to a public endpoint; mappings are removed on process exit.
- When a peer announces itself, the UDP packet source address is recorded into `peerCache[info_hash][i].ExternalEndPoint` — no longer a dead field.
- TUI self-test adds a `UPnP 状态:` line that explicitly states reachability when mapping fails.
- On a successful mapping, `MainlineDhtService.ApplyMapping` immediately fires one `AnnounceNowAsync` so the public DHT sees our public entry without waiting for the next 15-minute refresh.

**Paid:**
- UPnP implementations vary widely across routers; failures log at Debug and never block startup — consistent with the "best-effort" contract.
- ~200 lines of hand-written SSDP / HTTP / SOAP parsing. No third-party NAT library is introduced to preserve AOT safety.
- Tests use `FakeUpnpClient` (DI swap) covering success / failure / timeout; **no real router** is required.
- On shutdown, the `finally` block calls `RemoveAsync`; failures are Debug-logged and the router's lease expiry is the backstop.

## Verification

- `dotnet build`: 0 errors, 0 warnings in scope (`src/P2PChat.Networking`, `src/P2PChat.Core`, `src/P2PChat.UI`, `src/P2PChat.App`).
- `dotnet test`:
  - `P2PChat.Networking.Tests`: **19 pass** (12 from Phase 1 + 6 new `UpnpClientTests` + 1 new `RealDiscoveryTests.AnnouncePeer_收到后_peerCache的ExternalEndPoint_等于包源地址`).
  - `P2PChat.Core.Tests`: 37 pass (unchanged).
  - `P2PChat.Crypto.Tests`: 8 pass (unchanged).
- All UPnP tests run without a real router or public network; CI / sandbox is happy.

- **Real-discovery smoke test**: `AnnouncePeer_收到后_peerCache的ExternalEndPoint_等于包源地址` opens a raw UDP socket on `127.0.0.1:0`, issues `get_peers` to a fresh `MainlineDhtService` instance to obtain a token, then sends `announce_peer` with that token. The test asserts the announcement is recorded in `peerCache` with `ExternalEndPoint = packet source UDP address`. This is the 2.2 acceptance criterion verified end-to-end without any NAT or external network.

## Deferred

- **NAT-PMP / PCP support**: UPnP IGD only for now. Apple routers and certain IPv6 NAT devices are out of scope.
- **Hole punching**: requires symmetric NAT / Endpoint-Independent Mapping; out of scope.
- **Mapping refresh**: routers may drop mappings on lease expiry; `RefreshLoopAsync` does not currently re-detect external reachability. A future improvement: "on incoming ping, check whether our mapping still exists".