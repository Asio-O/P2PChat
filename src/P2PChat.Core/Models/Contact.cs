namespace P2PChat.Core.Models;

/// <summary>
/// 联系人
/// </summary>
public record Contact
{
    /// <summary>联系人节点ID</summary>
    public required NodeId NodeId { get; init; }

    /// <summary>别名/昵称</summary>
    public string Alias { get; set; } = string.Empty;

    /// <summary>
    /// 显式端点（<c>ip:port</c>），可选。
    /// <para>
    /// 手工添加联系人时（<c>/add &lt;节点ID&gt; &lt;ip:port&gt;</c>）写入，
    /// 存在时**优先于** DHT 查找结果，使两个节点无需任何发现机制即可直连。
    /// </para>
    /// <para>
    /// 公共 DHT 无法解析任意 NodeId（从不宣告自己），因此这是当前唯一可靠的直连手段。
    /// </para>
    /// </summary>
    public string? EndPoint { get; set; }

    /// <summary>是否在线</summary>
    public bool IsOnline { get; set; }

    /// <summary>状态</summary>
    public Enums.PeerState State { get; set; } = Enums.PeerState.Offline;

    /// <summary>添加时间</summary>
    public DateTime AddedAt { get; init; } = DateTime.UtcNow;

    /// <summary>最后在线时间</summary>
    public DateTime? LastOnlineAt { get; set; }
}
