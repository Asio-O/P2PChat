namespace P2PChat.Core.Models;

/// <summary>
/// 群组信息
/// </summary>
public record GroupInfo
{
    /// <summary>群组ID (SHA-256)</summary>
    public required string GroupId { get; init; }

    /// <summary>群组名称</summary>
    public required string GroupName { get; init; }

    /// <summary>创建者节点ID</summary>
    public required NodeId CreatorId { get; init; }

    /// <summary>创建时间</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>成员节点ID列表</summary>
    public List<byte[]> MemberIds { get; init; } = new();

    /// <summary>群组密钥 (256位AES密钥)</summary>
    public required byte[] GroupKey { get; init; }
}
