namespace P2PChat.Crypto.Keys;

/// <summary>
/// 持久化联系人
/// </summary>
public class StoredContact
{
    public required string NodeId { get; set; }
    public string Alias { get; set; } = string.Empty;
    public DateTime AddedAt { get; set; }

    /// <summary>显式端点（<c>ip:port</c>），可选。存在时优先于 DHT 查找结果。</summary>
    public string? EndPoint { get; set; }
}

/// <summary>
/// 持久化群组
/// </summary>
public class StoredGroup
{
    public required string GroupId { get; set; }
    public required string GroupName { get; set; }
    public string? CreatorId { get; set; }
    public List<string> MemberIds { get; set; } = new();
    public string? EncryptedGroupKey { get; set; }
    public DateTime CreatedAt { get; set; }
}
