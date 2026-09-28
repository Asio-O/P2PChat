# Agent Note: File transfer send loop must use state.ChunkSize

Status: implemented

## Problem

`FileTransferService.SendFileChunksAsync` (in `src/P2PChat.FileTransfer/Services/FileTransferService.cs:237-275`) allocates its read buffer against `DefaultChunkSize` (65536):

```csharp
var buffer = new byte[DefaultChunkSize];
await using var fs = System.IO.File.OpenRead(state.FilePath!);
```

The sender-side `TransferState` constructed by `SendOfferAsync` **never assigns `ChunkSize`**, so the field falls back to its class default of 65536. Under the current default this is invisible, but chunk size is a sender-decided policy (the `FileMetaMessage.ChunkSize` value is filled in by the sender and sent to the peer). Anything that later exposes `SendOfferAsync` as chunk-size-configurable — or anything that records the actual chunk size on `TransferState` — exposes the silent hard-coding at line 243.
More directly: the implementation currently makes `state.ChunkSize` a no-op on the sender path, while the receive path (`HandleFileChunkAsync` writing at offset `chunk.ChunkIndex * state.ChunkSize`) uses the genuinely negotiated value. The two paths therefore disagree on what the negotiated chunk size even *is*; the "negotiation" is fake.

## Decision

- `SendFileChunksAsync` now allocates its read buffer with `state.ChunkSize` (the field is the single source of truth on the sender side).
- `SendOfferAsync` constructs the `TransferState` with `ChunkSize = chunkSize`, so the sender's `state.ChunkSize` and the announced `FileMetaMessage.ChunkSize` come from the same expression. The receive path (`HandleFileMetaAsync`) still uses the value from the meta.
- `SendOfferAsync` gains an overload `SendOfferAsync(NodeId, string, int chunkSize, CancellationToken)` so callers can pick a non-default chunk size; `IFileTransferService` exposes the same overload.
- The 65536 default is preserved; no migration is required.

## Alternatives considered

**Only patch `SendFileChunksAsync` and leave `SendOfferAsync` alone.** Rejected: the sender `TransferState.ChunkSize` still defaults to 65536 (from the class default) while coincidentally matching `FileMetaMessage.ChunkSize`. Once the sender's chunk size becomes configurable, the two immediately drift apart, and the field-vs-meta source disagreement is exactly the bug the patch is meant to remove. Setting both from the same expression is the durable fix.
**Promote the buffer to a long-lived field on `FileTransferService`.** Rejected: a per-transfer local buffer is the right memory model; a shared field would need explicit lifecycle management for no observable benefit.
**Delete the `DefaultChunkSize` constant entirely.** Rejected: the constant is still useful as the sender's default policy value. The bug is not the constant's existence, it is the constant being used where state should be read.

## Consequences

- Sender `TransferState.ChunkSize` and `FileMetaMessage.ChunkSize` now share a single expression; "chunk size only has one source of truth" holds.
- Any non-default `ChunkSize` produces a complete, integrity-checked transfer — see the new tests `发送方_SendOfferAsync_非默认ChunkSize_必须同步写入state并以该大小实际切片` (asserts 200 KB at `ChunkSize=4096` → 49 chunks of 4096 B with the final partial, SHA-256 verified) and `发送方_SendOfferAsync_默认路径_回归基线分块大小65536` (default path is unchanged).
- At the default value, behavior is byte-for-byte identical to before; the existing 10 `分块传输_*` tests stay green.
- `dotnet build` 0 errors 0 warnings; `dotnet test` all green.
- **Future risk:** `TransferState.ChunkSize` still has a 65536 default. If a future refactor introduces a "construct `TransferState` first, set `ChunkSize` later" pattern, the same drift could reappear. Marking `ChunkSize` `required` would prevent that; deferred unless needed.
- **Verification:** the two new `发送方_*` tests pin the sender-side behavior; `FileTransferService:243` reading `state.ChunkSize` is the only sender-side runtime point of failure.