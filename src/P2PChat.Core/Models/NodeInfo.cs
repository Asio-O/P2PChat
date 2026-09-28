using System.Net;

namespace P2PChat.Core.Models;

/// <summary>
/// DHT网络中的节点信息
/// </summary>
/// <remarks>
/// <para>
/// 端点语义（自 Phase 1 起）：
/// </para>
/// <list type="bullet">
///   <item>
///     <see cref="EndPoint"/> —— <b>TCP 端点</b>，对端用于建立 TCP 聊听/文件传输连接；
///     对本机而言就是监听地址。设置于 <c>Program.cs</c> 构造本机 <see cref="NodeInfo"/> 时。
///   </item>
///   <item>
///     <see cref="DhtEndPoint"/> —— 可选；KRPC 报文（UDP）的源地址。仅由 <c>MainlineDhtService</c>
///     在收到对端查询/响应时填入。无 NAT 时与 <see cref="EndPoint"/> 相等；NAT 后等于对端的公网映射，
///     这是 BitTorrent 客户端「NAT 后仍可被找到」的机制。
///   </item>
///   <item>
///     <see cref="ExternalEndPoint"/> —— 可选；预留作为外部观察到的公网入口（Phase 2 填充）。
///   </item>
/// </list>
/// </remarks>
public record NodeInfo
{
    /// <summary>节点唯一标识</summary>
    public required NodeId NodeId { get; init; }

    /// <summary>节点的 TCP 端点 —— 用于建立聊天/文件传输的 TCP 连接（MessageRouter 即依赖此字段）。</summary>
    /// <remarks>
    /// 本机的 <see cref="EndPoint"/> 由 <c>Program.cs</c> 在拿到 <c>actualTcpPort</c> 后填入；
    /// 远端节点则由宣告方放入 DHT 的 <c>announce_peer.port</c> 字段填充（由 MainlineDhtService 解析）。
    /// </remarks>
    public required IPEndPoint EndPoint { get; init; }

    /// <summary>
    /// 节点的 DHT 源端点（可选）—— KRPC 报文（UDP）的源地址，由 <c>MainlineDhtService</c> 写入。
    /// </summary>
    /// <remarks>
    /// 无 NAT 时与 <see cref="EndPoint"/> 相等；NAT 后等于对端在公网上的入口地址（NAT 穿透后的外网映射）。
    /// 不被 TCP 连接路径使用；只为 NAT 诊断与外部公网可达性提供信息。
    /// </remarks>
    public IPEndPoint? DhtEndPoint { get; init; }

    /// <summary>外部可观察IP终结点 (用于NAT穿透)</summary>
    public IPEndPoint? ExternalEndPoint { get; init; }

    /// <summary>
    /// 节点身份公钥 — ECDH nistP256，格式为 SubjectPublicKeyInfo (DER，91字节)。
    /// <para>
    /// <b>可空是刻意的</b>：<c>null</c> 表示「尚未知」，不能靠填一个占位值（尤其是**填本机自己的公钥**）
    /// 冒充已知。凡是把「本机公钥」写进一条描述<b>对端</b>的 <see cref="NodeInfo"/>，任何
    /// <c>NodeId.FromPublicKey(node.PublicKey)</c> 都会算出本机的 NodeId 而不是对端的 ——
    /// 与 <c>MessageRouter.VerifyEnvelope</c> 的「公钥派生 NodeId 必须等于 SenderId」防冒名守卫等价失效。
    /// </para>
    /// <para>
    /// 真实的 <see cref="PublicKey"/> 来源只有一处：<b>已验签的 <c>MessageEnvelope.SenderPublicKey</c></b>
    /// （密钥交换响应信封）。<c>MainlineDhtService</c> 的各条解析路径与手工 <c>/add</c>、<c>/connect</c>
    /// 登记都不会得到它；需要在群邀请等场景使用时，由 <c>GroupChatService</c> 主动握手取回。
    /// </para>
    /// </summary>
    /// <remarks>
    /// 注意：这是 ECDH P-256 公钥，<b>不是</b> Ed25519 公钥——历史上本注释曾误写为 Ed25519。
    /// NodeId 由该公钥经 SHA-1 派生 (见 <see cref="NodeId.FromPublicKey"/>)。
    /// </remarks>
    public required byte[]? PublicKey { get; init; }

    /// <summary>最后探测时间</summary>
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    /// <summary>在线状态</summary>
    public Enums.PeerState State { get; set; } = Enums.PeerState.Online;
}
