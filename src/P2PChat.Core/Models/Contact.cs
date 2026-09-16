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

    /// <summary>是否在线</summary>
    public bool IsOnline { get; set; }

    /// <summary>状态</summary>
    public Enums.PeerState State { get; set; } = Enums.PeerState.Offline;

    /// <summary>添加时间</summary>
    public DateTime AddedAt { get; init; } = DateTime.UtcNow;

    /// <summary>最后在线时间</summary>
    public DateTime? LastOnlineAt { get; set; }
}
