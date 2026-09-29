# Agent Note: 文件传输发送循环必须使用 state.ChunkSize

Status: implemented

## Problem

`FileTransferService.SendFileChunksAsync`（`src/P2PChat.FileTransfer/Services/FileTransferService.cs:237-275`）按 `DefaultChunkSize`（65536）分配读缓冲：

```csharp
var buffer = new byte[DefaultChunkSize];
await using var fs = System.IO.File.OpenRead(state.FilePath!);
```

而发送方在 `SendOfferAsync` 创建 `TransferState` 时**没有设置 `ChunkSize`**（默认仍是 65536）。这一组合在默认配置下不出现症状，但「分块大小」本质上是发送方决定的策略项（`FileMetaMessage.ChunkSize` 由发送方填并告知对端）。任何后续把 `SendOfferAsync` 暴露成可配置分块大小——或代码自身在 `TransferState` 上记录实际分块大小——都会暴露这里仍然硬编码 `DefaultChunkSize` 的不一致。
更直接的是：当前实现把发送方 `TransferState.ChunkSize` 字段当摆设，使 `state.ChunkSize` 在发送路径上完全无作用；接收路径（`HandleFileChunkAsync` 落盘偏移 `chunk.ChunkIndex * state.ChunkSize`）用的是真实协商值，两个路径依赖的不是同一个事实，分块大小的"协商"语义是假的。

## Decision

- `SendFileChunksAsync` 的 `var buffer = new byte[DefaultChunkSize];` 已改为 `var buffer = new byte[state.ChunkSize];`，按发送方 `state.ChunkSize` 分配读缓冲。
- `SendOfferAsync` 构造 `TransferState` 时显式写入 `ChunkSize = chunkSize`（与 `FileMetaMessage.ChunkSize` 同源），发送路径的 `state.ChunkSize` 与对外宣告值一致；接收路径仍以 `HandleFileMetaAsync` 收到的 `meta.ChunkSize` 为准。
- `SendOfferAsync` 新增 `SendOfferAsync(NodeId, string, int chunkSize, CancellationToken)` 重载，使调用方可以指定非默认分块大小；`IFileTransferService` 接口同步暴露。
- 默认 65536 不变；现状兼容，无须迁移。

## Alternatives considered

**只改 `SendFileChunksAsync`，不动 `SendOfferAsync`。** 否决：发送方 `TransferState.ChunkSize` 仍是默认 65536（来自 `TransferState` 默认值），与 `FileMetaMessage.ChunkSize` 表面上同值但不同源——任何后续把发送方分块大小改为可配置都会让 `state.ChunkSize` 立即落后；且两份「握手」值应当来自同一表达式。两处一起改让「分块大小只有一个事实来源」成立。
**抽出 `_buffer = new byte[state.ChunkSize]` 为字段复用。** 否决：单次传输生命周期内的局部 buffer 足够，不必引入字段状态；当前实现的"按传输构造 buffer"是正确的内存模式。
**彻底删除 `DefaultChunkSize` 常量。** 否决：作为「发送方默认策略值」仍需要一个名字；常量本身没有害处，害处只在它被错误地用于运行时计算——也就是本次修的位置。

## Consequences

- 发送方 `TransferState.ChunkSize` 与 `FileMetaMessage.ChunkSize` 现在**同源**（同一表达式 `chunkSize`），不再依赖 `TransferState` 字段默认值；「分块大小只有一个事实来源」成立。
- 任意非默认 `ChunkSize` 下，发送循环按该大小切片并完整落盘（`tests/P2PChat.Integration.Tests/FileTransferIntegrityTests` 新增 `发送方_SendOfferAsync_非默认ChunkSize_必须同步写入state并以该大小实际切片`，断言 `ChunkSize=4096` 时 200 KB 文件分 49 块、每块 4096 B、SHA-256 校验通过；以及 `发送方_SendOfferAsync_默认路径_回归基线分块大小65536` 保证默认路径不变）。
- 默认 65536 时行为完全等价于改前；既有基线测试（10 条 `分块传输_*`）保持全绿。
- `dotnet build` 0 错 0 警；`dotnet test` 全绿。
- **未来风险：** `TransferState.ChunkSize` 默认值 65536 仍存在；若未来引入「先 `new TransferState(...)` 再设置 `ChunkSize`」的反模式，可能再次让 `DefaultChunkSize` 与实际值脱钩。修法是把 `TransferState.ChunkSize` 改为 `required`，迫使构造时显式赋值。本期未触发，留作将来重构时再处理。
- **Verification：** 上述两条新增 `发送方_*` 测试即守门；`FileTransferService:243` 的 `state.ChunkSize` 即为唯一发送侧运行时切点。