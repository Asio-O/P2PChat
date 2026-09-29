# Agent Note: NAT 穿透（UPnP / 外部端点）

Status: implemented

## Problem

REPAIR-PLAN §阶段 0 把「可达性」拆给用户：要么同局域网，要么用 `/add <ip:port>` 手工指定。当两台设备不在同网段时（如各自的家庭宽带/移动网络），没有任何自动机制让对端找到本机的入站 IP 与端口 —— `MessageRouter.GetOrCreateConnectionAsync` 用 `node.EndPoint` 建 TCP 连接，而这个 `EndPoint` 若取自 KRPC 包源端点则是对方看到的「我们」的 NAT 公网 IP；若取自 `GetLocalIPAddress()` 则是我们自己的 LAN IP（不可能被对端连入）。`NodeInfo.ExternalEndPoint` 字段在 Phase 0 注释里就被声明为「NAT 穿透用」，但全仓库无任何写入或读取。

Phase 1 已实现 DHT 宣告：宣告里填的是本机 `LocalNode.EndPoint.Port`（即 `actualTcpPort`），但若本机在 NAT 后且 NAT 未转发该端口，对端收下宣告后用「公网 IP + TCP 端口」来连我们，会被 NAT 拒绝。

## Decision

实现一个尽力而为的 NAT 穿透层：

- **2.1 UPnP IGD 端口映射（`P2PChat.Networking.Transport.UpnpClient`）**：HTTP/1.1 over SSDP + SOAP，**TCP 与 UDP 用同一端口号**（`actualTcpPort`），向路由器申请外网 → 内网映射。**不走 COM（NATUPnP）**—— 与 Native-AOT 直接冲突。本期用裸 SSDP（UDP multicast）+ 裸 TCP socket + 手工 HTTP/1.1 + SOAP 报文，全部 AOT 友好。
- **2.2 DHT 源地址回填 `ExternalEndPoint`**：
  - 本机的 `ExternalEndPoint` 由 UPnP `GetExternalIPAddress` SOAP 调用填入（`MainlineDhtService.ApplyMapping(mapping)`）；
  - 对端的 `ExternalEndPoint` 在收到 `announce_peer` 时记入 `_peerCache` 的 `AnnouncedPeer.ExternalEndPoint` 字段（= KRPC 包源 UDP 地址），提供 TUI / 诊断使用 —— 这正是 BitTorrent 客户端「NAT 后仍可被找到」的机制。
- **2.3 失败降级**：无 UPnP / 映射失败时，`TUI` 自检块新增「UPnP 状态」行显式标注「无 UPnP —— 仅同网段/公网可达方可主动连入」，不静默失败。
- **2.4 中继/打洞**：本期不做。

## 协议与接口

- **`IUpnpClient` 接口**（`P2PChat.Core.Abstractions`）：
  - `Task<UpnpMapping?> TryMapAsync(int port, int leaseDurationSeconds = 3600, CancellationToken ct = default)`
  - `Task RemoveAsync(int port, CancellationToken ct = default)`
  - 默认实现 `UpnpClient`（Networking/Transport）；测试用 `UpnpClientTests.FakeUpnpClient`。
- **`UpnpMapping`** 记录 `(ExternalEndPoint, Gateway, LeaseDuration)`。
- **`NatMappingState` enum**（Core/Abstractions/IDhtService）：`NotAttempted` / `Mapped` / `Unavailable`。`IDhtService` 接口暴露 `NatMappingState` 与 `LocalExternalEndPoint`（默认 `NotAttempted` / `null`），TUI 与 SELFTEST 直接读接口，不需强转 `MainlineDhtService`。

## P2PC 协议扩展（与 Phase 1 复用）

announce_peer / get_peers 报文里**不新增** P2PC 字段 —— Phase 1 的 26 字节 `p2pc_peers` 已足够传递对端宣告的 IP+port；本机的 `ExternalEndPoint` 由 UPnP 探测单独获取，不进入 KRPC 报文。

## Alternatives considered

**走 COM（NATUPnP）。** 否决：COM interop 与 Native-AOT 直接冲突，会引入 IL 警告且单文件发布失败。改用裸 SSDP/SOAP。

**用 NAT-PMP（Apple 路由器）/ PCP（RFC 6887）。** 保留：与 SSDP/UPnP 并行时增加复杂度，对本期收益小；当前实现仅 UPnP IGD。如未来需要再扩展。

**中继节点。** 否决：与「去中心化、无中心服务器」前提冲突，且需要部署基础设施。

**打洞（UDP/TCP hole punching）。** 否决：实现复杂、与 NAT 行为强相关、不可移植，本期不做。

**自动重试 / 退避。** 保留：失败即 log（`UdpSocketErrorClassifier` 风格），由 `RefreshLoopAsync` 周期触发再次尝试；不做热退避。

## Consequences

**赢得：**
- 路由器支持 UPnP 时，本机 TCP+UDP 端口成功映射到公网，进程退出时正确释放。
- 对端宣告后，本机把对端的 NAT 后入口（KRPC 包源地址）写入 `peerCache[info_hash][i].ExternalEndPoint`，不再是死字段。
- TUI 自检块新增「UPnP 状态」行：无映射时显式标注可达性范围，不静默失败。
- UPnP 成功后立即触发一次 `AnnounceNowAsync`，让公共 DHT 看到我们的公网入口（缩短发现窗口）。

**付出：**
- 不同路由器的 UPnP 实现兼容性差异大，失败仅 Debug 日志不阻塞 —— 与「尽力而为」契约一致。
- 引入裸 SSDP / SOAP 手工解析（~200 行）—— 不引入第三方 NAT 库以保持 AOT 安全。
- 测试用 FakeUpnpClient（DI 替换）覆盖成功/失败/超时路径，**不依赖真实路由器**。
- 进程退出时通过 `try/finally` 调 `RemoveAsync` —— 失败仅 Debug 日志，由路由器租约自然过期兜底。

## Verification

- `dotnet build`：在我写范围内（`src/P2PChat.Networking`、`src/P2PChat.Core`、`src/P2PChat.UI`、`src/P2PChat.App`）0 错 0 警。
- `dotnet test`：
  - `P2PChat.Networking.Tests`：**19 通过**（原 12 + Phase 2 新增 6 条 `UpnpClientTests` + 1 条 `AnnouncePeer_收到后_peerCache的ExternalEndPoint_等于包源地址`）。
  - `P2PChat.Core.Tests`：37 通过（不变）。
  - `P2PChat.Crypto.Tests`：8 通过（不变）。
- 所有 UPnP 测试不依赖真实路由器 / 公网；CI / 沙箱环境可重复运行。

## Deferred

- **NAT-PMP / PCP 支持**：当前仅 UPnP IGD。Apple 路由器与部分 IPv6 NAT 设备不在覆盖范围内。
- **打洞**：跨网络对称 NAT / Endpoint-Independent Mapping 才有意义；本期不引入。
- **重连后的映射刷新**：路由器可能主动撤映射（租约到期），RefreshLoopAsync 当前不重检测外部可达性。后续可加上「收到 ping 自查映射是否存在」。