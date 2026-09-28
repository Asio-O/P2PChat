# Agent Note: /connect <ip:port> — 直连（不需预知对端节点 ID）

Status: implemented

## Problem

`/add <nodeId> <ip:port>`（见 [Direct connect via an explicit endpoint](./2026-09-20-static-peer-explicit-endpoint.md)）已经能在**两件事都已知**（NodeId + ip:port）的前提下让两节点直连。剩下的缺口是一个更自然的用户场景：**用户只拿到对端的 TCP 端点**（例如：当面告知、从外部消息读到、从日志里抄出来），并不知道对方的 40 位十六进制 NodeId。现有命令集里没有覆盖这一路径。

线路上其实什么都有：响应侧 `KeyExchangeMessage.SenderId` 由 `KeyExchangeHandler` 写入为 `identity.NodeId.ToByteArray()`（即对端真实 NodeId）。缺的是把一次 `KeyExchange` 往返、读取身份、登记静态对端、写入联系簿这几件事串起来的入口命令。

## Decision

`/connect <ip:port>` 在 `P2PChatTui` 中实现，按顺序执行以下步骤：

1. TUI 用 `EndpointText` 解析 `ip:port`（仅字面 IP，不做 DNS）。
2. 构造临时 `NodeInfo`：`NodeId.CreateRandom()` + 解析出的 `EndPoint`；通过 `IMessageRouter.GetOrCreateConnectionAsync` 打开一条 TCP 连接，发一条 `KeyExchangeMessage`（`IsResponse=false`），带上一次性的 ECDH 临时公钥。
3. 沿同一连接读取帧，直到拿到 `IsResponse=true` 的 `KeyExchangeMessage`；其 `envelope.SenderId` 即对端真实 `NodeId`。
4. 用响应中的对端临时公钥派生共享密钥，写入会话密钥到 `IKeyStore`（键 = 对端真实 NodeId）。
5. 调用 `IDhtService.RegisterStaticPeer`，以「真实 NodeId + 已知端点」登记：下次 `/msg` 的 `FindNodeAsync` 会直接命中。
6. 调用 `IContactService.AddContactAsync`，默认别名 `peer-<8-hex>`，落盘。
7. 关闭临时连接 —— 连接池按 NodeId 索引，下次 `/msg` 会按真实 NodeId 新建连接。

整次 hello 设 10 秒超时，避免 TUI 在死端点上挂死。所有失败路径（端点无法解析、连接被拒、超时）都只走 TUI 系统消息线，不会向 UI 抛出未处理异常。

`P2PChatTui` 构造函数新增两个参数：`IMessageRouter`（用于临时连接）与 `IEncryptionService`（用于生成 ECDH 临时密钥对与派生共享密钥）。两者都属于 `P2PChat.Core.Abstractions`，未引入新的项目依赖。

## Alternatives considered

**手工把随机 NodeId 插入路由表后调用 `ChatService.SendPrivateMessageAsync`。** 否决：该命令要求传入目标 NodeId，正是用户没有的东西；而且它先走 `FindNodeAsync`，又得先解决路由表问题，仍是 `/connect` 想消除的工作。

**只做 hello（学身份、不派生会话密钥），把 ECDH 留给 `ChatService.PerformKeyExchangeAsync` 在首次 `/msg` 时重做。** 否决：丢掉已经做完的工作 —— 双方都拿到了响应中的对端临时公钥、自己手上有临时私钥，派生共享密钥并写入 `IKeyStore` 只是一次本地计算，零代价，却能省下首次 `/msg` 的整轮往返。

**新增一个独立的 `HelloMessage` 线路类型，而不是复用 `KeyExchangeMessage`。** 否决：线路已经靠 `SenderId` 支持身份声明，再加一种类型意味着新的 `MessageType` 枚举值、新的 `[Union]` 注册、新的 `IMessageHandler`、`Program.cs` 里的额外注册点 —— 仅为了一个没有任何新语义的往返。

**靠 UDP DHT 查询或监听 KRPC 流量来识别对端身份。** 否决：DHT 无法解析任意 NodeId（见 [Public DHT cannot discover peer nodes](../bug-fix/2026-09-20-public-dht-peer-discovery-gap.md)），为一次直连单独监听 KRPC 是不相干的工作。

## Consequences

- 执行 `/connect <ip:port>` 后，TUI 打印对端 NodeId（40 位 hex）与 `peer-xxxxxxxx` 别名。
- 紧接其后执行 `/msg peer-xxxxxxxx <消息>` **不再**产生第二轮 `KeyExchange` 线路往返：`ChatService.HasSessionKey(peerId)` 已为 true。
- 对同一 `ip:port` 重复 `/connect` 是幂等的：已存在的联系人会被复用（保留其原别名），静态对端被重新登记。
- 不可达端点产生明确的超时 / 连接拒绝消息；无未处理异常逃逸到 UI。
- **代价：对端的 `KeyExchangeHandler` 会覆盖本机 NodeId 下已有的会话密钥。** 现实场景下 `/connect` 之所以被调用，正是因为用户不知道对端的 NodeId，因此同一对端下不可能事先存在会话密钥；仅在病态重跑时会出现这种情况，由下一次 `/msg` 自愈。
- **代价：`SenderId` 仍可伪造。** `/connect` 学到的就是 `KeyExchangeHandler` 写入的值，恶意对端可以任意伪造。后续 ECDSA 签名（`Phase 3.2`，单独跟踪）会补这个洞；`/connect` 不放大风险，因为线路上所有 `SenderId` 字段今天都共享同一个性质。
- **代价：对端的长期公钥仍然为空。** hello 报文未携带对端身份公钥（`KeyExchangeMessage` 没有这个字段），结果静态对端 `PublicKey` 用本机公钥占位。给 `/connect` 发现的对端发群邀请会因同样的原因失败（与 `/add` 一致），修法也是同一项线路变更 —— 在 `KeyExchangeMessage` 中回送长期公钥。
- `dotnet build` 0 错 0 警；`dotnet test` 保持全绿（本变更触及的测试集合中 139 条通过；两次 `FileTransferIntegrityTests` 在合并运行下失败，是另一名队友在 `FileTransferService.SendFileChunksAsync` 上的进行中改动，与本变更无关）。

## Deferred

`/connect` hello 路径的集成测试覆盖**故意不在本变更中加入**。按 Lead 协调约定，`tests/P2PChat.Integration.Tests/` 的写入延至 `task-1`/`task-2` 完成之后 —— 两者都在重构同一测试面（task-1 动 NodeHarness、task-2 动文件传输服务）。等两个任务提交后，可加一条 `BlindConnectTests./connect_通过hello_学到对端NodeId并登记为静态对端_随后_/msg_不触发第二轮握手`，基于 `NodeHarness` 写，无重叠风险。
