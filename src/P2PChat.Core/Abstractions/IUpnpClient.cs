using System.Net;

namespace P2PChat.Core.Abstractions;

/// <summary>
/// UPnP IGD 端口映射客户端接口 —— 用于 NAT 后将本机 TCP/UDP 端口暴露到公网。
/// </summary>
/// <remarks>
/// <para>
/// 本接口为 Phase 2 NAT 穿透新增。Phase 0 的「对端即引导节点」路径已能让两台同网段设备
/// 互相发现并私聊；UPnP 是 Phase 1 宣告机制的接缝 —— 宣告把端口广播到公共 DHT，
/// 但若 NAT 未转发该端口，对端拿到宣告后仍连不上。映射成功后 `NodeInfo.ExternalEndPoint`
/// 也被填上（本机的公网入口），便于对端做诊断。
/// </para>
/// <para>
/// 实现是**尽力而为**：路由器无 UPnP / 端口冲突 / 鉴权失败等均只记 Debug 日志，
/// 不抛、不阻塞。失败时调用方应让用户改走手工 `/connect <ip:port>` 或 `/add <ip:port>` 路径。
/// </para>
/// </remarks>
public interface IUpnpClient
{
    /// <summary>
    /// 探测当前网络是否存在 UPnP IGD 网关，并申请将本机指定端口同时映射为 TCP 与 UDP（公网 → 内网）。
    /// </summary>
    /// <param name="port">期望被映射的端口（外网与内网同号）。</param>
    /// <param name="leaseDurationSeconds">映射租约秒数（默认 3600）；多数路由器会忽略此值并使用自己的租约。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>
    /// 成功时返回 <see cref="UpnpMapping"/> 含网关报告的外部 IP；无 UPnP / 失败时返回 null。
    /// </returns>
    Task<UpnpMapping?> TryMapAsync(int port, int leaseDurationSeconds = 3600, CancellationToken ct = default);

    /// <summary>
    /// 移除之前通过 <see cref="TryMapAsync"/> 申请的端口映射（同时删除 TCP 与 UDP）。
    /// </summary>
    /// <remarks>
    /// 在 <c>Program.cs</c> 的退出路径上调用。无映射 / 失败均不抛、不阻塞。
    /// </remarks>
    Task RemoveAsync(int port, CancellationToken ct = default);
}

/// <summary>
/// UPnP 映射成功的结果：包含网关报告的公网 IP 与租约。
/// </summary>
/// <param name="ExternalEndPoint">网关报告的本机公网入口 IP 与映射端口。</param>
/// <param name="Gateway">成功响应的网关基 URL（仅用于诊断）。</param>
/// <param name="LeaseDuration">网关报告的租约（可能为 0 = 永久）。</param>
public sealed record UpnpMapping(IPEndPoint ExternalEndPoint, Uri Gateway, TimeSpan LeaseDuration);